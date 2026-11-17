using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ces.Analysis;
using Ces.Compare;
using Ces.Controls;
using Ces.Dialogs;
using Ces.Model;
using Ces.Report;

namespace Ces;

public sealed partial class MainWindow
{
    private readonly ComboBox _qRef = new() { MinWidth = 220 };
    private readonly CheckBox _qPsnr = new() { Content = "PSNR" };
    private readonly CheckBox _qSsim = new() { Content = "SSIM" };
    private readonly CheckBox _qVmaf = new() { Content = "VMAF" };
    private readonly CheckBox _qXpsnr = new() { Content = "XPSNR" };
    private readonly TextBox _qStart = new() { PlaceholderText = "middle", Width = 110 };
    private readonly TextBox _qLength = new() { Text = "60", Width = 80 };
    private readonly NumericUpDown _qSub = new() { Minimum = 1, Maximum = 10, Value = 1, FormatString = "0", Width = 100 };
    private readonly CheckBox _qDeint = new() { Content = "Deinterlace reference" };
    private readonly StackPanel _qResults = new() { Spacing = 10 };
    private readonly LineChart _qChart = new() { Title = "Per frame (click to screenshot that moment)", Height = 260 };
    private readonly List<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> _metrics = [];
    private readonly NumericUpDown _qWindows = new() { Minimum = 1, Maximum = 30, Value = 5, FormatString = "0", Width = 100 };
    private readonly StackPanel _rankPanel = new() { Spacing = 6 };
    private List<RankRow> _ranking = [];

