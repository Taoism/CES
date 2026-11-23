using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Ces.Report;

namespace Ces.Dialogs;

/// <summary>
/// An exported report (.zip / .tar.gz) read straight from the archive: the plain-words summary, the technical text and
/// the screenshots, with the HTML version one click away in the browser.
/// </summary>
public sealed partial class ReportViewer : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,monospace");
    private readonly ReportBundle _bundle;
    private readonly List<Bitmap> _bitmaps = [];

    [GeneratedRegex(@"^images/(?:.+-)?shot-(\d+)-(\d+)\.(png|jpg|jpeg|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShotName();

    private readonly Func<string, string, Task>? _openComparison;

    private ReportViewer(ReportBundle bundle, Func<string, string, Task>? openComparison)
    {
        _bundle = bundle;
        _openComparison = openComparison;
        Title = ReportArchive.BaseName(bundle.Path) + " · CES report";
        Icon = MainWindow.AppIcon;
        Width = 1100;
        Height = 780;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var tabs = new TabControl { Margin = new Thickness(8, 4, 8, 0) };
        if (bundle.Notes is { } nf && bundle.ReadText(nf) is { } notes)
            tabs.Items.Add(new TabItem { Header = "Notes", Content = Scroll(Text(notes, false, 14), false) });
        if (bundle.Summary is { } sf && bundle.ReadText(sf) is { } plain)
            tabs.Items.Add(new TabItem { Header = "In plain words", Content = Scroll(Text(plain, false, 14), false) });
        if (bundle.Text is { } tf && bundle.ReadText(tf) is { } full)
            tabs.Items.Add(new TabItem { Header = "Technical details", Content = Scroll(Text(full, true, 12), true) });
        if (Screenshots() is { } shots)
            tabs.Items.Add(new TabItem { Header = "Screenshots", Content = shots });
        foreach (var extra in bundle.Files.Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !f.Contains('/')
                                                      && f != bundle.Summary && f != bundle.Text && f != bundle.Notes))
            tabs.Items.Add(new TabItem { Header = extra, Content = Scroll(Text(bundle.ReadText(extra) ?? "", true, 12), true) });
        if (tabs.Items.Count == 0)
            tabs.Items.Add(new TabItem { Header = "Report", Content = new TextBlock { Text = "This archive has no summary or text report.", Margin = new Thickness(12), Classes = { "muted" } } });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var where = new TextBlock { Text = bundle.Path, Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (bundle.Session != null && _openComparison != null)
        {
            var compare = new Button { Content = "Open comparison" };
            ToolTip.SetTip(compare, "Open the saved results in this report in Compare: sources, sync, screenshots and every measurement (needs the sources at their paths)");
            compare.Click += async (_, _) => await OpenComparisonAsync();
            buttons.Children.Add(compare);
        }
        if (bundle.Has(ReportArchive.HtmlName))
        {
            var browser = new Button { Content = "Open in browser" };
            ToolTip.SetTip(browser, "Unpack the HTML report to a temporary folder and open it in the web browser");
            browser.Click += async (_, _) => await OpenInBrowserAsync();
            buttons.Children.Add(browser);
        }
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Classes.Add("accent");
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(where);
        var footer = new Border { Classes = { "footer" }, Padding = new Thickness(12, 10), Child = row };
        DockPanel.SetDock(footer, Dock.Bottom);
        var dock = new DockPanel();
        dock.Children.Add(footer);
        dock.Children.Add(tabs);
        Content = dock;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Closed += (_, _) =>
        {
            foreach (var b in _bitmaps)
                b.Dispose();
            _bundle.Dispose();
        };
    }

    /// <summary>Opens <paramref name="path"/> in a viewer window; false (with a message) when it is not a readable report.</summary>
    public static async Task<bool> ShowAsync(Window owner, string path, Func<string, string, Task>? openComparison = null)
    {
        ReportBundle bundle;
        try
        {
            bundle = await Task.Run(() => ReportArchive.Open(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or FormatException)
        {
            await MessageBox.ShowAsync(owner, "Could not open the report:\n" + ex.Message, MessageKind.Error);
            return false;
        }
        if (bundle.Summary == null && bundle.Text == null && !bundle.Has(ReportArchive.HtmlName))
        {
            bundle.Dispose();
            await MessageBox.ShowAsync(owner, "That archive is not a CES report (no summary.txt, report.txt or index.html in it).", MessageKind.Info);
            return false;
        }
        var w = new ReportViewer(bundle, openComparison);
        // centred on the owner's screen (CenterOwner can land on the primary monitor)
        var scale = owner.RenderScaling;
        var ow = owner.FrameSize ?? owner.ClientSize;
        w.Position = new PixelPoint(owner.Position.X + (int)Math.Max(0, (ow.Width - w.Width) * scale / 2), owner.Position.Y + (int)Math.Max(0, (ow.Height - w.Height) * scale / 3));
        w.Show(owner);
        return true;
    }

    /// <summary>True when <paramref name="path"/> looks like an exported report (by name; the contents are checked on opening).</summary>
    public static bool LooksLikeReport(string path) => ReportArchive.KindOf(path) != null && File.Exists(path);

    private static SelectableTextBlock Text(string text, bool mono, double size)
    {
        var t = new SelectableTextBlock { Text = text, FontSize = size, Margin = new Thickness(14, 10), TextWrapping = mono ? TextWrapping.NoWrap : TextWrapping.Wrap };
        if (mono)
            t.FontFamily = Mono;
        else
            t.LineHeight = size * 1.5;
        return t;
    }

    private static Border Scroll(Control content, bool horizontal) => new()
    {
        Classes = { "panel" },
        Margin = new Thickness(0, 8, 0, 8),
        Child = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        },
    };

    /// <summary>The screenshots row by row (shot-RR-S.png), each row's sources side by side, then any other pictures.</summary>
    private Border? Screenshots()
    {
        var images = _bundle.Files.Where(f => f.StartsWith("images/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (images.Count == 0)
            return null;
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(12, 10) };
        var rows = images.Select(f => (File: f, M: ShotName().Match(f))).Where(x => x.M.Success)
            .GroupBy(x => int.Parse(x.M.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).OrderBy(g => g.Key);
        foreach (var g in rows)
        {
            var shots = g.OrderBy(x => int.Parse(x.M.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var grid = new UniformGrid { Columns = Math.Min(shots.Count, 3) };
            foreach (var (file, m) in shots)
                grid.Children.Add(Picture(file, "Source " + m.Groups[2].Value));
            panel.Children.Add(new TextBlock { Text = "Screenshot " + g.Key, FontWeight = FontWeight.SemiBold });
            panel.Children.Add(grid);
        }
        var others = images.Where(f => !ShotName().IsMatch(f)).ToList();
        if (others.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Other pictures", FontWeight = FontWeight.SemiBold });
            var grid = new UniformGrid { Columns = 2 };
            foreach (var f in others)
                grid.Children.Add(Picture(f, Path.GetFileNameWithoutExtension(f)));
            panel.Children.Add(grid);
        }
        return new Border { Classes = { "panel" }, Margin = new Thickness(0, 8, 0, 8), Child = new ScrollViewer { Content = panel } };
    }

    private StackPanel Picture(string file, string caption)
    {
        var box = new StackPanel { Spacing = 3, Margin = new Thickness(3) };
        try
        {
            using var ms = new MemoryStream(_bundle.Read(file) ?? []);
            var bmp = Bitmap.DecodeToWidth(ms, 1280);
            _bitmaps.Add(bmp);
            box.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            box.Children.Add(new TextBlock { Text = "(could not read " + file + ")", Classes = { "muted" } });
        }
        box.Children.Add(new TextBlock { Text = caption, Classes = { "muted" }, FontSize = 12 });
        return box;
    }

    /// <summary>Unpacks the saved comparison to a temporary folder and hands it to the main window.</summary>
    private async Task OpenComparisonAsync()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ces-reports", MainWindow.SafeName(ReportArchive.BaseName(_bundle.Path)));
            var file = _bundle.ExtractFile(_bundle.Session!, dir);
            var archive = _bundle.Path;
            Close();
            await _openComparison!(file, archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await MessageBox.ShowAsync(this, "Could not open the comparison:\n" + ex.Message, MessageKind.Error);
        }
    }

    private async Task OpenInBrowserAsync()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(_bundle.Path).Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            var dir = Path.Combine(Path.GetTempPath(), "ces-reports", MainWindow.SafeName(ReportArchive.BaseName(_bundle.Path)) + "-" + stamp[^8..]);
            var html = Path.Combine(dir, ReportArchive.HtmlName);
            if (!File.Exists(html))
                html = await Task.Run(() => _bundle.ExtractTo(dir)) ?? throw new IOException("index.html is missing from the archive.");
            Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            await MessageBox.ShowAsync(this, "Could not open the HTML report:\n" + ex.Message, MessageKind.Error);
        }
    }
}
