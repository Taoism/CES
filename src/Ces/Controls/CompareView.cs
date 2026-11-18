using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Ces.Controls;

public enum CompareMode
{
    Single,
    Wipe,
    SideBySide,

    /// <summary>|A − B| per pixel, amplified: shows exactly where an encode differs.</summary>
    Difference,

    /// <summary>Alternates A and B automatically; small differences jump out.</summary>
    Flicker,
}

/// <summary>
/// Shows the same frame from several sources: one at a time (switch with 1–9), as a wipe between two, or side by
/// side. Zoom (wheel, nearest-neighbour above 150 %) and pan (drag) are shared by all views, so the same pixels line up.
/// Every image is stretched to the largest one's size so a 720p encode lines up with its 1080p source.
/// </summary>
public sealed class CompareView : Control
{
    private Bitmap?[] _images = [];
    private string[] _labels = [];
    private IBrush[] _colors = [];
    private double _zoom; // 0 = fit
    private Point _center = new(double.NaN, double.NaN); // image coordinates at the view centre
    private Point? _dragStart;
    private Point _dragCenter;
    private double _wipe = 0.5;
    private string? _message;
    private readonly DispatcherTimer _flicker = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _flickOther;
    private WriteableBitmap? _diff;
    private (Bitmap? A, Bitmap? B, double Gain) _diffKey;

    public CompareView()
    {
        _flicker.Tick += (_, _) =>
        {
            _flickOther = !_flickOther;
            InvalidateVisual();
        };
    }

    /// <summary>Difference-mode amplification.</summary>
    public double DiffGain
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = 4;

    /// <summary>Flicker interval in milliseconds.</summary>
    public int FlickerMs
    {
        get => (int)_flicker.Interval.TotalMilliseconds;
        set => _flicker.Interval = TimeSpan.FromMilliseconds(Math.Clamp(value, 80, 5000));
    }

    /// <summary>Mean absolute difference (0–255) and share of visibly changed pixels of the last difference image.</summary>
    public (double Mean, double Changed) DiffStats { get; private set; }

    public event EventHandler? ViewChanged;

