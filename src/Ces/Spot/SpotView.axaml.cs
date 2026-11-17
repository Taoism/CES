using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EncoderSpot.Core;
using EncoderSpot.Models;
using EncoderSpot.Services;

namespace EncoderSpot;

public sealed partial class MainWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<FileRow> _rows = [];
    private readonly DataGridCollectionView _view;
    private readonly ConcurrentQueue<(FileRow Row, AnalysisResult Result)> _done = new();
    private readonly DispatcherTimer _flush;
    private CancellationTokenSource? _cts;
    private int _pending;
    private int _total;
    private bool _revealing;
    private string[] _currentInputs = [];
    private string? _openWhenDone;

    public static WindowIcon AppIcon { get; } = new(AssetLoader.Open(new Uri("avares://EncoderSpot/Assets/encoderspot.png")));

    public MainWindow() : this([])
    {
    }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        App.ApplyTheme(_settings.Theme);
        _view = new DataGridCollectionView(_rows) { Filter = FilterRow };
        Grid.ItemsSource = _view;
        FolderTree.ItemsSource = FolderNode.Roots().ToList();
        SubfoldersBox.IsChecked = _settings.IncludeSubfolders;
        SubfoldersMenu.IsChecked = _settings.IncludeSubfolders;
        ProblemsMenu.IsChecked = _settings.ProblemsOnly;
        DecodeMenu.IsChecked = _settings.DecodeFlac;
        (_settings.Theme switch { "Dark" => ThemeDark, "Light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(_settings.TreeWidth, 120, 800));
        RestorePlacement();

        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Flush());
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        KeyDown += OnWindowKeyDown;
        Closing += (_, _) => SavePlacement();

        Opened += (_, _) =>
        {
            var start = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();
            if (start.Length == 1 && File.Exists(start[0]))
            {
                // "Open with EncoderSpot" on one file: show its details as soon as it is analyzed.
                _openWhenDone = Path.GetFullPath(start[0]);
            }
            if (start.Length > 0)
            {
                Load(start, false);
            }
            else if (_settings.LastFolder is { } last && Directory.Exists(last))
            {
                Load([last], false);
            }
            UpdateStatus();
        };
    }

    // ---------------------------------------------------------------- loading

    private void Load(string[] inputs, bool append)
    {
        if (!append)
        {
            Cancel();
            _rows.Clear();
            _done.Clear();
            _currentInputs = inputs;
        }
        else
        {
            _currentInputs = [.. _currentInputs, .. inputs];
        }
        if (inputs.Length == 1 && Directory.Exists(inputs[0]))
        {
            _settings.LastFolder = inputs[0];
            PathBox.Text = inputs[0];
            Reveal(inputs[0]);
        }
        else if (inputs.Length > 0)
        {
            PathBox.Text = inputs[0];
        }
        bool recursive = _settings.IncludeSubfolders;
        _cts ??= new CancellationTokenSource();
        var ct = _cts.Token;
        var existing = new HashSet<string>(_rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        ProgressText.Text = "finding files…";
        Task.Run(() =>
        {
            var files = new List<string>();
            foreach (var input in inputs)
            {
                if (File.Exists(input))
                {
                    files.Add(Path.GetFullPath(input));
                }
                else if (Directory.Exists(input))
                {
                    files.AddRange(Enumerate(input, recursive, ct));
                }
            }
            return files.Where(f => existing.Add(f)).ToList();
        }, ct).ContinueWith(t =>
        {
            if (t.IsCanceled || t.IsFaulted || ct.IsCancellationRequested)
            {
                return;
            }
            var rows = t.Result.Select(f => new FileRow(f)).ToList();
            foreach (var r in rows)
            {
                _rows.Add(r);
            }
            Analyze(rows, ct);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static IEnumerable<string> Enumerate(string folder, bool recursive, CancellationToken ct)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = recursive, AttributesToSkip = FileAttributes.System };
        IEnumerable<string> found;
        try
        {
            found = Directory.EnumerateFiles(folder, "*", opts).Where(AudioAnalyzer.IsSupported).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        ct.ThrowIfCancellationRequested();
        return found.Order(StringComparer.OrdinalIgnoreCase);
    }

    private void Analyze(List<FileRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            UpdateStatus();
            return;
        }
        _total += rows.Count;
        _pending += rows.Count;
        StopButton.IsEnabled = true;
        Progress.IsVisible = true;
        _flush.Start();
        var options = new AnalyzeOptions { DecodeFlac = _settings.DecodeFlac };
        int threads = Math.Max(1, Environment.ProcessorCount / 2);
        Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, row =>
                {
                    var result = AudioAnalyzer.Analyze(row.Path, options, ct);
                    _done.Enqueue((row, result));
                });
            }
            catch (OperationCanceledException)
            {
                // stopped by the user or a new folder
            }
        }, ct);
    }

    private void Flush()
    {
        int n = 0;
        while (_done.TryDequeue(out var item))
        {
            item.Row.Result = item.Result;
            if (_openWhenDone is not null && item.Row.Path.Equals(_openWhenDone, StringComparison.OrdinalIgnoreCase))
            {
                _openWhenDone = null;
                Grid.SelectedItem = item.Row;
                new DetailsWindow(item.Row, _settings, Reanalyze).Show(this);
            }
            _pending--;
            n++;
        }
        if (n > 0 && (ProblemsMenu.IsChecked || !string.IsNullOrEmpty(FilterBox.Text)))
        {
            _view.Refresh();
        }
        UpdateStatus();
        if (_pending <= 0)
        {
            _pending = 0;
            _total = 0;
            _flush.Stop();
            StopButton.IsEnabled = false;
            Progress.IsVisible = false;
            ProgressText.Text = "";
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _pending = 0;
        _total = 0;
        _flush?.Stop();
        StopButton.IsEnabled = false;
        Progress.IsVisible = false;
        ProgressText.Text = "";
    }

    private void UpdateStatus()
    {
        var done = _rows.Where(r => r.IsDone && r.Result!.Error is null).ToList();
        long bytes = _rows.Sum(r => r.Size);
        double seconds = done.Sum(r => r.Seconds);
        double kbits = done.Sum(r => r.Bitrate * r.Seconds);
        int problems = _rows.Count(r => r.HasProblems);
        var sb = new StringBuilder();
        sb.Append(string.Create(Inv, $"{_rows.Count} files, {bytes / 1048576.0:0.0} MB"));
        if (seconds > 0)
        {
            sb.Append(string.Create(Inv, $", {Fmt.Duration(seconds)}, {kbits / seconds:0.0} kbps average"));
        }
        if (problems > 0)
        {
            sb.Append(string.Create(Inv, $", {problems} with problems"));
        }
        StatusText.Text = sb.ToString();
        if (_total > 0 && _pending > 0)
        {
            Progress.Maximum = _total;
            Progress.Value = _total - _pending;
            ProgressText.Text = string.Create(Inv, $"analyzing {_total - _pending} / {_total}");
        }
    }

    private bool FilterRow(object o)
    {
        var r = (FileRow)o;
        if (_settings.ProblemsOnly && r.IsDone && !r.HasProblems)
        {
            return false;
        }
        string? f = FilterBox?.Text;
        if (string.IsNullOrWhiteSpace(f))
        {
            return true;
        }
        return r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Encoder.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Settings.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Folder.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Artist.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Expands the folder tree down to <paramref name="path"/> and selects it, without reloading the list.</summary>
    private void Reveal(string path)
    {
        if (FolderTree.ItemsSource is not IEnumerable<FolderNode> roots)
        {
            return;
        }
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Norm(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = roots.Where(r => r.FullPath.Length > 0 && (full.Equals(Norm(r.FullPath), cmp) || full.StartsWith(Norm(r.FullPath) + Path.DirectorySeparatorChar, cmp)))
            .OrderByDescending(r => r.FullPath.Length).FirstOrDefault();
        if (root is null)
        {
            return;
        }
        _revealing = true;
        try
        {
            var node = root;
            while (!Norm(node.FullPath).Equals(full, cmp))
            {
                node.IsExpanded = true;
                var next = node.Children.FirstOrDefault(c => c.FullPath.Length > 0 &&
                    (full.Equals(c.FullPath, cmp) || full.StartsWith(c.FullPath + Path.DirectorySeparatorChar, cmp)));
                if (next is null)
                {
                    break;
                }
                node = next;
            }
            FolderTree.SelectedItem = node;
            Dispatcher.UIThread.Post(() =>
            {
                if (FolderTree.TreeContainerFromItem(node) is Control c)
                {
                    c.BringIntoView();
                }
                _revealing = false;
            }, DispatcherPriority.Background);
        }
        catch
        {
            _revealing = false;
            throw;
        }
    }

    // ---------------------------------------------------------------- events

    private void OnTreeSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (_revealing || FolderTree.SelectedItem is not FolderNode node || node.FullPath.Length == 0)
        {
            return;
        }
        Load([node.FullPath], false);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var start = _settings.LastFolder is { } lf && Directory.Exists(lf) ? await StorageProvider.TryGetFolderFromPathAsync(lf) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open folder", SuggestedStartLocation = start });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null)
        {
            Load([path], false);
        }
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("MP3 / FLAC") { Patterns = AudioAnalyzer.Extensions.Select(x => "*" + x).ToList() },
                FilePickerFileTypes.All,
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
        {
            Load(paths, true);
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        if (paths.Length == 0)
        {
            return;
        }
        bool replace = paths.Length == 1 && Directory.Exists(paths[0]);
        Load(paths, !replace);
    }

    private void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (_currentInputs.Length > 0)
        {
            Load(_currentInputs, false);
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        Cancel();
        UpdateStatus();
    }

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        string p = (PathBox.Text ?? "").Trim().Trim('"');
        if (File.Exists(p) || Directory.Exists(p))
        {
            Load([p], false);
        }
        e.Handled = true;
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => _view?.Refresh();

    private void OnSubfoldersBoxChanged(object? sender, RoutedEventArgs e)
    {
        bool on = SubfoldersBox.IsChecked == true;
        if (on == _settings.IncludeSubfolders)
        {
            return;
        }
        SetSubfolders(on);
    }

    private void OnToggleSubfolders(object? sender, RoutedEventArgs e) => SetSubfolders(SubfoldersMenu.IsChecked);

    private void SetSubfolders(bool on)
    {
        _settings.IncludeSubfolders = on;
        SubfoldersMenu.IsChecked = on;
        SubfoldersBox.IsChecked = on;
        OnRescan(null, new RoutedEventArgs());
    }

    private void OnToggleProblems(object? sender, RoutedEventArgs e)
    {
        _settings.ProblemsOnly = ProblemsMenu.IsChecked;
        _view.Refresh();
    }

    private void OnToggleDecode(object? sender, RoutedEventArgs e) => _settings.DecodeFlac = DecodeMenu.IsChecked;

    private void OnTheme(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string theme })
        {
            _settings.Theme = theme;
            App.ApplyTheme(theme);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.F5)
        {
            OnRescan(null, e);
        }
        else if (ctrl && e.Key == Key.O)
        {
            if (shift)
            {
                OnAddFiles(null, e);
            }
            else
            {
                OnOpenFolder(null, e);
            }
        }
        else if (ctrl && e.Key == Key.E)
        {
            OnExport(null, e);
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnDetails(null, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            OnRemove(null, e);
            e.Handled = true;
        }
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e) => OnDetails(null, e);

    private IEnumerable<FileRow> SelectedRows => Grid.SelectedItems.OfType<FileRow>().ToList();

    private void OnDetails(object? sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is FileRow { IsDone: true } row)
        {
            new DetailsWindow(row, _settings, Reanalyze).Show(this);
        }
    }

    private void Reanalyze(FileRow row)
    {
        _cts ??= new CancellationTokenSource();
        Analyze([row], _cts.Token);
    }

    private void OnReanalyze(object? sender, RoutedEventArgs e)
    {
        foreach (var r in SelectedRows)
        {
            Reanalyze(r);
        }
    }

    private void OnOpenContaining(object? sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is FileRow row)
        {
            ShowInFileManager(row.Path);
        }
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, SelectedRows.Select(r => r.Path));
        if (Clipboard is not null && text.Length > 0)
        {
            await Clipboard.SetTextAsync(text);
        }
    }

    private async void OnCopyInfo(object? sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, SelectedRows.Where(r => r.IsDone).Select(r => Report.Text(r.Result!)));
        if (Clipboard is not null && text.Length > 0)
        {
            await Clipboard.SetTextAsync(text);
        }
    }

    private async void OnCopyList(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null)
        {
            await Clipboard.SetTextAsync(Report.Table(_view.OfType<FileRow>()));
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        foreach (var r in SelectedRows)
        {
            _rows.Remove(r);
        }
        UpdateStatus();
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export report",
            SuggestedFileName = "encoderspot-report.csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV (spreadsheet)") { Patterns = ["*.csv"] },
                new FilePickerFileType("Text report with details") { Patterns = ["*.txt"] },
            ],
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }
        var rows = _view.OfType<FileRow>().Where(r => r.IsDone).ToList();
        string content = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? string.Join(Environment.NewLine, rows.Select(r => Report.Text(r.Result!, details: true)))
            : Report.Csv(rows);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), CancellationToken.None);
    }

    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        var dlg = new Window
        {
            Title = "About EncoderSpot",
            Icon = AppIcon,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 14,
                    Children =
                    {
                        new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://EncoderSpot/Assets/encoderspot.png"))), Width = 56, Height = 56 },
                        new StackPanel
                        {
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = "EncoderSpot " + version, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                                new TextBlock { Text = "MP3 and FLAC encoder spotter and stream analyzer", Classes = { "muted" } },
                            },
                        },
                    },
                },
                new TextBlock
                {
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = "A modern take on EncSpot (Jon Dee, Guerillasoft, 2001–2005) for Windows, Linux and macOS. "
                         + "It reads every frame: MPEG headers, Layer III side info and bit reservoir, Xing/Info/LAME and VBRI headers, "
                         + "and decodes FLAC to check frame CRCs and the MD5 signature.",
                },
                ok,
            },
        };
        dlg.ShowDialog(this);
    }

    internal static void ShowInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("/usr/bin/open", ["-R", path]);
            }
            else
            {
                string xdg = File.Exists("/usr/bin/xdg-open") ? "/usr/bin/xdg-open" : "/usr/local/bin/xdg-open";
                Process.Start(xdg, [Path.GetDirectoryName(path) ?? path]);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no file manager available
        }
    }

    // ---------------------------------------------------------------- window placement

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (!double.IsNaN(_settings.WindowX) && !double.IsNaN(_settings.WindowY))
        {
            var pos = new PixelPoint((int)_settings.WindowX, (int)_settings.WindowY);
            if (Screens.All.Any(s => s.WorkingArea.Contains(pos + new PixelPoint(40, 40))))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = pos;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        Cancel();
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.TreeWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
        _settings.Save();
    }
}
