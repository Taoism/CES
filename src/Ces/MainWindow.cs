using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ces.SpotUi;
using Ces.Compare;
using Ces.Dialogs;
using Ces.Disc;
using Ces.Model;
using Ces.Probe;
using Ces.Tools;

namespace Ces;

/// <summary>Main window: sources on the left, one tab per kind of comparison.</summary>
public sealed partial class MainWindow : Window
{
    public static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#FF4A8CE3")), new SolidColorBrush(Color.Parse("#FFE0A040")),
        new SolidColorBrush(Color.Parse("#FF6CC070")), new SolidColorBrush(Color.Parse("#FFD070C0")),
        new SolidColorBrush(Color.Parse("#FF50C8C8")), new SolidColorBrush(Color.Parse("#FFE06060")),
    ];

    public static WindowIcon? AppIcon { get; } = LoadIcon();

    private readonly AppSettings _settings;
    private readonly List<CompareItem> _items = [];
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "CES", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    private FfTools? _tools;
    private readonly TaskCompletionSource<FfTools?> _toolsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;

    private readonly StackPanel _sourcePanel = new() { Spacing = 8 };
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Controls.JobArea _job = new();
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Classes = { "mono" } };
    private readonly List<Control> _busyDisabled = [];
    private readonly List<Control> _cardControls = []; // rebuilt with the source cards / disc tab

    private IEnumerable<Control> BusyControls => _busyDisabled.Concat(_cardControls);

    // Spot / Compare modes
    private readonly ContentControl _modeHost = new();
    private readonly ToggleButton _spotToggle = new() { Content = "Spot", Classes = { "seg" } };
    private readonly ToggleButton _compareToggle = new() { Content = "Compare", Classes = { "seg" } };
    private readonly TextBlock _modeHint = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly MenuItem _viewSpot = new() { Header = "_Spot", ToggleType = MenuItemToggleType.Radio, GroupName = "mode", InputGesture = new KeyGesture(Key.D1, KeyModifiers.Control) };
    private readonly MenuItem _viewCompare = new() { Header = "_Compare", ToggleType = MenuItemToggleType.Radio, GroupName = "mode", InputGesture = new KeyGesture(Key.D2, KeyModifiers.Control) };
    private readonly TextBlock _spotStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Controls.JobArea _spotJob = new();
    private Control _compareStatusRow = null!;
    private Control _spotStatusRow = null!;
    private Grid _compareBody = null!;
    private readonly FolderTree _compareTree = new();
    private bool _compareTreeRevealed;
    private readonly SpotView _spot;
    private Control _compareView = null!;
    private bool _spotMode;

    public MainWindow(AppSettings settings, string[] args)
    {
        _settings = settings;
        Title = "CES — Compare Enc Spot";
        _spot = new SpotView(settings) { SkipLastFolder = args.Any(a => File.Exists(a) || Directory.Exists(a)) };
        _spot.CompareRequested += async paths => await CompareFromSpotAsync(paths);
        _spot.SessionUsed += RememberRecent;
        _spot.Tools = () => _toolsReady.Task;
        Icon = AppIcon;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.WindowX is { } wx && settings.WindowY is { } wy)
        {
            // restore where it was, if that spot is still on a screen
            var p = new PixelPoint(wx, wy);
            if (Screens.All.Any(sc => sc.WorkingArea.Contains(p + new PixelPoint(40, 40))))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = p;
            }
        }
        if (settings.Maximized)
            WindowState = WindowState.Maximized;

        Content = BuildLayout();
        RebuildRecentMenu();
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(this, async (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files == null)
                return;
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.FirstOrDefault(IsSavedResults) is { } saved)
                await OpenSavedAsync(saved);
            else if (_spotMode)
                _spot.Open(paths, append: !(paths.Count == 1 && Directory.Exists(paths[0])));
            else
                await AddPathsAsync(paths);
        });
        ProcessRunner.CommandStarted += c => Dispatcher.UIThread.Post(() => AppendLog("> " + c));
        Opened += async (_, _) => await StartupAsync(args);
        Closing += OnClosing;
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://ces-gui/Assets/ces.ico"));
            return new WindowIcon(s);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }
    }

    private Control BuildLayout()
    {
        var menu = new Menu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "_File",
                    Items =
                    {
                        MenuItemFor("_Open…", () => _spotMode ? _spot.OpenFolderAsync() : AddFilesAsync(), new KeyGesture(Key.O, KeyModifiers.Control)),
                        MenuItemFor("Open saved _results…", OpenSavedAsync, new KeyGesture(Key.L, KeyModifiers.Control)),
                        (_recentMenu = new MenuItem { Header = "_Recent" }),
                        new Separator(),
                        MenuItemFor("_Save results", () => _spotMode ? _spot.SaveResultsAsync(false) : SaveSessionAsync(false), new KeyGesture(Key.S, KeyModifiers.Control)),
                        MenuItemFor("Save results _as…", () => _spotMode ? _spot.SaveResultsAsync(true) : SaveSessionAsync(true), new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)),
                        MenuItemFor("Save _tab as text…", SaveTabTextAsync, new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift)),
                        MenuItemFor("_Export…", () => _spotMode ? _spot.ExportAsync() : ExportReportAsync(), new KeyGesture(Key.E, KeyModifiers.Control)),
                        new Separator(),
                        MenuItemFor("_Settings…", ShowSettingsAsync),
                        new Separator(),
                        MenuItemFor("E_xit", () =>
                        {
                            Close();
                            return Task.CompletedTask;
                        }),
                    },
                },
                SpotMenu(),
                new MenuItem
                {
                    Header = "_Compare",
                    Items =
                    {
                        InCompare("Add _files…", AddFilesAsync),
                        InCompare("Add disc _folder (BDMV / VIDEO_TS)…", AddFolderAsync, new KeyGesture(Key.O, KeyModifiers.Control | KeyModifiers.Shift)),
                        InCompare("Add folder of _releases…", AddReleaseFolderAsync),
                        new Separator(),
                        InCompare("_Open comparison…", OpenSessionAsync),
                        InCompare("_Save comparison", () => SaveSessionAsync(false)),
                        InCompare("Save comparison _as…", () => SaveSessionAsync(true)),
                        InCompare("Save this _tab as text…", SaveTabTextAsync),
                        new Separator(),
                        InCompare("Export HTML _report…", ExportReportAsync),
                        InCompare("Save _screenshots…", ExportShotsAsync),
                        new Separator(),
                        InCompare("_Upload screenshots to slow.pics…", UploadSlowPicsAsync),
                        InCompare("Copy summary (_BBCode)", () => CopySummaryAsync(Share.SummaryFormat.BBCode)),
                        InCompare("Copy summary (_Markdown)", () => CopySummaryAsync(Share.SummaryFormat.Markdown)),
                    },
                },
                new MenuItem
                {
                    Header = "_View",
                    Items =
                    {
                        _viewSpot,
                        _viewCompare,
                    },
                },
                new MenuItem
                {
                    Header = "_Help",
                    Items =
                    {
                        MenuItemFor("_Keyboard shortcuts", () => MessageBox.ShowAsync(this, Shortcuts, MessageKind.Info, "Keyboard shortcuts")),
                        MenuItemFor("_About", ShowAboutAsync),
                    },
                },
            },
        };
        DockPanel.SetDock(menu, Dock.Top);
        _viewSpot.Click += (_, _) => SetMode(true);
        _viewCompare.Click += (_, _) => SetMode(false);

        // status bar
        // status bar: message on the left, job state + Stop always on the right
        _job.StopRequested += () => _cts?.Cancel();
        var statusGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        statusGrid.Children.Add(_status);
        Grid.SetColumn(_job, 1);
        statusGrid.Children.Add(_job);
        _compareStatusRow = statusGrid;
        _spotJob.StopRequested += () => _spot.Stop();
        var spotGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        spotGrid.Children.Add(_spotStatus);
        Grid.SetColumn(_spotJob, 1);
        spotGrid.Children.Add(_spotJob);
        _spotStatusRow = spotGrid;
        _spot.StatusChanged += (text, job, done, total) =>
        {
            _spotStatus.Text = text;
            if (job == null)
            {
                _spotJob.Finish();
                return;
            }
            if (_spotJob.JobName != job)
                _spotJob.Start(job);
            if (total > 0)
                _spotJob.Count(done, total);
        };
        var statusBar = new Border { Classes = { "footer" }, Padding = new Thickness(10, 5), Child = new Panel { Children = { statusGrid, spotGrid } }, BorderThickness = new Thickness(0, 1, 0, 0) }.Themed(Border.BorderBrushProperty, "EdgeBrush");
        DockPanel.SetDock(statusBar, Dock.Bottom);

        // sources
        var addFiles = new Button { Content = "Add files…" };
        addFiles.Click += async (_, _) => await AddFilesAsync();
        var addFolder = new Button { Content = "Add disc folder…" };
        addFolder.Click += async (_, _) => await AddFolderAsync();
        var sync = new Button { Content = "Compare", Classes = { "accent" } };
        ToolTip.SetTip(sync, "Line the sources up with source 1: finds each one's time offset by matching their audio, then refines it on the picture to the exact frame.");
        sync.Click += async (_, _) => await AutoSyncAsync();
        _busyDisabled.AddRange([addFiles, addFolder, sync]);
        var syncMode = new ComboBox
        {
            ItemsSource = new[] { "Sync: near (±90 s)", "Sync: whole title", "Sync: whole title + edits" },
            SelectedIndex = _settings.SyncMode switch
            {
                "Whole" => 1,
                "Edits" => 2,
                _ => 0,
            },
            FontSize = 12,
        };
        ToolTip.SetTip(syncMode, "Near: same moment ±90 s (fast).\nWhole title: finds any offset (decodes the other source's audio once).\nEdits: also maps different cuts — sections with their own offset, parts that are missing.");
        syncMode.SelectionChanged += (_, _) => _settings.SyncMode = syncMode.SelectedIndex switch
        {
            1 => "Whole",
            2 => "Edits",
            _ => "Near",
        };
        _busyDisabled.Add(syncMode);
        var srcButtons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        srcButtons.Children.AddRange([addFiles, addFolder, sync, syncMode]);
        var srcHeader = new TextBlock { Text = "Sources", Classes = { "h2" } };
        var hint = new TextBlock
        {
            Text = "Double-click a file or disc folder in the tree, or drop files, BDMV / VIDEO_TS folders or ISOs here. Source 1 is the reference; Compare lines the others up with it.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "muted" },
            FontSize = 12,
        };
        var left = new DockPanel { Margin = new Thickness(10, 10, 4, 10) };
        var leftTop = new StackPanel { Spacing = 8, Children = { srcHeader, srcButtons, hint } };
        DockPanel.SetDock(leftTop, Dock.Top);
        left.Children.Add(leftTop);
        // the same directory tree as Spot, with media files and disc folders; the added sources below it
        _compareTree.ActivateText = "Add as source";
        _compareTree.Roots = FolderNode.Roots(p => Disc.SourceOpener.MediaExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase) || Ces.Vfs.Vfs.IsRarSet(p)).ToList();
        _compareTree.NodeActivated += async node => await AddPathsAsync([node.FullPath]);
        _busyDisabled.Add(_compareTree);
        var sourcesLabel = new TextBlock { Text = "Added sources", Classes = { "header" }, Margin = new Thickness(2, 8, 0, 6) };
        var sourcesBox = new DockPanel();
        DockPanel.SetDock(sourcesLabel, Dock.Top);
        sourcesBox.Children.Add(sourcesLabel);
        sourcesBox.Children.Add(new ScrollViewer { Content = _sourcePanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var split = new Grid { RowDefinitions = new RowDefinitions("3*,5,2*"), Margin = new Thickness(0, 10, 0, 0) };
        split.Children.Add(_compareTree);
        var rowSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(rowSplitter, 1);
        split.Children.Add(rowSplitter);
        Grid.SetRow(sourcesBox, 2);
        split.Children.Add(sourcesBox);
        left.Children.Add(split);

        _tabs.Margin = new Thickness(4, 4, 10, 6);
        _tabs.Items.Add(new TabItem { Header = "Screenshots", Content = BuildShotsTab() });
        _tabs.Items.Add(new TabItem { Header = "Specs", Content = BuildSpecsTab() });
        _tabs.Items.Add(new TabItem { Header = "Video analysis", Content = BuildVideoTab() });
        _tabs.Items.Add(new TabItem { Header = "Audio analysis", Content = BuildAudioTab() });
        _tabs.Items.Add(new TabItem { Header = "Quality metrics", Content = BuildQualityTab() });
        _tabs.Items.Add(new TabItem { Header = "Remaster", Content = BuildGradeTab() });
        _tabs.Items.Add(new TabItem { Header = "Subtitles", Content = BuildSubsTab() });
        _tabs.Items.Add(new TabItem { Header = "Discs", Content = BuildDiscTab() });
        _tabs.Items.Add(new TabItem { Header = "BDInfo", Content = BuildBdInfoTab() });
        _tabs.Items.Add(new TabItem { Header = "Log", Content = new Border { Classes = { "panel" }, Child = _log, Margin = new Thickness(0, 6, 0, 0) } });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Math.Clamp(_settings.SidebarWidth, 200, 800).ToString(CultureInfo.InvariantCulture)},5,*") };
        _compareBody = body;
        body.Children.Add(left);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);
        // Copy / Save… for whatever tab is open, at the right end of the tab strip
        var tabHost = new Panel { Children = { _tabs, BuildTabActions() } };
        Grid.SetColumn(tabHost, 2);
        body.Children.Add(tabHost);

        _compareView = body;

        // mode switch: Spot (encoder spotting of MP3 / FLAC) or Compare (releases side by side)
        ToolTip.SetTip(_spotToggle, "Spot (Ctrl+1): which encoder made each MP3 / FLAC file, and is the stream intact");
        ToolTip.SetTip(_compareToggle, "Compare (Ctrl+2): screenshots, audio, video and quality metrics of releases side by side");
        _spotToggle.Click += (_, _) => SetMode(true);
        _compareToggle.Click += (_, _) => SetMode(false);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.D1, KeyModifiers.Control), Command = new SimpleCommand(() => ModeCommand(true)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5), Command = new SimpleCommand(() => { if (_spotMode) _spot.Rescan(); return Task.CompletedTask; }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.D2, KeyModifiers.Control), Command = new SimpleCommand(() => ModeCommand(false)) });
        var modeBar = new Border
        {
            Classes = { "footer" },
            Padding = new Thickness(10, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _spotToggle, _compareToggle, new Border { Width = 10 }, _modeHint } },
        }.Themed(Border.BorderBrushProperty, "EdgeBrush");
        DockPanel.SetDock(modeBar, Dock.Top);

        var dock = new DockPanel();
        dock.Children.Add(menu);
        dock.Children.Add(modeBar);
        dock.Children.Add(statusBar);
        dock.Children.Add(_modeHost);
        SetMode(_settings.Mode != "Compare");
        return dock;
    }

    private Task ModeCommand(bool spot)
    {
        SetMode(spot);
        return Task.CompletedTask;
    }

    private void SetMode(bool spot)
    {
        // the left pane keeps its width when switching
        if (_modeHost.Content != null)
        {
            var width = _spotMode ? _spot.SidebarWidth : _compareBody.ColumnDefinitions[0].ActualWidth;
            if (width > 0)
            {
                _settings.SidebarWidth = width;
                _spot.SidebarWidth = width;
                _compareBody.ColumnDefinitions[0].Width = new GridLength(width);
            }
        }
        _spotMode = spot;
        _settings.Mode = spot ? "Spot" : "Compare";
        _spotToggle.IsChecked = spot;
        _compareToggle.IsChecked = !spot;
        _modeHost.Content = spot ? _spot : _compareView;
        _viewSpot.IsChecked = spot;
        _viewCompare.IsChecked = !spot;
        _spotStatusRow.IsVisible = spot;
        _compareStatusRow.IsVisible = !spot;
        _modeHint.Text = spot
            ? "Which encoder made each MP3 / FLAC file, with which settings, and is the stream intact"
            : "Releases side by side: screenshots, audio, video, quality metrics, remasters, subtitles";
        if (spot)
            _spot.EnsureStarted();
        else if (!_compareTreeRevealed)
        {
            // like Spot: open the tree where the last source came from
            _compareTreeRevealed = true;
            if (Directory.Exists(_settings.LastFolder))
                _compareTree.Reveal(_settings.LastFolder, expand: true);
        }
    }

    private MenuItem SpotMenu()
    {
        MenuItem Toggle(string header, bool on, Action<bool> set)
        {
            var mi = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
            mi.Click += (_, _) => set(mi.IsChecked);
            return mi;
        }
        return new MenuItem
        {
            Header = "_Spot",
            Items =
            {
                InSpot("_Open folder…", () => _spot.OpenFolderAsync()),
                InSpot("_Add files…", () => _spot.AddFilesAsync()),
                InSpot("_Rescan  (F5)", () =>
                {
                    _spot.Rescan();
                    return Task.CompletedTask;
                }),
                InSpot("Compare _selected files", () =>
                {
                    _spot.CompareSelected();
                    return Task.CompletedTask;
                }),
                new Separator(),
                Toggle("Include _subfolders", _settings.SpotSubfolders, on => _spot.SetSubfolders(on)),
                Toggle("Only files with _problems", _settings.SpotProblemsOnly, on => _spot.SetProblemsOnly(on)),
                Toggle("_Decode FLAC (CRC + MD5 check)", _settings.SpotDecodeFlac, on => _settings.SpotDecodeFlac = on),
                Toggle("Include _video files and discs (needs ffmpeg)", _settings.SpotVideo, on =>
                {
                    _settings.SpotVideo = on;
                    _spot.Rescan();
                }),
                SpotColumnsMenu(),
                new Separator(),
                InSpot("Open saved spot _results…", () => _spot.OpenResultsAsync()),
                InSpot("Sa_ve spot results…", () => _spot.SaveResultsAsync(true)),
                InSpot("Save _details of selected files…", () => _spot.SaveSelectedDetailsAsync()),
                InSpot("_Export spot report…", () => _spot.ExportAsync()),
                InSpot("_Copy list as text", () => _spot.CopyListAsync()),
            },
        };
    }

    /// <summary>Spot › Columns: the column toggles, rebuilt each time it opens (the header's right-click menu too).</summary>
    private MenuItem SpotColumnsMenu()
    {
        var mi = new MenuItem { Header = "C_olumns", ItemsSource = new[] { new MenuItem { Header = "…" } } };
        mi.SubmenuOpened += (_, _) => mi.ItemsSource = _spot.ColumnMenuItems();
        return mi;
    }

    /// <summary>A Spot menu item: switches to Spot mode first.</summary>
    private MenuItem InSpot(string header, Func<Task> action, KeyGesture? gesture = null) =>
        MenuItemFor(header, () =>
        {
            SetMode(true);
            return action();
        }, gesture);

    /// <summary>A Compare menu item: switches to Compare mode first.</summary>
    private MenuItem InCompare(string header, Func<Task> action, KeyGesture? gesture = null) =>
        MenuItemFor(header, () =>
        {
            SetMode(false);
            return action();
        }, gesture);

    /// <summary>"Compare selected" in Spot mode: open those files as Compare sources.</summary>
    private async Task CompareFromSpotAsync(IReadOnlyList<string> paths)
    {
        SetMode(false);
        if (_tools == null)
        {
            await MessageBox.ShowAsync(this, "Compare mode needs ffmpeg: set its folder in File › Settings.", MessageKind.Warning);
            return;
        }
        await AddPathsAsync(paths.ToList());
    }

    /// <summary>MP3 / FLAC files and plain folders go to Spot mode; media files, discs and sessions to Compare.</summary>
    private static bool IsSpotInput(string p)
    {
        if (p.EndsWith(SpotUi.SpotSession.Extension, StringComparison.OrdinalIgnoreCase))
            return true;
        if (File.Exists(p) && Ces.Vfs.Vfs.IsRarSet(p))
            return RarIsAudioOnly(p);
        if (File.Exists(p))
            return Ces.Spot.AudioAnalyzer.IsSupported(p);
        if (!Directory.Exists(p))
            return false;
        if (Disc.SourceOpener.FirstRarIn(p) is { } rar)
            return RarIsAudioOnly(rar); // a packed release folder
        var name = Path.GetFileName(p.TrimEnd('\\', '/'));
        return !name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) && !name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(Path.Combine(p, "BDMV")) && !Directory.Exists(Path.Combine(p, "VIDEO_TS"));
    }

    /// <summary>A RAR set with MP3 / FLAC and nothing else Compare could open (video, ISO).</summary>
    private static bool RarIsAudioOnly(string rar)
    {
        var inside = Ces.Vfs.Vfs.FilesInside(rar, _ => true);
        return inside.Any(Ces.Spot.AudioAnalyzer.IsSupported)
            && !inside.Any(f => Disc.SourceOpener.MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !Ces.Spot.AudioAnalyzer.IsSupported(f));
    }

    private MenuItem MenuItemFor(string header, Func<Task> action, KeyGesture? gesture = null)
    {
        var mi = new MenuItem { Header = header, InputGesture = gesture };
        mi.Click += async (_, _) => await action();
        if (gesture != null)
        {
            KeyBindings.Add(new KeyBinding
            {
                Gesture = gesture,
                Command = new SimpleCommand(async () => await action()),
            });
        }
        return mi;
    }

    private const string Shortcuts = """
        Screenshots tab
          1 … 9            show that source
          Space            back to the previously shown source
          ← / →            previous / next source
          ↑ / ↓            previous / next screenshot time
          W / S / A        wipe / side by side / single view
          D / T            difference (|A − B| × gain) / flicker A ↔ B
          Shift+1 … 9      choose B (the right side / the other picture) in wipe, difference and flicker
          F or 0           fit;  Z  100 %;  mouse wheel  zoom;  drag  pan;  double-click  fit ↔ 100 %
          Shift+drag       pick a region: every source side by side, zoomed to it
          , / .            step the shown frame back / forward one frame (all sources)

        Spot mode
          Enter / double-click   details of the selected file
          Delete           remove from the list
          F5               rescan

        Anywhere
          Ctrl+1 / Ctrl+2  Spot / Compare mode
          Ctrl+O           open (Spot: folder · Compare: add files)
          Ctrl+L           open saved results (.cess spot results / .cesc comparison)
          Ctrl+S           save results (Spot: .cess · Compare: .cesc);  Ctrl+Shift+S  save as
          Ctrl+Shift+T     save the current Compare tab as text
          Ctrl+E           export (Spot: CSV / text report · Compare: HTML report)
          Ctrl+Shift+O     add disc folder    Ctrl+L  open comparison    Ctrl+S  save comparison
        """;

    private void ApplyRuntimeSettings()
    {
        Tools.InputBuilder.HardwareDecode = _settings.HardwareDecode ? (OperatingSystem.IsWindows() ? "d3d11va" : "auto") : null;
        Tools.ShotCache.Dir = _settings.ShotCacheGb > 0 ? Tools.ShotCache.DefaultDir : null;
        Tools.ShotCache.MaxBytes = Math.Max(1, _settings.ShotCacheGb) * 1024L * 1024 * 1024;
        Cache.ResultsCache.Shared.MaxBytes = Math.Max(0, _settings.ResultsCacheMb) * 1024L * 1024;
        _ = Task.Run(Cache.ResultsCache.Shared.Trim, CancellationToken.None);
    }

    private async Task StartupAsync(string[] args)
    {
        ApplyRuntimeSettings();
        var real = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
        if (real.Count > 0 && real.All(IsSpotInput))
        {
            // MP3 / FLAC on the command line ("Open with"): Spot mode, no ffmpeg needed for that
            SetMode(true);
            _spot.Open(real, append: false);
            args = [];
        }
        else if (real.Count > 0)
        {
            SetMode(false);
        }
        SetStatus("Looking for ffmpeg…");
        var (tools, error) = await FfTools.LocateAsync(_settings.FFmpegFolder, _settings.MediaInfoPath);
        _tools = tools;
        _toolsReady.TrySetResult(tools);
        if (tools == null)
        {
            SetStatus(error ?? "ffmpeg not found");
            if (!_spotMode)
                await MessageBox.ShowAsync(this, (error ?? "ffmpeg not found.") + "\n\nSet the ffmpeg folder in File › Settings.", MessageKind.Warning);
            return;
        }
        SetStatus($"Ready · {tools.Version}{(tools.MediaInfo == null ? " · MediaInfo not found (HDR/codec details limited)" : "")}{(tools.HasVmaf ? "" : " · no libvmaf")}");
        if (args.Length > 0)
            await AddPathsAsync(args.ToList());
        if (Environment.GetEnvironmentVariable("CES_TOUR") is { Length: > 0 } tour && _items.Count > 0)
            await RunTourAsync(tour);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _spot.Shutdown();
        var sidebar = _spotMode ? _spot.SidebarWidth : _compareBody.ColumnDefinitions[0].ActualWidth;
        if (sidebar > 0)
            _settings.SidebarWidth = sidebar;
        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        if (!_tour)
        {
            SaveShotSettings();
            _settings.Save();
        }
        if (_cts != null)
            await _cts.CancelAsync();
        DisposeShotBitmaps();
        try
        {
            await SourceOpener.CloseAllAsync();
            if (Directory.Exists(_temp))
                Directory.Delete(_temp, true);
        }
        catch (IOException)
        {
            // temp files stay
        }
    }

    // ---------- sources ----------

    private async Task AddFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add media files, ISOs or disc structure files",
            AllowMultiple = true,
            SuggestedStartLocation = await StartFolderAsync(),
            FileTypeFilter =
            [
                new FilePickerFileType("Media, ISO, RAR sets, disc files") { Patterns = SourceOpener.MediaExtensions.Select(e => "*" + e).Concat(["*.rar", "*.mpls", "*.ifo", "*.bdmv"]).ToList() },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        await AddPathsAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    private async Task AddFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a Blu-ray (BDMV) or DVD (VIDEO_TS) folder",
            AllowMultiple = true,
            SuggestedStartLocation = await StartFolderAsync(),
        });
        await AddPathsAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    /// <summary>Every release in a folder: media files, ISOs and disc folders one level down.</summary>
    private async Task AddReleaseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder with the releases to compare",
            SuggestedStartLocation = await StartFolderAsync(),
        });
        var dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (dir == null)
            return;
        var paths = SourceOpener.ExpandFolder(dir);
        if (paths.Count == 0)
        {
            await MessageBox.ShowAsync(this, "No video files, ISOs or disc folders in that folder.", MessageKind.Info);
            return;
        }
        await AddPathsAsync(paths);
    }

    private async Task<IStorageFolder?> StartFolderAsync() =>
        Directory.Exists(_settings.LastFolder) ? await StorageProvider.TryGetFolderFromPathAsync(_settings.LastFolder) : null;

    private async Task AddPathsAsync(List<string> paths)
    {
        if (paths.Count == 0 || _tools == null)
            return;
        var session = paths.FirstOrDefault(p => p.EndsWith(SessionExtension, StringComparison.OrdinalIgnoreCase));
        if (session != null)
        {
            await LoadSessionAsync(session);
            return;
        }
        await RunJobAsync("Opening sources", async ct =>
        {
            foreach (var p in paths)
            {
                SetStatus("Opening " + p);
                try
                {
                    var src = await SourceOpener.OpenAsync(p, _settings.IncludeShortPlaylists, ct);
                    var item = new CompareItem { Source = src, Title = src.MainTitle! };
                    item.Synced = item.Title.IsStill;
                    await new MediaProber(_tools).ProbeAsync(src, item.Title, ct);
                    _items.Add(item);
                    _settings.LastFolder = Path.GetDirectoryName(src.Path.TrimEnd('\\', '/')) ?? _settings.LastFolder;
                    AppendLog($"Opened {src.DisplayName}: {item.Title}");
                    if (await RestoreFromCacheAsync(item, ct) is { Count: > 0 } back)
                        AppendLog($"From the results cache for {item.Name}: {string.Join(", ", back)}");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    await MessageBox.ShowAsync(this, $"Could not open\n{p}\n\n{ex.Message}", MessageKind.Error);
                }
            }
            SourcesChanged();
        });
    }

    private async Task RemoveSourceAsync(CompareItem item)
    {
        var idx = _items.IndexOf(item);
        if (idx < 0)
            return;
        _items.RemoveAt(idx);
        RemoveShotColumn(idx);
        SourcesChanged();
        await SourceOpener.CloseAsync(item.Source);
        if (idx == 0)
            await RetakeAllAsync();
    }

    /// <summary>Rebuilds every view that depends on the list of sources.</summary>
    private void SourcesChanged()
    {
        RebuildSourceCards();
        RefreshSpecs();
        RefreshShotView();
        RefreshVideoResults();
        RefreshAudioSetup();
        RefreshAudioResults();
        RefreshQualitySetup();
        RefreshDiscTab();
        RefreshBdInfoTab();
        RefreshSubtitleChoices();
        RefreshSubsSetup();
        RefreshSubResults();
        RefreshGradeResults();
    }

    private void RebuildSourceCards()
    {
        _sourcePanel.Children.Clear();
        _cardControls.Clear();
        for (var i = 0; i < _items.Count; i++)
            _sourcePanel.Children.Add(SourceCard(i, _items[i]));
    }

    private Control SourceCard(int index, CompareItem item)
    {
        var chip = new Border
        {
            Background = Palette[index % Palette.Length],
            CornerRadius = new CornerRadius(4),
            Width = 24,
            Height = 24,
            Child = new TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture), Foreground = Brushes.Black, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Top,
        };
        var name = new TextBox { Text = item.Name, FontWeight = FontWeight.SemiBold, MinHeight = 24, Padding = new Thickness(4, 2), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        ToolTip.SetTip(name, item.Source.Path + "\n(click to rename the label)");
        name.LostFocus += (_, _) =>
        {
            var t = name.Text?.Trim() ?? "";
            item.Label = t == item.Source.DisplayName ? "" : t;
            RefreshSpecs();
            RefreshShotView();
        };
        var remove = new Button { Content = "✕", Classes = { "small" }, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(remove, "Remove this source");
        remove.Click += async (_, _) => await RemoveSourceAsync(item);

        var info = item.Title.Info;
        var v = info?.Video;
        var summary = string.Join(" · ", new[]
        {
            item.Source.Kind == SourceKind.File ? Path.GetExtension(item.Source.Path).TrimStart('.').ToUpperInvariant() : item.Source.Kind == SourceKind.BluRay ? "Blu-ray" : "DVD",
            Format.Time(item.Title.Duration, false),
            v != null ? $"{v.Width}×{v.Height} {v.CodecDisplay} {v.BitDepth}-bit {v.HdrShort}".Trim() : "no video",
            info != null ? $"{info.Audio.Count()} audio" : "",
        }.Where(s => s.Length > 0));

        var body = new StackPanel { Spacing = 5 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        top.Children.Add(chip);
        Grid.SetColumn(name, 1);
        top.Children.Add(name);
        Grid.SetColumn(remove, 2);
        top.Children.Add(remove);
        body.Children.Add(top);
        body.Children.Add(new TextBlock { Text = summary, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        if (item.Source.Titles.Count > 1)
        {
            var titles = new ComboBox { ItemsSource = item.Source.Titles, SelectedItem = item.Title, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            ToolTip.SetTip(titles, "Title / playlist to compare (the longest is picked first).");
            titles.SelectionChanged += async (_, _) =>
            {
                if (titles.SelectedItem is MediaTitle t && t != item.Title)
                    await ChangeTitleAsync(item, t);
            };
            _cardControls.Add(titles);
            body.Children.Add(titles);
        }

        if (index > 0)
        {
            var fps = Tools.InputBuilder.FrameRate(_items[0].Title) is > 0 and var f ? f : 24000 / 1001.0;
            var offset = new TextBox { Text = item.Offset.ToString("0.000#", CultureInfo.InvariantCulture), Width = 92, HorizontalContentAlignment = HorizontalAlignment.Right };
            ToolTip.SetTip(offset, "Offset in seconds: the same moment comes this much later in this source (negative = earlier). Enter applies.");
            async Task Apply(double value)
            {
                item.Offset = Math.Round(value, 6);
                item.Synced = true;
                offset.Text = item.Offset.ToString("0.000#", CultureInfo.InvariantCulture);
                RefreshSpecs();
                await RetakeSourceAsync(_items.IndexOf(item));
            }
            Avalonia.Automation.AutomationProperties.SetName(offset, $"Offset {index + 1}");
            // applies on Enter, or by itself once typing pauses
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            debounce.Tick += async (_, _) =>
            {
                debounce.Stop();
                if (Format.TryParseTime(offset.Text, out var val) && Math.Abs(val - item.Offset) > 0.0006)
                    await Apply(val);
            };
            offset.TextChanged += (_, _) =>
            {
                debounce.Stop();
                debounce.Start();
            };
            offset.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter && Format.TryParseTime(offset.Text, out var val))
                {
                    e.Handled = true;
                    debounce.Stop();
                    await Apply(val);
                }
            };
            var minus = new Button { Content = "−1 frame", Classes = { "small" } };
            minus.Click += async (_, _) => await Apply(item.Offset - 1 / fps);
            var plus = new Button { Content = "+1 frame", Classes = { "small" } };
            plus.Click += async (_, _) => await Apply(item.Offset + 1 / fps);
            _cardControls.AddRange([minus, plus]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "Offset", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } }, offset, minus, plus } };
            body.Children.Add(row);

            // speed: same frames played faster/slower (PAL speed-up)
            var speeds = new (string Name, double Value)[]
            {
                ("Speed: same", 1),
                ("PAL speed-up (23.976 → 25)", 24000 / 1001.0 / 25),
                ("PAL speed-up (24 → 25)", 24 / 25.0),
                ("Slowed to 23.976 (25 → 23.976)", 25 / (24000 / 1001.0)),
                ("Slowed to 24 (25 → 24)", 25 / 24.0),
            };
            var speedBox = new ComboBox { ItemsSource = speeds.Select(x => x.Name).ToList(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
            var si = Array.FindIndex(speeds, x => Math.Abs(x.Value - item.Speed) < 1e-5);
            if (si < 0)
            {
                speedBox.ItemsSource = speeds.Select(x => x.Name).Append($"Speed ×{item.Speed:0.#####}").ToList();
                si = speeds.Length;
            }
            speedBox.SelectedIndex = si;
            ToolTip.SetTip(speedBox, "Source seconds per reference second. Compare detects a PAL speed-up from the frame rates.");
            speedBox.SelectionChanged += async (_, _) =>
            {
                if (speedBox.SelectedIndex >= 0 && speedBox.SelectedIndex < speeds.Length && Math.Abs(speeds[speedBox.SelectedIndex].Value - item.Speed) > 1e-9)
                {
                    item.Speed = speeds[speedBox.SelectedIndex].Value;
                    RefreshSpecs();
                    await RetakeSourceAsync(_items.IndexOf(item));
                }
            };
            _cardControls.Add(speedBox);
            body.Children.Add(speedBox);

            if (item.Sections.Count > 0)
            {
                var lines = item.Sections.Select((s, k) =>
                    $"{Format.Time(s.Start, false)} – {(k + 1 < item.Sections.Count ? Format.Time(item.Sections[k + 1].Start, false) : "end")}: " +
                    (s.Offset is { } o ? $"{o:+0.000;-0.000;0.000} s" : "not in this source"));
                var missing = item.Sections.Count(s => s.Offset == null);
                var edit = new TextBlock
                {
                    Text = $"Different edit: {item.Sections.Count} sections{(missing > 0 ? $", {missing} missing" : "")}",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Themed(TextBlock.ForegroundProperty, "FindingWarnBrush");
                ToolTip.SetTip(edit, string.Join("\n", lines));
                var clear = new Button { Content = "Clear", Classes = { "small" } };
                ToolTip.SetTip(clear, "Forget the sections and use one offset for the whole title.");
                clear.Click += async (_, _) =>
                {
                    var keep = item.Offset;
                    item.Sections.Clear();
                    item.Offset = keep;
                    RebuildSourceCards();
                    RefreshSpecs();
                    await RetakeSourceAsync(_items.IndexOf(item));
                };
                _cardControls.Add(clear);
                body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { edit, clear } });
            }
        }
        else
        {
            body.Children.Add(new TextBlock { Text = "Reference (timeline)", Classes = { "muted" }, FontSize = 12 });
        }
        return new Border { Classes = { "card" }, Child = body };
    }

    private async Task ChangeTitleAsync(CompareItem item, MediaTitle title)
    {
        if (_tools == null)
            return;
        await RunJobAsync("Reading title", async ct =>
        {
            if (title.Info == null)
                await new MediaProber(_tools).ProbeAsync(item.Source, title, ct);
            item.Title = title;
            item.Video = null;
            item.Audio.Clear();
            SourcesChanged();
        });
        await RetakeSourceAsync(_items.IndexOf(item));
    }

    private async Task AutoSyncAsync(bool onlyUnsynced = false, bool quiet = false)
    {
        if (_tools == null || _items.Count < 2)
        {
            await MessageBox.ShowAsync(this, "Add at least two sources with audio.", MessageKind.Info);
            return;
        }
        var lines = new List<string>();
        await RunJobAsync("Compare (sync)", async ct =>
        {
            var detector = new Analysis.SyncDetector(_tools, _temp);
            for (var i = 1; i < _items.Count; i++)
            {
                if ((onlyUnsynced && _items[i].Synced) || _items[i].Title.IsStill || _items[0].Title.IsStill)
                    continue;
                if (_items[0].Title.Info?.Audio.Any() != true || _items[i].Title.Info?.Audio.Any() != true)
                {
                    lines.Add($"{i + 1}. {_items[i].Name}: no audio to match");
                    continue;
                }
                SetStatus($"Matching audio of source {i + 1}…");
                var prog = new Progress<string>(SetStatus);
                var sopt = new Analysis.SyncOptions
                {
                    MaxShift = 90,
                    WholeTitle = _settings.SyncMode is "Whole" or "Edits",
                    DetectEdits = _settings.SyncMode == "Edits",
                };
                var r = await detector.DetectAsync(_items[0].Title, _items[i].Title, sopt, prog, ct);
                foreach (var p in r.Probes)
                    AppendLog($"sync {i + 1} at {Format.Time(p.RefTime, false)}: {p.Offset:+0.000;-0.000;0.000} s (confidence {p.Confidence:0.0})");
                if (r.Reliable)
                {
                    var it = _items[i];
                    it.Sections.Clear();
                    it.Speed = r.Speed;
                    it.Offset = Math.Round(r.Offset, 4);
                    it.Sections.AddRange(r.Sections);
                    it.Synced = true;
                }
                lines.Add($"{i + 1}. {_items[i].Name}: {r.Summary}");
            }
            RebuildSourceCards();
            RefreshSpecs();
        });
        if (lines.Count > 0)
        {
            if (!quiet)
                await MessageBox.ShowAsync(this, string.Join("\n\n", lines), MessageKind.Info, "Compare");
            else
                AppendLog(string.Join(Environment.NewLine, lines));
            await RetakeAllAsync();
        }
    }

    // ---------- jobs ----------

    private bool Busy => _cts != null;

    /// <summary>Runs one background job at a time with status, progress and cancel.</summary>
    private async Task<bool> RunJobAsync(string name, Func<CancellationToken, Task> job)
    {
        if (Busy)
        {
            await MessageBox.ShowAsync(this, "Another task is still running — wait for it or cancel it first.", MessageKind.Info);
            return false;
        }
        _cts = new CancellationTokenSource();
        foreach (var c in BusyControls)
            c.IsEnabled = false;
        _job.Start(name);
        SetStatus(name + "…");
        AppendLog($"== {name}");
        var ok = false;
        try
        {
            await job(_cts.Token);
            ok = true;
            SetStatus(name + " — done.");
            await StoreInCacheAsync();
        }
        catch (OperationCanceledException)
        {
            SetStatus(name + " — cancelled.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FormatException)
        {
            SetStatus(name + " — failed.");
            AppendLog("ERROR " + ex.Message);
            await MessageBox.ShowAsync(this, ex.Message, MessageKind.Error, name);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            foreach (var c in BusyControls)
                c.IsEnabled = true;
            _job.Finish();
        }
        return ok;
    }

    private void SetProgress(double fraction)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _job.Report(fraction);
        });
    }

    private void SetStatus(string text) => Dispatcher.UIThread.Post(() => _status.Text = text);

    private void AppendLog(string line)
    {
        var t = _log.Text ?? "";
        if (t.Length > 400_000)
            t = t[^300_000..];
        _log.Text = t + line + Environment.NewLine;
        _log.CaretIndex = _log.Text.Length;
    }

    // ---------- keyboard ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (_tabs.SelectedIndex == 0)
            e.Handled = HandleShotKey(e);
    }

    // ---------- helpers ----------

    private static TextBlock Muted(string text, double size = 12) => new() { Text = text, Classes = { "muted" }, FontSize = size, TextWrapping = TextWrapping.Wrap };

    private static Border Chip(int index) => new()
    {
        Background = Palette[index % Palette.Length],
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(5, 0),
        Child = new TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture), Foreground = Brushes.Black, FontWeight = FontWeight.Bold, FontSize = 12 },
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel ChipLabel(int index, string text, bool bold = true) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children = { Chip(index), new TextBlock { Text = text, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } },
    };

    private static Bitmap? TryLoadBitmap(string? path)
    {
        if (path == null || !File.Exists(path))
            return null;
        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task ShowAboutAsync()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        var details = string.Join(Environment.NewLine,
            $"Version {v}",
            $"ffmpeg: {_tools?.FFmpeg ?? "not found"}",
            _tools?.Version ?? "",
            $"MediaInfo: {_tools?.MediaInfo ?? "not found"}");
        await new AboutDialog(details).ShowDialog(this);
    }

    private async Task ShowSettingsAsync()
    {
        var dlg = new SettingsDialog(_settings);
        if (await dlg.ShowDialog<bool>(this))
        {
            _settings.Save();
            App.ApplyTheme(_settings.Theme);
            ApplyRuntimeSettings();
            var (tools, error) = await FfTools.LocateAsync(_settings.FFmpegFolder, _settings.MediaInfoPath);
            if (tools != null)
            {
                _tools = tools;
                SetStatus($"Ready · {tools.Version}");
            }
            else
            {
                await MessageBox.ShowAsync(this, error ?? "ffmpeg not found.", MessageKind.Warning);
            }
        }
    }

    private sealed class SimpleCommand(Func<Task> action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public async void Execute(object? parameter) => await action();
    }
}
