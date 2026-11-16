using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

[Flags]
public enum Metric
{
    None = 0,
    Psnr = 1,
    Ssim = 2,
    Vmaf = 4,
    Xpsnr = 8,
}

public sealed class MetricOptions
{
    public Metric Metrics { get; set; } = Metric.Psnr | Metric.Ssim | Metric.Vmaf;
    public double Start { get; set; } = double.NaN;
    public double Length { get; set; } = 60;

    /// <summary>Crop applied to the reference before comparing (e.g. bars the encode removed); null = auto when aspect differs.</summary>
    public CropBox? ReferenceCrop { get; set; }

    public bool AutoCrop { get; set; } = true;
    public bool DeinterlaceReference { get; set; }

    /// <summary>Compare only every n-th frame (VMAF n_subsample) to go faster.</summary>
    public int Subsample { get; set; } = 1;

    public string VmafModel { get; set; } = "";
}

public sealed class MetricSeries
{
    public required string Name { get; init; }
    public List<double> Values { get; } = [];
    public double Mean => Values.Count > 0 ? Values.Where(v => !double.IsInfinity(v)).DefaultIfEmpty(100).Average() : double.NaN;
    public double Min => Values.Count > 0 ? Values.Min() : double.NaN;

    /// <summary>Mean of the worst 1 % of frames.</summary>
    public double Low1 => Values.Count > 0 ? Values.Order().Take(Math.Max(1, Values.Count / 100)).Average() : double.NaN;

    public double HarmonicMean => Values.Count > 0 ? Values.Count / Values.Sum(v => 1 / (v + 1)) - 1 : double.NaN;
}

public sealed class MetricResult
{
    public double Start { get; set; }
    public double FrameRate { get; set; }
    public int Frames { get; set; }
    public Dictionary<Metric, MetricSeries> Series { get; } = [];
    public string ReferenceGeometry { get; set; } = "";
    public string Notes { get; set; } = "";

    public double TimeOf(int frame) => Start + frame / (FrameRate > 0 ? FrameRate : 24);

    /// <summary>The worst frames by the primary metric (VMAF, else SSIM, else PSNR), spaced at least a second apart.</summary>
    public List<(int Frame, double Value)> Worst(int count)
    {
        var s = Series.TryGetValue(Metric.Vmaf, out var v) ? v : Series.TryGetValue(Metric.Ssim, out var ss) ? ss : Series.Values.FirstOrDefault();
        if (s == null)
            return [];
        var picked = new List<(int, double)>();
        var gap = (int)Math.Max(1, FrameRate);
        foreach (var (val, i) in s.Values.Select((x, i) => (x, i)).OrderBy(x => x.x))
        {
            if (picked.All(p => Math.Abs(p.Item1 - i) >= gap))
                picked.Add((i, val));
            if (picked.Count >= count)
                break;
        }
        return picked;
    }
}

