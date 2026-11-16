using System.Globalization;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Dsp;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

/// <summary>A small RGB frame (sRGB/BT.709 after tone mapping), width-normalised for comparison.</summary>
public sealed class RgbFrame
{
    public required byte[] Data { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Time { get; init; }
}

public sealed class GradeFrame
{
    public double Time { get; init; }

    /// <summary>Other's picture relative to the reference: scale (&gt;1 = zoomed in, shows less) and shift in % of the width.</summary>
    public double Scale { get; set; } = 1;
    public double ShiftX { get; set; }
    public double ShiftY { get; set; }
    public double Match { get; set; }

    /// <summary>L* percentiles (2, 10, 25, 50, 75, 90, 98) of reference and other in the overlapping picture.</summary>
    public double[] RefL { get; set; } = [];
    public double[] OtherL { get; set; } = [];
    public double ContrastRatio { get; set; } = double.NaN;
    public double SaturationRatio { get; set; } = double.NaN;
    public double DeltaA { get; set; } = double.NaN;
    public double DeltaB { get; set; } = double.NaN;
}

public sealed class GradeReport
{
    public required CompareItem Item { get; init; }
    public List<GradeFrame> Frames { get; } = [];
    public List<Finding> Findings { get; } = [];

    public static readonly double[] Percentiles = [2, 10, 25, 50, 75, 90, 98];

    public double Median(Func<GradeFrame, double> f)
    {
        var v = Frames.Where(x => x.Match > 0.1).Select(f).Where(x => !double.IsNaN(x)).Order().ToList();
        return v.Count == 0 ? double.NaN : v[v.Count / 2];
    }

    /// <summary>Median tone curve: other's L* at each reference percentile.</summary>
    public (double[] Ref, double[] Other) ToneCurve()
    {
        var good = Frames.Where(x => x.Match > 0.1 && x.RefL.Length == Percentiles.Length).ToList();
        if (good.Count == 0)
            return ([], []);
        var r = new double[Percentiles.Length];
        var o = new double[Percentiles.Length];
        for (var k = 0; k < r.Length; k++)
        {
            r[k] = good.Select(x => x.RefL[k]).Order().ElementAt(good.Count / 2);
            o[k] = good.Select(x => x.OtherL[k]).Order().ElementAt(good.Count / 2);
        }
        return (r, o);
    }
}

/// <summary>
/// What a remaster (or another release) changed in the picture: framing (zoom/crop, shift, pan &amp; scan) found by
/// aligning matched frames, and the grade (tone curve, contrast, saturation, colour cast) compared in CIELAB.
/// </summary>
public sealed class GradeCompare(FfTools tools, string tempDir)
{
    private const int W = 480;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<RgbFrame?> GrabAsync(MediaTitle title, double time, CancellationToken ct)
    {
        var v = title.Info?.Video;
        if (v == null)
            return null;
        var input = InputBuilder.Input(title, time, 2, tempDir);
        var chain = new List<string>();
        var sw = InputBuilder.SoftwareFormat(title);
        if (sw.Length > 0)
            chain.Add(sw.TrimEnd(','));
        if (input.Skip > 0)
            chain.Add(input.TrimFilter.TrimEnd(','));
        if (v.IsHdr && v.ColorTransfer is "smpte2084" or "arib-std-b67" && tools.HasZscale)
            chain.Add($"zscale=tin={(v.ColorTransfer == "arib-std-b67" ? "arib-std-b67" : "smpte2084")}:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        else if (v.Height is > 0 and <= 576)
            chain.Add("scale=in_color_matrix=bt601:out_color_matrix=bt709");
        chain.Add("scale=w='trunc(iw*max(1,sar)/2)*2':h='trunc(ih*max(1,1/sar)/2)*2',setsar=1");
        chain.Add($"scale={W}:-2:flags=area,format=rgb24");
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        args.AddRange(input.Args);
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", string.Join(",", chain), "-frames:v", "1", "-f", "rawvideo", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var h = res.StdOut.Length / (W * 3);
        return h < 16 ? null : new RgbFrame { Data = res.StdOut[..(W * h * 3)], Width = W, Height = h, Time = time };
    }

    /// <summary>Compares the other source with the reference at the given timeline times (matched to the reference's frames).</summary>
    public async Task<GradeReport> CompareAsync(CompareItem reference, CompareItem item, IReadOnlyList<double> times, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rep = new GradeReport { Item = item };
        var shooter = new Screenshotter(tools, tempDir);
        var k = 0;
        foreach (var t in times)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{item.Name}: frame {++k}/{times.Count}");
            if (reference.SourceTime(t) is not { } rt)
                continue;
            // the reference's real frame decides; the other is aimed at its matching frame
            var rf = await shooter.FrameTimeAtAsync(reference.Title, rt, ct).ConfigureAwait(false);
            var target = FrameMatch.TargetTime(reference, double.IsNaN(rf) ? null : rf, t, item);
            if (target is not { } tt)
                continue;
            var a = await GrabAsync(reference.Title, double.IsNaN(rf) ? rt : rf, ct).ConfigureAwait(false);
            var b = await GrabAsync(item.Title, tt, ct).ConfigureAwait(false);
            if (a == null || b == null)
                continue;
            rep.Frames.Add(await Task.Run(() => CompareFrames(a, b, t), ct).ConfigureAwait(false));
        }
        BuildFindings(rep);
        return rep;
    }

