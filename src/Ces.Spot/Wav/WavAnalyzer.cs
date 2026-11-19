using System.Globalization;
using System.Text;

namespace Ces.Spot.Wav;

/// <summary>A RIFF/WAVE file: format, chunks, software tags and what the samples say about the source.</summary>
public sealed class WavAnalysis
{
    public int FormatTag { get; internal set; }
    public string FormatName { get; internal set; } = "";
    public int Channels { get; internal set; }
    public int SampleRate { get; internal set; }
    public int BitsPerSample { get; internal set; }
    public int ValidBits { get; internal set; }
    public int BlockAlign { get; internal set; }
    public bool IsFloat { get; internal set; }
    public long DataOffset { get; internal set; }
    public long DataSize { get; internal set; }

    /// <summary>Chunks in file order ("fmt ", "LIST", "data", ...) with their sizes.</summary>
    public List<KeyValuePair<string, long>> Chunks { get; } = [];

    /// <summary>LIST/INFO fields (ISFT software, INAM title, IART artist, ...) and BWF bext originator / coding history.</summary>
    public Dictionary<string, string> Info { get; } = new(StringComparer.Ordinal);

    public int UsedBits { get; internal set; }
    public double PeakDb { get; internal set; } = double.NaN;
    public double[] Spectrum { get; internal set; } = [];
    public double CutoffHz { get; internal set; } = double.NaN;
    public double CutoffSteepnessDb { get; internal set; } = double.NaN;

    /// <summary>The data chunk claims more bytes than the file has.</summary>
    public bool Truncated { get; internal set; }
}

