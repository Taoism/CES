using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Ces.Report;

namespace Ces;

/// <summary>Copy / save the open Compare tab as text.</summary>
public sealed partial class MainWindow
{
    private StackPanel BuildTabActions()
    {
        var copy = new Button { Content = "Copy", Classes = { "small" } };
        ToolTip.SetTip(copy, "Copy what this tab shows as text");
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, TabText());
                SetStatus($"{TabTitle} copied to the clipboard.");
            }
        };
        var save = new Button { Content = "Save…", Classes = { "small" } };
        ToolTip.SetTip(save, "Save what this tab shows as a text file (Ctrl+Shift+T)");
        save.Click += async (_, _) => await SaveTabTextAsync();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // level with the tab headers (the TabControl's own margin is 4,4,10,6)
            Margin = new Thickness(0, 9, 12, 0),
            Children = { copy, save },
        };
    }

    private string TabTitle => (_tabs.SelectedItem as TabItem)?.Header as string ?? "Compare";

    /// <summary>The open tab as text, with a header naming the sources (the log as it is).</summary>
    private string TabText()
    {
        var title = TabTitle;
        if (title == "Log")
            return _log.Text ?? "";
        string body = title switch
        {
            "Screenshots" => TextReport.Screenshots(_items, _rows.Select(r => r.Row).ToList()),
            "Specs" => SpecsText(),
            "Video analysis" => TextReport.Video(_items),
            "Audio analysis" => TextReport.Audio(_items),
            "Quality metrics" => TextReport.Quality(_metrics, _ranking),
            "Remaster" => TextReport.Remaster(_items),
            "Subtitles" => TextReport.Subtitles(_subReports, _items),
            "Discs" => TextReport.Discs(_items),
            "BDInfo" => BdReports() is { Count: > 0 } r ? JoinReports(r) : "No Blu-ray sources." + Environment.NewLine,
            _ => "",
        };
        return TextReport.Header("CES · " + title, _items) + body;
    }

    /// <summary>File › Save tab as text (Compare): what the open tab shows.</summary>
    private async Task SaveTabTextAsync()
    {
        SetMode(false);
        if (_items.Count == 0 && TabTitle != "Log")
        {
            await Dialogs.MessageBox.ShowAsync(this, "Nothing to save yet — add sources first.", Dialogs.MessageKind.Info);
            return;
        }
        var name = SafeName($"{string.Join(" vs ", _items.Take(3).Select(i => i.Name))} - {TabTitle}");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save {TabTitle} as text",
            DefaultExtension = ".txt",
            SuggestedFileName = name + ".txt",
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        var path = file?.TryGetLocalPath();
        if (path == null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, TabText(), new UTF8Encoding(false), CancellationToken.None);
            SetStatus("Saved " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageBox.ShowAsync(this, "Could not save:\n" + ex.Message, Dialogs.MessageKind.Error);
        }
    }
}
