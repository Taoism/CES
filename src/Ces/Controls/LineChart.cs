using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Ces.Controls;

public sealed record ChartSeries(string Name, IBrush Color, double[] X, double[] Y)
{
    /// <summary>Drawn dashed (a second quantity of the same source).</summary>
    public bool Dashed { get; init; }
}

/// <summary>Simple multi-series line chart with axes, legend, hover readout and click-to-pick an X value.</summary>
public sealed class LineChart : Control
{
    private readonly List<ChartSeries> _series = [];
    private Point? _hover;

    public string Title { get; set; } = "";
    public Func<double, string> XFormat { get; set; } = v => v.ToString("0.##", CultureInfo.InvariantCulture);
    public Func<double, string> YFormat { get; set; } = v => v.ToString("0.##", CultureInfo.InvariantCulture);
    public double? YMin { get; set; }
    public double? YMax { get; set; }

    /// <summary>Raised with the X value under the pointer when the chart is clicked.</summary>
    public event EventHandler<double>? XPicked;

    public LineChart()
    {
        MinHeight = 160;
        ClipToBounds = true;
    }

    public void SetSeries(IEnumerable<ChartSeries> series)
    {
        _series.Clear();
        _series.AddRange(series.Where(s => s.X.Length > 0 && s.X.Length == s.Y.Length));
        InvalidateVisual();
    }

    private (double X0, double X1, double Y0, double Y1) Ranges()
    {
        var xs = _series.SelectMany(s => s.X).ToList();
        var ys = _series.SelectMany(s => s.Y).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        if (xs.Count == 0 || ys.Count == 0)
            return (0, 1, 0, 1);
        var y0 = YMin ?? ys.Min();
        var y1 = YMax ?? ys.Max();
        if (y1 - y0 < 1e-9)
        {
            y0 -= 1;
            y1 += 1;
        }
        else if (YMin == null || YMax == null)
        {
            var pad = (y1 - y0) * 0.05;
            if (YMin == null)
                y0 -= pad;
            if (YMax == null)
                y1 += pad;
        }
        var x0 = xs.Min();
        var x1 = xs.Max();
        if (x1 - x0 < 1e-12)
            x1 = x0 + 1;
        return (x0, x1, y0, y1);
    }

    private Rect Plot => new(56, Title.Length > 0 ? 24 : 10, Math.Max(10, Bounds.Width - 70), Math.Max(10, Bounds.Height - (Title.Length > 0 ? 24 : 10) - 26));

