using System.Globalization;
using System.Text.RegularExpressions;
using Ces.Model;
using Ces.Tools;

namespace Ces.Analysis;

public sealed class VideoFrameResult
{
    public double Time { get; init; }
    public required LevelStats Levels { get; init; }
    public double Sharpness { get; init; }
    public required SpectrumStats Spectrum { get; init; }
    public required CropBox Crop { get; init; }
    public double PeakNits { get; set; } = double.NaN;
    public double AvgNits { get; set; } = double.NaN;
    public ArtefactStats? Artefacts { get; set; }
    public DynamicHdr? DynamicHdr { get; set; }
}

public sealed class InterlaceResult
{
    public long Tff { get; set; }
    public long Bff { get; set; }
    public long Progressive { get; set; }
    public long Undetermined { get; set; }
    public long RepeatedFields { get; set; }

    /// <summary>
    /// 3:2 pulldown: film at 29.97 shows about 2 combed frames in 5 (hard telecine) or repeated fields (soft telecine,
    /// flags in the stream).
    /// </summary>
    public bool IsTelecine
    {
        get
        {
            var frames = Tff + Bff + Progressive + Undetermined;
            var combed = Tff + Bff;
            return frames > 0 && (RepeatedFields > frames * 0.15 || (combed > frames * 0.2 && combed < frames * 0.6 && Progressive > frames * 0.3));
        }
    }

    public string Verdict
    {
        get
        {
            var total = Tff + Bff + Progressive;
            // repeated fields tell telecine even when every single frame is undetermined
            if (total == 0)
                return IsTelecine ? "telecined film (3:2 pulldown: repeated fields)" : "unknown";
            if (Progressive >= total * 0.8)
                return RepeatedFields > (Tff + Bff + Progressive) * 0.15 ? "progressive (telecine pattern)" : "progressive";
            if (IsTelecine)
                return "telecined film (3:2 pulldown, about 2 in 5 frames combed)";
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
    public double AvgNoise => Avg(f => f.Artefacts?.Noise);
    public double AvgBandRun => Avg(f => f.Artefacts?.BandRun);
    public double AvgHalo => Avg(f => f.Artefacts?.Halo);
    public double ChromaHfH => Avg(f => f.Artefacts?.ChromaHfH);
    public double ChromaHfV => Avg(f => f.Artefacts?.ChromaHfV);

    private double Avg(Func<VideoFrameResult, double?> sel)
    {
        var v = Frames.Select(sel).OfType<double>().Where(x => !double.IsNaN(x)).ToList();
        return v.Count > 0 ? v.Average() : double.NaN;
    }
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
        var points = title.IsStill ? [0] : InputBuilder.EvenPoints(title.Duration, opt.SamplePoints, 8, 10).Select(t => t + offset).Where(t => t >= 0 && t < title.Duration).ToList();
        // a few frames at once: decoding dominates and the machine has cores to spare
        var grabbed = new (LumaFrame Frame, VideoFrameResult Result)?[points.Count];
        var done = 0;
        var chromaPlanes = v.ChromaSubsampling is "4:4:4" or "4:2:2";
        var dynamicHdr = v.IsHdr && v.ColorTransfer == "smpte2084";
        var meta = new HdrMetadata(tools);
        using (var gate = new SemaphoreSlim(3))
        {
            await Task.WhenAll(points.Select(async (t, k) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var f = await LumaFrame.GrabAsync(tools, title, t, tempDir, opt.Deinterlace, ct).ConfigureAwait(false);
                    if (f == null)
                        return;
                    var crop = FrameMetrics.DetectCrop(f);
                    var fr = await Task.Run(() => new VideoFrameResult
                    {
                        Time = t,
                        Levels = FrameMetrics.Levels(f),
                        Sharpness = FrameMetrics.Sharpness(f, crop),
                        Spectrum = FrameMetrics.Spectrum(f, crop),
                        Crop = crop,
                        Artefacts = Analysis.Artefacts.Measure(f, crop),
                    }, ct).ConfigureAwait(false);
                    if (v.ColorTransfer == "smpte2084")
                        (fr.PeakNits, fr.AvgNits) = FrameMetrics.PqBrightness(f);
                    // chroma origin: only meaningful when the stream carries more chroma than 4:2:0 (first 3 frames)
                    if (chromaPlanes && k < 3 && await LumaFrame.GrabAsync(tools, title, t, tempDir, opt.Deinterlace, ct, "u").ConfigureAwait(false) is { } u)
                        (fr.Artefacts!.ChromaHfH, fr.Artefacts.ChromaHfV) = Analysis.Artefacts.ChromaDetail(f, u);
                    if (dynamicHdr)
                        fr.DynamicHdr = await meta.ReadAsync(title, t, ct).ConfigureAwait(false);
                    grabbed[k] = (f, fr);
                }
                finally
                {
                    gate.Release();
                    progress?.Report($"Frame {Interlocked.Increment(ref done)}/{points.Count}");
                }
            })).ConfigureAwait(false);
        }
        var frames = new List<LumaFrame>();
        foreach (var g in grabbed)
        {
            if (g is { } x)
            {
                frames.Add(x.Frame);
                rep.Frames.Add(x.Result);
            }
        }
        if (rep.Frames.Count == 0)
            throw new IOException("No frames could be decoded.");

