using System.Globalization;
using System.Text.RegularExpressions;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

public sealed class VideoFrameResult
{
    public double Time { get; init; }
    public required LevelStats Levels { get; init; }
    public double Sharpness { get; init; }
    public required SpectrumStats Spectrum { get; init; }
    public required CropBox Crop { get; init; }
    public double PeakNits { get; init; } = double.NaN;
    public double AvgNits { get; init; } = double.NaN;
}

public sealed class InterlaceResult
{
    public long Tff { get; set; }
    public long Bff { get; set; }
    public long Progressive { get; set; }
    public long Undetermined { get; set; }
    public long RepeatedFields { get; set; }

    public string Verdict
    {
        get
        {
            var total = Tff + Bff + Progressive;
            if (total == 0)
                return "unknown";
            if (Progressive >= total * 0.8)
                return RepeatedFields > (Tff + Bff + Progressive) * 0.15 ? "progressive (telecine pattern)" : "progressive";
            return Tff >= Bff ? "interlaced (TFF)" : "interlaced (BFF)";
        }
    }
}

public sealed class Finding
{
    public required string Area { get; init; }
    public required string Text { get; init; }

    /// <summary>0 = info, 1 = notable, 2 = suspicious.</summary>
    public int Level { get; init; }
}

public sealed class VideoReport
{
    public List<VideoFrameResult> Frames { get; } = [];
    public NativeResResult? NativeRes { get; set; }
    public double NativeResFrameTime { get; set; }
    public InterlaceResult? Interlace { get; set; }
    public CropBox? Crop { get; set; }
    public List<Finding> Findings { get; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitDepth { get; set; }

    public double AvgSharpness => Frames.Count > 0 ? Frames.Average(f => f.Sharpness) : double.NaN;
    public double AvgHfRatioH => Frames.Count > 0 ? Frames.Average(f => f.Spectrum.HfRatioH) : double.NaN;
    public double AvgHfRatioV => Frames.Count > 0 ? Frames.Average(f => f.Spectrum.HfRatioV) : double.NaN;
}

public sealed class VideoAnalysisOptions
{
    public int SamplePoints { get; set; } = 8;
    public bool NativeResolution { get; set; } = true;
    public bool Interlace { get; set; } = true;
    public bool Deinterlace { get; set; }
}

/// <summary>Image analysis on sampled frames: bit depth, upscale/native resolution, sharpness, crop, HDR brightness, interlacing.</summary>
public sealed partial class VideoAnalyzer(FfTools tools, string tempDir)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<VideoReport> AnalyzeAsync(MediaTitle title, double offset, VideoAnalysisOptions opt, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rep = new VideoReport();
        var v = title.Info?.Video ?? throw new InvalidOperationException("No video stream.");
        rep.Width = v.Width;
        rep.Height = v.Height;
        rep.BitDepth = v.BitDepth;
        var points = InputBuilder.EvenPoints(title.Duration, opt.SamplePoints, 8, 10).Select(t => t + offset).Where(t => t >= 0 && t < title.Duration).ToList();
        var frames = new List<LumaFrame>();
        var i = 0;
        foreach (var t in points)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Frame {++i}/{points.Count} at {Format.Time(t, false)}");
            var f = await LumaFrame.GrabAsync(tools, title, t, tempDir, opt.Deinterlace, ct).ConfigureAwait(false);
            if (f == null)
                continue;
            frames.Add(f);
            var crop = FrameMetrics.DetectCrop(f);
            var spec = await Task.Run(() => FrameMetrics.Spectrum(f, crop), ct).ConfigureAwait(false);
            var fr = new VideoFrameResult
            {
                Time = t,
                Levels = FrameMetrics.Levels(f),
                Sharpness = FrameMetrics.Sharpness(f, crop),
                Spectrum = spec,
                Crop = crop,
            };
            if (v.ColorTransfer == "smpte2084")
            {
                var (peak, avg) = FrameMetrics.PqBrightness(f);
                fr = new VideoFrameResult { Time = t, Levels = fr.Levels, Sharpness = fr.Sharpness, Spectrum = spec, Crop = crop, PeakNits = peak, AvgNits = avg };
            }
            rep.Frames.Add(fr);
        }
        if (rep.Frames.Count == 0)
            throw new IOException("No frames could be decoded.");