    public override void Render(DrawingContext ctx)
    {
        var text = Res("TextBrush", Brushes.White);
        var muted = Res("MutedTextBrush", Brushes.Gray);
        var edge = Res("EdgeBrush", Brushes.DimGray);
        ctx.FillRectangle(Res("PanelBrush", Brushes.Black), new Rect(Bounds.Size));
        var plot = Plot;
        if (Title.Length > 0)
            ctx.DrawText(Text(Title, 12.5, text, FontWeight.SemiBold), new Point(10, 4));
        if (_series.Count == 0)
        {
            var ft = Text("No data yet", 12, muted);
            ctx.DrawText(ft, new Point(plot.Center.X - ft.Width / 2, plot.Center.Y - ft.Height / 2));
            return;
        }
        var (x0, x1, y0, y1) = Ranges();
        double Px(double x) => plot.X + (x - x0) / (x1 - x0) * plot.Width;
        double Py(double y) => plot.Bottom - (y - y0) / (y1 - y0) * plot.Height;

        var grid = new Pen(edge, 1);
        for (var i = 0; i <= 4; i++)
        {
            var yv = y0 + (y1 - y0) * i / 4;
            var py = Py(yv);
            ctx.DrawLine(grid, new Point(plot.X, py), new Point(plot.Right, py));
            var ft = Text(YFormat(yv), 10.5, muted);
            ctx.DrawText(ft, new Point(plot.X - ft.Width - 6, py - ft.Height / 2));
            var xv = x0 + (x1 - x0) * i / 4;
            var px = Px(xv);
            ctx.DrawLine(grid, new Point(px, plot.Y), new Point(px, plot.Bottom));
            var fx = Text(XFormat(xv), 10.5, muted);
            ctx.DrawText(fx, new Point(Math.Clamp(px - fx.Width / 2, 0, Bounds.Width - fx.Width), plot.Bottom + 4));
        }

        using (ctx.PushClip(plot.Inflate(1)))
        {
            foreach (var s in _series)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var started = false;
                    // thin out very long series to about two points per pixel
                    var step = Math.Max(1, s.X.Length / Math.Max(1, (int)(plot.Width * 2)));
                    // separate windows (ranking) are not joined: break where the spacing jumps
                    var gaps = Enumerable.Range(1, s.X.Length - 1).Select(i => s.X[i] - s.X[i - 1]).Where(d => d > 0).Order().ToList();
                    var maxGap = gaps.Count > 0 ? gaps[gaps.Count / 2] * step * 8 : double.MaxValue;
                    var prevX = double.NaN;
                    for (var i = 0; i < s.X.Length; i += step)
                    {
                        var y = s.Y[i];
                        if (double.IsNaN(y) || double.IsInfinity(y) || (!double.IsNaN(prevX) && s.X[i] - prevX > maxGap))
                            started = false;
                        prevX = s.X[i];
                        if (double.IsNaN(y) || double.IsInfinity(y))
                            continue;
                        var pt = new Point(Px(s.X[i]), Py(Math.Clamp(y, y0, y1)));
                        if (!started)
                        {
                            g.BeginFigure(pt, false);
                            started = true;
                        }
                        else
                        {
                            g.LineTo(pt);
                        }
                    }
                }
                ctx.DrawGeometry(null, s.Dashed ? new Pen(s.Color, 1.6, new DashStyle([4, 3], 0)) : new Pen(s.Color, 1.6), geo);
            }
        }

        // legend
        var lx = plot.Right - 6;
        var ly = plot.Y + 4;
        foreach (var s in _series.AsEnumerable().Reverse())
        {
            var ft = Text(s.Name, 11, text);
            lx -= ft.Width + 22;
            ctx.FillRectangle(s.Color, new Rect(lx, ly + ft.Height / 2 - 2, 12, 4));
            ctx.DrawText(ft, new Point(lx + 16, ly));
        }

        // hover readout
        if (_hover is { } h && plot.Contains(h))
        {
            var xv = x0 + (h.X - plot.X) / plot.Width * (x1 - x0);
            ctx.DrawLine(new Pen(muted, 1, new DashStyle([3, 3], 0)), new Point(h.X, plot.Y), new Point(h.X, plot.Bottom));
            var lines = new List<(string, IBrush)> { (XFormat(xv), text) };
            foreach (var s in _series)
            {
                var idx = Nearest(s.X, xv);
                if (idx >= 0)
                    lines.Add(($"{s.Name}: {YFormat(s.Y[idx])}", s.Color));
            }
            var fts = lines.Select(l => Text(l.Item1, 11, l.Item2)).ToList();
            var w = fts.Max(f => f.Width) + 12;
            var hh = fts.Sum(f => f.Height) + 8;
            var bx = h.X + 12 + w > Bounds.Width ? h.X - 12 - w : h.X + 12;
            var box = new Rect(bx, plot.Y + 4, w, hh);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 24, 25, 28)), new Pen(edge, 1), box, 4, 4);
            var yy = box.Y + 4;
            foreach (var f in fts)
            {
                ctx.DrawText(f, new Point(box.X + 6, yy));
                yy += f.Height;
            }
        }
    }

    private static int Nearest(double[] xs, double x)
    {
        if (xs.Length == 0)
            return -1;
        var i = Array.BinarySearch(xs, x);
        if (i >= 0)
            return i;
        i = ~i;
        if (i <= 0)
            return 0;
        if (i >= xs.Length)
            return xs.Length - 1;
        return x - xs[i - 1] < xs[i] - x ? i - 1 : i;
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_series.Count == 0)
            return;
        var p = e.GetPosition(this);
        var plot = Plot;
        if (!plot.Contains(p))
            return;
        var (x0, x1, _, _) = Ranges();
        XPicked?.Invoke(this, x0 + (p.X - plot.X) / plot.Width * (x1 - x0));
    }
}
