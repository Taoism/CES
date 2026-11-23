using System.Globalization;
using System.Net;
using System.Text;
using Ces.Analysis;
using Ces.Compare;
using Ces.Model;

namespace Ces.Report;

/// <summary>One line of a report chart: which source (colour), its points, dashed for a second quantity.</summary>
public sealed record ReportSeries(string Name, int Source, double[] X, double[] Y, bool Dashed = false, bool Guide = false);

/// <summary>A report chart: title, lines and how the axes read.</summary>
public sealed class ReportChart
{
    public required string Title { get; init; }
    public List<ReportSeries> Series { get; } = [];
    public Func<double, string> XFormat { get; init; } = v => v.ToString("0.##", CultureInfo.InvariantCulture);
    public Func<double, string> YFormat { get; init; } = v => v.ToString("0.##", CultureInfo.InvariantCulture);
    public double? YMin { get; init; }
    public double? YMax { get; init; }
    public double? XMin { get; init; }
    public double? XMax { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// The charts of the Video analysis, Audio analysis, Quality metrics and Remaster tabs for the HTML report, drawn as
/// inline SVG in the sources' colours: every source on its own line in one chart.
/// </summary>
public static class ReportCharts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Source colours, the same as the GUI and the report's number chips.</summary>
    public static readonly string[] Colors = ["#4a8ce3", "#e0a040", "#6cc070", "#d070c0", "#50c8c8", "#e06060"];

    private const int MaxPoints = 1500;

    // ---------- data ----------

    public static List<ReportChart> Video(IReadOnlyList<CompareItem> items)
    {
        var charts = new List<ReportChart>();
        var native = new ReportChart
        {
            Title = "Native resolution: descale error per height (a notch = upscaled from there)",
            XFormat = v => v.ToString("0", Inv) + "p",
            YFormat = v => v.ToString("0.00", Inv),
        };
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Video?.NativeRes is { Curve.Count: > 1 } nr)
                native.Series.Add(new ReportSeries(Label(items, i), i, nr.Curve.Select(p => (double)p.Height).ToArray(), nr.Curve.Select(p => p.Error * 1000).ToArray()));
        }
        if (native.Series.Count > 0)
            charts.Add(native);
        foreach (var (title, pick) in new (string, Func<VideoFrameResult, double[]>)[]
                 {
                     ("Horizontal detail spectrum", f => f.Spectrum.Horizontal),
                     ("Vertical detail spectrum", f => f.Spectrum.Vertical),
                 })
        {
            var c = new ReportChart
            {
                Title = title,
                XFormat = v => (v * 100).ToString("0", Inv) + " % Nyq",
                YFormat = v => v.ToString("0", Inv) + " dB",
            };
            for (var i = 0; i < items.Count; i++)
            {
                if (DetailSpectrum(items[i].Video, pick) is { } s)
                    c.Series.Add(new ReportSeries(Label(items, i), i, s.X, s.Y));
            }
            if (c.Series.Count > 0)
                charts.Add(c);
        }
        return charts;
    }

    /// <summary>Power-averaged detail spectrum over the sampled frames, without the DC / lowest bins.</summary>
    public static (double[] X, double[] Y)? DetailSpectrum(VideoReport? rep, Func<VideoFrameResult, double[]> pick)
    {
        if (rep == null || rep.Frames.Count == 0)
            return null;
        var arrays = rep.Frames.Select(pick).Where(a => a.Length > 0).ToList();
        if (arrays.Count == 0)
            return null;
        var n = arrays.Min(a => a.Length);
        if (n <= 3)
            return null;
        var y = new double[n];
        for (var k = 0; k < n; k++)
            y[k] = 10 * Math.Log10(arrays.Average(a => Math.Pow(10, a[k] / 10)));
        var x = Enumerable.Range(0, n).Select(k => (k + 0.5) / n).ToArray();
        return (x.Skip(2).ToArray(), y.Skip(2).ToArray());
    }

    public static ReportChart? AudioSpectrum(IReadOnlyList<CompareItem> items)
    {
        var c = new ReportChart
        {
            Title = "Average spectrum (first analysed track per source)",
            XFormat = v => v >= 1000 ? (v / 1000).ToString("0.#", Inv) + " kHz" : v.ToString("0", Inv) + " Hz",
            YFormat = v => v.ToString("0", Inv) + " dB",
            YMin = -140,
        };
        for (var i = 0; i < items.Count; i++)
        {
            var rep = items[i].Audio.FirstOrDefault(a => a.Spectrum.Length > 1);
            if (rep == null)
                continue;
            var n = rep.Spectrum.Length;
            var hz = Enumerable.Range(0, n).Select(k => k * (rep.SpectrumRate / 2.0) / (n - 1)).ToArray();
            c.Series.Add(new ReportSeries($"{Label(items, i)} · track {rep.TrackIndex + 1}", i, hz, rep.Spectrum));
        }
        return c.Series.Count > 0 ? c : null;
    }

    /// <summary>One chart per metric, each compared source on its own line over the timeline.</summary>
    public static List<ReportChart> Quality(IReadOnlyList<CompareItem> items,
        IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics)
    {
        var charts = new List<ReportChart>();
        foreach (var m in new[] { Metric.Vmaf, Metric.Psnr, Metric.Xpsnr, Metric.Ssim })
        {
            var lines = metrics.Where(x => x.Result.Series.TryGetValue(m, out var s) && s.Values.Count > 1).ToList();
            if (lines.Count == 0)
                continue;
            var name = lines[0].Result.Series[m].Name;
            var refs = lines.Select(l => l.Reference).Distinct().ToList();
            var c = new ReportChart
            {
                Title = $"{name} per frame" + (refs.Count == 1 ? $" against {Math.Max(0, IndexOf(items, refs[0])) + 1} · {refs[0].Name}" : ""),
                XFormat = v => Format.Time(v, false),
                YFormat = v => v.ToString(m == Metric.Ssim ? "0.000" : "0", Inv),
                YMax = m switch { Metric.Vmaf => 100, Metric.Ssim => 1, _ => null },
            };
            foreach (var (_, ds, res) in lines)
            {
                var s = res.Series[m];
                var i = Math.Max(0, IndexOf(items, ds));
                var x = Enumerable.Range(0, s.Values.Count).Select(k => res.TimeOf(k)).ToArray();
                c.Series.Add(new ReportSeries($"{Label(items, i)} (mean {s.Mean.ToString(m == Metric.Ssim ? "0.0000" : "0.00", Inv)})", i, x, [.. s.Values]));
            }
            charts.Add(c);
        }
        return charts;
    }

    public static List<ReportChart> Remaster(IReadOnlyList<CompareItem> items)
    {
        var charts = new List<ReportChart>();
        var tone = new ReportChart
        {
            Title = items.Count > 0 ? $"Tone curve: L* of each source at the reference's ({items[0].Name}) brightness percentiles" : "Tone curve",
            XFormat = v => v.ToString("0", Inv),
            YFormat = v => v.ToString("0", Inv),
            XMin = 0,
            XMax = 100,
            YMin = 0,
            YMax = 100,
            Note = "The grey diagonal is the same tone curve as the reference; above it is brighter, below it darker.",
        };
        var time = new ReportChart
        {
            Title = "Grade over time: midtone ΔL* (solid) and warmth Δb* (dashed) against the reference",
            XFormat = v => Format.Time(v, false),
            YFormat = v => v.ToString("+0.#;-0.#;0", Inv),
        };
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].Grade is not { } g)
                continue;
            var (r, o) = g.ToneCurve();
            if (r.Length > 1)
            {
                if (tone.Series.Count == 0)
                    tone.Series.Add(new ReportSeries("same as the reference", 0, [0, 100], [0, 100], Dashed: true, Guide: true));
                tone.Series.Add(new ReportSeries(Label(items, i), i, r, o));
            }
            var t = g.OverTime();
            if (t.Count > 1)
            {
                var x = t.Select(p => p.Time).ToArray();
                time.Series.Add(new ReportSeries($"{Label(items, i)} L*", i, x, t.Select(p => p.MidL).ToArray()));
                time.Series.Add(new ReportSeries($"{Label(items, i)} b*", i, x, t.Select(p => p.DeltaB).ToArray(), Dashed: true));
            }
        }
        if (tone.Series.Count > 1)
            charts.Add(tone);
        if (time.Series.Count > 0)
            charts.Add(time);
        return charts;
    }

    /// <summary>Subtitle timing: each source's shift against the reference line by line, in milliseconds.</summary>
    public static ReportChart? Subtitles(IReadOnlyList<CompareItem> items, IReadOnlyList<SubtitleTimingReport>? reports)
    {
        var c = new ReportChart
        {
            Title = "Subtitle timing: shift of each matched line against the reference (ms; above zero = later)",
            XFormat = v => Format.Time(v, false),
            YFormat = v => v.ToString("+0;-0;0", Inv) + " ms",
        };
        foreach (var r in reports ?? [])
        {
            var i = IndexOf(items, r.Item);
            if (r.Deltas.Count > 1 && i > 0)
                c.Series.Add(new ReportSeries($"{Label(items, i)} · {r.TrackLabel}", i, [.. r.Deltas.Select(d => d.Time)], [.. r.Deltas.Select(d => d.Delta * 1000)]));
        }
        return c.Series.Count > 0 ? c : null;
    }

    private static int IndexOf(IReadOnlyList<CompareItem> items, CompareItem it)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], it))
                return i;
        }
        return -1;
    }

    private static string Label(IReadOnlyList<CompareItem> items, int i) => $"{i + 1} · {items[i].Name}";

    // ---------- drawing ----------

    public static string Svg(ReportChart c)
    {
        const double w = 1000, h = 280, left = 64, right = 14, top = 12, bottom = 30;
        var pw = w - left - right;
        var ph = h - top - bottom;
        var pts = c.Series.SelectMany(s => s.X.Zip(s.Y)).Where(p => double.IsFinite(p.First) && double.IsFinite(p.Second)).ToList();
        if (pts.Count < 2)
            return "";
        var (x0, x1) = (c.XMin ?? pts.Min(p => p.First), c.XMax ?? pts.Max(p => p.First));
        var dataMin = pts.Min(p => p.Second);
        var dataMax = pts.Max(p => p.Second);
        // a fixed range when both ends are given (tone curve); a lone YMin is a floor (spectra), a lone YMax a ceiling
        var y0 = c.YMin is { } lo ? (c.YMax.HasValue ? lo : Math.Max(lo, dataMin)) : dataMin;
        var y1 = c.YMax ?? dataMax;
        if (x1 <= x0)
            x1 = x0 + 1;
        if (y1 <= y0)
            (y0, y1) = (y0 - 1, y1 + 1);
        var yTicks = Ticks(y0, y1, 5);
        y0 = Math.Min(y0, yTicks[0]);
        y1 = Math.Max(y1, yTicks[^1]);
        var xTicks = Ticks(x0, x1, 7);
        double X(double v) => left + (v - x0) / (x1 - x0) * pw;
        double Y(double v) => top + ph - (Math.Clamp(v, y0, y1) - y0) / (y1 - y0) * ph;

        var sb = new StringBuilder();
        sb.Append("<figure class=\"chart\"><figcaption class=\"title\">").Append(H(c.Title)).Append("</figcaption>");
        sb.Append(Inv, $"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-label=\"{H(c.Title)}\">");
        // grid and axis labels
        foreach (var t in yTicks.Where(t => t >= y0 - 1e-9 && t <= y1 + 1e-9))
        {
            var y = Y(t);
            sb.Append(Inv, $"<line x1=\"{left}\" y1=\"{y:0.#}\" x2=\"{w - right}\" y2=\"{y:0.#}\" class=\"grid\"/>");
            sb.Append(Inv, $"<text x=\"{left - 6}\" y=\"{y + 4:0.#}\" text-anchor=\"end\">{H(c.YFormat(t))}</text>");
        }
        foreach (var t in xTicks.Where(t => t >= x0 - 1e-9 && t <= x1 + 1e-9))
        {
            var x = X(t);
            sb.Append(Inv, $"<line x1=\"{x:0.#}\" y1=\"{top}\" x2=\"{x:0.#}\" y2=\"{top + ph}\" class=\"grid\"/>");
            sb.Append(Inv, $"<text x=\"{x:0.#}\" y=\"{h - 10}\" text-anchor=\"middle\">{H(c.XFormat(t))}</text>");
        }
        sb.Append(Inv, $"<rect x=\"{left}\" y=\"{top}\" width=\"{pw}\" height=\"{ph}\" class=\"frame\"/>");
        foreach (var s in c.Series)
        {
            var line = new StringBuilder();
            var n = Math.Min(s.X.Length, s.Y.Length);
            var step = Math.Max(1, n / MaxPoints);
            for (var k = 0; k < n; k += step)
            {
                if (!double.IsFinite(s.X[k]) || !double.IsFinite(s.Y[k]) || s.X[k] < x0 || s.X[k] > x1)
                    continue;
                line.Append(X(s.X[k]).ToString("0.#", Inv)).Append(',').Append(Y(s.Y[k]).ToString("0.#", Inv)).Append(' ');
            }
            var color = s.Guide ? "#8a8c91" : Colors[s.Source % Colors.Length];
            sb.Append("<polyline fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"").Append(s.Guide ? "1" : "1.6").Append('"')
              .Append(s.Dashed ? " stroke-dasharray=\"6 4\"" : "").Append(" stroke-linejoin=\"round\" points=\"").Append(line).Append("\"><title>")
              .Append(H(s.Name)).Append("</title></polyline>");
        }
        sb.Append("</svg><div class=\"legend\">");
        foreach (var s in c.Series)
        {
            var color = s.Guide ? "#8a8c91" : Colors[s.Source % Colors.Length];
            sb.Append("<span><i style=\"border-top:2px ").Append(s.Dashed ? "dashed" : "solid").Append(' ').Append(color).Append("\"></i>").Append(H(s.Name)).Append("</span>");
        }
        sb.Append("</div>");
        if (c.Note.Length > 0)
            sb.Append("<p class=\"muted\">").Append(H(c.Note)).Append("</p>");
        return sb.Append("</figure>").ToString();
    }

    /// <summary>About <paramref name="count"/> round tick values covering lo…hi.</summary>
    internal static double[] Ticks(double lo, double hi, int count)
    {
        var span = hi - lo;
        if (span <= 0 || !double.IsFinite(span))
            return [lo];
        var raw = span / Math.Max(1, count - 1);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * mag).First(s => s >= raw);
        var start = Math.Floor(lo / step) * step;
        var ticks = new List<double>();
        for (var v = start; v <= hi + step * 0.5 && ticks.Count < 20; v += step)
            ticks.Add(Math.Round(v, 10));
        return [.. ticks];
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);
}