    /// <summary>Framing by a scale search with phase correlation, then CIELAB statistics over the overlapping picture.</summary>
    public static GradeFrame CompareFrames(RgbFrame a, RgbFrame b, double time)
    {
        var ga = Gray(a);
        var gb = Gray(b);
        var best = (Scale: 1.0, Dx: 0.0, Dy: 0.0, Peak: double.MinValue);
        // coarse, then fine around the best scale
        foreach (var s in Range(0.80, 1.25, 0.02))
            Try(s);
        var c = best.Scale;
        foreach (var s in Range(c - 0.02, c + 0.02, 0.004))
            Try(s);
        void Try(double s)
        {
            var warped = Warp(gb, b.Width, b.Height, a.Width, a.Height, s, 0, 0);
            var (dx, dy, peak) = PhaseCorrelate(ga, warped, a.Width, a.Height);
            if (peak > best.Peak)
                best = (s, dx, dy, peak);
        }
        var g = new GradeFrame
        {
            Time = time,
            Scale = best.Scale,
            ShiftX = best.Dx / a.Width * 100,
            ShiftY = best.Dy / a.Width * 100,
            Match = best.Peak,
        };
        Colour(a, b, best.Scale, best.Dx, best.Dy, g);
        return g;
    }

    private static IEnumerable<double> Range(double from, double to, double step)
    {
        for (var s = from; s <= to + 1e-9; s += step)
            yield return s;
    }

    private static float[] Gray(RgbFrame f)
    {
        var g = new float[f.Width * f.Height];
        for (var i = 0; i < g.Length; i++)
            g[i] = 0.2126f * f.Data[3 * i] + 0.7152f * f.Data[3 * i + 1] + 0.0722f * f.Data[3 * i + 2];
        return g;
    }

    /// <summary>
    /// Other's image on the reference's canvas: reference pixel (x, y) shows other pixel at
    /// ((x − cxA − dx)/s + cxB, (y − cyA − dy)/s + cyB). Outside = NaN.
    /// </summary>
    private static float[] Warp(float[] src, int sw, int sh, int dw, int dh, double s, double dx, double dy)
    {
        var dst = new float[dw * dh];
        double cxA = dw / 2.0, cyA = dh / 2.0, cxB = sw / 2.0, cyB = sh / 2.0;
        for (var y = 0; y < dh; y++)
        {
            var sy = (y + 0.5 - cyA - dy) / s + cyB - 0.5;
            for (var x = 0; x < dw; x++)
            {
                var sx = (x + 0.5 - cxA - dx) / s + cxB - 0.5;
                dst[y * dw + x] = Sample(src, sw, sh, sx, sy);
            }
        }
        return dst;
    }