    public CompareMode Mode
    {
        get;
        set
        {
            field = value;
            if (value == CompareMode.Flicker)
                _flicker.Start();
            else
                _flicker.Stop();
            _flickOther = false;
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Source shown in Single mode, left side in Wipe mode, A in Difference/Flicker.</summary>
    public int Current
    {
        get;
        set
        {
            if (_images.Length == 0)
                return;
            var v = ((value % _images.Length) + _images.Length) % _images.Length;
            if (v == field)
                return;
            if (v == WipeOther)
                WipeOther = field;
            field = v;
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Right side in Wipe mode, B in Difference/Flicker.</summary>
    public int WipeOther { get; set; } = 1;

    public double Zoom => _zoom;

    public Size ReferenceSize { get; private set; }

    public void SetImages(Bitmap?[] images, string[] labels, IBrush[] colors, string? message = null)
    {
        _images = images;
        _labels = labels;
        _colors = colors;
        _message = message;
        var w = images.Where(b => b != null).Select(b => b!.Size.Width).DefaultIfEmpty(0).Max();
        var h = images.Where(b => b != null).Select(b => b!.Size.Height).DefaultIfEmpty(0).Max();
        var changed = ReferenceSize != new Size(w, h);
        ReferenceSize = new Size(w, h);
        if (changed || double.IsNaN(_center.X))
            _center = new Point(w / 2, h / 2);
        if (Current >= images.Length)
            Current = 0;
        if (WipeOther >= images.Length || WipeOther == Current)
            WipeOther = images.Length > 1 ? (Current + 1) % images.Length : 0;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetZoom(double zoom, Point? anchor = null)
    {
        var bounds = Bounds;
        if (anchor is { } a && ReferenceSize.Width > 0)
        {
            // keep the image point under the cursor in place
            var cell = Cells(bounds)[0];
            var sOld = Scale(cell);
            var img = ToImage(a, cell, sOld);
            _zoom = zoom;
            var sNew = Scale(cell);
            var cellCenter = cell.Center;
            _center = new Point(img.X - (a.X - cellCenter.X) / sNew, img.Y - (a.Y - cellCenter.Y) / sNew);
        }
        else
        {
            _zoom = zoom;
            if (zoom == 0)
                _center = new Point(ReferenceSize.Width / 2, ReferenceSize.Height / 2);
        }
        ClampCenter();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Current effective scale of the first cell (for the zoom label).</summary>
    public double EffectiveScale => Bounds.Width > 0 && ReferenceSize.Width > 0 ? Scale(Cells(Bounds)[0]) : 1;

    private List<Rect> Cells(Rect bounds)
    {
        var r = new Rect(bounds.Size);
        if (Mode != CompareMode.SideBySide || _images.Length <= 1)
            return [r];
        var n = _images.Length;
        var cols = n <= 3 ? n : n == 4 ? 2 : 3;
        // portrait-ish windows: stack instead
        if (bounds.Width < bounds.Height * 1.2 && n <= 3)
            cols = 1;
        var rows = (n + cols - 1) / cols;
        var cw = r.Width / cols;
        var ch = r.Height / rows;
        var list = new List<Rect>();
        for (var i = 0; i < n; i++)
            list.Add(new Rect(i % cols * cw, i / cols * ch, cw, ch).Deflate(2));
        return list;
    }

    private double Scale(Rect cell)
    {
        if (ReferenceSize.Width <= 0)
            return 1;
        var fit = Math.Min(cell.Width / ReferenceSize.Width, cell.Height / ReferenceSize.Height);
        return _zoom <= 0 ? fit : _zoom;
    }

    private Rect ImageRect(Rect cell, double s) =>
        new(cell.Center.X - _center.X * s, cell.Center.Y - _center.Y * s, ReferenceSize.Width * s, ReferenceSize.Height * s);

    private Point ToImage(Point p, Rect cell, double s) =>
        new(_center.X + (p.X - cell.Center.X) / s, _center.Y + (p.Y - cell.Center.Y) / s);

    private void ClampCenter()
    {
        var w = ReferenceSize.Width;
        var h = ReferenceSize.Height;
        _center = new Point(Math.Clamp(_center.X, 0, w), Math.Clamp(_center.Y, 0, h));
        if (_zoom <= 0)
            _center = new Point(w / 2, h / 2);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Res("PreviewBackgroundBrush", Brushes.Black), bounds);
        if (_images.Length == 0 || ReferenceSize.Width <= 0)
        {
            DrawCentered(ctx, bounds, _message ?? "Add sources, then take screenshots.");
            return;
        }
        var cells = Cells(bounds);
        var s = Scale(cells[0]);
        var mode = s >= 1.5 ? Avalonia.Media.Imaging.BitmapInterpolationMode.None : Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            switch (Mode)
            {
                case CompareMode.SideBySide when _images.Length > 1:
                    for (var i = 0; i < _images.Length && i < cells.Count; i++)
                    {
                        var cell = cells[i];
                        using (ctx.PushClip(cell))
                        {
                            DrawImage(ctx, i, ImageRect(cell, Scale(cell)));
                            Badge(ctx, i, cell.TopLeft + new Point(8, 8));
                        }
                    }
                    break;

                case CompareMode.Wipe when _images.Length > 1:
                {
                    var cell = cells[0];
                    var rect = ImageRect(cell, s);
                    var x = Math.Clamp(cell.X + cell.Width * _wipe, cell.X, cell.Right);
                    using (ctx.PushClip(new Rect(cell.X, cell.Y, x - cell.X, cell.Height)))
                        DrawImage(ctx, Current, rect);
                    using (ctx.PushClip(new Rect(x, cell.Y, cell.Right - x, cell.Height)))
                        DrawImage(ctx, WipeOther, rect);
                    ctx.DrawLine(new Pen(Brushes.White, 1.5), new Point(x, cell.Y), new Point(x, cell.Bottom));
                    Badge(ctx, Current, new Point(Math.Max(cell.X + 8, x - 260), cell.Y + 8), rightAlignAt: x - 8);
                    Badge(ctx, WipeOther, new Point(x + 8, cell.Y + 8));
                    break;
                }

                case CompareMode.Difference when _images.Length > 1:
                {
                    var cell = cells[0];
                    var diff = DiffImage();
                    if (diff != null)
                        ctx.DrawImage(diff, new Rect(diff.Size), ImageRect(cell, s));
                    else
                        DrawCentered(ctx, cell, "Both sources need a frame here");
                    var (mean, changed) = DiffStats;
                    TextBadge(ctx, $" |{Current + 1} − {WipeOther + 1}| × {DiffGain:0.#}   mean {mean:0.00}   changed {changed:P1} ", cell.TopLeft + new Point(8, 8));
                    break;
                }

                case CompareMode.Flicker when _images.Length > 1:
                {
                    var cell = cells[0];
                    var i = _flickOther ? WipeOther : Current;
                    DrawImage(ctx, i, ImageRect(cell, s));
                    Badge(ctx, i, cell.TopLeft + new Point(8, 8));
                    break;
                }

                default:
                {
                    var cell = cells[0];
                    DrawImage(ctx, Current, ImageRect(cell, s));
                    Badge(ctx, Current, cell.TopLeft + new Point(8, 8));
                    break;
                }
            }
        }
    }

    private void DrawImage(DrawingContext ctx, int i, Rect dest)
    {
        if (i < 0 || i >= _images.Length)
            return;
        var bmp = _images[i];
        if (bmp == null)
        {
            DrawCentered(ctx, dest.Intersect(new Rect(Bounds.Size)), "No frame for this source");
            return;
        }
        ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
    }

    private void Badge(DrawingContext ctx, int i, Point at, double? rightAlignAt = null)
    {
        if (i < 0 || i >= _labels.Length)
            return;
        var ft = new FormattedText($" {i + 1}  {_labels[i]} ", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12.5, Res("OverlayTextBrush", Brushes.White));
        var x = rightAlignAt.HasValue ? rightAlignAt.Value - ft.Width - 10 : at.X;
        var rect = new Rect(x, at.Y, ft.Width + 10, ft.Height + 6);
        ctx.DrawRectangle(Res("OverlayBrush", Brushes.Black), null, rect, 4, 4);
        ctx.FillRectangle(i < _colors.Length ? _colors[i] : Brushes.Gray, new Rect(rect.X, rect.Y, 4, rect.Height));
        ctx.DrawText(ft, new Point(rect.X + 6, rect.Y + 3));
    }

    private void TextBadge(DrawingContext ctx, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12.5, Res("OverlayTextBrush", Brushes.White));
        ctx.DrawRectangle(Res("OverlayBrush", Brushes.Black), null, new Rect(at.X, at.Y, ft.Width + 8, ft.Height + 6), 4, 4);
        ctx.DrawText(ft, new Point(at.X + 4, at.Y + 3));
    }

    /// <summary>
    /// Grey |A − B| (largest channel difference) × gain, at A's size; B is sampled to A's size when they differ.
    /// Cached until the pictures or the gain change.
    /// </summary>
    private WriteableBitmap? DiffImage()
    {
        if (Current >= _images.Length || WipeOther >= _images.Length)
            return null;
        var a = _images[Current];
        var b = _images[WipeOther];
        if (a == null || b == null)
            return null;
        if (_diff != null && ReferenceEquals(_diffKey.A, a) && ReferenceEquals(_diffKey.B, b) && Math.Abs(_diffKey.Gain - DiffGain) < 1e-9)
            return _diff;
        var pa = Pixels(a);
        var pb = Pixels(b);
        int wa = a.PixelSize.Width, ha = a.PixelSize.Height, wb = b.PixelSize.Width, hb = b.PixelSize.Height;
        _diff?.Dispose();
        _diff = new WriteableBitmap(a.PixelSize, new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        double sum = 0;
        long changed = 0;
        using (var fb = _diff.Lock())
        {
            var outRow = new byte[wa * 4];
            for (var y = 0; y < ha; y++)
            {
                var yb = (int)((y + 0.5) * hb / ha);
                for (var x = 0; x < wa; x++)
                {
                    var xb = (int)((x + 0.5) * wb / wa);
                    var ia = (y * wa + x) * 4;
                    var ib = (yb * wb + xb) * 4;
                    var d = Math.Max(Math.Abs(pa[ia] - pb[ib]), Math.Max(Math.Abs(pa[ia + 1] - pb[ib + 1]), Math.Abs(pa[ia + 2] - pb[ib + 2])));
                    sum += d;
                    if (d > 6)
                        changed++;
                    var v = (byte)Math.Min(255, d * DiffGain);
                    outRow[x * 4] = v;
                    outRow[x * 4 + 1] = v;
                    outRow[x * 4 + 2] = v;
                    outRow[x * 4 + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(outRow, 0, fb.Address + y * fb.RowBytes, outRow.Length);
            }
        }
        DiffStats = (sum / ((double)wa * ha), (double)changed / ((double)wa * ha));
        _diffKey = (a, b, DiffGain);
        return _diff;
    }

    /// <summary>32-bit pixels of a bitmap (channel order does not matter for a max-channel difference).</summary>
    private static byte[] Pixels(Bitmap bmp)
    {
        var w = bmp.PixelSize.Width;
        var h = bmp.PixelSize.Height;
        var buf = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, w * 4);
        }
        finally
        {
            handle.Free();
        }
        return buf;
    }

    private void DrawCentered(DrawingContext ctx, Rect r, string text)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 14, Res("MutedTextBrush", Brushes.Gray));
        ctx.DrawText(ft, new Point(r.Center.X - ft.Width / 2, r.Center.Y - ft.Height / 2));
    }

    private IBrush Res(string key, IBrush fallback) => this.ThemeBrush(key, fallback);

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

    // ---------- input ----------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ReferenceSize.Width <= 0)
            return;
        var cur = EffectiveScale;
        var steps = new[] { 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16 };
        double next;
        if (e.Delta.Y > 0)
            next = steps.FirstOrDefault(z => z > cur * 1.01, 16);
        else
            next = steps.LastOrDefault(z => z < cur * 0.99, 0.25);
        // zooming out past "fit" snaps back to fit
        var cell = Cells(Bounds)[0];
        var fit = Math.Min(cell.Width / ReferenceSize.Width, cell.Height / ReferenceSize.Height);
        SetZoom(next <= fit ? 0 : next, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (e.ClickCount == 2)
        {
            SetZoom(_zoom == 0 ? 1 : 0, e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (p.Properties.IsLeftButtonPressed)
        {
            _dragStart = e.GetPosition(this);
            _dragCenter = _center;
            e.Pointer.Capture(this);
            Focus();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_dragStart is { } start && _zoom > 0)
        {
            var s = EffectiveScale;
            _center = new Point(_dragCenter.X - (pos.X - start.X) / s, _dragCenter.Y - (pos.Y - start.Y) / s);
            ClampCenter();
            InvalidateVisual();
            return;
        }
        // the wipe line follows the pointer (while not panning a zoomed picture)
        if (Mode == CompareMode.Wipe && Bounds.Width > 0)
        {
            _wipe = Math.Clamp(pos.X / Bounds.Width, 0, 1);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
}
