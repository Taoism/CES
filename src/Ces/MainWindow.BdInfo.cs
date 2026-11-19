using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BDInfo;
using Ces.Compare;
using Ces.Dialogs;
using Ces.Disc;
using Ces.Model;
using Ces.Report;

namespace Ces;

/// <summary>Compare: the BDInfo tab (BDInfo engine, LGPL, in BDInfo.dll) — the in-depth view of each Blu-ray source.</summary>
public sealed partial class MainWindow
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Menlo,monospace");

    private readonly Dictionary<MediaSource, BdInfoDisc> _bdDiscs = [];
    private readonly Dictionary<MediaSource, string> _bdErrors = [];
    private readonly Grid _bdGrid = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _bdCards = new() { Spacing = 10 };
    private readonly CheckBox _bdDiffOnly = new() { Content = "Only rows that differ" };
    private readonly TextBlock _bdHint = new() { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private bool _bdScanning;
    private int _bdGeneration;

    private DockPanel BuildBdInfoTab()
    {
        var scan = new Button { Content = "Full scan", Classes = { "accent" } };
        ToolTip.SetTip(scan, "Read the compared title of every Blu-ray in full for exact sizes, bitrates and stream diagnostics (BDInfo's Scan).");
        scan.Click += async (_, _) => await FullScanAsync();
        var copy = new Button { Content = "Copy reports" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null && BdReports() is { Count: > 0 } r)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, JoinReports(r));
        };
        var save = new Button { Content = "Save reports…" };
        save.Click += async (_, _) => await SaveBdReportsAsync();
        _bdDiffOnly.IsCheckedChanged += (_, _) => RenderBdInfo();
        _busyDisabled.AddRange([scan, copy, save]);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { scan, copy, save, _bdDiffOnly, _bdHint } };
        foreach (var c in bar.Children)
            c.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new Border { Classes = { "panel" }, Padding = new Thickness(6), Child = new ScrollViewer { Content = _bdGrid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } },
                _bdCards,
            },
        };
        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Margin = new Thickness(0, 8, 0, 0), Content = content });
        return root;
    }

    private List<CompareItem> BluRayItems() => _items.Where(i => i.Source.Kind == SourceKind.BluRay).ToList();

    /// <summary>Opens new Blu-ray sources with BDInfo (quick read), forgets removed ones, then redraws the tab.</summary>
    private async Task RefreshBdInfoAsync()
    {
        var gen = ++_bdGeneration;
        var current = BluRayItems().Select(i => i.Source).ToHashSet();
        foreach (var gone in _bdDiscs.Keys.Where(s => !current.Contains(s)).ToList())
        {
            _bdDiscs[gone].Dispose();
            _bdDiscs.Remove(gone);
        }
        foreach (var gone in _bdErrors.Keys.Where(s => !current.Contains(s)).ToList())
            _bdErrors.Remove(gone);
        RenderBdInfo();
        foreach (var src in current.Where(s => !_bdDiscs.ContainsKey(s) && !_bdErrors.ContainsKey(s)).ToList())
        {
            try
            {
                var disc = await Task.Run(() => BdInfoDisc.FromSource(src), CancellationToken.None); // quick (<1 s); not a job
                // removed meanwhile, or opened by a newer refresh
                if (!BluRayItems().Any(i => i.Source == src) || _bdDiscs.ContainsKey(src))
                {
                    disc.Dispose();
                    continue;
                }
                _bdDiscs[src] = disc;
                foreach (var p in disc.Problems)
                    AppendLog($"BDInfo {disc.DisplayName}: could not read {p}");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _bdErrors[src] = ex.Message;
                AppendLog($"BDInfo {src.DisplayName}: {ex.Message}");
            }
        }
        if (gen == _bdGeneration)
            RenderBdInfo();
    }

    private void RefreshBdInfoTab() => _ = RefreshBdInfoAsync();

    private static TSPlaylistFile? PlaylistFor(CompareItem it, BdInfoDisc disc) => disc.FindPlaylist(it.Title.Name) ?? disc.MainPlaylist;

    /// <summary>The current report of each Blu-ray source that BDInfo could read, for its compared title.</summary>
    private List<(CompareItem Item, int Index, BdInfoDisc Disc, TSPlaylistFile? Playlist, string Report)> BdReports()
    {
        var list = new List<(CompareItem, int, BdInfoDisc, TSPlaylistFile?, string)>();
        foreach (var it in BluRayItems())
        {
            if (!_bdDiscs.TryGetValue(it.Source, out var disc))
                continue;
            var pl = PlaylistFor(it, disc);
            // a full scan from an earlier visit stands in until this disc is scanned again
            var report = pl == null ? disc.Report([])
                : !disc.IsScanned([pl]) && CachedBdReport(it, pl.Name) is { } cached ? cached
                : disc.Report([pl]);
            list.Add((it, _items.IndexOf(it), disc, pl, report));
        }
        return list;
    }

    private static string JoinReports(List<(CompareItem Item, int Index, BdInfoDisc Disc, TSPlaylistFile? Playlist, string Report)> reports) =>
        string.Join(Environment.NewLine + new string('=', 80) + Environment.NewLine + Environment.NewLine, reports.Select(r => $"[{r.Index + 1}] {r.Item.Name}{Environment.NewLine}{Environment.NewLine}{r.Report}"));

    /// <summary>The BDInfo reports for the HTML report (Blu-ray sources only).</summary>
    private List<BdInfoSection> BdInfoSections() =>
        _bdScanning ? [] : BdReports().Select(r => new BdInfoSection(r.Index, r.Item.Name, r.Report, r.Playlist != null && r.Disc.IsScanned([r.Playlist]))).ToList();

    private bool IsBdScanned(CompareItem it, BdInfoDisc disc, TSPlaylistFile? pl) =>
        pl != null && (disc.IsScanned([pl]) || CachedBdReport(it, pl.Name) != null);

    private void RenderBdInfo()
    {
        if (_bdScanning)
            return; // the engine is busy on another thread; redrawn when the scan ends
        _bdGrid.Children.Clear();
        _bdGrid.RowDefinitions.Clear();
        _bdGrid.ColumnDefinitions.Clear();
        _bdCards.Children.Clear();
        var items = BluRayItems();
        if (items.Count == 0)
        {
            _bdHint.Text = "";
            _bdGrid.Children.Add(Muted("Add a Blu-ray (BDMV folder, ISO or RAR set) to see its BDInfo report. BDInfo reads Blu-rays only."));
            return;
        }
        var reports = BdReports();
        var pending = items.Count(i => !_bdDiscs.ContainsKey(i.Source) && !_bdErrors.ContainsKey(i.Source));
        var unscanned = reports.Count(r => !IsBdScanned(r.Item, r.Disc, r.Playlist));
        _bdHint.Text = pending > 0 ? "Reading discs…"
            : unscanned > 0 ? "Quick read: sizes and bitrates are 0 until a full scan. Highlighted rows differ."
            : "Full scan done. Highlighted rows differ.";

        // side-by-side quick summary
        _bdGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        foreach (var _ in reports)
            _bdGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 240 });
        _bdGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < reports.Count; c++)
        {
            var h = ChipLabel(reports[c].Index, reports[c].Item.Name);
            h.Margin = new Thickness(8, 4);
            Grid.SetColumn(h, c + 1);
            _bdGrid.Children.Add(h);
        }
        var y = 1;
        foreach (var r in BdInfoTable.Build(reports.Select(x => BdInfoDisc.QuickSummary(x.Report)).ToList()).Where(r => _bdDiffOnly.IsChecked != true || r.Differs))
        {
            _bdGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (r.Differs && reports.Count > 1)
            {
                var bg = new Border { CornerRadius = new CornerRadius(3) }.Themed(Border.BackgroundProperty, "DiffHighlightBrush");
                Grid.SetRow(bg, y);
                Grid.SetColumnSpan(bg, reports.Count + 1);
                _bdGrid.Children.Add(bg);
            }
            var label = new TextBlock { Text = r.Label, Classes = { "muted" }, Margin = new Thickness(6, 3, 16, 3) };
            Grid.SetRow(label, y);
            _bdGrid.Children.Add(label);
            for (var c = 0; c < r.Values.Length; c++)
            {
                var v = new SelectableTextBlock { Text = r.Values[c], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 3) };
                Grid.SetRow(v, y);
                Grid.SetColumn(v, c + 1);
                _bdGrid.Children.Add(v);
            }
            y++;
        }

        // one card per Blu-ray: state, problems, the full report
        foreach (var it in items)
            _bdCards.Children.Add(BdCard(it, reports.FirstOrDefault(r => r.Item == it)));
    }

    private Border BdCard(CompareItem it, (CompareItem Item, int Index, BdInfoDisc Disc, TSPlaylistFile? Playlist, string Report) r)
    {
        var idx = _items.IndexOf(it);
        var card = new StackPanel { Spacing = 6 };
        if (r.Disc == null)
        {
            card.Children.Add(ChipLabel(idx, it.Name));
            card.Children.Add(Muted(_bdErrors.TryGetValue(it.Source, out var err) ? "BDInfo could not read this disc: " + err : "Reading the disc…"));
            return new Border { Classes = { "card" }, Child = card };
        }
        var scanned = IsBdScanned(it, r.Disc, r.Playlist);
        var fromCache = scanned && r.Playlist != null && !r.Disc.IsScanned([r.Playlist]);
        var state = r.Playlist == null ? "no playlist BDInfo lists"
            : $"{r.Playlist.Name}{(r.Playlist.Name.Equals(it.Title.Name, StringComparison.OrdinalIgnoreCase) ? "" : " (BDInfo leaves out " + it.Title.Name + ")")}  ·  {(fromCache ? "full scan (from the results cache)" : scanned ? "full scan" : "quick read")}";
        card.Children.Add(ChipLabel(idx, $"{it.Name}  ·  {state}"));
        foreach (var p in r.Disc.Problems)
            card.Children.Add(new TextBlock { Text = "Could not read " + p, TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, "ProblemBrush"));
        if (r.Disc.ScanResult?.FileExceptions is { Count: > 0 } fileErrors && scanned)
            foreach (var (file, ex) in fileErrors)
                card.Children.Add(new TextBlock { Text = $"Scan error in {file}: {ex.Message}", TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, "ProblemBrush"));
        var text = new SelectableTextBlock { Text = r.Report, FontFamily = MonoFont, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, text.Text ?? "");
        };
        card.Children.Add(new Expander
        {
            Header = "BDInfo report",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    copy,
                    new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                },
            },
        });
        return new Border { Classes = { "card" }, Child = card };
    }

    /// <summary>Full BDInfo scan of each Blu-ray's compared title, one after another.</summary>
    private async Task FullScanAsync()
    {
        var jobs = BdReports().Where(r => r.Playlist != null).Select(r => (r.Item, r.Disc, Playlist: r.Playlist!)).ToList();
        if (jobs.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Add a Blu-ray that BDInfo can read first.", MessageKind.Info, "BDInfo");
            return;
        }
        var total = jobs.Sum(j => (double)Math.Max(1, j.Playlist.FileSize));
        _bdScanning = true;
        try
        {
            await RunJobAsync("BDInfo full scan", async ct =>
            {
                double done = 0;
                foreach (var (item, disc, pl) in jobs)
                {
                    SetStatus($"BDInfo full scan: {item.Name} ({pl.Name})…");
                    var size = Math.Max(1, pl.FileSize);
                    var start = done;
                    await disc.ScanAsync([pl], new Progress<double>(p => SetProgress((start + p * size) / total)), ct);
                    done += size;
                    StoreBdReport(item, pl.Name, disc.Report([pl]));
                    AppendLog($"BDInfo {item.Name} {pl.Name}: {pl.TotalBitRate / 1e6:0.00} Mbps total");
                }
            });
        }
        finally
        {
            _bdScanning = false;
            RenderBdInfo();
        }
    }

    private async Task SaveBdReportsAsync()
    {
        var reports = BdReports();
        if (reports.Count == 0)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save BDInfo reports",
            DefaultExtension = ".txt",
            SuggestedFileName = "BDINFO." + SafeName(reports.Count == 1 ? reports[0].Disc.VolumeLabel : string.Join(" vs ", reports.Take(3).Select(r => r.Item.Name))) + ".txt",
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            await File.WriteAllTextAsync(path, JoinReports(reports), new UTF8Encoding(false), CancellationToken.None);
            SetStatus("BDInfo reports saved to " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageBox.ShowAsync(this, ex.Message, MessageKind.Error, "Save BDInfo reports");
        }
    }
}