        // crop: the smallest borders seen (bright shots reveal the real picture edge)
        rep.Crop = new CropBox(
            rep.Frames.Min(f => f.Crop.Left), rep.Frames.Min(f => f.Crop.Top),
            rep.Frames.Min(f => f.Crop.Right), rep.Frames.Min(f => f.Crop.Bottom));

        if (opt.NativeResolution)
        {
            // the most detailed frames give the clearest answer
            var best = rep.Frames.Select((f, k) => (f.Sharpness, k)).OrderByDescending(x => x.Sharpness).Take(2).Select(x => x.k).ToList();
            progress?.Report("Native resolution test…");
            var crop = rep.Crop;
            var parts = new List<NativeResResult>();
            foreach (var k in best)
                parts.Add(await Task.Run(() => FrameMetrics.NativeResolution(frames[k], crop, ct), ct).ConfigureAwait(false));
            rep.NativeRes = FrameMetrics.Combine(parts, crop.Height(rep.Height), rep.Height);
            rep.NativeResFrameTime = rep.Frames[best[0]].Time;
        }
        if (opt.Interlace)
        {
            progress?.Report("Interlace detection…");
            rep.Interlace = await DetectInterlaceAsync(title, title.Duration / 2 + offset, ct).ConfigureAwait(false);
        }
        BuildFindings(rep, v);
        return rep;
    }

    public async Task<InterlaceResult> DetectInterlaceAsync(MediaTitle title, double time, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin" };
        args.AddRange(InputBuilder.Input(title, Math.Max(0, time), 30, tempDir).Args);
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "idet", "-frames:v", "500", "-f", "null", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var r = new InterlaceResult();
        // the filter graph can be created twice (probe + real run): the last report counts
        var m = MultiRegex().Matches(res.StdErr).LastOrDefault();
        if (m != null)
        {
            r.Tff = long.Parse(m.Groups[1].Value, Inv);
            r.Bff = long.Parse(m.Groups[2].Value, Inv);
            r.Progressive = long.Parse(m.Groups[3].Value, Inv);
            r.Undetermined = long.Parse(m.Groups[4].Value, Inv);
        }
        var rep = RepeatRegex().Matches(res.StdErr).LastOrDefault();
        if (rep != null)
            r.RepeatedFields = long.Parse(rep.Groups[1].Value, Inv) + long.Parse(rep.Groups[2].Value, Inv);
        return r;
    }

    private static void BuildFindings(VideoReport rep, StreamInfo v)
    {
        var f = rep.Findings;
        // bit depth
        if (v.BitDepth > 8)
        {
            var eff = rep.Frames.Count(x => x.Levels.EffectiveBits <= 8);
            var padded = rep.Frames.Average(x => x.Levels.PaddedShare);
            if (eff >= rep.Frames.Count * 0.75)
                f.Add(new Finding { Area = "Bit depth", Level = 2, Text = $"{v.BitDepth}-bit stream, but the luma values sit on an 8-bit grid in {eff}/{rep.Frames.Count} frames (padded {padded:P0}): 8-bit content in a {v.BitDepth}-bit container." });
            else
                f.Add(new Finding { Area = "Bit depth", Level = 0, Text = $"Real {v.BitDepth}-bit values (only {padded:P0} of samples have empty low bits)." });
        }
        else
        {
            f.Add(new Finding { Area = "Bit depth", Level = 0, Text = "8-bit stream." });
        }

        // range
        var below = rep.Frames.Max(x => x.Levels.BelowBlackShare);
        var above = rep.Frames.Max(x => x.Levels.AboveWhiteShare);
        if (v.ColorRange != "pc" && (below > 0.02 || above > 0.02))
            f.Add(new Finding { Area = "Levels", Level = 1, Text = $"Limited-range stream with values outside 16–235: {below:P1} below black, {above:P1} above white (full-range source or level shift?)." });

        // crop
        if (rep.Crop is { } c && (c.Top + c.Bottom + c.Left + c.Right) > 4)
        {
            var w = c.Width(rep.Width);
            var h = c.Height(rep.Height);
            f.Add(new Finding { Area = "Black bars", Level = 0, Text = $"Active picture {w}×{h} (bars T{c.Top} B{c.Bottom} L{c.Left} R{c.Right}), aspect ≈ {(double)w / h * ParseSar(v.Sar):0.00}:1." });
        }

        // native resolution
        if (rep.NativeRes is { } nr)
        {
            if (nr.NativeHeight is { } nh && nh < rep.Height * 0.95)
                f.Add(new Finding { Area = "Upscale", Level = 2, Text = $"Looks upscaled from {nh}p: descaling to {nh}p ({nr.Kernel}) rebuilds the frame {nr.Confidence:P0} better than the neighbouring heights." });
            else
                f.Add(new Finding { Area = "Upscale", Level = 0, Text = $"No native-resolution notch below {rep.Height}p (best {nr.Confidence:P0}): native, or upscaled with a kernel/filtering the test does not model." });
        }
        var cuts = rep.Frames.Select(x => x.Spectrum.CutoffHeight).Where(x => !double.IsNaN(x)).ToList();
        if (cuts.Count >= rep.Frames.Count / 2 && cuts.Count > 0)
            f.Add(new Finding { Area = "Detail", Level = 1, Text = $"Vertical detail stops sharply near {cuts.Average():0} px of {rep.Height} in {cuts.Count}/{rep.Frames.Count} frames (soft source, upscale or heavy filtering)." });
        f.Add(new Finding { Area = "Detail", Level = 0, Text = $"Sharpness {rep.AvgSharpness:0.0}, high-frequency energy H {rep.AvgHfRatioH:0.0} dB / V {rep.AvgHfRatioV:0.0} dB (compare between sources)." });

        // HDR
        if (v.ColorTransfer == "smpte2084")
        {
            var peak = rep.Frames.Max(x => x.PeakNits);
            var avg = rep.Frames.Average(x => x.AvgNits);
            var lvl = peak < 200 ? 2 : 0;
            f.Add(new Finding
            {
                Area = "HDR",
                Level = lvl,
                Text = $"Measured luma peak ≈ {peak:0} nits, average ≈ {avg:0.0} nits over the samples" +
                       (lvl == 2 ? " — PQ stream that never goes above SDR brightness (fake/flat HDR?)." : ".") +
                       (v.ContentLight.Length > 0 ? $" Metadata: {v.ContentLight}." : ""),
            });
        }
        else if (v.ColorTransfer == "arib-std-b67")
        {
            f.Add(new Finding { Area = "HDR", Level = 0, Text = "HLG stream." });
        }

        // interlace
        if (rep.Interlace is { } il)
        {
            var declared = v.FieldOrder is "tt" or "bb" or "tb" or "bt" ? "interlaced" : v.FieldOrder == "progressive" ? "progressive" : "";
            var lvl = declared == "progressive" && il.Verdict.StartsWith("interlaced", StringComparison.Ordinal) ? 2 : 0;
            f.Add(new Finding { Area = "Scan", Level = lvl, Text = $"Content: {il.Verdict} (TFF {il.Tff}, BFF {il.Bff}, progressive {il.Progressive}); stream flagged {(declared.Length > 0 ? declared : "unknown")}." });
        }
    }

    private static double ParseSar(string sar)
    {
        var p = sar.Split(':');
        return p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var a) && double.TryParse(p[1], NumberStyles.Float, Inv, out var b) && a > 0 && b > 0 ? a / b : 1;
    }

    [GeneratedRegex(@"Multi frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)\s*Undetermined:\s*(\d+)")]
    private static partial Regex MultiRegex();

    [GeneratedRegex(@"Repeated Fields:\s*Neither:\s*\d+\s*Top:\s*(\d+)\s*Bottom:\s*(\d+)")]
    private static partial Regex RepeatRegex();
}
