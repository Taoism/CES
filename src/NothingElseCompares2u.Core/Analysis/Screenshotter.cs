using System.Globalization;
using System.Text.RegularExpressions;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

public enum ToneMapMode
{
    Auto,
    Off,
    On,
}

public sealed class ShotOptions
{
    /// <summary>Scale anamorphic video (DVD, some BD) to its display aspect ratio.</summary>
    public bool CorrectAspect { get; set; } = true;

    public bool Deinterlace { get; set; }

    /// <summary>Auto = tone-map HDR (PQ/HLG) to SDR so the shots look right next to SDR sources.</summary>
    public ToneMapMode ToneMap { get; set; } = ToneMapMode.Auto;

    /// <summary>Scale every shot to this height (0 = native) — handy for comparing a 4K source to a 1080p encode.</summary>
    public int ScaleToHeight { get; set; }

    /// <summary>Subtitle stream (index among the subtitle streams) to burn in, null = none.</summary>
    public int? Subtitle { get; set; }

    public string CacheKey => $"{CorrectAspect}|{Deinterlace}|{ToneMap}|{ScaleToHeight}|{Subtitle}";

    public ShotOptions With(int? subtitle) => new()
    {
        CorrectAspect = CorrectAspect,
        Deinterlace = Deinterlace,
        ToneMap = ToneMap,
        ScaleToHeight = ScaleToHeight,
        Subtitle = subtitle,
    };
}

public sealed class FrameStats
{
    public double YMin { get; set; }
    public double YLow { get; set; }
    public double YAvg { get; set; }
    public double YHigh { get; set; }
    public double YMax { get; set; }
    public double SatAvg { get; set; }
    public double Blur { get; set; } = double.NaN;
    public double Block { get; set; } = double.NaN;
    public int BitDepth { get; set; } = 8;
}

public sealed class ShotResult
{
    public required string ImagePath { get; init; }
    public double RequestedTime { get; init; }

    /// <summary>Presentation time of the frame that came out (title seconds).</summary>
    public double FrameTime { get; set; } = double.NaN;

    public string PictType { get; set; } = "?";
    public bool KeyFrame { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public FrameStats? Stats { get; set; }
    public string? Error { get; set; }
}

/// <summary>Grabs the frame at a title time from any source, with frame type and signal statistics.</summary>
public sealed partial class Screenshotter(FfTools tools, string tempDir)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>As <see cref="TakeAsync(MediaTitle, double, string, ShotOptions, CancellationToken)"/>; a null time means the moment was cut from this source.</summary>
    public Task<ShotResult> TakeAsync(MediaTitle title, double? time, string outPng, ShotOptions opt, CancellationToken ct = default) =>
        time is { } t
            ? TakeAsync(title, t, outPng, opt, ct)
            : Task.FromResult(new ShotResult { ImagePath = outPng, RequestedTime = double.NaN, Error = "Not in this source (cut from this edit)." });

