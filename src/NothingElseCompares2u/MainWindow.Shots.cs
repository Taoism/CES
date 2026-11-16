using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Controls;
using NothingElseCompares2u.Dialogs;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u;

public sealed partial class MainWindow
{
    private sealed class GuiShotRow
    {
        public required ShotRow Row { get; init; }
        public List<Bitmap?> Bitmaps { get; } = [];
        public override string ToString() => Format.Time(Row.Time) + (Row.Caption.Length > 0 ? "  " + Row.Caption : "");
    }

    private readonly List<GuiShotRow> _rows = [];
    private readonly ListBox _rowList = new() { MinWidth = 150 };
    private readonly CompareView _view = new() { Focusable = true };
    private readonly ComboBox _shotMode = new() { ItemsSource = new[] { "Evenly spaced", "Random", "Chapters", "Custom times", "Detailed shots" }, SelectedIndex = 0, Width = 140 };
    private readonly NumericUpDown _shotCount = new() { Minimum = 1, Maximum = 200, Value = 8, Increment = 1, FormatString = "0", Width = 110 };
    private readonly TextBox _shotTimes = new() { PlaceholderText = "0:10:00, 1:02:03.5, …", Width = 260, IsVisible = false };
    private readonly CheckBox _shotAspect = new() { Content = "Correct aspect", IsChecked = true };
    private readonly CheckBox _shotDeint = new() { Content = "Deinterlace" };
    private readonly ComboBox _shotTone = new() { ItemsSource = new[] { "HDR→SDR: auto", "HDR→SDR: on", "HDR→SDR: off" }, SelectedIndex = 0, Width = 165 };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, Classes = { "muted" } };
    private readonly StackPanel _shotStats = new() { Spacing = 2 };
    private readonly ToggleButton _modeSingle = new() { Content = "Single", Classes = { "seg" }, IsChecked = true };
    private readonly ToggleButton _modeWipe = new() { Content = "Wipe", Classes = { "seg" } };
    private readonly ToggleButton _modeSbs = new() { Content = "Side by side", Classes = { "seg" } };
    private readonly ToggleButton _modeDiff = new() { Content = "Difference", Classes = { "seg" } };
    private readonly ToggleButton _modeFlick = new() { Content = "Flicker", Classes = { "seg" } };
    private readonly ComboBox _diffGain = new() { ItemsSource = new[] { "×1", "×2", "×4", "×8", "×16", "×32" }, SelectedIndex = 2, IsVisible = false, FontSize = 12 };
    private readonly ComboBox _flickSpeed = new() { ItemsSource = new[] { "250 ms", "500 ms", "750 ms", "1 s", "2 s" }, SelectedIndex = 1, IsVisible = false, FontSize = 12 };
    private int _previousSource;

    private Control BuildShotsTab()
    {
        _shotCount.Value = _settings.ShotCount;
        _shotMode.SelectedIndex = _settings.ShotMode switch
        {
            "Random" => 1,
            "Chapters" => 2,
            "Custom" => 3,
            "Smart" => 4,
            _ => 0,
        };
        ToolTip.SetTip(_shotMode, "Detailed shots: one quick keyframe pass over source 1 picks well-lit, detailed moments (no black frames, fades or credits).");
        _shotAspect.IsChecked = _settings.CorrectAspect;
        _shotDeint.IsChecked = _settings.Deinterlace;
        _shotTone.SelectedIndex = _settings.ToneMap switch
        {
            "On" => 1,
            "Off" => 2,
            _ => 0,
        };
        _shotMode.SelectionChanged += (_, _) =>
        {
            _shotTimes.IsVisible = _shotMode.SelectedIndex == 3;
            _shotCount.IsVisible = _shotMode.SelectedIndex is 0 or 1 or 4;
        };
        _shotTimes.IsVisible = _shotMode.SelectedIndex == 3;
        _shotCount.IsVisible = _shotMode.SelectedIndex is 0 or 1 or 4;
        Avalonia.Automation.AutomationProperties.SetName(_shotCount, "Screenshot count");
        Avalonia.Automation.AutomationProperties.SetName(_shotTimes, "Screenshot times");
        Avalonia.Automation.AutomationProperties.SetName(_shotMode, "Screenshot points");
        Avalonia.Automation.AutomationProperties.SetName(_rowList, "Screenshot rows");
        ToolTip.SetTip(_shotAspect, "Scale anamorphic video (DVD, 4:3 in 16:9 …) to its display aspect ratio.");
        ToolTip.SetTip(_shotTone, "HDR (PQ/HLG) sources are tone-mapped so they can be compared with SDR ones.");

        var take = new Button { Content = "Take screenshots", Classes = { "accent" } };
        take.Click += async (_, _) => await TakeShotsAsync(replace: true);
        var add = new Button { Content = "Add time…" };
        ToolTip.SetTip(add, "Add one more screenshot row at a time you type.");
        add.Click += async (_, _) => await AddTimeAsync();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => ClearShots();
        _busyDisabled.AddRange([take, add, clear]);

        var bar1 = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        ToolTip.SetTip(_shotSubs, "Burn subtitles into the screenshots: each source uses its own track in that language (PGS, VobSub and text).");
        Avalonia.Automation.AutomationProperties.SetName(_shotSubs, "Screenshot subtitles");
        _shotSubs.SelectionChanged += async (_, _) =>
        {
            if (_rows.Count > 0 && !Busy)
                await RetakeAllAsync();
        };
        bar1.Children.AddRange([_shotMode, _shotCount, _shotTimes, _shotAspect, _shotDeint, _shotTone, _shotSubs, take, add, clear]);
        foreach (var c in bar1.Children.OfType<Control>())
            c.VerticalAlignment = VerticalAlignment.Center;

        // view controls
        _modeSingle.Click += (_, _) => SetViewMode(CompareMode.Single);
        _modeWipe.Click += (_, _) => SetViewMode(CompareMode.Wipe);
        _modeSbs.Click += (_, _) => SetViewMode(CompareMode.SideBySide);
        _modeDiff.Click += (_, _) => SetViewMode(CompareMode.Difference);
        _modeFlick.Click += (_, _) => SetViewMode(CompareMode.Flicker);
        ToolTip.SetTip(_modeDiff, "|A − B| amplified (A = shown source, B = Shift+number). D");
        ToolTip.SetTip(_modeFlick, "Switches between A and B automatically (B = Shift+number). T");
        Avalonia.Automation.AutomationProperties.SetName(_diffGain, "Difference gain");
        Avalonia.Automation.AutomationProperties.SetName(_flickSpeed, "Flicker speed");
        _diffGain.SelectionChanged += (_, _) => _view.DiffGain = Math.Pow(2, Math.Max(0, _diffGain.SelectedIndex));
        _flickSpeed.SelectionChanged += (_, _) => _view.FlickerMs = _flickSpeed.SelectedIndex switch
        {
            0 => 250,
            2 => 750,
            3 => 1000,
            4 => 2000,
            _ => 500,
        };
        var fit = new Button { Content = "Fit", Classes = { "small" } };
        fit.Click += (_, _) => _view.SetZoom(0);
        var z1 = new Button { Content = "100 %", Classes = { "small" } };
        z1.Click += (_, _) => _view.SetZoom(1);
        var z2 = new Button { Content = "200 %", Classes = { "small" } };
        z2.Click += (_, _) => _view.SetZoom(2);
        var z4 = new Button { Content = "400 %", Classes = { "small" } };
        z4.Click += (_, _) => _view.SetZoom(4);
        var back = new Button { Content = "◀ frame", Classes = { "small" } };
        ToolTip.SetTip(back, "Step this screenshot row one frame back in all sources (,)");
        back.Click += async (_, _) => await StepFrameAsync(-1);
        var fwd = new Button { Content = "frame ▶", Classes = { "small" } };
        ToolTip.SetTip(fwd, "Step this screenshot row one frame forward in all sources (.)");
        fwd.Click += async (_, _) => await StepFrameAsync(1);
        _busyDisabled.AddRange([back, fwd]);
        var bar2 = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        bar2.Children.AddRange([
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { _modeSingle, _modeWipe, _modeSbs, _modeDiff, _modeFlick } },
            _diffGain, _flickSpeed,
            new Border { Width = 10 }, fit, z1, z2, z4, _zoomLabel, new Border { Width = 10 }, back, fwd,
            Muted("1–9 source · Shift+1–9 compare-with · Space toggles · ↑↓ rows · wheel zoom · drag pan"),
        ]);
        foreach (var c in bar2.Children.OfType<Control>())
            c.VerticalAlignment = VerticalAlignment.Center;

        _view.ViewChanged += (_, _) =>
        {
            _zoomLabel.Text = _view.Zoom <= 0 ? $"fit ({_view.EffectiveScale:P0})" : $"{_view.Zoom:P0}";
            UpdateStatsHighlight();
        };
        _rowList.SelectionChanged += (_, _) => RefreshShotView();
        _rowList.KeyDown += (_, e) =>
        {
            if (e.Key is >= Key.D1 and <= Key.D9 || e.Key is Key.Space or Key.Left or Key.Right or Key.W or Key.S or Key.A or Key.D or Key.T or Key.F or Key.Z or Key.D0 or Key.OemComma or Key.OemPeriod)
                e.Handled = HandleShotKey(e);
        };

        var rowsPanel = new DockPanel();
        var rowsHeader = new TextBlock { Text = "Times", Classes = { "header" } };
        DockPanel.SetDock(rowsHeader, Dock.Top);
        rowsPanel.Children.Add(rowsHeader);
        rowsPanel.Children.Add(new Border { Classes = { "panel" }, Child = _rowList });

        var viewer = new DockPanel();
        DockPanel.SetDock(bar2, Dock.Top);
        viewer.Children.Add(bar2);
        var statsBorder = new Border { Classes = { "panel" }, Padding = new Thickness(8, 5), Margin = new Thickness(0, 6, 0, 0), Child = _shotStats };
        DockPanel.SetDock(statsBorder, Dock.Bottom);
        viewer.Children.Add(statsBorder);
        viewer.Children.Add(new Border { Classes = { "panel" }, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true, Child = _view });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,6,*"), Margin = new Thickness(0, 8, 0, 0) };
        grid.Children.Add(rowsPanel);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        grid.Children.Add(split);
        Grid.SetColumn(viewer, 2);
        grid.Children.Add(viewer);

        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar1, Dock.Top);
        root.Children.Add(bar1);
        root.Children.Add(grid);
        return root;
    }

    private void SaveShotSettings()
    {
        _settings.ShotCount = (int)(_shotCount.Value ?? 8);
        _settings.ShotMode = _shotMode.SelectedIndex switch
        {
            1 => "Random",
            2 => "Chapters",
            3 => "Custom",
            4 => "Smart",
            _ => "Even",
        };
        _settings.CorrectAspect = _shotAspect.IsChecked == true;
        _settings.Deinterlace = _shotDeint.IsChecked == true;
        _settings.ToneMap = _shotTone.SelectedIndex switch
        {
            1 => "On",
            2 => "Off",
            _ => "Auto",
        };
    }

    private ShotOptions ShotOpts() => new()
    {
        CorrectAspect = _shotAspect.IsChecked == true,
        Deinterlace = _shotDeint.IsChecked == true,
        ToneMap = _shotTone.SelectedIndex switch
        {
            1 => ToneMapMode.On,
            2 => ToneMapMode.Off,
            _ => ToneMapMode.Auto,
        },
    };

    /// <summary>Part of the reference timeline that every source covers: t with 0 ≤ t + offsetᵢ ≤ durationᵢ.</summary>
    private (double Start, double End) TimelineRange() =>
        _items.Count == 0 ? (0, 0) : (Math.Max(0, _items.Max(i => i.Coverage().Start)), _items.Min(i => i.Coverage().End));

    private async Task TakeShotsAsync(bool replace)
    {
        if (_tools == null || _items.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Add at least one source first.", MessageKind.Info);
            return;
        }
        SaveShotSettings();
        var (start, end) = TimelineRange();
        if (_items.All(i => i.Title.IsStill))
        {
            // pictures only: one row, the images themselves
            if (replace)
                ClearShots();
            AddRows([new GuiShotRow { Row = new ShotRow { Time = 0, Caption = "still images" } }]);
            await FillMissingAsync();
            return;
        }
        if (double.IsInfinity(end))
            end = start + _items.Where(i => !i.Title.IsStill).Select(i => i.Title.Duration).DefaultIfEmpty(0).Max();
        var dur = end - start;
        if (dur <= 1)
        {
            await MessageBox.ShowAsync(this, "The sources do not overlap on the timeline — check the offsets.", MessageKind.Warning);
            return;
        }
        List<double> times;
        var captions = new Dictionary<double, string>();
        switch (_shotMode.SelectedIndex)
        {
            case 1:
                times = InputBuilder.RandomPoints(dur, (int)(_shotCount.Value ?? 8), _settings.RandomSeed).Select(t => t + start).ToList();
                break;
            case 2:
                times = [];
                var ch = _items[0].Title.Info?.Chapters ?? _items[0].Title.Chapters;
                for (var k = 0; k < ch.Count; k++)
                {
                    // a little into the chapter: chapter starts are often black or a cut
                    var t = ch[k].Start + Math.Min(10, (k + 1 < ch.Count ? ch[k + 1].Start - ch[k].Start : 20) / 3);
                    if (t < start + dur)
                    {
                        times.Add(t);
                        captions[t] = ch[k].Name;
                    }
                }
                if (times.Count == 0)
                {
                    await MessageBox.ShowAsync(this, "Source 1 has no chapters.", MessageKind.Info);
                    return;
                }
                break;
            case 3:
                times = [];
                foreach (var part in (_shotTimes.Text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Format.TryParseTime(part, out var t))
                    {
                        await MessageBox.ShowAsync(this, $"“{part}” is not a time (use 1:02:03.5 or seconds).", MessageKind.Warning);
                        return;
                    }
                    times.Add(t);
                }
                break;
            case 4:
            {
                var picks = await SmartPointsAsync((int)(_shotCount.Value ?? 8), start, end);
                if (picks == null)
                    return;
                times = picks.Select(p => p.Time).ToList();
                foreach (var p in picks)
                    captions[p.Time] = p.Caption;
                break;
            }
            default:
                times = InputBuilder.EvenPoints(dur, (int)(_shotCount.Value ?? 8)).Select(t => t + start).ToList();
                break;
        }
        if (replace)
            ClearShots();
        var newRows = times.Select(t => new GuiShotRow { Row = new ShotRow { Time = t, Caption = captions.GetValueOrDefault(t, "") } }).ToList();
        AddRows(newRows);
        await FillMissingAsync();
    }

    private readonly Dictionary<MediaTitle, List<KeyframeInfo>> _keyframeScans = [];

    /// <summary>Scans source 1's keyframes once per title (cached) and picks detailed shots inside the shared range.</summary>
    private async Task<List<ShotPick>?> SmartPointsAsync(int count, double start, double end)
    {
        var title = _items[0].Title;
        if (title.Info?.Video == null)
        {
            await MessageBox.ShowAsync(this, "Source 1 has no video.", MessageKind.Info);
            return null;
        }
        if (!_keyframeScans.TryGetValue(title, out var frames))
        {
            List<KeyframeInfo>? scanned = null;
            var ok = await RunJobAsync("Scanning keyframes", async ct =>
            {
                var prog = new Progress<double>(f =>
                {
                    SetProgress(f);
                    SetStatus($"Scanning keyframes of {_items[0].Name}: {f:P0}");
                });
                scanned = await new ShotPicker(_tools!, _temp).ScanAsync(title, prog, ct: ct);
            });
            if (!ok || scanned == null || scanned.Count == 0)
                return null;
            frames = scanned;
            _keyframeScans[title] = frames;
            AppendLog($"Keyframe scan: {frames.Count} keyframes");
        }
        // keyframe times are source-1 time; the shared timeline is reference time (source 1 normally has no offset)
        var refItem = _items[0];
        var mapped = frames.Select(f => f with { Time = refItem.TimelineTime(f.Time) }).ToList();
        return ShotPicker.Pick(mapped, count, start, end);
    }

    private void AddRows(IEnumerable<GuiShotRow> rows)
    {
        foreach (var r in rows)
        {
            while (r.Row.Shots.Count < _items.Count)
            {
                r.Row.Shots.Add(null);
                r.Bitmaps.Add(null);
            }
            _rows.Add(r);
        }
        _rows.Sort((a, b) => a.Row.Time.CompareTo(b.Row.Time));
        var sel = _rowList.SelectedItem;
        _rowList.ItemsSource = null;
        _rowList.ItemsSource = _rows.ToList();
        _rowList.SelectedItem = sel ?? _rows.FirstOrDefault();
    }

    /// <summary>Public entry for other tabs (worst frames, chart clicks): add rows at reference times and grab them.</summary>
    private async Task AddShotTimesAsync(IEnumerable<(double Time, string Caption)> times)
    {
        var rows = times.Select(t => new GuiShotRow { Row = new ShotRow { Time = t.Time, Caption = t.Caption } }).ToList();
        AddRows(rows);
        _tabs.SelectedIndex = 0;
        _rowList.SelectedItem = rows.FirstOrDefault();
        await FillMissingAsync();
    }

    private async Task AddTimeAsync()
    {
        var box = new TextBox { PlaceholderText = "1:02:03.5", Width = 200 };
        var dlg = new InputDialog("Add screenshot time", "Time on the reference timeline (h:mm:ss.fff or seconds):", box);
        if (await dlg.ShowDialog<bool>(this) && Format.TryParseTime(box.Text, out var t))
            await AddShotTimesAsync([(t, "")]);
    }

    private void ClearShots()
    {
        DisposeShotBitmaps();
        _rows.Clear();
        _rowList.ItemsSource = null;
        RefreshShotView();
    }

    private void DisposeShotBitmaps()
    {
        foreach (var r in _rows)
        {
            foreach (var b in r.Bitmaps)
                b?.Dispose();
            r.Bitmaps.Clear();
            for (var i = 0; i < r.Row.Shots.Count; i++)
                r.Bitmaps.Add(null);
        }
    }

    private void RemoveShotColumn(int idx)
    {
        foreach (var r in _rows)
        {
            if (idx < r.Row.Shots.Count)
                r.Row.Shots.RemoveAt(idx);
            if (idx < r.Bitmaps.Count)
            {
                r.Bitmaps[idx]?.Dispose();
                r.Bitmaps.RemoveAt(idx);
            }
        }
    }

    private Task RetakeSourceAsync(int idx)
    {
        if (idx < 0)
            return Task.CompletedTask;
        // the reference decides which frame the others show
        if (idx == 0)
            return RetakeAllAsync();
        foreach (var r in _rows)
        {
            if (idx < r.Row.Shots.Count)
                r.Row.Shots[idx] = null;
        }
        return FillMissingAsync();
    }

    private Task RetakeAllAsync()
    {
        foreach (var r in _rows)
        {
            for (var i = 0; i < r.Row.Shots.Count; i++)
                r.Row.Shots[i] = null;
        }
        return FillMissingAsync();
    }

    /// <summary>Grabs every missing (row, source) frame, a few ffmpeg processes at a time.</summary>
    private async Task FillMissingAsync()
    {
        if (_tools == null || _rows.Count == 0 || _items.Count == 0)
            return;
        foreach (var r in _rows)
        {
            while (r.Row.Shots.Count < _items.Count)
            {
                r.Row.Shots.Add(null);
                r.Bitmaps.Add(null);
            }
        }
        var todo = _rows.SelectMany(r => Enumerable.Range(0, _items.Count).Where(i => r.Row.Shots[i] == null).Select(i => (r, i))).ToList();
        if (todo.Count == 0)
            return;
        // the visible row first
        var current = _rowList.SelectedItem as GuiShotRow;
        todo = todo.OrderBy(x => x.r == current ? 0 : 1).ToList();
        var opt = ShotOpts();
        var items = _items.ToList();
        var shotDir = Path.Combine(_temp, "shots");
        await RunJobAsync($"Screenshots ({todo.Count})", async ct =>
        {
            var shooter = new Screenshotter(_tools, _temp);
            var done = 0;
            using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 4, 2, 4));

            async Task Shoot(GuiShotRow row, int i, double? refFrame)
            {
                await gate.WaitAsync(ct);
                try
                {
                    var png = Path.Combine(shotDir, $"{Guid.NewGuid():N}.png");
                    var target = FrameMatch.TargetTime(items[0], refFrame, row.Row.Time, items[i]);
                    var res = await shooter.TakeAsync(items[i].Title, target, png, ShotOptsFor(opt, items[i]), ct);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (i >= row.Row.Shots.Count || !_rows.Contains(row))
                            return;
                        row.Row.Shots[i] = res;
                        row.Bitmaps[i]?.Dispose();
                        row.Bitmaps[i] = TryLoadBitmap(res.Error == null ? res.ImagePath : null);
                        done++;
                        SetProgress((double)done / todo.Count);
                        if (_rowList.SelectedItem == row)
                            RefreshShotView();
                    });
                }
                finally
                {
                    gate.Release();
                }
            }

            // per row: the reference frame first, then the others aimed at the frame it really showed
            var tasks = todo.GroupBy(x => x.r).Select(async g =>
            {
                var row = g.Key;
                if (g.Any(x => x.i == 0))
                    await Shoot(row, 0, null);
                var refShot = row.Row.Shots.Count > 0 ? row.Row.Shots[0] : null;
                double? refFrame = refShot is { Error: null } rs && !double.IsNaN(rs.FrameTime) ? rs.FrameTime : null;
                await Task.WhenAll(g.Where(x => x.i != 0).Select(x => Shoot(row, x.i, refFrame)));
            });
            await Task.WhenAll(tasks);
        });
        RefreshShotView();
    }

    private async Task StepFrameAsync(int frames)
    {
        if (_rowList.SelectedItem is not GuiShotRow row || _items.Count == 0)
            return;
        var fps = InputBuilder.FrameRate(_items[0].Title) is > 0 and var f ? f : 24000 / 1001.0;
        var t = Math.Max(0, row.Row.Time + frames / fps);
        var replacement = new GuiShotRow { Row = new ShotRow { Time = t, Caption = row.Row.Caption } };
        var idx = _rows.IndexOf(row);
        foreach (var b in row.Bitmaps)
            b?.Dispose();
        _rows.RemoveAt(idx);
        AddRows([replacement]);
        _rowList.SelectedItem = replacement;
        await FillMissingAsync();
    }

    private void SetViewMode(CompareMode mode)
    {
        _view.Mode = mode;
        _modeSingle.IsChecked = mode == CompareMode.Single;
        _modeWipe.IsChecked = mode == CompareMode.Wipe;
        _modeSbs.IsChecked = mode == CompareMode.SideBySide;
        _modeDiff.IsChecked = mode == CompareMode.Difference;
        _modeFlick.IsChecked = mode == CompareMode.Flicker;
        _diffGain.IsVisible = mode == CompareMode.Difference;
        _flickSpeed.IsVisible = mode == CompareMode.Flicker;
    }

    private void RefreshShotView()
    {
        if (_rowList.SelectedItem is not GuiShotRow row)
        {
            _view.SetImages([], [], [], _items.Count == 0 ? "Add sources (drop them here), then take screenshots." : "Press “Take screenshots”.");
            _shotStats.Children.Clear();
            return;
        }
        var n = Math.Min(_items.Count, row.Bitmaps.Count);
        var labels = new string[n];
        for (var i = 0; i < n; i++)
        {
            var s = row.Row.Shots[i];
            labels[i] = _items[i].Name + (s is { PictType: not "?" } ? $"  ·  {s.PictType}-frame" : "");
        }
        _view.SetImages(row.Bitmaps.Take(n).ToArray(), labels, Palette.Take(n).Concat(Enumerable.Repeat(Palette[^1], Math.Max(0, n - Palette.Length))).ToArray());

        _shotStats.Children.Clear();
        _shotStats.Children.Add(new TextBlock { Text = $"{Format.Time(row.Row.Time)} on the reference timeline{(row.Row.Caption.Length > 0 ? " · " + row.Row.Caption : "")}", FontWeight = FontWeight.SemiBold, FontSize = 12 });
        for (var i = 0; i < n; i++)
        {
            var s = row.Row.Shots[i];
            string text;
            if (s == null)
                text = "…";
            else if (s.Error != null)
                text = s.Error;
            else
            {
                var st = s.Stats;
                var max = st != null ? (1 << st.BitDepth) - 1 : 255;
                text = $"{Format.Time(!double.IsNaN(s.FrameTime) ? s.FrameTime : _items[i].SourceTime(row.Row.Time) ?? double.NaN)}  ·  {s.PictType}-frame  ·  {s.Width}×{s.Height}" +
                       (st == null ? "" : $"  ·  Y {st.YMin:0}–{st.YMax:0} (of {max}) avg {st.YAvg:0.0}  ·  sat {st.SatAvg:0.0}" +
                                          (double.IsNaN(st.Blur) ? "" : $"  ·  blur {st.Blur:0.00}") + (double.IsNaN(st.Block) ? "" : $"  ·  blockiness {st.Block:0.00}"));
            }
            var line = ChipLabel(i, text, bold: false);
            line.Tag = i;
            _shotStats.Children.Add(line);
        }
        UpdateStatsHighlight();
    }

    private void UpdateStatsHighlight()
    {
        foreach (var c in _shotStats.Children.OfType<StackPanel>())
        {
            if (c.Tag is int i)
                c.Opacity = _view.Mode == CompareMode.SideBySide || i == _view.Current || (_view.Mode is CompareMode.Wipe or CompareMode.Difference or CompareMode.Flicker && i == _view.WipeOther) ? 1 : 0.55;
        }
    }

    private bool HandleShotKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case >= Key.D1 and <= Key.D9:
            case >= Key.NumPad1 and <= Key.NumPad9:
            {
                var i = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
                if (i < _items.Count)
                {
                    if (_view.Mode is CompareMode.Wipe or CompareMode.Difference or CompareMode.Flicker && (e.KeyModifiers & KeyModifiers.Shift) != 0)
                    {
                        _view.WipeOther = i;
                        _view.InvalidateVisual();
                    }
                    else
                    {
                        _previousSource = _view.Current;
                        _view.Current = i;
                    }
                }
                return true;
            }
            case Key.Space:
            {
                var p = _view.Current;
                _view.Current = _previousSource;
                _previousSource = p;
                return true;
            }
            case Key.Right:
                _previousSource = _view.Current;
                _view.Current++;
                return true;
            case Key.Left:
                _previousSource = _view.Current;
                _view.Current--;
                return true;
            case Key.Down:
                if (_rowList.SelectedIndex < _rows.Count - 1)
                    _rowList.SelectedIndex++;
                return true;
            case Key.Up:
                if (_rowList.SelectedIndex > 0)
                    _rowList.SelectedIndex--;
                return true;
            case Key.W:
                SetViewMode(CompareMode.Wipe);
                return true;
            case Key.S:
                SetViewMode(CompareMode.SideBySide);
                return true;
            case Key.A:
                SetViewMode(CompareMode.Single);
                return true;
            case Key.D:
                SetViewMode(CompareMode.Difference);
                return true;
            case Key.T:
                SetViewMode(_view.Mode == CompareMode.Flicker ? CompareMode.Single : CompareMode.Flicker);
                return true;
            case Key.F or Key.D0 or Key.NumPad0:
                _view.SetZoom(0);
                return true;
            case Key.Z:
                _view.SetZoom(1);
                return true;
            case Key.OemComma:
                _ = StepFrameAsync(-1);
                return true;
            case Key.OemPeriod:
                _ = StepFrameAsync(1);
                return true;
            default:
                return false;
        }
    }

    private async Task ExportShotsAsync()
    {
        if (_rows.Count == 0)
        {
            await MessageBox.ShowAsync(this, "No screenshots yet.", MessageKind.Info);
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Save screenshots to folder" });
        var dir = folders.FirstOrDefault()?.TryGetLocalPath();
        if (dir == null)
            return;
        var n = 0;
        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            for (var i = 0; i < row.Row.Shots.Count && i < _items.Count; i++)
            {
                if (row.Row.Shots[i] is { Error: null } s && File.Exists(s.ImagePath))
                {
                    var name = $"{r + 1:00}_{Format.Time(row.Row.Time).Replace(':', '.')}_{i + 1}_{SafeName(_items[i].Name)}.png";
                    File.Copy(s.ImagePath, Path.Combine(dir, name), true);
                    n++;
                }
            }
        }
        SetStatus($"Saved {n} screenshots to {dir}");
    }

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var r = new string(s.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return r.Length > 50 ? r[..50] : r;
    }
}
