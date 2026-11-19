using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Ces.Dialogs;

/// <summary>One-field prompt.</summary>
public sealed class InputDialog : DialogWindow
{
    public InputDialog(string title, string prompt, Control input) : base(title, 420)
    {
        SetBody(Column(8, Label(prompt), input));
        Opened += (_, _) => input.Focus();
    }
}

public sealed class SettingsDialog : DialogWindow
{
    private readonly AppSettings _s;
    private readonly TextBox _ffmpeg;
    private readonly TextBox _mediainfo;
    private readonly ComboBox _theme;
    private readonly CheckBox _short;
    private readonly NumericUpDown _seed;
    private readonly CheckBox _hw;
    private readonly NumericUpDown _cache;
    private readonly NumericUpDown _results;

    public SettingsDialog(AppSettings s) : base("Settings", 560)
    {
        _s = s;
        _ffmpeg = new TextBox { Text = s.FFmpegFolder, PlaceholderText = "empty = next to the program, then PATH" };
        _mediainfo = new TextBox { Text = s.MediaInfoPath, PlaceholderText = "empty = next to the program, then PATH" };
        _theme = new ComboBox { ItemsSource = new[] { "Dark", "Light", "Follow system" }, SelectedIndex = s.Theme switch { "Light" => 1, "System" => 2, _ => 0 }, Width = 180 };
        _short = new CheckBox { Content = "List Blu-ray playlists shorter than a minute", IsChecked = s.IncludeShortPlaylists };
        _seed = new NumericUpDown { Minimum = 0, Maximum = 999999, Value = s.RandomSeed, FormatString = "0", Width = 150 };
        _hw = new CheckBox { Content = "Hardware video decoding (D3D11VA / auto)", IsChecked = s.HardwareDecode };
        _cache = new NumericUpDown { Minimum = 0, Maximum = 100, Value = s.ShotCacheGb, FormatString = "0", Width = 120 };
        var clear = new Button { Content = "Clear cache" };
        var size = Label($"{Model.Format.Size(Tools.ShotCache.SizeBytes())} in use", muted: true);
        clear.Click += (_, _) =>
        {
            Tools.ShotCache.Clear();
            size.Text = $"{Model.Format.Size(Tools.ShotCache.SizeBytes())} in use";
        };
        _results = new NumericUpDown { Minimum = 0, Maximum = 100000, Increment = 128, Value = s.ResultsCacheMb, FormatString = "0", Width = 120 };
        var clearResults = new Button { Content = "Clear cache" };
        var resultsSize = Label($"{Model.Format.Size(Cache.ResultsCache.Shared.SizeBytes())} in use", muted: true);
        clearResults.Click += (_, _) =>
        {
            Cache.ResultsCache.Shared.Clear();
            resultsSize.Text = $"{Model.Format.Size(Cache.ResultsCache.Shared.SizeBytes())} in use";
        };
        SetBody(Column(10,
            Label("ffmpeg folder (with ffmpeg and ffprobe)"),
            PathRow(_ffmpeg, async () =>
            {
                var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "ffmpeg folder" });
                if (f.FirstOrDefault()?.TryGetLocalPath() is { } p)
                    _ffmpeg.Text = p;
            }),
            Label("MediaInfo CLI (optional: HDR format, commercial codec names)"),
            PathRow(_mediainfo, async () =>
            {
                if (await PickFileAsync("MediaInfo executable") is { } p)
                    _mediainfo.Text = p;
            }),
            Rule(),
            Row(10, Label("Theme"), _theme),
            _short,
            Row(10, Label("Random screenshot seed"), _seed),
            Label("The same seed picks the same random times again, so later comparisons use the same frames.", muted: true),
            Rule(),
            _hw,
            Label("Only frames copied back from the GPU can be analysed: on fast CPUs software decoding is quicker (measured: 4K HEVC 3.5 s vs 4.0 s per 10 s).", muted: true),
            Row(10, Label("Screenshot cache (GB, 0 = off)"), _cache, clear, size),
            Row(10, Label("Results cache (MB, 0 = off)"), _results, clearResults, resultsSize),
            Label("Analysis results of files seen before (Spot files; Compare analyses, metrics and scans) come back instantly while the file is unchanged.", muted: true)));
    }

    protected override Task<bool> ValidateAsync()
    {
        _s.FFmpegFolder = _ffmpeg.Text?.Trim() ?? "";
        _s.MediaInfoPath = _mediainfo.Text?.Trim() ?? "";
        _s.Theme = _theme.SelectedIndex switch
        {
            1 => "Light",
            2 => "System",
            _ => "Dark",
        };
        _s.IncludeShortPlaylists = _short.IsChecked == true;
        _s.RandomSeed = (int)(_seed.Value ?? 2024);
        _s.HardwareDecode = _hw.IsChecked == true;
        _s.ShotCacheGb = (int)(_cache.Value ?? 2);
        _s.ResultsCacheMb = (int)(_results.Value ?? 512);
        return Task.FromResult(true);
    }
}
