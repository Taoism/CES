using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ces.Controls;

/// <summary>
/// A small hourglass whose sand runs from the top bulb into the bottom one as <see cref="Fraction"/> goes 0 → 1,
/// with a thin falling stream while it runs. Drawn in theme colours (frame: muted text, sand: accent).
/// </summary>
public sealed class Hourglass : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<Hourglass, double>(nameof(Fraction));

    private bool _streamPhase;

    static Hourglass() => AffectsRender<Hourglass>(FractionProperty);

    public Hourglass()
    {
        Width = 14;
        Height = 20;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, Math.Clamp(value, 0, 1));
    }

    /// <summary>Called once a second: the falling stream flickers so the glass looks alive even when progress stalls.</summary>
    public void Tick()
    {
        _streamPhase = !_streamPhase;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 6)
            return;
        var frame = this.ThemeBrush("MutedTextBrush", Brushes.Gray);
        var sand = this.ThemeBrush("AccentBrush", Brushes.Orange);
        var pen = new Pen(frame, 1.2);
        const double cap = 1.5;
        var top = cap + 0.5;
        var bottom = h - cap - 0.5;
        var mid = h / 2;
        var neck = 1.2;
        var left = 1.5;
        var right = w - 1.5;
        var cx = w / 2;

        // caps
        ctx.DrawLine(new Pen(frame, 2), new Point(0.5, cap / 2 + 0.25), new Point(w - 0.5, cap / 2 + 0.25));
        ctx.DrawLine(new Pen(frame, 2), new Point(0.5, h - cap / 2 - 0.25), new Point(w - 0.5, h - cap / 2 - 0.25));

        // sand: the top bulb holds (1 − f), the bottom f; both drawn as triangles cut at their level
        var f = Fraction;
        var bulb = mid - top;
        var topLevel = Math.Sqrt(1 - f); // area of a triangle grows with height²
        if (1 - f > 0.01)
        {
            // top bulb: triangle with its tip at the neck; sand fills from the neck up to height topLevel·bulb
            var y = mid - bulb * topLevel;
            var half = (cx - left - neck) * topLevel + neck;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx - half + 0.8, y + 0.6), true);
                c.LineTo(new Point(cx + half - 0.8, y + 0.6));
                c.LineTo(new Point(cx, mid - 0.3));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(sand, null, g);
        }
        if (f > 0.01)
        {
            // bottom bulb: a heap on the floor, growing upward
            var level = 1 - Math.Sqrt(1 - f);
            var y = bottom - bulb * level;
            var half = (cx - left) * (1 - level) + neck * level;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(left + 0.8, bottom - 0.4), true);
                c.LineTo(new Point(right - 0.8, bottom - 0.4));
                c.LineTo(new Point(cx + Math.Max(0.6, half - 0.8), y));
                c.LineTo(new Point(cx - Math.Max(0.6, half - 0.8), y));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(sand, null, g);
        }
        if (f is > 0.005 and < 0.995)
        {
            var from = _streamPhase ? mid : mid + 1;
            ctx.DrawLine(new Pen(sand, 1), new Point(cx, from), new Point(cx, bottom - bulb * (1 - Math.Sqrt(1 - f)) - 0.5));
        }

        // the glass
        var glass = new StreamGeometry();
        using (var c = glass.Open())
        {
            c.BeginFigure(new Point(left, top), false);
            c.LineTo(new Point(right, top));
            c.LineTo(new Point(cx + neck, mid));
            c.LineTo(new Point(right, bottom));
            c.LineTo(new Point(left, bottom));
            c.LineTo(new Point(cx - neck, mid));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(null, pen, glass);
    }
}
