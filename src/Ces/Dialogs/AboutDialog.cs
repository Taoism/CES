using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ces.Dialogs;

/// <summary>About box: what CES does and which tools it found.</summary>
public sealed class AboutDialog : DialogWindow
{
    private const string Credits = """
        CES — Compare Enc Spot
        frame-exact screenshots · grade & framing · audio · subtitles
        PSNR · SSIM · VMAF · XPSNR
        encoder spotting · MP3 frames & LAME tags · FLAC CRC & MD5

        Built on ffmpeg, ffprobe and MediaInfo
        Spot mode reads the streams itself, in the spirit of EncSpot
        """;

    public AboutDialog(string details) : base("About CES", 560)
    {
        var text = new TextBlock
        {
            Text = Credits,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 22,
        }.Themed(TextBlock.ForegroundProperty, "AboutTextBrush");
        var credits = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 14),
            Child = text,
        }.Themed(Border.BackgroundProperty, "AboutCreditsBrush").Themed(Border.BorderBrushProperty, "AboutCreditsEdgeBrush");
        SetBody(Column(12, credits, new SelectableTextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "muted" } }), "Close", null);
    }
}
