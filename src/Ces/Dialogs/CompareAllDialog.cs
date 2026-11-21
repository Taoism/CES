using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ces.Compare;

namespace Ces.Dialogs;

/// <summary>
/// "Compare all": the steps that will run, each with what it covers and a rough time, and the total. Steps that
/// cannot run are greyed with the reason; steps whose results are already there start unticked.
/// </summary>
public sealed class CompareAllDialog : DialogWindow
{
    private readonly List<(PlannedStep Step, CheckBox Box)> _rows = [];
    private readonly TextBlock _total = new() { FontWeight = FontWeight.SemiBold };

    private CompareAllDialog(IReadOnlyList<PlannedStep> plan) : base("Compare all", 560)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, RowSpacing = 6 };
        var r = 0;
        foreach (var s in plan)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var detail = !s.Applicable ? s.WhyNot : s.AlreadyDone ? (s.Detail.Length > 0 ? s.Detail + " · " : "") + "done — tick to run it again" : s.Detail;
            var box = new CheckBox
            {
                IsChecked = s.Applicable && !s.AlreadyDone,
                IsEnabled = s.Applicable,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = s.Name },
                        new TextBlock { Text = detail, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            box.IsCheckedChanged += (_, _) => UpdateTotal();
            Grid.SetRow(box, r);
            grid.Children.Add(box);
            var time = new TextBlock
            {
                Text = !s.Applicable ? "" : s.AlreadyDone && s.Seconds <= 0 ? "—" : CompareAllPlanner.Rough(s.Seconds),
                Classes = { "muted" },
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
            };
            Grid.SetRow(time, r);
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            _rows.Add((s, box));
            r++;
        }
        var intro = Label("Runs the checked steps one after another on all sources, with the current settings of each tab. Source 1 is the reference.", true);
        var note = Label("Times are rough: they scale with duration, resolution and the options, and adjust to this machine after each run. Results from the cache come back at once.", true);
        note.FontSize = 12;
        var body = Column(12, intro, grid, new Separator(), _total, note);
        SetBody(Scroll(body, 520), "Start", "Cancel");
        UpdateTotal();
    }

    /// <summary>The ticked steps.</summary>
    public IReadOnlyList<PlannedStep> Selected => _rows.Where(x => x.Box.IsChecked == true).Select(x => x.Step).ToList();

    private void UpdateTotal()
    {
        var sel = Selected;
        OkButton.IsEnabled = sel.Count > 0;
        _total.Text = sel.Count == 0 ? "Nothing ticked." : $"Estimated total: {CompareAllPlanner.Rough(sel.Sum(s => s.Seconds))} for {sel.Count} step{(sel.Count == 1 ? "" : "s")}";
    }

    /// <summary>Shows the plan; the ticked steps, or null when cancelled.</summary>
    public static async Task<IReadOnlyList<PlannedStep>?> ShowAsync(Window owner, IReadOnlyList<PlannedStep> plan)
    {
        var dlg = new CompareAllDialog(plan);
        return await dlg.ShowDialog<bool>(owner) ? dlg.Selected : null;
    }
}