/// <summary>Reads RIFF/WAVE: chunk walk, fmt, LIST/INFO and bext, then decodes the PCM for the lossless checks.</summary>
public static class WavAnalyzer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool IsWave(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WAVE"u8);

    public static void Analyze(Stream s, long start, AnalysisResult r, CancellationToken ct)
    {
        var a = new WavAnalysis();
        r.Wav = a;
        r.Format = "WAV";
        s.Position = start + 12;
        var hdr = new byte[8];
        while (s.Position + 8 <= s.Length)
        {
            ct.ThrowIfCancellationRequested();
            s.ReadExactly(hdr);
            var id = Encoding.ASCII.GetString(hdr, 0, 4);
            long size = BitConverter.ToUInt32(hdr, 4);
            var body = s.Position;
            a.Chunks.Add(new(id, size));
            switch (id)
            {
                case "fmt ":
                    ReadFmt(s, (int)Math.Min(size, 64), a);
                    break;
                case "LIST":
                    ReadList(s, Math.Min(size, 1 << 20), a);
                    break;
                case "bext":
                    ReadBext(s, Math.Min(size, 1 << 20), a);
                    break;
                case "data":
                    a.DataOffset = body;
                    a.DataSize = size;
                    if (body + size > s.Length)
                    {
                        a.Truncated = true;
                        a.DataSize = s.Length - body;
                    }
                    break;
            }
            // chunks are word-aligned
            s.Position = body + size + (size & 1);
        }
        if (a.DataOffset == 0 || a.BlockAlign <= 0 || a.SampleRate <= 0)
        {
            r.Error = a.BlockAlign <= 0 ? "WAV without a usable fmt chunk" : "WAV without a data chunk";
            return;
        }
        Decode(s, a, ct);
        Fill(r, a);
    }

    private static void ReadFmt(Stream s, int size, WavAnalysis a)
    {
        var b = new byte[size];
        s.ReadExactly(b);
        if (size < 16)
            return;
        a.FormatTag = BitConverter.ToUInt16(b, 0);
        a.Channels = BitConverter.ToUInt16(b, 2);
        a.SampleRate = (int)BitConverter.ToUInt32(b, 4);
        a.BlockAlign = BitConverter.ToUInt16(b, 12);
        a.BitsPerSample = BitConverter.ToUInt16(b, 14);
        a.ValidBits = a.BitsPerSample;
        var tag = a.FormatTag;
        if (tag == 0xFFFE && size >= 40)
        {
            // WAVE_FORMAT_EXTENSIBLE: valid bits and the real format in the sub-format GUID
            a.ValidBits = BitConverter.ToUInt16(b, 18) is var vb and > 0 ? vb : a.BitsPerSample;
            tag = BitConverter.ToUInt16(b, 24);
        }
        a.IsFloat = tag == 3;
        a.FormatName = tag switch
        {
            1 => "PCM",
            3 => "IEEE float",
            6 => "A-law",
            7 => "µ-law",
            0x55 => "MP3 in WAV",
            0x2000 => "AC-3 in WAV",
            _ => string.Create(Inv, $"format 0x{tag:X4}"),
        } + (a.FormatTag == 0xFFFE ? " (extensible)" : "");
        if (tag is not (1 or 3))
            a.BlockAlign = 0; // not decodable here
    }

    private static void ReadList(Stream s, long size, WavAnalysis a)
    {
        var b = new byte[size];
        s.ReadExactly(b);
        if (size < 4 || Encoding.ASCII.GetString(b, 0, 4) != "INFO")
            return;
        var p = 4;
        while (p + 8 <= size)
        {
            var id = Encoding.ASCII.GetString(b, p, 4);
            var len = (int)BitConverter.ToUInt32(b, p + 4);
            if (len < 0 || p + 8 + len > size)
                break;
            a.Info[id] = Text(b.AsSpan(p + 8, len));
            p += 8 + len + (len & 1);
        }
    }

    private static void ReadBext(Stream s, long size, WavAnalysis a)
    {
        var b = new byte[size];
        s.ReadExactly(b);
        if (size >= 256 + 32)
            a.Info["Originator"] = Text(b.AsSpan(256, 32));
        if (size > 602)
            a.Info["CodingHistory"] = Text(b.AsSpan(602));
    }

    private static string Text(ReadOnlySpan<byte> b)
    {
        var end = b.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end >= 0 ? b[..end] : b).Trim();
    }

    private static void Decode(Stream s, WavAnalysis a, CancellationToken ct)
    {
        var bits = a.BitsPerSample;
        if (bits is < 8 or > 64 || a.Channels <= 0 || a.BlockAlign != a.Channels * ((bits + 7) / 8))
            return;
        var frames = a.DataSize / a.BlockAlign;
        var stats = new PcmStats(a.SampleRate, a.Channels, bits, frames, a.IsFloat);
        var buf = new byte[a.BlockAlign * 16384];
        s.Position = a.DataOffset;
        var left = frames * a.BlockAlign;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buf.Length, left);
            var got = s.ReadAtLeast(buf.AsSpan(0, want), want, false);
            if (got <= 0)
                break;
            stats.Add(buf.AsSpan(0, got - got % a.BlockAlign));
            left -= got;
        }
        a.UsedBits = stats.UsedBits;
        a.PeakDb = stats.PeakDb;
        a.Spectrum = stats.Spectrum;
        if (a.Spectrum.Length > 0)
            (a.CutoffHz, a.CutoffSteepnessDb) = Dsp.Spectrum.FindCutoff(a.Spectrum, a.SampleRate);
    }

    private static void Fill(AnalysisResult r, WavAnalysis a)
    {
        r.SampleRate = a.SampleRate;
        r.Channels = a.Channels;
        r.BitsPerSample = a.ValidBits;
        r.DurationSeconds = a.BlockAlign > 0 ? (double)a.DataSize / a.BlockAlign / a.SampleRate : 0;
        r.BitrateKbps = r.DurationSeconds > 0 ? a.DataSize * 8 / r.DurationSeconds / 1000 : 0;
        r.ModeName = a.Channels switch { 1 => "mono", 2 => "stereo", _ => string.Create(Inv, $"{a.Channels} channels") };
        r.FrameCount = a.BlockAlign > 0 ? a.DataSize / a.BlockAlign : 0;
        var software = a.Info.GetValueOrDefault("ISFT") ?? a.Info.GetValueOrDefault("Originator") ?? "";
        (r.Encoder, r.Confidence, r.GuessReason) = software.Length > 0
            ? (EncoderNames.Normalize(software), GuessConfidence.High, "software tag (LIST/INFO ISFT or BWF originator)")
            : ("Unknown", GuessConfidence.None, "no software tag");
        r.EncoderSettings = a.FormatName + (a.IsFloat ? "" : string.Create(Inv, $", {a.ValidBits}-bit"));
        foreach (var (k, field) in new[] { ("INAM", "Title"), ("IART", "Artist"), ("IPRD", "Album"), ("ICRD", "Year"), ("IGNR", "Genre"), ("ICMT", "Comment") })
        {
            if (a.Info.TryGetValue(k, out var v) && v.Length > 0)
                (r.Tags ??= new Tags.TagInfo()).Fields[field] = v;
        }
        if (a.Truncated)
            r.Problems.Add("the data chunk is longer than the file: truncated");
        var streamProblems = r.Problems.Count;

        r.Details.Add(new InfoItem("Stream", "Type", "WAV (RIFF/WAVE)"));
        r.Details.Add(new InfoItem("Stream", "Format", a.FormatName));
        r.Details.Add(new InfoItem("Stream", "Sample rate", string.Create(Inv, $"{a.SampleRate} Hz")));
        r.Details.Add(new InfoItem("Stream", "Channels", a.Channels.ToString(Inv)));
        r.Details.Add(new InfoItem("Stream", "Bits per sample", a.ValidBits == a.BitsPerSample ? a.BitsPerSample.ToString(Inv) : string.Create(Inv, $"{a.ValidBits} valid in {a.BitsPerSample}")));
        r.Details.Add(new InfoItem("Stream", "Length", Fmt.Duration(r.DurationSeconds)));
        r.Details.Add(new InfoItem("Stream", "Audio data", string.Create(Inv, $"{a.DataSize:N0} bytes at {a.DataOffset:N0}")));
        if (!double.IsNaN(a.PeakDb))
            r.Details.Add(new InfoItem("Stream", "Peak", double.IsNegativeInfinity(a.PeakDb) ? "silence" : string.Create(Inv, $"{a.PeakDb:0.0} dBFS")));
        r.Details.Add(new InfoItem("Stream", "Chunks", string.Join(", ", a.Chunks.Select(c => string.Create(Inv, $"{c.Key.Trim()} ({c.Value:N0})")))));
        foreach (var (k, v) in a.Info)
            r.Details.Add(new InfoItem("Tags", k, v));
        r.Details.Add(new InfoItem("Encoder", "Encoder (guess)", r.Encoder));

        LosslessSource.Check(r, a.SampleRate, a.ValidBits, a.UsedBits, a.Spectrum, a.CutoffHz, a.CutoffSteepnessDb);
        r.Quality = streamProblems > 0 ? QualityRating.Poor : r.Problems.Count > 0 ? QualityRating.Fair : QualityRating.Lossless;
    }
}