    private static float Sample(float[] img, int w, int h, double x, double y)
    {
        if (x < 0 || y < 0 || x > w - 1 || y > h - 1)
            return float.NaN;
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var fx = (float)(x - x0);
        var fy = (float)(y - y0);
        var top = img[y0 * w + x0] * (1 - fx) + img[y0 * w + x1] * fx;
        var bot = img[y1 * w + x0] * (1 - fx) + img[y1 * w + x1] * fx;
        return top * (1 - fy) + bot * fy;
    }

    /// <summary>Phase correlation (windowed, edges filled with the mean): shift of b relative to a and the peak height.</summary>
    private static (double Dx, double Dy, double Peak) PhaseCorrelate(float[] a, float[] b, int w, int h)
    {
        var n = Fft.NextPow2(Math.Max(w, h));
        var ar = new double[n * n];
        var ai = new double[n * n];
        var br = new double[n * n];
        var bi = new double[n * n];
        double ma = 0, mb = 0;
        var cnt = 0;
        for (var i = 0; i < w * h; i++)
        {
            if (float.IsNaN(b[i]))
                continue;
            ma += a[i];
            mb += b[i];
            cnt++;
        }
        if (cnt < w * h / 4)
            return (0, 0, 0);
        ma /= cnt;
        mb /= cnt;
        for (var y = 0; y < h; y++)
        {
            var wy = 0.5 - 0.5 * Math.Cos(2 * Math.PI * y / (h - 1));
            for (var x = 0; x < w; x++)
            {
                var wx = 0.5 - 0.5 * Math.Cos(2 * Math.PI * x / (w - 1));
                var i = y * w + x;
                var valid = !float.IsNaN(b[i]);
                ar[y * n + x] = valid ? (a[i] - ma) * wx * wy : 0;
                br[y * n + x] = valid ? (b[i] - mb) * wx * wy : 0;
            }
        }
        Fft2(ar, ai, n, false);
        Fft2(br, bi, n, false);
        for (var i = 0; i < ar.Length; i++)
        {
            // cross-power spectrum, normalised
            var re = br[i] * ar[i] + bi[i] * ai[i];
            var im = bi[i] * ar[i] - br[i] * ai[i];
            var mag = Math.Sqrt(re * re + im * im) + 1e-9;
            ar[i] = re / mag;
            ai[i] = im / mag;
        }
        Fft2(ar, ai, n, true);
        var bestI = 0;
        for (var i = 1; i < ar.Length; i++)
        {
            if (ar[i] > ar[bestI])
                bestI = i;
        }
        var py = bestI / n;
        var px = bestI % n;
        // the warp moved b by its own shift: a positive peak means b's content sits further right/down
        double dx = px > n / 2 ? px - n : px;
        double dy = py > n / 2 ? py - n : py;
        return (-dx, -dy, ar[bestI]);
    }

