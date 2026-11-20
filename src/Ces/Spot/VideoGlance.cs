using System.Globalization;
using Ces.Compare;
using Ces.Disc;
using Ces.Model;
using Ces.Probe;
using Ces.Spot;
using Ces.Tools;

namespace Ces.SpotUi;

/// <summary>
/// Spot's quick look at video: MKV/MP4/M2TS/AVI/... files, ISOs and Blu-ray / DVD folders become rows with codec,
/// resolution, HDR, bitrate, the encoder library and its key settings, and the audio / subtitle tracks; the details
/// window gets Compare's Specs rows. Needs ffprobe (and MediaInfo for HDR formats and encoder strings).
/// </summary>
public static class VideoGlance
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] Extensions =
        [".mkv", ".mp4", ".m4v", ".mov", ".m2ts", ".mts", ".ts", ".avi", ".webm", ".wmv", ".mpg", ".mpeg", ".vob", ".iso"];

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".mpls", StringComparison.OrdinalIgnoreCase); // one mix of a music Blu-ray

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "ces", "spot-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// A music Blu-ray (by its release name) as one row per mix: its music playlists as .mpls paths. Anything else,
    /// or a disc whose playlists are not music, stays the one path.
    /// </summary>
    public static async Task<List<string>> ExpandMusicAsync(string path, Func<Task<FfTools?>> tools, CancellationToken ct)
    {
        var disc = Directory.Exists(path) || Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase);
        if (!disc || !Ces.Music.MusicAnalyzer.NameSaysMusic(path) || await tools().ConfigureAwait(false) is not { } ff)
            return [path];
        try
        {
            var found = await Ces.Music.MusicAnalyzer.FindAsync(ff, path, ct).ConfigureAwait(false);
            return found.Count > 0 ? found.Select(f => f.Path).ToList() : [path];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [path];
        }
    }

    /// <summary>A Blu-ray or DVD folder (the folder holding BDMV / VIDEO_TS, or that folder itself).</summary>
    public static bool IsDiscFolder(string dir)
    {
        if (!Directory.Exists(dir))
            return false;
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        return name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) || name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(dir, "BDMV")) || Directory.Exists(Path.Combine(dir, "VIDEO_TS"));
    }

    public static async Task<AnalysisResult> AnalyzeAsync(string path, Func<Task<FfTools?>> tools, CancellationToken ct)
    {
        var real = Vfs.Vfs.RealFile(path);
        var isDir = Directory.Exists(path);
        var r = new AnalysisResult
        {
            Path = path,
            FileSize = isDir ? 0 : File.Exists(real) ? new FileInfo(real).Length : 0,
            LastWriteUtc = isDir ? Directory.GetLastWriteTimeUtc(path) : File.Exists(real) ? File.GetLastWriteTimeUtc(real) : default,
        };
        var ff = await tools().ConfigureAwait(false);
        if (ff == null)
        {
            r.Error = "video needs ffprobe: set the ffmpeg folder in File › Settings";
            return r;
        }
        MediaSource? src = null;
        try
        {
            src = await SourceOpener.OpenAsync(path, false, ct).ConfigureAwait(false);
            var title = src.MainTitle ?? throw new InvalidDataException("no playable title");
            var info = await new MediaProber(ff).ProbeAsync(src, title, ct).ConfigureAwait(false);
            // a music release (songs as chapters, lossless audio): every song checked like a lossless file
            if (Path.GetExtension(path).Equals(".mpls", StringComparison.OrdinalIgnoreCase) || Ces.Music.MusicAnalyzer.IsMusic(path, title, info))
                await Ces.Music.MusicAnalyzer.AnalyzeAsync(ff, src, title, info, r, TempDir, ct).ConfigureAwait(false);
            else
                Fill(r, src, title, info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or FormatException)
        {
            r.Error = ex.Message;
        }
        finally
        {
            if (src != null)
                await SourceOpener.CloseAsync(src).ConfigureAwait(false);
        }
        return r;
    }

    private static void Fill(AnalysisResult r, MediaSource src, MediaTitle title, MediaInfoSummary info)
    {
        var v = info.Video;
        r.Format = src.Kind switch
        {
            SourceKind.BluRay => "Blu-ray",
            SourceKind.Dvd => "DVD",
            _ => Path.GetExtension(src.Path).TrimStart('.').ToUpperInvariant(),
        };
        if (r.FileSize == 0)
            r.FileSize = title.SizeBytes > 0 ? title.SizeBytes : info.SizeBytes;
        r.DurationSeconds = info.Duration > 0 ? info.Duration : title.Duration;
        r.BitrateKbps = info.OverallBitRate > 0 ? info.OverallBitRate / 1000.0
            : r.DurationSeconds > 0 && r.FileSize > 0 ? r.FileSize * 8 / r.DurationSeconds / 1000 : 0;
        r.Channels = info.Audio.Count();
        if (v != null)
        {
            r.ModeName = string.Join(" ", new[]
            {
                string.Create(Inv, $"{v.Width}×{v.Height}"),
                v.FrameRateText.Length > 0 ? v.FrameRateText : v.FrameRate > 0 ? v.FrameRate.ToString("0.###", Inv) : "",
                v.IsHdr ? v.HdrShort : "",
                v.ScanType.Equals("Interlaced", StringComparison.OrdinalIgnoreCase) ? "interlaced" : "",
            }.Where(x => x.Length > 0));
        }
        var lib = info.Encoder;
        (r.Encoder, r.Confidence, r.GuessReason) = lib.Length > 0
            ? (lib, GuessConfidence.High, "encoder library string in the video stream / container")
            : (v?.CodecDisplay ?? "no video", GuessConfidence.None, "no encoder string (MediaInfo shows it when installed)");
        r.EncoderSettings = string.Join(" · ", new[]
        {
            v?.CodecDisplay ?? "",
            SettingsSummary(info.EncodingSettings),
            string.Create(Inv, $"{info.Audio.Count()} audio, {info.Subtitles.Count()} subs"),
        }.Where(x => x.Length > 0));
        if (v == null)
            r.Problems.Add("no video stream");
        r.Quality = QualityRating.Unknown;

        // the same rows as Compare's Specs tab (without the comparison-only time map)
        var item = new CompareItem { Source = src, Title = title };
        foreach (var row in SpecTable.Build([item]).Where(x => x.Label != "Time map" && x.Values[0].Length > 0))
            r.Details.Add(new InfoItem(row.Group, row.Label, row.Values[0]));
        if (info.EncodingSettings.Length > 160)
            r.Details.Add(new InfoItem("Video", "Encoding settings (all)", info.EncodingSettings));
    }

    /// <summary>The settings that matter at a glance from an x264/x265 info string (rate control, ref, bframes, aq, psy).</summary>
    public static string SettingsSummary(string settings)
    {
        if (settings.Length == 0)
            return "";
        var kv = settings.Split(['/', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        var rc = kv.GetValueOrDefault("rc");
        if (kv.TryGetValue("crf", out var crf))
            parts.Add("crf " + crf);
        else if (rc is "2pass" or "abr" or "cbr" && kv.TryGetValue("bitrate", out var br))
            parts.Add($"{rc} {br} kbps");
        else if (rc != null)
            parts.Add(rc);
        // x264 and x265 spell some keys differently (aq / aq-mode, psy_rd / psy-rd)
        foreach (var (keys, label) in new[] { (["preset"], "preset"), (["ref"], "ref"), (["bframes"], "bframes"), (new[] { "aq", "aq-mode" }, "aq"), (["psy_rd", "psy-rd"], "psy-rd"), (["deblock"], "deblock") })
        {
            if (keys.Select(k => kv.GetValueOrDefault(k)).FirstOrDefault(v => v != null) is { } val)
                parts.Add($"{label} {val}");
        }
        return string.Join(", ", parts);
    }
}
