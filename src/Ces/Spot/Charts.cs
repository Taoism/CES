using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ces.SpotUi;

/// <summary>Shared drawing helpers: theme brushes and text.</summary>
public abstract class ChartBase : Control
{
    protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    protected const double Left = 56, Right = 16, Top = 28, Bottom = 40;

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<ChartBase, string?>(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    protected IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    protected IBrush TextBrush => Brush("MutedTextBrush", Brushes.Gray);
    protected IBrush GridBrush => Brush("ChartGridBrush", Brushes.DimGray);

    protected void Text(DrawingContext dc, string s, double x, double y, double size = 11, bool centered = false, bool rightAligned = false, IBrush? brush = null)
    {
        var ft = new FormattedText(s, Inv, FlowDirection.LeftToRight, Typeface.Default, size, brush ?? TextBrush);
        if (centered)
        {
            x -= ft.Width / 2;
        }
        else if (rightAligned)
        {
            x -= ft.Width;
        }
        dc.DrawText(ft, new Point(x, y));
    }

    protected void DrawTitle(DrawingContext dc)
    {
        if (!string.IsNullOrEmpty(Title))
        {
            Text(dc, Title, Left, 6, 12.5, brush: Brush("TextBrush", Brushes.White));
        }
    }

    /// <summary>A "nice" axis maximum and step (1, 2, 5 × 10^n).</summary>
    protected static (double Max, double Step) NiceScale(double max, int ticks = 5)
    {
        if (max <= 0)
        {
            return (1, 0.25);
        }
        double raw = max / ticks;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
        return (Math.Ceiling(max / step) * step, step);
    }

    protected void YAxis(DrawingContext dc, Rect plot, double max, double step, Func<double, string> label)
    {
        var pen = new Pen(GridBrush, 1);
        for (double v = 0; v <= max + (step / 2); v += step)
        {
            double y = plot.Bottom - (v / max * plot.Height);
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            Text(dc, label(v), plot.Left - 6, y - 7, rightAligned: true);
        }
    }

    protected Rect PlotRect() => new(Left, Top, Math.Max(10, Bounds.Width - Left - Right), Math.Max(10, Bounds.Height - Top - Bottom));
}

/// <summary>Vertical bars with a label under each (histograms, counts).</summary>
public sealed class BarChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<KeyValuePair<string, double>>?> BarsProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<KeyValuePair<string, double>>?>(nameof(Bars));

    public static readonly StyledProperty<bool> ShowPercentProperty = AvaloniaProperty.Register<BarChart, bool>(nameof(ShowPercent), true);

    static BarChart() => AffectsRender<BarChart>(BarsProperty, TitleProperty, ShowPercentProperty);

    public IReadOnlyList<KeyValuePair<string, double>>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public bool ShowPercent
    {
        get => GetValue(ShowPercentProperty);
        set => SetValue(ShowPercentProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        DrawTitle(context);
        var bars = Bars;
        if (bars is null || bars.Count == 0)
        {
            Text(context, "no data", Bounds.Width / 2, Bounds.Height / 2, 13, centered: true);
            return;
        }
        var plot = PlotRect();
        double total = bars.Sum(b => b.Value);
        double maxValue = bars.Max(b => b.Value);
        var (max, step) = NiceScale(maxValue);
        YAxis(context, plot, max, step, v => v.ToString("N0", Inv));
        var fill = Brush("ChartBarBrush", Brushes.SteelBlue);
        double slot = plot.Width / bars.Count;
        double barWidth = Math.Max(2, Math.Min(64, slot * 0.7));
        // label every n-th bar so labels do not overlap
        int labelEvery = Math.Max(1, (int)Math.Ceiling(44 / slot));
        for (int i = 0; i < bars.Count; i++)
        {
            double x = plot.Left + (slot * i) + ((slot - barWidth) / 2);
            double h = bars[i].Value / max * plot.Height;
            if (h > 0)
            {
                context.FillRectangle(fill, new Rect(x, plot.Bottom - h, barWidth, Math.Max(1, h)), 2);
            }
            double cx = x + (barWidth / 2);
            if (i % labelEvery == 0)
            {
                Text(context, bars[i].Key, cx, plot.Bottom + 4, centered: true);
            }
            if (ShowPercent && total > 0 && bars[i].Value > 0 && slot >= 34)
            {
                Text(context, string.Create(Inv, $"{100 * bars[i].Value / total:0.#}%"), cx, plot.Bottom - h - 16, 10, centered: true);
            }
        }
    }
}

/// <summary>A per-frame value over time, drawn as min/max columns so long files stay readable.</summary>
public sealed class LineChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<short>?> ValuesProperty =
        AvaloniaProperty.Register<LineChart, IReadOnlyList<short>?>(nameof(Values));

    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<LineChart, double>(nameof(Duration));

