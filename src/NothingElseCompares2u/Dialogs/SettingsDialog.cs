using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace NothingElseCompares2u.Dialogs;

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

    public SettingsDialog(AppSettings s) : base("Settings", 560)
    {
        _s = s;
        _ffmpeg = new TextBox { Text = s.FFmpegFolder, PlaceholderText = "empty = next to the program, then PATH" };
        _mediainfo = new TextBox { Text = s.MediaInfoPath, PlaceholderText = "empty = next to the program, then PATH" };
        _theme = new ComboBox { ItemsSource = new[] { "Dark", "Light", "Follow system" }, SelectedIndex = s.Theme switch { "Light" => 1, "System" => 2, _ => 0 }, Width = 180 };
        _short = new CheckBox { Content = "List Blu-ray playlists shorter than a minute", IsChecked = s.IncludeShortPlaylists };
        _seed = new NumericUpDown { Minimum = 0, Maximum = 999999, Value = s.RandomSeed, FormatString = "0", Width = 150 };
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
            Label("The same seed picks the same random times again, so later comparisons use the same frames.", muted: true)));
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
        return Task.FromResult(true);
    }
}
