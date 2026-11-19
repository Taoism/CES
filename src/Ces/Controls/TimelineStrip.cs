using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Ces.Controls;

/// <summary>
/// The reference title as a bar: chapter ticks, a marker per screenshot time (the selected one highlighted) and
/// the times along it. Clicking picks the nearest screenshot.
/// </summary>
public sealed class TimelineStrip : Control
{
    private double _duration;
    private IReadOnlyList<double> _marks = [];
    private IReadOnlyList<double> _chapters = [];
    private int _selected = -1;
    private int _hover = -1;

    public TimelineStrip()
    {
        Height = 30;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Screenshot times along the reference title (ticks: chapters) · click to jump");
    }

    private bool _themeHooked;

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // theme colours are read while drawing: repaint when Light / Dark switches
        if (_themeHooked)
            return;
        _themeHooked = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>A marker was clicked: its index in the marks.</summary>
    public event Action<int>? MarkClicked;

    public void Set(double duration, IReadOnlyList<double> marks, IReadOnlyList<double> chapters, int selected)
    {
        _duration = duration;
        _marks = marks;
        _chapters = chapters;
        _selected = selected;
        InvalidateVisual();
    }

    private Rect Bar => new(8, 6, Math.Max(0, Bounds.Width - 16), 10);

    private double X(double t) => Bar.X + (_duration > 0 ? Math.Clamp(t / _duration, 0, 1) : 0) * Bar.Width;

    private int Nearest(double x)
    {
        var best = -1;
        var dist = double.MaxValue;
        for (var i = 0; i < _marks.Count; i++)
        {
            var d = Math.Abs(X(_marks[i]) - x);
            if (d < dist)
            {
                dist = d;
                best = i;
            }
        }
        return dist <= 14 ? best : -1;
    }

    public override void Render(DrawingContext ctx)
    {
        var bar = Bar;
        if (bar.Width <= 0)
            return;
        var panel = this.ThemeBrush("PanelAltBrush", Brushes.DimGray);
        var edge = this.ThemeBrush("EdgeBrush", Brushes.Gray);
        var muted = this.ThemeBrush("MutedTextBrush", Brushes.Gray);
        var accent = this.ThemeBrush("AccentBrush", Brushes.DodgerBlue);
        ctx.DrawRectangle(panel, new Pen(edge, 1), bar, 3, 3);
        if (_duration <= 0)
            return;
        foreach (var c in _chapters)
            ctx.DrawLine(new Pen(edge, 1), new Point(X(c), bar.Y + 2), new Point(X(c), bar.Bottom - 2));
        for (var i = 0; i < _marks.Count; i++)
        {
            var x = X(_marks[i]);
            var on = i == _selected;
            var w = on || i == _hover ? 4 : 2;
            ctx.FillRectangle(on ? accent : muted, new Rect(x - w / 2.0, bar.Y - (on ? 3 : 1), w, bar.Height + (on ? 6 : 2)));
        }
        Label(ctx, "0:00:00", bar.X, false, muted);
        Label(ctx, Model.Format.Time(_duration, false), bar.Right, true, muted);
        if (_selected >= 0 && _selected < _marks.Count)
        {
            var x = X(_marks[_selected]);
            // keep the label clear of the end labels
            if (x > bar.X + 60 && x < bar.Right - 60)
                Label(ctx, Model.Format.Time(_marks[_selected], false), x, null, accent);
        }
    }

    private void Label(DrawingContext ctx, string text, double x, bool? right, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, brush);
        var at = right switch { true => x - ft.Width, false => x, null => x - ft.Width / 2 };
        ctx.DrawText(ft, new Point(at, Bar.Bottom + 1));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var h = Nearest(e.GetPosition(this).X);
        if (h != _hover)
        {
            _hover = h;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Nearest(e.GetPosition(this).X) is var i and >= 0)
        {
            MarkClicked?.Invoke(i);
            e.Handled = true;
        }
    }
}