    public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<LineChart, string>(nameof(Unit), "");

    static LineChart() => AffectsRender<LineChart>(ValuesProperty, DurationProperty, TitleProperty);

    public IReadOnlyList<short>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Length in seconds (for the time axis).</summary>
    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        DrawTitle(context);
        var values = Values;
        if (values is null || values.Count == 0)
        {
            Text(context, "no data", Bounds.Width / 2, Bounds.Height / 2, 13, centered: true);
            return;
        }
        var plot = PlotRect();
        int maxV = 0;
        long sum = 0;
        foreach (short v in values)
        {
            maxV = Math.Max(maxV, v);
            sum += v;
        }
        var (max, step) = NiceScale(maxV);
        YAxis(context, plot, max, step, v => v.ToString("N0", Inv));

        int columns = Math.Max(1, (int)plot.Width);
        var fill = Brush("ChartBarBrush", Brushes.SteelBlue);
        var line = new Pen(Brush("ChartLineBrush", Brushes.LightBlue), 1);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            bool started = false;
            for (int c = 0; c < columns; c++)
            {
                int from = (int)((long)c * values.Count / columns);
                int to = Math.Max(from + 1, (int)((long)(c + 1) * values.Count / columns));
                int lo = int.MaxValue, hi = int.MinValue;
                long colSum = 0;
                for (int i = from; i < to && i < values.Count; i++)
                {
                    lo = Math.Min(lo, values[i]);
                    hi = Math.Max(hi, values[i]);
                    colSum += values[i];
                }
                if (hi < lo)
                {
                    continue;
                }
                double x = plot.Left + c + 0.5;
                double yLo = plot.Bottom - (lo / max * plot.Height);
                double yHi = plot.Bottom - (hi / max * plot.Height);
                context.DrawLine(new Pen(fill, 1), new Point(x, yLo), new Point(x, Math.Min(yLo - 1, yHi)));
                double yAvg = plot.Bottom - ((double)colSum / (to - from) / max * plot.Height);
                if (!started)
                {
                    g.BeginFigure(new Point(x, yAvg), false);
                    started = true;
                }
                else
                {
                    g.LineTo(new Point(x, yAvg));
                }
            }
        }
        context.DrawGeometry(null, line, geometry);

        double avg = (double)sum / values.Count;
        double ya = plot.Bottom - (avg / max * plot.Height);
        context.DrawLine(new Pen(Brush("ChartAltBrush", Brushes.Orange), 1, new DashStyle([4, 3], 0)), new Point(plot.Left, ya), new Point(plot.Right, ya));
        Text(context, string.Create(Inv, $"average {avg:0.#} {Unit}, peak {maxV} {Unit}"), plot.Right, 6, rightAligned: true, brush: Brush("ChartAltBrush", Brushes.Orange));

        // time axis
        double dur = Duration;
        if (dur > 0)
        {
            double tStep = NiceScale(dur, 8).Step;
            for (double t = 0; t <= dur; t += tStep)
            {
                double x = plot.Left + (t / dur * plot.Width);
                string label = dur >= 3600 ? Spot.Fmt.Duration(t) : Spot.Fmt.Duration(t)[3..];
                Text(context, label, x, plot.Bottom + 4, centered: true);
            }
        }
        else
        {
            Text(context, values.Count.ToString("N0", Inv) + " frames", plot.Right, plot.Bottom + 4, rightAligned: true);
        }
    }
}