public sealed partial class QualityMetrics(FfTools tools, string tempDir)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<MetricResult> RunAsync(MediaTitle reference, MediaTitle distorted, double distortedOffset, MetricOptions opt, IProgress<(double Fraction, string Text)>? progress = null, CancellationToken ct = default)
    {
        var rv = reference.Info?.Video ?? throw new InvalidOperationException("Reference has no video.");
        var dv = distorted.Info?.Video ?? throw new InvalidOperationException("Distorted source has no video.");
        var start = double.IsNaN(opt.Start) ? Math.Max(0, reference.Duration / 2 - opt.Length / 2) : opt.Start;
        var length = Math.Min(opt.Length, reference.Duration - start);
        if (length <= 0)
            throw new ArgumentException("The range is outside the reference.");
        var res = new MetricResult { Start = start, FrameRate = rv.FrameRate };
        var notes = new List<string>();

        // reference geometry: crop bars the encode does not have
        var crop = opt.ReferenceCrop;
        var rDar = Dar(rv);
        var dDar = Dar(dv);
        if (crop == null && opt.AutoCrop && Math.Abs(rDar - dDar) / rDar > 0.02)
        {
            progress?.Report((0, "Detecting the reference's black bars…"));
            crop = await DetectCropAsync(reference, start, length, ct).ConfigureAwait(false);
            if (crop != null)
                notes.Add($"reference cropped T{crop.Top} B{crop.Bottom} L{crop.Left} R{crop.Right} to match the encode's aspect");
        }
        var rw = rv.Width - (crop?.Left ?? 0) - (crop?.Right ?? 0);
        var rh = rv.Height - (crop?.Top ?? 0) - (crop?.Bottom ?? 0);
        res.ReferenceGeometry = $"{rw}×{rh}";
        if (dv.Width != rw || dv.Height != rh)
            notes.Add($"encode {dv.Width}×{dv.Height} scaled to {rw}×{rh} (bicubic)");
        var deep = rv.BitDepth > 8 || dv.BitDepth > 8;
        var fmt = deep ? "yuv420p10le" : "yuv420p";
        var fpsFix = Math.Abs(rv.FrameRate - dv.FrameRate) > 0.01 ? $"fps={rv.FrameRate.ToString("0.######", Inv)}," : "";
        if (fpsFix.Length > 0)
            notes.Add($"frame rates differ ({dv.FrameRateText} vs {rv.FrameRateText}): encode resampled — expect low scores");

        // anchor on the reference's real first frame, then open both half a frame early so their first frames pair up
        var r0 = await new Screenshotter(tools, tempDir).FrameTimeAtAsync(reference, start, ct).ConfigureAwait(false);
        if (double.IsNaN(r0))
            r0 = start;
        var halfR = 0.5 / Math.Max(1, rv.FrameRate);
        var halfD = 0.5 / Math.Max(1, dv.FrameRate);
        res.Start = r0;
        var refIn = InputBuilder.Input(reference, Math.Max(0, r0 - halfR), length, tempDir);
        var disIn = InputBuilder.Input(distorted, Math.Max(0, r0 + distortedOffset - halfD), length, tempDir);
        var refChain = refIn.TrimFilter + (opt.DeinterlaceReference ? "bwdif=mode=send_frame:deint=all," : "") +
                       (crop != null ? $"crop={rw}:{rh}:{crop.Left}:{crop.Top}," : "") +
                       $"settb=AVTB,setpts=N/FRAME_RATE/TB,trim=duration={InputBuilder.S(length)},format={fmt}";
        var disChain = disIn.TrimFilter + $"{fpsFix}settb=AVTB,setpts=N/FRAME_RATE/TB,trim=duration={InputBuilder.S(length)},scale={rw}:{rh}:flags=bicubic,format={fmt}";

        var metrics = opt.Metrics;
        if (!tools.HasVmaf && metrics.HasFlag(Metric.Vmaf))
        {
            metrics &= ~Metric.Vmaf;
            notes.Add("this ffmpeg has no libvmaf: VMAF skipped");
        }
        if (!tools.HasXpsnr && metrics.HasFlag(Metric.Xpsnr))
            metrics &= ~Metric.Xpsnr;
        var list = Enum.GetValues<Metric>().Where(m => m != Metric.None && metrics.HasFlag(m)).ToList();
        if (list.Count == 0)
            throw new ArgumentException("No metric selected.");

        var id = Guid.NewGuid().ToString("N")[..8];
        var sb = new System.Text.StringBuilder();
        sb.Append($"[0:v]{refChain},split={list.Count}");
        for (var i = 0; i < list.Count; i++)
            sb.Append($"[r{i}]");
        sb.Append($";[1:v]{disChain},split={list.Count}");
        for (var i = 0; i < list.Count; i++)
            sb.Append($"[d{i}]");
        var threads = Math.Max(2, Environment.ProcessorCount / 2);
        var sub = Math.Max(1, opt.Subsample);
        for (var i = 0; i < list.Count; i++)
        {
            var m = list[i];
            var sel = sub > 1 && m != Metric.Vmaf ? $"select='not(mod(n\\,{sub}))'," : "";
            var dIn = sel.Length > 0 ? $"[d{i}]{sel}null[ds{i}];[r{i}]{sel}null[rs{i}];[ds{i}][rs{i}]" : $"[d{i}][r{i}]";
            sb.Append(';').Append(dIn).Append(m switch
            {
                Metric.Psnr => $"psnr=stats_file=psnr-{id}.log:shortest=1",
                Metric.Ssim => $"ssim=stats_file=ssim-{id}.log:shortest=1",
                Metric.Xpsnr => $"xpsnr=stats_file=xpsnr-{id}.log:shortest=1",
                _ => $"libvmaf=log_fmt=json:log_path=vmaf-{id}.json:shortest=1:n_threads={threads}" + (sub > 1 ? $":n_subsample={sub}" : "") +
                     (opt.VmafModel.Length > 0 ? $":model={opt.VmafModel}" : rh > 1300 ? ":model=version=vmaf_4k_v0.6.1" : ""),
            });
            // the metric filters pass the main input through: each output goes to the null muxer
            sb.Append($"[o{i}]");
        }
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(refIn.Args);
        args.AddRange(disIn.Args);
        args.AddRange(["-filter_complex", sb.ToString()]);
        for (var i = 0; i < list.Count; i++)
            args.AddRange(["-map", $"[o{i}]", "-f", "null", "-"]);

        var total = Math.Max(1, length * rv.FrameRate);
        var resRun = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct, line =>
        {
            var m = FrameRegex().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out var fr))
                progress?.Report((Math.Min(1, fr / total), $"Frame {fr:0} / {total:0}"));
        }, workingDirectory: tempDir).ConfigureAwait(false);

        foreach (var m in list)
        {
            var series = new MetricSeries { Name = m.ToString().ToUpperInvariant() };
            var path = Path.Combine(tempDir, m switch
            {
                Metric.Psnr => $"psnr-{id}.log",
                Metric.Ssim => $"ssim-{id}.log",
                Metric.Xpsnr => $"xpsnr-{id}.log",
                _ => $"vmaf-{id}.json",
            });
            if (!File.Exists(path))
                continue;
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            series.Values.AddRange(m switch
            {
                Metric.Psnr => ParseKeyed(text, "psnr_y"),
                Metric.Ssim => ParseKeyed(text, "Y"),
                Metric.Xpsnr => ParseKeyed(text, "y"),
                _ => ParseVmaf(text),
            });
            TryDelete(path);
            if (series.Values.Count > 0)
                res.Series[m] = series;
        }
        if (res.Series.Count == 0)
            throw new IOException("ffmpeg produced no metric values: " + Screenshotter.LastError(resRun.StdErr));
        res.Frames = res.Series.Values.Max(s => s.Values.Count);
        if (sub > 1)
            res.FrameRate /= sub;
        res.Notes = string.Join("; ", notes);
        return res;
    }

    private async Task<CropBox?> DetectCropAsync(MediaTitle title, double start, double length, CancellationToken ct)
    {
        CropBox? best = null;
        foreach (var t in new[] { start + length * 0.2, start + length * 0.5, start + length * 0.8 })
        {
            var f = await LumaFrame.GrabAsync(tools, title, t, tempDir, false, ct).ConfigureAwait(false);
            if (f == null)
                continue;
            var c = FrameMetrics.DetectCrop(f);
            best = best == null ? c : new CropBox(Math.Min(best.Left, c.Left), Math.Min(best.Top, c.Top), Math.Min(best.Right, c.Right), Math.Min(best.Bottom, c.Bottom));
        }
        if (best == null || best.Left + best.Right + best.Top + best.Bottom == 0)
            return null;
        // even numbers for 4:2:0
        return new CropBox(best.Left & ~1, best.Top & ~1, best.Right & ~1, best.Bottom & ~1);
    }

    private static double Dar(StreamInfo v)
    {
        var sar = MediaProberSar(v.Sar);
        return v.Height > 0 ? v.Width * sar / v.Height : 1;
    }

    private static double MediaProberSar(string sar)
    {
        var p = sar.Split(':');
        return p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var a) && double.TryParse(p[1], NumberStyles.Float, Inv, out var b) && a > 0 && b > 0 ? a / b : 1;
    }

    internal static List<double> ParseKeyed(string text, string key)
    {
        var values = new List<double>();
        var rx = new Regex(@"(?:^|\s)" + Regex.Escape(key) + @":\s*([-\d.]+|inf)", RegexOptions.Multiline);
        foreach (var line in text.Split('\n'))
        {
            var m = rx.Match(line);
            if (m.Success)
                values.Add(m.Groups[1].Value == "inf" ? 100 : double.Parse(m.Groups[1].Value, Inv));
        }
        return values;
    }

    internal static List<double> ParseVmaf(string json)
    {
        var values = new List<double>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("frames", out var frames))
            return values;
        foreach (var f in frames.EnumerateArray())
        {
            if (f.TryGetProperty("metrics", out var m) && m.TryGetProperty("vmaf", out var v))
                values.Add(v.GetDouble());
        }
        return values;
    }

    private static void TryDelete(string p)
    {
        try
        {
            File.Delete(p);
        }
        catch (IOException)
        {
            // stays in temp
        }
    }

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FrameRegex();
}