    private Control BuildQualityTab()
    {
        _qPsnr.IsChecked = _settings.MetricPsnr;
        _qSsim.IsChecked = _settings.MetricSsim;
        _qVmaf.IsChecked = _settings.MetricVmaf;
        _qXpsnr.IsChecked = _settings.MetricXpsnr;
        _qLength.Text = _settings.MetricLength.ToString("0.###", CultureInfo.InvariantCulture);
        _qSub.Value = _settings.MetricSubsample;
        Avalonia.Automation.AutomationProperties.SetName(_qStart, "Metric start");
        Avalonia.Automation.AutomationProperties.SetName(_qLength, "Metric length");
        Avalonia.Automation.AutomationProperties.SetName(_qSub, "Every n-th frame");
        Avalonia.Automation.AutomationProperties.SetName(_qRef, "Reference source");
        ToolTip.SetTip(_qStart, "Start on the reference timeline (empty = centred on the middle). Use 0 with a long length for the whole title.");
        ToolTip.SetTip(_qSub, "Compare every n-th frame only (faster).");
        var run = new Button { Content = "Measure", Classes = { "accent" } };
        run.Click += async (_, _) => await RunMetricsAsync();
        var rank = new Button { Content = "Rank releases", Classes = { "accent" } };
        ToolTip.SetTip(rank, "Which release is best: syncs every source that is not synced yet, then measures each against the reference in several windows across the title (Length s each) and ranks them.");
        rank.Click += async (_, _) => await RankReleasesAsync();
        Avalonia.Automation.AutomationProperties.SetName(_qWindows, "Rank windows");
        ToolTip.SetTip(_qWindows, "Number of windows spread over the title for ranking.");
        _busyDisabled.Add(rank);
        var worst = new Button { Content = "Screenshot worst frames" };
        worst.Click += async (_, _) => await ShotWorstAsync();
        _busyDisabled.AddRange([run, worst]);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        bar.Children.AddRange([Muted("Reference", 13), _qRef, _qPsnr, _qSsim, _qVmaf, _qXpsnr, Muted("Start", 13), _qStart, Muted("Length s", 13), _qLength,
            Muted("Every n-th", 13), _qSub, _qDeint, run, worst, Muted("Windows", 13), _qWindows, rank]);
        foreach (var c in bar.Children.OfType<Control>())
            c.VerticalAlignment = VerticalAlignment.Center;
        _qChart.XFormat = v => Format.Time(v, false);
        _qChart.XPicked += async (_, t) => await AddShotTimesAsync([(t, "picked on metrics chart")]);
        var note = Muted("Every other source is compared with the reference at its offset. Different sizes are scaled to the reference; black bars the encode cropped are cropped from the reference automatically.");
        var content = new StackPanel { Spacing = 10, Children = { note, _rankPanel, _qResults, _qChart } };
        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = content, Margin = new Thickness(0, 8, 0, 0) });
        RefreshQualitySetup();
        return root;
    }

    private void RefreshQualitySetup()
    {
        var sel = _qRef.SelectedIndex;
        _qRef.ItemsSource = _items.Select((it, i) => $"{i + 1}. {it.Name}").ToList();
        _qRef.SelectedIndex = sel >= 0 && sel < _items.Count ? sel : _items.Count > 0 ? 0 : -1;
        _metrics.RemoveAll(m => !_items.Contains(m.Reference) || !_items.Contains(m.Distorted));
        RefreshMetricResults();
    }

    private async Task RunMetricsAsync()
    {
        if (_tools == null || _items.Count < 2)
        {
            await MessageBox.ShowAsync(this, "Add at least two sources (a reference and something to compare with it).", MessageKind.Info);
            return;
        }
        var m = Metric.None;
        if (_qPsnr.IsChecked == true)
            m |= Metric.Psnr;
        if (_qSsim.IsChecked == true)
            m |= Metric.Ssim;
        if (_qVmaf.IsChecked == true)
            m |= Metric.Vmaf;
        if (_qXpsnr.IsChecked == true)
            m |= Metric.Xpsnr;
        if (m == Metric.None)
        {
            await MessageBox.ShowAsync(this, "Tick at least one metric.", MessageKind.Info);
            return;
        }
        if (!Format.TryParseTime(_qLength.Text, out var length) || length <= 0)
        {
            await MessageBox.ShowAsync(this, "Length must be a time in seconds (or h:mm:ss).", MessageKind.Warning);
            return;
        }
        var start = double.NaN;
        if (!string.IsNullOrWhiteSpace(_qStart.Text) && !Format.TryParseTime(_qStart.Text, out start))
        {
            await MessageBox.ShowAsync(this, "Start must be a time (h:mm:ss.fff or seconds) or empty.", MessageKind.Warning);
            return;
        }
        _settings.MetricPsnr = m.HasFlag(Metric.Psnr);
        _settings.MetricSsim = m.HasFlag(Metric.Ssim);
        _settings.MetricVmaf = m.HasFlag(Metric.Vmaf);
        _settings.MetricXpsnr = m.HasFlag(Metric.Xpsnr);
        _settings.MetricLength = length;
        _settings.MetricSubsample = (int)(_qSub.Value ?? 1);
        var refItem = _items[Math.Max(0, _qRef.SelectedIndex)];
        var opt = new MetricOptions
        {
            Metrics = m,
            Length = length,
            Subsample = _settings.MetricSubsample,
            DeinterlaceReference = _qDeint.IsChecked == true,
        };
        _metrics.Clear();
        RefreshMetricResults();
        await RunJobAsync("Quality metrics", async ct =>
        {
            var qm = new QualityMetrics(_tools, _temp);
            var others = _items.Where(i => i != refItem && i.Title.Info?.Video != null).ToList();
            var k = 0;
            foreach (var d in others)
            {
                // ranges are on the shared timeline: the reference itself may have an offset
                opt.Start = double.IsNaN(start) ? double.NaN : start + refItem.Offset;
                var kk = k;
                var prog = new Progress<(double Fraction, string Text)>(p =>
                {
                    SetProgress((kk + p.Fraction) / others.Count);
                    SetStatus($"{d.Name} vs {refItem.Name}: {p.Text}");
                });
                // offset for the measured window (its section, when the source is a different edit)
                var t0 = double.IsNaN(opt.Start) ? refItem.TimelineTime(refItem.Title.Duration / 2) : refItem.TimelineTime(opt.Start);
                var off = d.SourceTime(t0) is { } st ? st - (refItem.SourceTime(t0) ?? t0) * d.Speed : d.Offset - refItem.Offset;
                var res = await qm.RunAsync(refItem.Title, d.Title, off, opt, prog, d.Speed, ct);
                for (var i = 0; i < res.FrameTimes.Count; i++)
                    res.FrameTimes[i] = refItem.TimelineTime(res.FrameTimes[i]);
                _metrics.Add((refItem, d, res));
                RefreshMetricResults();
                k++;
            }
        });
        RefreshMetricResults();
    }

    private void RefreshMetricResults()
    {
        RefreshRanking();
        _qResults.Children.Clear();
        var series = new List<ChartSeries>();
        foreach (var (rf, ds, res) in _metrics)
        {
            var di = _items.IndexOf(ds);
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(ChipLabel(di, $"{ds.Name}  vs  {rf.Name}"));
            panel.Children.Add(Muted($"{res.Frames} frames from {Format.Time(rf.TimelineTime(res.Start))} · compared at {res.ReferenceGeometry}{(res.Notes.Length > 0 ? " · " + res.Notes : "")}"));
            var table = new Grid { ColumnDefinitions = new ColumnDefinitions("80,110,110,110,110"), Margin = new Thickness(0, 4, 0, 0) };
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] head = ["Metric", "Mean", "1 % low", "Min", "Harmonic"];
            for (var c = 0; c < head.Length; c++)
            {
                var h = new TextBlock { Text = head[c], Classes = { "muted" }, FontSize = 12 };
                Grid.SetColumn(h, c);
                table.Children.Add(h);
            }
            var r = 1;
            foreach (var (m, s) in res.Series)
            {
                var f = m == Metric.Ssim ? "0.0000" : "0.00";
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                string[] vals = [s.Name, Format.Num(s.Mean, f), Format.Num(s.Low1, f), Format.Num(s.Min, f), m == Metric.Vmaf ? Format.Num(s.HarmonicMean, f) : ""];
                for (var c = 0; c < vals.Length; c++)
                {
                    var tb = new TextBlock { Text = vals[c], FontWeight = c == 1 ? FontWeight.SemiBold : FontWeight.Normal };
                    Grid.SetRow(tb, r);
                    Grid.SetColumn(tb, c);
                    table.Children.Add(tb);
                }
                r++;
            }
            panel.Children.Add(table);
            var worst = res.Worst(6);
            if (worst.Count > 0)
                panel.Children.Add(Muted("Worst frames: " + string.Join(",  ", worst.Select(w => $"{Format.Time(res.TimeOf(w.Frame))} ({w.Value:0.###})"))));
            _qResults.Children.Add(new Border { Classes = { "card" }, Child = panel });

            var primary = res.Series.TryGetValue(Metric.Vmaf, out var v) ? v : res.Series.Values.First();
            _qChart.Title = $"{primary.Name} per frame (click to screenshot that moment)";
            var x = Enumerable.Range(0, primary.Values.Count).Select(i => res.TimeOf(i)).ToArray();
            series.Add(new ChartSeries($"{di + 1}", Palette[di % Palette.Length], x, primary.Values.ToArray()));
        }
        if (_metrics.Count == 0)
            _qResults.Children.Add(Muted("Choose the reference (the source or the best release) and press “Measure”.", 13));
        _qChart.SetSeries(series);
    }

    private async Task RankReleasesAsync()
    {
        if (_tools == null || _items.Count < 2)
        {
            await MessageBox.ShowAsync(this, "Add the reference (best source, e.g. the Blu-ray) first, then the releases — for many at once use File › Add folder of releases.", MessageKind.Info);
            return;
        }
        var refItem = _items[Math.Max(0, _qRef.SelectedIndex)];
        if (refItem != _items[0])
        {
            await MessageBox.ShowAsync(this, "Ranking uses source 1 as the reference: remove and re-add sources so the best one is first.", MessageKind.Info);
            return;
        }
        // sync whatever is not synced yet
        if (_items.Skip(1).Any(i => !i.Synced))
            await AutoSyncAsync(onlyUnsynced: true, quiet: true);
        var m = Metric.None;
        if (_qPsnr.IsChecked == true)
            m |= Metric.Psnr;
        if (_qSsim.IsChecked == true)
            m |= Metric.Ssim;
        if (_qVmaf.IsChecked == true)
            m |= Metric.Vmaf;
        if (_qXpsnr.IsChecked == true)
            m |= Metric.Xpsnr;
        if (m == Metric.None)
            m = Metric.Vmaf | Metric.Psnr;
        var length = Format.TryParseTime(_qLength.Text, out var l) && l > 0 ? Math.Min(l, 120) : 20;
        var opt = new MetricOptions { Metrics = m, Length = length, Subsample = (int)(_qSub.Value ?? 1), DeinterlaceReference = _qDeint.IsChecked == true };
        var windows = (int)(_qWindows.Value ?? 5);
        await RunJobAsync("Ranking releases", async ct =>
        {
            var prog = new Progress<(double Fraction, string Text)>(p =>
            {
                SetProgress(p.Fraction);
                SetStatus(p.Text);
            });
            _ranking = await Ranker.RankAsync(_tools, _temp, refItem, _items.Skip(1).ToList(), opt, windows, prog, ct);
            _metrics.Clear();
            foreach (var r in _ranking.Where(r => r.Result != null))
                _metrics.Add((refItem, r.Item, r.Result!));
            AppendLog(Ranker.Table(_ranking));
        });
        RefreshMetricResults();
    }

    private void RefreshRanking()
    {
        _rankPanel.Children.Clear();
        if (_ranking.Count == 0 || _ranking.Any(r => !_items.Contains(r.Item)))
        {
            _ranking = _ranking.Where(r => _items.Contains(r.Item)).ToList();
            if (_ranking.Count == 0)
                return;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,80,80,80,90,110,110,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        string[] head = ["#", "Release", "VMAF", "1 % low", "PSNR", "SSIM", "Video rate", "Resolution", "Audio"];
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < head.Length; c++)
        {
            var h = new TextBlock { Text = head[c], Classes = { "muted" }, FontSize = 12 };
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        var y = 1;
        foreach (var r in _ranking)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var idx = _items.IndexOf(r.Item);
            var v = r.Item.Title.Info?.Video;
            var cells = new List<Control>
            {
                new TextBlock { Text = r.Rank > 0 ? r.Rank.ToString(CultureInfo.InvariantCulture) : "–", FontWeight = FontWeight.Bold, FontSize = 15, Foreground = r.Rank == 1 ? Brush("AccentBrush") : Brush("TextBrush") },
                ChipLabel(idx, r.Item.Name),
            };
            if (r.Result == null)
            {
                cells.Add(Muted(r.Error ?? "not measured"));
            }
            else
            {
                cells.AddRange([
                    new TextBlock { Text = Format.Num(r.Vmaf), FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = Format.Num(r.VmafLow) },
                    new TextBlock { Text = Format.Num(r.Psnr) },
                    new TextBlock { Text = Format.Num(r.Ssim, "0.0000") },
                    new TextBlock { Text = v != null ? Format.BitRate(v.BitRate > 0 ? v.BitRate : r.Item.Title.Info!.OverallBitRate) : "" },
                    new TextBlock { Text = v != null ? $"{v.Width}×{v.Height} {v.BitDepth}-bit" : "" },
                    new TextBlock { Text = r.Item.Title.Info?.Audio.FirstOrDefault()?.Label ?? "", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                ]);
            }
            for (var c = 0; c < cells.Count; c++)
            {
                cells[c].Margin = new Thickness(0, 3);
                cells[c].VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(cells[c], y);
                Grid.SetColumn(cells[c], c);
                if (r.Result == null && c == 2)
                    Grid.SetColumnSpan(cells[c], 7);
                grid.Children.Add(cells[c]);
            }
            y++;
        }
        var copy = new Button { Content = "Copy table", Classes = { "small" } };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, Ranker.Table(_ranking));
        };
        var rf = _items.FirstOrDefault();
        _rankPanel.Children.Add(new Border
        {
            Classes = { "card" },
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "Ranking", Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center }, Muted($"against {rf?.Name} · {(int)(_qWindows.Value ?? 5)} windows across the title"), copy } },
                    grid,
                },
            },
        });
    }

    private async Task ShotWorstAsync()
    {
        if (_metrics.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Measure first.", MessageKind.Info);
            return;
        }
        var times = new List<(double, string)>();
        foreach (var (rf, ds, res) in _metrics)
        {
            foreach (var (frame, value) in res.Worst(4))
                times.Add((res.TimeOf(frame), $"worst for {ds.Name}: {value:0.###}"));
        }
        await AddShotTimesAsync(times);
    }

    // ---------- report ----------

    private async Task ExportReportAsync()
    {
        if (_items.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Add sources first.", MessageKind.Info);
            return;
        }
        var start = Directory.Exists(_settings.ReportFolder) ? await StorageProvider.TryGetFolderFromPathAsync(_settings.ReportFolder) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the report (index.html + images)", SuggestedStartLocation = start });
        var dir = folders.FirstOrDefault()?.TryGetLocalPath();
        if (dir == null)
            return;
        _settings.ReportFolder = dir;
        var title = string.Join(" vs ", _items.Select(i => i.Name));
        var outDir = Path.Combine(dir, SafeName("ces " + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)));
        string? html = null;
        await RunJobAsync("Writing report", ct =>
        {
            ct.ThrowIfCancellationRequested();
            html = HtmlReport.Write(outDir, title, _items, _rows.Select(r => r.Row).ToList(), _metrics, _subReports, _slowPicsUrl, BdInfoSections());
            return Task.CompletedTask;
        });
        if (html != null)
        {
            SetStatus("Report: " + html);
            try
            {
                Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                await MessageBox.ShowAsync(this, "Report written to\n" + html, MessageKind.Info);
            }
        }
    }
}
