using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ces.Dialogs;

/// <summary>About box with the credits scrolling past.</summary>
public sealed class AboutDialog : DialogWindow
{
    private const string Credits = """

        CES — Compare Enc Spot
        (NothingElseCompares2u + EncoderSpot)
        frame-exact screenshots · grade & framing · audio · subtitles
        PSNR · SSIM · VMAF · XPSNR
        encoder spotting · MP3 frames & LAME tags · FLAC CRC & MD5

        Built on ffmpeg, ffprobe and MediaInfo
        Spot mode reads the streams itself, in the spirit of EncSpot
        Thanks for watching — now go compare something.
        """;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly TextBlock _text;
    private readonly Canvas _canvas;
    private double _y = double.NaN;
    private bool _paused;

    public AboutDialog(string details) : base("About CES", 560)
    {
        _text = new TextBlock
        {
            Text = Credits,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = 500,
            FontSize = 14,
            LineHeight = 22,
            Foreground = Brushes.Gainsboro,
        };
        _canvas = new Canvas { Height = 320, Width = 500, ClipToBounds = true, Background = Brushes.Transparent };
        _canvas.Children.Add(_text);
        ToolTip.SetTip(_canvas, "Hover to pause");
        _canvas.PointerEntered += (_, _) => _paused = true;
        _canvas.PointerExited += (_, _) => _paused = false;

        var credits = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF101113")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF2C2E33")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = _canvas,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        SetBody(Column(12, credits, new SelectableTextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray }), "Close", null);

        _timer.Tick += (_, _) => Step();
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Step()
    {
        if (_paused)
            return;
        var h = _text.Bounds.Height;
        if (double.IsNaN(_y) || h <= 0)
            _y = _canvas.Height;
        _y -= 0.6;
        // loop: once the last line has left the top, start again from the bottom
        if (h > 0 && _y < -h)
            _y = _canvas.Height;
        Canvas.SetTop(_text, _y);
    }
}
