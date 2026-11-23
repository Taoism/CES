using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ces.Report;

namespace Ces;

/// <summary>Notes tab: the comparison written out as prose (the same notes the exported report carries).</summary>
public sealed partial class MainWindow
{
    private readonly SelectableTextBlock _notesText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        LineHeight = 21,
        Margin = new Thickness(14, 10),
        MaxWidth = 980,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
    };

    private Border BuildNotesTab()
    {
        _tabs.SelectionChanged += (_, _) =>
        {
            if (TabTitle == "Notes")
                RefreshNotes();
        };
        return new Border
        {
            Classes = { "panel" },
            Margin = new Thickness(0, 6, 0, 0),
            Child = new ScrollViewer { Content = _notesText, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
    }

    private void RefreshNotes() => _notesText.Text = NotesText();

    private string NotesText()
    {
        if (_items.Count < 2)
            return "Notes need at least two sources. Add the releases to compare, then run Compare all (Ctrl+Shift+A) for the fullest notes.";
        var paragraphs = ReportNotes.Build(_items, _metrics, _subReports);
        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs) + Environment.NewLine + Environment.NewLine +
               "The notes grow with what has been measured: quality metrics, video and audio analysis, remaster (grade), frame and subtitle checks.";
    }
}
