using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ces.Spot;

namespace Ces.SpotUi;

/// <summary>Spot mode: folder tree + file list with encoder guesses and stream checks (the former EncoderSpot window).</summary>
public sealed partial class SpotView : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<FileRow> _rows = [];
    private readonly DataGridCollectionView _view;
    private readonly ConcurrentQueue<(FileRow Row, AnalysisResult Result)> _done = new();
    private readonly DispatcherTimer _flush;
    private CancellationTokenSource? _cts;
    private int _pending;
    private int _total;
    private bool _revealing;
    private bool _treeLoaded;
    private string[] _currentInputs = [];
    private string? _openWhenDone;

    /// <summary>Status line text, files done, files to do (0 = nothing running) for the window's status bar.</summary>
    public event Action<string, int, int>? StatusChanged;

    /// <summary>The user asked to compare these files (switch to Compare mode).</summary>
    public event Action<IReadOnlyList<string>>? CompareRequested;

    public SpotView() : this(new AppSettings())
    {
    }

    public SpotView(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _view = new DataGridCollectionView(_rows) { Filter = FilterRow };
        Grid.ItemsSource = _view;
        SubfoldersBox.IsChecked = _settings.SpotSubfolders;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 200, 800));
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Flush());
    }

    public int Count => _rows.Count;

    /// <summary>Set when paths come from the command line: do not load the last folder first.</summary>
    public bool SkipLastFolder { get; set; }

    /// <summary>Fills the folder tree and loads the last folder; called when Spot mode is first shown.</summary>
    public void EnsureStarted()
    {
        if (_treeLoaded)
        {
            return;
        }
        _treeLoaded = true;
        FolderTree.ItemsSource = FolderNode.Roots().ToList();
        if (!SkipLastFolder && _rows.Count == 0 && _settings.SpotFolder.Length > 0 && Directory.Exists(_settings.SpotFolder))
        {
            Load([_settings.SpotFolder], false);
        }
        UpdateStatus();
    }

    /// <summary>Saves layout state and stops work (window closing).</summary>
    public void Shutdown()
    {
        Cancel();
    }

    /// <summary>Width of the left pane (kept the same in both modes).</summary>
    public double SidebarWidth
    {
        get => MainGrid.ColumnDefinitions[0].ActualWidth;
        set => MainGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(value, 200, 800));
    }

    public bool IsBusy => _pending > 0;

    public void Stop()
    {
        Cancel();
        UpdateStatus();
    }

    public void CompareSelected() => OnCompareSelected(null, new RoutedEventArgs());

    /// <summary>Opens files/folders; a single file also opens its details window when analyzed.</summary>
    public void Open(IReadOnlyList<string> paths, bool append)
    {
        if (paths.Count == 1 && File.Exists(paths[0]))
        {
            _openWhenDone = Path.GetFullPath(paths[0]);
        }
        Load([.. paths], append);
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
            _settings.SpotFolder = inputs[0];
            PathBox.Text = inputs[0];
            Reveal(inputs[0]);
        }
        else if (inputs.Length > 0)
        {
            PathBox.Text = inputs[0];
        }
        bool recursive = _settings.SpotSubfolders;
        _cts ??= new CancellationTokenSource();
        var ct = _cts.Token;
        var existing = new HashSet<string>(_rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        StatusChanged?.Invoke("Finding files…", 0, 1);
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
            return files.Where(existing.Add).ToList();
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
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static IEnumerable<string> Enumerate(string folder, bool recursive, CancellationToken ct)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = recursive, AttributesToSkip = FileAttributes.System };
        List<string> found;
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
        _flush.Start();
        var options = new AnalyzeOptions { DecodeFlac = _settings.SpotDecodeFlac };
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
            _pending--;
            n++;
            if (_openWhenDone is not null && item.Row.Path.Equals(_openWhenDone, StringComparison.OrdinalIgnoreCase))
            {
                _openWhenDone = null;
                Grid.SelectedItem = item.Row;
                ShowDetails(item.Row);
            }
        }
        if (n > 0 && (_settings.SpotProblemsOnly || !string.IsNullOrEmpty(FilterBox.Text)))
        {
            _view.Refresh();
        }
        UpdateStatus();
        if (_pending <= 0)
        {
            _pending = 0;
            _total = 0;
            _flush.Stop();
            UpdateStatus();
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
        if (_total > 0 && _pending > 0)
        {
            sb.Append(string.Create(Inv, $" · analyzing {_total - _pending} / {_total}"));
            StatusChanged?.Invoke(sb.ToString(), _total - _pending, _total);
        }
        else
        {
            StatusChanged?.Invoke(sb.ToString(), 0, 0);
        }
    }

    private bool FilterRow(object o)
    {
        var r = (FileRow)o;
        if (_settings.SpotProblemsOnly && r.IsDone && !r.HasProblems)
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

    // ---------------------------------------------------------------- commands (also used by the main menu)

    public async Task OpenFolderAsync()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null)
        {
            return;
        }
        var start = Directory.Exists(_settings.SpotFolder) ? await sp.TryGetFolderFromPathAsync(_settings.SpotFolder) : null;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open folder", SuggestedStartLocation = start });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null)
        {
            Load([path], false);
        }
    }

    public async Task AddFilesAsync()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null)
        {
            return;
        }
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add MP3 / FLAC files",
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

    public void Rescan()
    {
        if (_currentInputs.Length > 0)
        {
            Load(_currentInputs, false);
        }
    }

    public void SetSubfolders(bool on)
    {
        if (on == _settings.SpotSubfolders)
        {
            return;
        }
        _settings.SpotSubfolders = on;
        SubfoldersBox.IsChecked = on;
        Rescan();
    }

    public void SetProblemsOnly(bool on)
    {
        _settings.SpotProblemsOnly = on;
        _view.Refresh();
    }

    public async Task ExportAsync()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null)
        {
            return;
        }
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export spot report",
            SuggestedFileName = "ces-spot-report.csv",
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
            ? string.Join(Environment.NewLine, rows.Select(r => SpotReport.Text(r.Result!)))
            : SpotReport.Csv(rows);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), CancellationToken.None);
    }

    public async Task CopyListAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(SpotReport.Table(_view.OfType<FileRow>()));
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

    private async void OnOpenFolder(object? sender, RoutedEventArgs e) => await OpenFolderAsync();

    private async void OnAddFiles(object? sender, RoutedEventArgs e) => await AddFilesAsync();

    private async void OnExport(object? sender, RoutedEventArgs e) => await ExportAsync();

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

    private void OnSubfoldersBoxChanged(object? sender, RoutedEventArgs e) => SetSubfolders(SubfoldersBox.IsChecked == true);

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

    private void OnGridSelection(object? sender, SelectionChangedEventArgs e) =>
        CompareButton.IsEnabled = Grid.SelectedItems.Count >= 2;

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e) => OnDetails(null, e);

    private List<FileRow> SelectedRows => Grid.SelectedItems.OfType<FileRow>().ToList();

    private void OnDetails(object? sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is FileRow { IsDone: true } row)
        {
            ShowDetails(row);
        }
    }

    private void ShowDetails(FileRow row)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            new DetailsWindow(row, _settings, Reanalyze).Show(owner);
        }
    }

    private void OnCompareSelected(object? sender, RoutedEventArgs e)
    {
        var paths = SelectedRows.Select(r => r.Path).ToList();
        if (paths.Count > 0)
        {
            CompareRequested?.Invoke(paths);
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
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && text.Length > 0)
        {
            await cb.SetTextAsync(text);
        }
    }

    private async void OnCopyInfo(object? sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, SelectedRows.Where(r => r.IsDone).Select(r => SpotReport.Text(r.Result!)));
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && text.Length > 0)
        {
            await cb.SetTextAsync(text);
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
}