        // crop: the smallest borders seen in reasonably bright frames (dark shots look like black bars)
        var lit = rep.Frames.Where(f => f.Levels.Mean > (40 << Math.Max(0, v.BitDepth - 8))).ToList();
        var cropFrom = lit.Count >= 2 ? lit : rep.Frames;
        rep.Crop = new CropBox(
            cropFrom.Min(f => f.Crop.Left), cropFrom.Min(f => f.Crop.Top),
            cropFrom.Min(f => f.Crop.Right), cropFrom.Min(f => f.Crop.Bottom));

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
        if (opt.Interlace && !title.IsStill)
        {
            progress?.Report("Interlace detection…");
            rep.Interlace = await DetectInterlaceAsync(title, title.Duration / 2 + offset, ct).ConfigureAwait(false);
        }
        BuildFindings(rep, v);
        return rep;
    }

    public async Task<InterlaceResult> DetectInterlaceAsync(MediaTitle title, double time, CancellationToken ct, int frames = 500)
    {
        var args = new List<string> { "-hide_banner", "-nostdin" };
        args.AddRange(InputBuilder.Input(title, Math.Max(0, time), 30, tempDir).Args);
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "idet", "-frames:v", frames.ToString(Inv), "-f", "null", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var r = new InterlaceResult();
        // the filter graph can be created twice (a probe run and the real one) and the two reports come in either
        // order: the one that counted frames is the real one
        static long Sum(Match x) => Enumerable.Range(1, x.Groups.Count - 1).Sum(g => long.TryParse(x.Groups[g].Value, NumberStyles.Integer, Inv, out var v) ? v : 0);
        var m = MultiRegex().Matches(res.StdErr).MaxBy(Sum);
        if (m != null)
        {
            r.Tff = long.Parse(m.Groups[1].Value, Inv);
            r.Bff = long.Parse(m.Groups[2].Value, Inv);
            r.Progressive = long.Parse(m.Groups[3].Value, Inv);
            r.Undetermined = long.Parse(m.Groups[4].Value, Inv);
        }
        var rep = RepeatRegex().Matches(res.StdErr).MaxBy(Sum);
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
        if (rep.Crop is { } c && (c.Top + c.Bottom + c.Left + c.Right) > 4 && rep.Frames.Count >= 3)
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
            var lvl = peak < 120 && rep.Frames.Count >= 6 ? 1 : 0;
            f.Add(new Finding
            {
                Area = "HDR",
                Level = lvl,
                Text = $"Measured luma peak ≈ {peak:0} nits, average ≈ {avg:0.0} nits over the samples" +
                       (lvl > 0 ? " — no sampled frame goes above SDR brightness: a very conservative grade or flat HDR (check bright shots)." : ".") +
                       (v.ContentLight.Length > 0 ? $" Metadata: {v.ContentLight}." : ""),
            });
        }
        else if (v.ColorTransfer == "arib-std-b67")
        {
            f.Add(new Finding { Area = "HDR", Level = 0, Text = "HLG stream." });
        }

        // artefacts (absolute; the comparison with the reference is in Compare.CrossFindings)
        if (!double.IsNaN(rep.AvgNoise))
            f.Add(new Finding { Area = "Grain", Level = 0, Text = $"Noise/grain level {rep.AvgNoise:0.00} (8-bit σ; compare with the reference: much lower = grain removed)." });
        var smooth = rep.Frames.Select(x => x.Artefacts?.SmoothShare ?? 0).DefaultIfEmpty(0).Average();
        if (!double.IsNaN(rep.AvgBandRun))
        {
            var banded = rep.AvgBandRun > BandRunLimit(v.BitDepth) && smooth > 0.03;
            f.Add(new Finding { Area = "Banding", Level = banded ? 1 : 0, Text = $"Smooth areas step in runs of {rep.AvgBandRun:0.0} px ({smooth:P0} of the picture is smooth)" + (banded ? " — visible banding likely in gradients (sky, walls, fades)." : ".") });
        }
        if (!double.IsNaN(rep.AvgHalo))
            f.Add(new Finding { Area = "Halos", Level = 0, Text = $"Edge overshoot {rep.AvgHalo:P0} of the edge height (content-dependent: compare with the reference)." });
        if (!double.IsNaN(rep.ChromaHfH))
            f.Add(ChromaFinding(v.ChromaSubsampling, rep.ChromaHfH, rep.ChromaHfV));
        DynamicHdrFindings(rep, v, f);

        // interlace
        if (rep.Interlace is { } il)
        {
            var declared = v.FieldOrder is "tt" or "bb" or "tb" or "bt" ? "interlaced" : v.FieldOrder == "progressive" ? "progressive" : "";
            var lvl = declared == "progressive" && il.Verdict.StartsWith("interlaced", StringComparison.Ordinal) ? 2 : 0;
            f.Add(new Finding { Area = "Scan", Level = lvl, Text = $"Content: {il.Verdict} (TFF {il.Tff}, BFF {il.Bff}, progressive {il.Progressive}); stream flagged {(declared.Length > 0 ? declared : "unknown")}." });
        }
    }

    /// <summary>Run length above which smooth areas look banded (8-bit content naturally runs longer than 10-bit).</summary>
    internal static double BandRunLimit(int bitDepth) => bitDepth > 8 ? 6 : 12;

    /// <summary>Chroma high-frequency level (dB vs luma) below which a chroma plane is considered upsampled.</summary>
    internal const double ChromaLimit = -8;

    /// <summary>
    /// Between this and <see cref="ChromaLimit"/>: probably upsampled. A lossy re-encode fills the missing chroma
    /// detail with its own noise (clean upsampled 4:4:4 measures about −15 dB, the same at CRF 35 about −5 dB, genuine
    /// 4:4:4 about −1 dB); soft camera chroma can land here too, so it is a hint, not a verdict.
    /// </summary>
    internal const double ChromaHint = -4;

    internal static Finding ChromaFinding(string subsampling, double h, double v)
    {
        // the hint tier allows some slack on the second direction: a re-encode lifts both a little unevenly
        string? Origin(double limit, double slack = 0) => subsampling switch
        {
            "4:4:4" when (h < limit && v < limit + slack) || (v < limit && h < limit + slack) => "4:2:0",
            "4:4:4" when h < limit => "4:2:2",
            "4:2:2" when v < limit => "4:2:0",
            _ => null,
        };
        var measured = string.Create(CultureInfo.InvariantCulture, $"{h:0} / {v:0} dB vs luma");
        if (Origin(ChromaLimit) is { } sure)
            return new Finding { Area = "Chroma", Level = 2, Text = $"{subsampling} stream, but the colour planes have no fine detail ({measured}): upsampled from {sure}." };
        if (Origin(ChromaHint, 1.5) is { } likely)
            return new Finding { Area = "Chroma", Level = 1, Text = $"{subsampling} stream with little fine colour detail ({measured}): probably upsampled from {likely} and re-encoded (a lossy encode adds its own noise to the colour planes; soft camera chroma looks similar)." };
        return new Finding { Area = "Chroma", Level = 0, Text = $"No sign of upsampled chroma ({measured}; a heavy lossy re-encode can hide it)." };
    }

    private static void DynamicHdrFindings(VideoReport rep, StreamInfo v, List<Finding> f)
    {
        if (v.HdrFormat.Contains("profile 5", StringComparison.OrdinalIgnoreCase))
            f.Add(new Finding { Area = "Dolby Vision", Level = 1, Text = "Profile 5 (IPT-PQ base layer): without Dolby Vision processing colours look wrong — screenshots and metrics of this source are not representative." });
        var dyn = rep.Frames.Where(x => x.DynamicHdr is { } d && !double.IsNaN(d.MaxNits)).ToList();
        var declared = v.HdrFormat.Contains("HDR10+", StringComparison.Ordinal) || v.HdrFormat.Contains("2094", StringComparison.Ordinal) || v.HdrFormat.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase);
        if (dyn.Count == 0)
        {
            if (declared)
                f.Add(new Finding { Area = "Dynamic HDR", Level = 1, Text = $"Stream is flagged {v.HdrFormat}, but no per-frame dynamic metadata was read at the sampled frames." });
        }
        else
        {
            var kind = dyn[0].DynamicHdr!.Kind;
            var maxes = dyn.Select(x => x.DynamicHdr!.MaxNits).ToList();
            if (dyn.Count >= 3 && maxes.Max() - maxes.Min() < 1)
                f.Add(new Finding { Area = kind, Level = 1, Text = $"Dynamic metadata is the same in every sampled shot (max {maxes[0]:0} nits): not analysed per shot — generated or converted metadata." });
            // luma peak is below maxRGB, so only a picture clearly brighter than its metadata is a contradiction
            var brighter = dyn.Count(x => !double.IsNaN(x.PeakNits) && x.PeakNits > x.DynamicHdr!.MaxNits * 1.25 + 20);
            var dimmer = dyn.Count(x => !double.IsNaN(x.PeakNits) && x.DynamicHdr!.MaxNits > x.PeakNits * 4 + 100);
            if (brighter * 2 >= dyn.Count && brighter > 0)
                f.Add(new Finding { Area = kind, Level = 2, Text = $"In {brighter}/{dyn.Count} sampled shots the picture is brighter than the metadata says: metadata from another master or a bad conversion (displays will clip highlights)." });
            else if (dimmer * 2 >= dyn.Count && dimmer > 0)
                f.Add(new Finding { Area = kind, Level = 1, Text = $"In {dimmer}/{dyn.Count} sampled shots the metadata claims far brighter highlights than the picture has (over-dimmed tone mapping on displays)." });
            else
                f.Add(new Finding { Area = kind, Level = 0, Text = $"Dynamic metadata follows the picture: max {maxes.Min():0}–{maxes.Max():0} nits across {dyn.Count} sampled shots." });
        }
        // static HDR10: MaxCLL should not be below what the picture really reaches
        var m = MaxCllRegex().Match(v.ContentLight);
        var peak = rep.Frames.Select(x => x.PeakNits).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max();
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, Inv, out var cll) && cll > 0 && peak > cll * 1.3)
            f.Add(new Finding { Area = "HDR10", Level = 1, Text = $"MaxCLL says {cll} nits, but sampled frames reach ≈{peak:0} nits (wrong static metadata)." });
    }

    private static double ParseSar(string sar)
    {
        var p = sar.Split(':');
        return p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var a) && double.TryParse(p[1], NumberStyles.Float, Inv, out var b) && a > 0 && b > 0 ? a / b : 1;
    }

    [GeneratedRegex(@"MaxCLL (\d+)")]
    private static partial Regex MaxCllRegex();

    [GeneratedRegex(@"Multi frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)\s*Undetermined:\s*(\d+)")]
    private static partial Regex MultiRegex();

    [GeneratedRegex(@"Repeated Fields:\s*Neither:\s*\d+\s*Top:\s*(\d+)\s*Bottom:\s*(\d+)")]
    private static partial Regex RepeatRegex();
}