    public async Task<ShotResult> TakeAsync(MediaTitle title, double time, string outPng, ShotOptions opt, CancellationToken ct = default)
    {
        var info = title.Info;
        var video = info?.Video;
        var result = new ShotResult { ImagePath = outPng, RequestedTime = time };
        if (time < 0 || (title.Duration > 0 && time > title.Duration))
        {
            result.Error = "Time is outside this source (check the sync offset).";
            return result;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        Directory.CreateDirectory(tempDir);
        var cacheKey = ShotCache.Key(title, time, opt.CacheKey);
        if (ShotCache.TryGet(cacheKey, outPng) is { } cached)
            return cached;
        var metaName = $"meta-{Guid.NewGuid():N}.txt";

        var sub = opt.Subtitle is { } si ? info?.Subtitles.ElementAtOrDefault(si) : null;
        var bitmapSub = sub != null && sub.Codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";
        var textSub = sub != null && !bitmapSub && title.Segments.Count == 1 && File.Exists(title.Segments[0].Url);
        // a subtitle shown now may have started seconds ago: decode from earlier so it is on screen
        var input = InputBuilder.Input(title, time, 2, tempDir, minPreroll: sub != null ? 10 : 0);
        var chain = new List<string>();
        var sw = InputBuilder.VideoPrefix(title);
        if (sw.Length > 0)
            chain.Add(sw.TrimEnd(','));
        if (input.Skip > 0)
            chain.Add(input.TrimFilter.TrimEnd(','));
        if (opt.Deinterlace)
            chain.Add("bwdif=mode=send_frame:deint=all");
        chain.Add("showinfo");
        chain.Add("signalstats");
        chain.Add("blurdetect");
        chain.Add("blockdetect");
        chain.Add($"metadata=mode=print:file={metaName}");
        var hdr = video?.IsHdr == true;
        if ((opt.ToneMap == ToneMapMode.On || (opt.ToneMap == ToneMapMode.Auto && hdr)) && tools.HasZscale)
        {
            var tin = video?.ColorTransfer == "arib-std-b67" ? "arib-std-b67" : "smpte2084";
            chain.Add($"zscale=tin={tin}:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        }
        else if (video != null && video.ColorSpace.StartsWith("bt2020", StringComparison.Ordinal) == false && video.Height <= 576 && video.Height > 0)
        {
            // SD is BT.601: convert explicitly so the PNG colours match HD sources
            chain.Add("scale=in_color_matrix=bt601:out_color_matrix=bt709");
        }
        // text subtitles are rendered at absolute times: give the frames their real time while drawing
        var seekBase = time - input.Skip;
        if (textSub)
            chain.Add($"setpts=PTS+{InputBuilder.S(seekBase)}/TB,subtitles=filename='{FilterPath(title.Segments[0].Url)}':si={opt.Subtitle},setpts=PTS-{InputBuilder.S(seekBase)}/TB");
        var afterSubs = new List<string>();
        if (opt.CorrectAspect)
            afterSubs.Add("scale=w='trunc(iw*max(1,sar)/2)*2':h='trunc(ih*max(1,1/sar)/2)*2':flags=lanczos,setsar=1");
        if (opt.ScaleToHeight > 0)
            afterSubs.Add($"scale=-2:{opt.ScaleToHeight}:flags=lanczos");
        afterSubs.Add("format=rgb24");

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(input.Args);
        if (bitmapSub && video != null)
        {
            // picture subtitles: scale their canvas to the frame and lay them over it (before the aspect fix, like a player)
            // PGS is drawn on a 1920×1080 canvas (also on UHD discs and cropped encodes): fit its width, centre it;
            // DVD subpictures share the video's own canvas
            var (cw, chh) = sub!.Codec == "hdmv_pgs_subtitle" ? (sub.Width > 0 ? sub.Width : 1920, sub.Height > 0 ? sub.Height : 1080) : (video.Width, video.Height);
            var sh = (int)Math.Round(video.Width * (double)chh / cw / 2) * 2;
            var y = (video.Height - sh) / 2;
            var graph = $"[0:v:0]{string.Join(",", chain)}[v];[0:s:{opt.Subtitle}]scale={video.Width}:{sh}[s];" +
                        $"[v][s]overlay=x=0:y={y}:eof_action=pass:format=auto,{string.Join(",", afterSubs)}[out]";
            args.AddRange(["-filter_complex", graph, "-map", "[out]", "-an", "-dn", "-frames:v", "1", "-update", "1", Path.GetFullPath(outPng)]);
        }
        else
        {
            chain.AddRange(afterSubs);
            args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", string.Join(",", chain), "-frames:v", "1", "-update", "1", Path.GetFullPath(outPng)]);
        }

        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct, workingDirectory: tempDir).ConfigureAwait(false);
        if (!File.Exists(outPng) || new FileInfo(outPng).Length == 0)
        {
            result.Error = LastError(res.StdErr);
            return result;
        }
        var m = ShowInfoRegex().Match(res.StdErr);
        if (m.Success)
        {
            // after an input -ss the target frame is at 0: pts_time is the distance from the requested time
            if (double.TryParse(m.Groups["t"].Value, NumberStyles.Float, Inv, out var pt))
                result.FrameTime = time - input.Skip + pt - (input.VideoTrimStart - input.Skip);
            result.PictType = m.Groups["type"].Value;
            result.KeyFrame = m.Groups["key"].Value == "1";
        }
        var metaPath = Path.Combine(tempDir, metaName);
        if (File.Exists(metaPath))
        {
            result.Stats = ParseMeta(await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false), video?.BitDepth ?? 8);
            TryDelete(metaPath);
        }
        (result.Width, result.Height) = PngSize(outPng);
        ShotCache.Put(cacheKey, result);
        return result;
    }

    /// <summary>A path inside a filter argument: forward slashes, ':' and quotes escaped.</summary>
    internal static string FilterPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').Replace(":", "\\:").Replace("'", @"'\''");

    /// <summary>Title time of the first frame at or after <paramref name="time"/> (NaN when nothing decodes).</summary>
    public async Task<double> FrameTimeAtAsync(MediaTitle title, double time, CancellationToken ct = default)
    {
        var input = InputBuilder.Input(title, time, 2, tempDir);
        var args = new List<string> { "-hide_banner", "-nostdin" };
        args.AddRange(input.Args);
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", InputBuilder.VideoPrefix(title) + input.TrimFilter + "showinfo", "-frames:v", "1", "-f", "null", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var m = ShowInfoRegex().Match(res.StdErr);
        return m.Success && double.TryParse(m.Groups["t"].Value, NumberStyles.Float, Inv, out var pt) ? time - input.Skip + pt - (input.VideoTrimStart - input.Skip) : double.NaN;
    }

    internal static FrameStats ParseMeta(string text, int bitDepth)
    {
        var st = new FrameStats { BitDepth = bitDepth };
        foreach (var line in text.Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i < 0)
                continue;
            var key = line[..i].Trim();
            if (!double.TryParse(line[(i + 1)..].Trim(), NumberStyles.Float, Inv, out var v))
                continue;
            switch (key)
            {
                case "lavfi.signalstats.YMIN": st.YMin = v; break;
                case "lavfi.signalstats.YLOW": st.YLow = v; break;
                case "lavfi.signalstats.YAVG": st.YAvg = v; break;
                case "lavfi.signalstats.YHIGH": st.YHigh = v; break;
                case "lavfi.signalstats.YMAX": st.YMax = v; break;
                case "lavfi.signalstats.SATAVG": st.SatAvg = v; break;
                case "lavfi.blur": st.Blur = v; break;
                case "lavfi.block": st.Block = v; break;
            }
        }
        return st;
    }

    /// <summary>Width/height from the PNG IHDR chunk (no image library needed in Core).</summary>
    public static (int W, int H) PngSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b = new byte[24];
            if (fs.Read(b, 0, 24) == 24)
                return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        }
        catch (IOException)
        {
            // fall through
        }
        return (0, 0);
    }

    public static string LastError(string stderr)
    {
        var lines = stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var err = lines.LastOrDefault(l => l.Contains("rror", StringComparison.Ordinal) || l.Contains("Invalid", StringComparison.Ordinal) || l.Contains("No such", StringComparison.Ordinal));
        return err ?? (lines.Count > 0 ? lines[^1] : "ffmpeg produced no frame.");
    }

    private static void TryDelete(string p)
    {
        try
        {
            File.Delete(p);
        }
        catch (IOException)
        {
            // temp file stays
        }
    }

    [GeneratedRegex(@"\] n:\s*0 .*?pts_time:(?<t>-?[\d.]+).*?iskey:(?<key>\d).*?type:(?<type>\w)", RegexOptions.Singleline)]
    private static partial Regex ShowInfoRegex();
}
