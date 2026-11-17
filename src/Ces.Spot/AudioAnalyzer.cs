using Ces.Spot.Flac;
using Ces.Spot.Mp3;
using Ces.Spot.Tags;

namespace Ces.Spot;

public sealed record AnalyzeOptions
{
    /// <summary>Decode every FLAC frame (frame CRCs, MD5 check, subframe statistics). Slower but complete.</summary>
    public bool DecodeFlac { get; init; } = true;

    /// <summary>Receives the decoded FLAC samples (interleaved little-endian PCM), for checks and tests.</summary>
    public Stream? PcmOutput { get; init; }
}

/// <summary>Entry point: analyzes one file of any supported format.</summary>
public static class AudioAnalyzer
{
    public static readonly string[] Extensions = [".mp3", ".mp2", ".mp1", ".mpa", ".flac"];

    public static bool IsSupported(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static AnalysisResult Analyze(string path, AnalyzeOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AnalyzeOptions();
        // a file inside a RAR set ("set.rar::track.flac") is read in place
        bool inside = Vfs.Vfs.IsVirtual(path);
        var fi = new FileInfo(Vfs.Vfs.RealFile(path));
        var result = new AnalysisResult
        {
            Path = inside ? path : fi.FullName,
            FileSize = !fi.Exists ? 0 : inside ? SafeLength(path) : fi.Length,
            LastWriteUtc = fi.Exists ? fi.LastWriteTimeUtc : default,
        };
        try
        {
            using var s = inside
                ? Vfs.Vfs.OpenRead(path)
                : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            var tags = TagScanner.Scan(s);
            result.Tags = tags;
            var magic = new byte[4];
            s.Position = tags.AudioStart;
            int got = s.ReadAtLeast(magic, 4, false);
            if (got == 4 && magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
            {
                // FLAC files must not carry ID3v1/APE at the end, but some do: the frames stop where the tags start.
                result.Format = "FLAC";
                result.Flac = FlacAnalyzer.Analyze(s, tags.AudioStart, tags.AudioEnd, options.DecodeFlac, ct, options.PcmOutput);
                FlacReport.Fill(result);
            }
            else if (got == 4 && magic[0] == 'O' && magic[1] == 'g' && magic[2] == 'g' && magic[3] == 'S')
            {
                result.Error = "Ogg files are not supported yet";
            }
            else
            {
                AnalyzeMpeg(s, tags, result, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            result.Error = ex.Message;
        }
        CheckExtension(result);
        return result;
    }

    private static void CheckExtension(AnalysisResult r)
    {
        if (r.Error is not null || r.Format == "Unknown")
        {
            return;
        }
        string ext = System.IO.Path.GetExtension(r.Path).ToLowerInvariant();
        bool ok = r.Format switch
        {
            "FLAC" => ext is ".flac" or ".fla",
            "MP3" => ext is ".mp3" or ".mpa",
            "MP2" => ext is ".mp2" or ".mpa" or ".mp3",
            "MP1" => ext is ".mp1" or ".mpa",
            _ => true,
        };
        if (!ok)
        {
            r.Problems.Insert(0, $"file is {r.Format} but named {ext}");
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return Vfs.Vfs.Length(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return 0;
        }
    }

    private static void AnalyzeMpeg(Stream s, TagInfo tags, AnalysisResult result, CancellationToken ct)
    {
        long length = tags.AudioEnd - tags.AudioStart;
        if (length <= 0)
        {
            result.Error = "No audio data";
            return;
        }
        if (length > Array.MaxLength)
        {
            length = Array.MaxLength;
        }
        var buf = new byte[length];
        s.Position = tags.AudioStart;
        s.ReadExactly(buf);
        var mp3 = Mp3Analyzer.Analyze(buf, tags.AudioStart, ct);
        if (mp3 is null)
        {
            result.Error = "No MPEG audio frames found";
            return;
        }
        result.Mp3 = mp3;
        result.Format = mp3.FirstHeader.Layer switch
        {
            1 => "MP1",
            2 => "MP2",
            _ => "MP3",
        };
        Mp3Report.Fill(result);
    }
}
