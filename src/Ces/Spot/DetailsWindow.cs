using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Ces.Spot;
using Ces.Spot.Flac;
using Ces.Spot.Mp3;

namespace Ces.SpotUi;

/// <summary>Per-file window: details table, flags and graphs (EncSpot's file window).</summary>
public sealed class DetailsWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly FileRow _row;
    private readonly AppSettings _settings;
    private readonly Action<FileRow> _reanalyze;
    private readonly ContentControl _body = new();
    private int _selectedTab;

    public DetailsWindow(FileRow row, AppSettings settings, Action<FileRow> reanalyze)
    {
        _row = row;
        _settings = settings;
        _reanalyze = reanalyze;
        Icon = MainWindow.AppIcon;
        Width = Math.Max(600, settings.DetailsWidth);
        Height = Math.Max(420, settings.DetailsHeight);
        MinWidth = 600;
        MinHeight = 420;
        if (settings.DetailsX is int dx && settings.DetailsY is int dy)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(dx, dy);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(MakeButton("Go to folder", () => SpotView.ShowInFileManager(_row.Path)));
        buttons.Children.Add(MakeButton("Refresh", () => _reanalyze(_row)));
        buttons.Children.Add(MakeButton("Copy info", CopyInfo));
        buttons.Children.Add(MakeButton("Save…", async () =>
        {
            if (_row.Result is { } r)
            {
                await SpotDetailsFile.SaveAsync(this, [r]);
            }
        }));
        var close = MakeButton("Close", Close);
        close.IsCancel = true;
        close.Classes.Add("accent");
        buttons.Children.Add(close);
        var footer = new Border { Classes = { "footer" }, Padding = new Thickness(12, 10), Child = buttons };
        DockPanel.SetDock(footer, Dock.Bottom);
        var dock = new DockPanel();
        dock.Children.Add(footer);
        dock.Children.Add(_body);
        Content = dock;

        row.PropertyChanged += OnRowChanged;
        Closing += (_, _) =>
        {
            row.PropertyChanged -= OnRowChanged;
            if (WindowState == WindowState.Normal)
            {
                _settings.DetailsX = Position.X;
                _settings.DetailsY = Position.Y;
                _settings.DetailsWidth = Width;
                _settings.DetailsHeight = Height;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Build();
    }

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button MakeButton(string text, Func<Task> click)
    {
        var b = new Button { Content = text, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Click += async (_, _) => await click();
        return b;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Build);

    private async void CopyInfo()
    {
        if (_row.Result is { } r && Clipboard is not null)
        {
            await Clipboard.SetTextAsync(SpotReport.Text(r));
        }
    }

    private void Build()
    {
        var r = _row.Result;
        Title = _row.Path;
        if (r is null)
        {
            _body.Content = new TextBlock { Text = "analyzing…", Margin = new Thickness(20) };
            return;
        }
        var tabs = new TabControl { Margin = new Thickness(10, 6, 10, 0) };
        tabs.Items.Add(Tab("Overview", Overview(r)));
        if (r.Mp3 is { } mp3)
        {
            AddMp3Tabs(tabs, mp3, r);
        }
        else if (r.Flac is { } flac && flac.Frames > 0)
        {
            AddFlacTabs(tabs, flac, r);
        }
        tabs.SelectedIndex = Math.Min(_selectedTab, tabs.ItemCount - 1);
        tabs.SelectionChanged += (_, _) => _selectedTab = tabs.SelectedIndex;

        var header = new StackPanel { Margin = new Thickness(14, 10, 14, 0), Spacing = 2 };
        header.Children.Add(new TextBlock { Text = _row.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string summary = r.Error is not null
            ? "error: " + r.Error
            : string.Create(Inv, $"{r.Format} · {r.Encoder} ({r.Confidence}) · {r.BitrateKbps:0} kbps · {r.SampleRate} Hz {_row.ModeText} · {Fmt.Duration(r.DurationSeconds)}");
        header.Children.Add(new TextBlock { Text = summary, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis });
        foreach (var p in r.Problems)
        {
            header.Children.Add(new TextBlock { Text = "⚠ " + p, TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, "ProblemBrush"));
        }
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(tabs);
        _body.Content = dock;
    }

    /// <summary>Average spectrum of the decoded audio, the evidence for the lossy-source / upsampling verdict.</summary>
    internal static void AddSpectrumTab(TabControl tabs, double[] spectrum, int rate, double cutoffHz, string verdict)
    {
        if (spectrum.Length < 2 || rate <= 0)
        {
            return;
        }
        var n = spectrum.Length;
        var hz = Enumerable.Range(0, n).Select(k => k * (rate / 2.0) / (n - 1)).ToArray();
        var cut = double.IsNaN(cutoffHz) || cutoffHz >= rate / 2.0 * 0.97 ? "content up to Nyquist" : string.Create(Inv, $"content up to {cutoffHz / 1000:0.0} kHz");
        var chart = new Ces.Controls.LineChart
        {
            Title = $"Average spectrum (dB) · {cut}{(verdict.Length > 0 && verdict != "ok" ? " · " + verdict : "")}",
            XFormat = v => string.Create(Inv, $"{v / 1000:0.#} kHz"),
            YFormat = v => string.Create(Inv, $"{v:0} dB"),
            Margin = new Thickness(8),
        };
        chart.SetSeries([new Ces.Controls.ChartSeries("spectrum", MainWindow.Palette[0], hz, spectrum)]);
        tabs.Items.Add(Tab("Spectrum", chart));
    }

    private static TabItem Tab(string header, Control content) => new() { Header = header, Content = content };

    private static Control Overview(AnalysisResult r)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            ItemsSource = r.Details,
            CanUserSortColumns = false,
            Margin = new Thickness(0, 6, 0, 0),
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding(nameof(InfoItem.Group)), Width = new DataGridLength(90) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Attribute", Binding = new Binding(nameof(InfoItem.Name)), Width = new DataGridLength(190) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(InfoItem.Value)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var flags = new StackPanel { Spacing = 0, Margin = new Thickness(12, 6, 0, 0), MinWidth = 180 };
        flags.Children.Add(new TextBlock { Text = "Info", Classes = { "header" } });
        foreach (var (name, on) in Flags(r))
        {
            flags.Children.Add(new CheckBox { Content = name, IsChecked = on, IsHitTestVisible = false, Focusable = false, MinHeight = 26, Padding = new Thickness(6, 0, 0, 0) });
        }
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(grid);
        Grid.SetColumn(flags, 1);
        g.Children.Add(new ScrollViewer { Content = flags, [Grid.ColumnProperty] = 1 });
        return g;
    }

    private static IEnumerable<(string, bool)> Flags(AnalysisResult r)
    {
        if (r.Mp3 is { } a)
        {
            yield return ("Xing / Info header", a.Xing is not null);
            yield return ("LAME tag", a.Xing?.Lame is not null);
            yield return ("VBRI header", a.Vbri is not null);
            yield return ("VBR", a.IsVbr);
            yield return ("Padding", a.PaddedFrames > 0);
            yield return ("CRC protected", a.CrcFrames > 0);
            yield return ("Original", a.OriginalFrames > 0);
            yield return ("Copyright", a.CopyrightFrames > 0);
            yield return ("Private bit", a.PrivateBitFrames > 0);
            yield return ("Mid/side stereo", a.MidSideFrames > 0);
            yield return ("Intensity stereo", a.IntensityFrames > 0);
            yield return ("Short blocks", a.Layer3.ShortBlocks > 0);
            yield return ("Mixed blocks", a.Layer3.MixedBlocks > 0);
            yield return ("scalefac_scale", a.Layer3.ScalefacScale > 0);
            yield return ("scfsi", a.Layer3.ScfsiFrames > 0);
            yield return ("Sync errors", a.SyncErrors > 0);
            yield return ("Bad last frame", a.BadLastFrame);
        }
        else if (r.Flac is { } f)
        {
            yield return ("MD5 stored", !f.Md5Unset);
            yield return ("MD5 verified", f.Md5Ok == true);
            yield return ("Variable block size", f.VariableBlockSize);
            yield return ("Seek table", f.SeekPoints > 0);
            yield return ("Picture", f.Pictures > 0);
            yield return ("Cue sheet", f.HasCueSheet);
            yield return ("Padding", f.PaddingBytes > 0);
            yield return ("Stereo decorrelation", f.ChannelAssignments.Any(kv => kv.Key != FlacChannelAssignment.Independent));
            yield return ("Wasted bits", f.WastedBitsSubframes > 0);
            yield return ("Frame CRC errors", f.FrameCrcErrors > 0);
            yield return ("Sync errors", f.SyncErrors > 0);
        }
    }

    private static void AddMp3Tabs(TabControl tabs, Mp3Analysis a, AnalysisResult r)
    {
        tabs.Items.Add(Tab("Bitrate histogram", new BarChart
        {
            Title = "Frames per bitrate (kbps)",
            Bars = a.BitrateHistogram.Select(kv => new KeyValuePair<string, double>(kv.Key.ToString(Inv), kv.Value)).ToList(),
        }));
        tabs.Items.Add(Tab("Bitrate graph", new LineChart
        {
            Title = "Bitrate per frame",
            Values = a.FrameBitrates,
            Duration = r.DurationSeconds,
            Unit = "kbps",
        }));
        if (a.FirstHeader.Layer != 3)
        {
            return;
        }
        tabs.Items.Add(Tab("Reservoir", new LineChart
        {
            Title = string.Create(Inv, $"Bit reservoir use per frame (main_data_begin, bytes) — max {a.Layer3.MaxReservoir}"),
            Values = a.FrameReservoir,
            Duration = r.DurationSeconds,
            Unit = "bytes",
        }));
        var l3 = a.Layer3;
        tabs.Items.Add(Tab("Block types", new BarChart
        {
            Title = "Granules per block type",
            Bars =
            [
                new("long", l3.LongBlocks),
                new("start", l3.StartBlocks),
                new("short", l3.ShortBlocks),
                new("stop", l3.StopBlocks),
                new("mixed", l3.MixedBlocks),
            ],
        }));
        tabs.Items.Add(Tab("Big values", new BarChart
        {
            Title = "Granules per big_values (16-wide buckets, max 288)",
            Bars = l3.BigValuesHistogram.Select((n, i) => new KeyValuePair<string, double>((i * 16).ToString(Inv), n)).ToList(),
            ShowPercent = false,
        }));
        var stereo = new List<KeyValuePair<string, double>>();
        foreach (var kv in a.ModeCounts.OrderBy(k => k.Key))
        {
            stereo.Add(new(kv.Key switch
            {
                ChannelMode.Stereo => "stereo",
                ChannelMode.JointStereo => "joint",
                ChannelMode.DualChannel => "dual",
                _ => "mono",
            }, kv.Value));
        }
        if (a.ModeCounts.ContainsKey(ChannelMode.JointStereo))
        {
            stereo.Add(new("mid/side", a.MidSideFrames));
            stereo.Add(new("intensity", a.IntensityFrames));
        }
        tabs.Items.Add(Tab("Stereo", new BarChart { Title = "Frames per channel mode", Bars = stereo }));
    }

    private static void AddFlacTabs(TabControl tabs, FlacAnalysis f, AnalysisResult r)
    {
        AddSpectrumTab(tabs, f.Spectrum, f.SampleRate, f.CutoffHz, r.SourceCheck);
        tabs.Items.Add(Tab("Bitrate graph", new LineChart
        {
            Title = "Bitrate per frame",
            Values = f.FrameBitrates,
            Duration = r.DurationSeconds,
            Unit = "kbps",
        }));
        var types = new List<KeyValuePair<string, double>>
        {
            new("constant", f.ConstantSubframes),
            new("verbatim", f.VerbatimSubframes),
        };
        for (int i = 0; i < f.FixedOrders.Length; i++)
        {
            types.Add(new($"fixed {i}", f.FixedOrders[i]));
        }
        types.Add(new("LPC", f.LpcOrders.Sum()));
        tabs.Items.Add(Tab("Subframes", new BarChart { Title = "Subframes per predictor type", Bars = types }));
        int maxOrder = Math.Max(1, f.MaxLpcOrderUsed);
        tabs.Items.Add(Tab("LPC orders", new BarChart
        {
            Title = "LPC subframes per predictor order",
            Bars = Enumerable.Range(1, maxOrder).Select(o => new KeyValuePair<string, double>(o.ToString(Inv), f.LpcOrders[o])).ToList(),
        }));
        tabs.Items.Add(Tab("Block sizes", new BarChart
        {
            Title = "Frames per block size (samples)",
            Bars = f.BlockSizeHistogram.Select(kv => new KeyValuePair<string, double>(kv.Key.ToString(Inv), kv.Value)).ToList(),
        }));
        if (f.Channels == 2)
        {
            tabs.Items.Add(Tab("Stereo", new BarChart
            {
                Title = "Frames per stereo decorrelation mode",
                Bars =
                [
                    new("independent", f.ChannelAssignments.GetValueOrDefault(FlacChannelAssignment.Independent)),
                    new("left/side", f.ChannelAssignments.GetValueOrDefault(FlacChannelAssignment.LeftSide)),
                    new("side/right", f.ChannelAssignments.GetValueOrDefault(FlacChannelAssignment.RightSide)),
                    new("mid/side", f.ChannelAssignments.GetValueOrDefault(FlacChannelAssignment.MidSide)),
                ],
            }));
        }
    }
}