    private static void Fft2(double[] re, double[] im, int n, bool inverse)
    {
        var rr = new double[n];
        var ri = new double[n];
        for (var y = 0; y < n; y++)
        {
            Array.Copy(re, y * n, rr, 0, n);
            Array.Copy(im, y * n, ri, 0, n);
            Fft.Transform(rr, ri, inverse);
            Array.Copy(rr, 0, re, y * n, n);
            Array.Copy(ri, 0, im, y * n, n);
        }
        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++)
            {
                rr[y] = re[y * n + x];
                ri[y] = im[y * n + x];
            }
            Fft.Transform(rr, ri, inverse);
            for (var y = 0; y < n; y++)
            {
                re[y * n + x] = rr[y];
                im[y * n + x] = ri[y];
            }
        }
    }

    /// <summary>CIELAB statistics of the overlapping picture (aligned with the found scale and shift).</summary>
    private static void Colour(RgbFrame a, RgbFrame b, double s, double dx, double dy, GradeFrame g)
    {
        var la = new List<double>();
        var lb = new List<double>();
        double ca = 0, cb = 0, aa = 0, ab = 0, ba = 0, bb = 0;
        var n = 0;
        double cxA = a.Width / 2.0, cyA = a.Height / 2.0, cxB = b.Width / 2.0, cyB = b.Height / 2.0;
        for (var y = (int)(a.Height * 0.08); y < a.Height * 0.92; y += 2)
        {
            for (var x = (int)(a.Width * 0.08); x < a.Width * 0.92; x += 2)
            {
                var sx = (int)Math.Round((x + 0.5 - cxA - dx) / s + cxB - 0.5);
                var sy = (int)Math.Round((y + 0.5 - cyA - dy) / s + cyB - 0.5);
                if (sx < 0 || sy < 0 || sx >= b.Width || sy >= b.Height)
                    continue;
                var (l1, a1, b1) = Lab(a.Data, (y * a.Width + x) * 3);
                var (l2, a2, b2) = Lab(b.Data, (sy * b.Width + sx) * 3);
                la.Add(l1);
                lb.Add(l2);
                ca += Math.Sqrt(a1 * a1 + b1 * b1);
                cb += Math.Sqrt(a2 * a2 + b2 * b2);
                aa += a1;
                ab += a2;
                ba += b1;
                bb += b2;
                n++;
            }
        }
        if (n < 500)
            return;
        la.Sort();
        lb.Sort();
        g.RefL = GradeReport.Percentiles.Select(p => la[(int)(p / 100 * (la.Count - 1))]).ToArray();
        g.OtherL = GradeReport.Percentiles.Select(p => lb[(int)(p / 100 * (lb.Count - 1))]).ToArray();
        static double Std(List<double> v)
        {
            var m = v.Average();
            return Math.Sqrt(v.Average(x => (x - m) * (x - m)));
        }
        var sa = Std(la);
        g.ContrastRatio = sa > 1 ? Std(lb) / sa : double.NaN;
        g.SaturationRatio = ca > 1 ? cb / ca : double.NaN;
        g.DeltaA = (ab - aa) / n;
        g.DeltaB = (bb - ba) / n;
    }

    /// <summary>sRGB (BT.709 primaries, D65) byte triple to CIELAB.</summary>
    internal static (double L, double A, double B) Lab(byte[] d, int i)
    {
        static double Lin(byte v)
        {
            var c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double r = Lin(d[i]), gg = Lin(d[i + 1]), bl = Lin(d[i + 2]);
        var x = (0.4124 * r + 0.3576 * gg + 0.1805 * bl) / 0.95047;
        var y = 0.2126 * r + 0.7152 * gg + 0.0722 * bl;
        var z = (0.0193 * r + 0.1192 * gg + 0.9505 * bl) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static void BuildFindings(GradeReport rep)
    {
        var f = rep.Findings;
        var good = rep.Frames.Where(x => x.Match > 0.1).ToList();
        if (good.Count == 0)
        {
            f.Add(new Finding { Area = "Framing", Level = 1, Text = "The pictures could not be aligned (different content at these times? check the sync)." });
            return;
        }
        var s = rep.Median(x => x.Scale);
        var sx = rep.Median(x => x.ShiftX);
        var sy = rep.Median(x => x.ShiftY);
        var spread = good.Count >= 3 ? good.Select(x => x.ShiftX).Max() - good.Select(x => x.ShiftX).Min() : 0;
        // s is the scale that maps the other picture onto the reference: the other is zoomed by 1/s
        var zoom = 1 / s;
        if (Math.Abs(zoom - 1) >= 0.015)
            f.Add(new Finding
            {
                Area = "Framing",
                Level = 1,
                Text = zoom > 1
                    ? $"Zoomed in {(zoom - 1) * 100:0.0} %: shows only {s * s * 100:0} % of the reference's picture area (edges cropped)."
                    : $"Zoomed out {(1 - zoom) * 100:0.0} %: shows {s * s * 100 - 100:0} % more picture than the reference (open matte or less cropping).",
            });
        if (Math.Abs(sx) >= 0.8 || Math.Abs(sy) >= 0.8)
        {
            var parts = new List<string>();
            if (Math.Abs(sx) >= 0.8)
                parts.Add($"{(sx > 0 ? "right" : "left")} by {Math.Abs(sx):0.0} %");
            if (Math.Abs(sy) >= 0.8)
                parts.Add($"{(sy > 0 ? "down" : "up")} by {Math.Abs(sy):0.0} %");
            f.Add(new Finding { Area = "Framing", Level = 1, Text = $"Framing window moved {string.Join(" and ", parts)} of the width (reframed)." });
        }
        if (spread > 3)
            f.Add(new Finding { Area = "Framing", Level = 1, Text = $"Horizontal position changes by up to {spread:0.0} % between shots: pan & scan or per-shot reframing." });
        if (f.All(x => x.Area != "Framing"))
            f.Add(new Finding { Area = "Framing", Level = 0, Text = $"Same framing (zoom {zoom:0.000}, shift {sx:+0.0;-0.0;0.0}/{sy:+0.0;-0.0;0.0} %)." });

        var (refL, othL) = rep.ToneCurve();
        if (refL.Length > 0)
        {
            var shadows = othL[1] - refL[1];
            var mids = othL[3] - refL[3];
            var highs = othL[5] - refL[5];
            var tone = new List<string>();
            void Part(string name, double d)
            {
                if (Math.Abs(d) >= 2.5)
                    tone.Add($"{name} {(d > 0 ? "brighter" : "darker")} ({d:+0.0;-0.0;0.0} L*)");
            }
            Part("shadows", shadows);
            Part("midtones", mids);
            Part("highlights", highs);
            f.Add(new Finding { Area = "Tone", Level = tone.Count > 0 ? 1 : 0, Text = tone.Count > 0 ? "Different tone curve: " + string.Join(", ", tone) + "." : "Same brightness and tone curve (within 2.5 L*)." });
        }
        var contrast = rep.Median(x => x.ContrastRatio);
        if (!double.IsNaN(contrast) && Math.Abs(contrast - 1) >= 0.08)
            f.Add(new Finding { Area = "Contrast", Level = 1, Text = $"{(contrast > 1 ? "More" : "Less")} contrast ({(contrast - 1) * 100:+0;-0;0} %)." });
        var sat = rep.Median(x => x.SaturationRatio);
        if (!double.IsNaN(sat))
            f.Add(new Finding { Area = "Saturation", Level = Math.Abs(sat - 1) >= 0.1 ? 1 : 0, Text = Math.Abs(sat - 1) >= 0.1 ? $"{(sat > 1 ? "More" : "Less")} saturated ({(sat - 1) * 100:+0;-0;0} % chroma)." : "Same saturation." });
        var da = rep.Median(x => x.DeltaA);
        var db = rep.Median(x => x.DeltaB);
        if (!double.IsNaN(da))
        {
            var cast = new List<string>();
            if (Math.Abs(db) >= 2)
                cast.Add(db > 0 ? $"warmer/yellower (b* {db:+0.0;-0.0;0.0})" : $"cooler/bluer (b* {db:+0.0;-0.0;0.0})");
            if (Math.Abs(da) >= 2)
                cast.Add(da > 0 ? $"more magenta (a* {da:+0.0;-0.0;0.0})" : $"greener (a* {da:+0.0;-0.0;0.0})");
            f.Add(new Finding { Area = "Colour", Level = cast.Count > 0 ? 1 : 0, Text = cast.Count > 0 ? "Colour cast: " + string.Join(", ", cast) + "." : "Same colour balance." });
        }
        if (f.Count(x => x.Level > 0 && x.Area is "Tone" or "Contrast" or "Saturation" or "Colour") >= 2)
            f.Insert(0, new Finding
            {
                Area = "Grade",
                Level = 2,
                Text = good.Count > 1
                    ? "Re-graded: the colour/tone differences are consistent across shots — a different master or colour grade, not encoding loss."
                    : "Re-graded: colour and tone differ beyond encoding loss — a different master or colour grade.",
            });
    }
}
