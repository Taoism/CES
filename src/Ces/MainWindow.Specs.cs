using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ces.Compare;
using Ces.Model;

namespace Ces;

public sealed partial class MainWindow
{
    private readonly Grid _specGrid = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _specDiffOnly = new() { Content = "Only rows that differ" };
    private readonly StackPanel _discPanel = new() { Spacing = 10 };

    private Control BuildSpecsTab()
    {
        _specDiffOnly.IsCheckedChanged += (_, _) => RefreshSpecs();
        var copy = new Button { Content = "Copy as text" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, SpecsText());
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _specDiffOnly, copy, Muted("Highlighted rows differ between sources. Analysis rows appear after running the analysis tabs.") } };
        foreach (var c in bar.Children)
            c.VerticalAlignment = VerticalAlignment.Center;
        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Border
        {
            Classes = { "panel" },
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(6),
            Child = new ScrollViewer { Content = _specGrid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
        });
        return root;
    }

    private void RefreshSpecs()
    {
        _specGrid.Children.Clear();
        _specGrid.RowDefinitions.Clear();
        _specGrid.ColumnDefinitions.Clear();
        if (_items.Count == 0)
        {
            _specGrid.Children.Add(Muted("No sources yet."));
            return;
        }
        _specGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        foreach (var _ in _items)
            _specGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 220 });
        var rows = SpecTable.Build(_items).Where(r => _specDiffOnly.IsChecked != true || r.Differs || r.Group == "General" && r.Label == "Source").ToList();
        var y = 0;
        void AddRow() => _specGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        AddRow();
        for (var i = 0; i < _items.Count; i++)
        {
            var h = ChipLabel(i, _items[i].Name);
            h.Margin = new Thickness(8, 4);
            Grid.SetColumn(h, i + 1);
            _specGrid.Children.Add(h);
        }
        y++;
        string? group = null;
        var diffBrush = new SolidColorBrush(Color.FromArgb(40, 224, 160, 64));
        foreach (var r in rows)
        {
            if (r.Group != group)
            {
                group = r.Group;
                AddRow();
                var g = new TextBlock { Text = group, Foreground = Brush("AccentBrush"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 12, 4, 4) };
                Grid.SetRow(g, y++);
                _specGrid.Children.Add(g);
            }
            AddRow();
            if (r.Differs)
            {
                var bg = new Border { Background = diffBrush, CornerRadius = new CornerRadius(3) };
                Grid.SetRow(bg, y);
                Grid.SetColumnSpan(bg, _items.Count + 1);
                _specGrid.Children.Add(bg);
            }
            var label = new TextBlock { Text = r.Label, Classes = { "muted" }, Margin = new Thickness(6, 3, 16, 3) };
            Grid.SetRow(label, y);
            _specGrid.Children.Add(label);
            for (var i = 0; i < r.Values.Length; i++)
            {
                var v = new SelectableTextBlock { Text = r.Values[i], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 3) };
                Grid.SetRow(v, y);
                Grid.SetColumn(v, i + 1);
                _specGrid.Children.Add(v);
            }
            y++;
        }
    }

    private string SpecsText()
    {
        var sb = new StringBuilder();
        var rows = SpecTable.Build(_items);
        var w = rows.Count > 0 ? rows.Max(r => r.Label.Length) + 2 : 10;
        sb.Append(new string(' ', w)).AppendJoin(" | ", _items.Select((it, i) => $"[{i + 1}] {it.Name}")).AppendLine();
        foreach (var r in rows)
            sb.Append(r.Label.PadRight(w)).AppendJoin(" | ", r.Values).AppendLine();
        return sb.ToString();
    }

    // ---------- discs ----------

    private Control BuildDiscTab() => new ScrollViewer
    {
        Margin = new Thickness(0, 8, 0, 0),
        Content = _discPanel,
    };

    private void RefreshDiscTab()
    {
        _discPanel.Children.Clear();
        var discs = _items.Select((it, i) => (it, i)).Where(x => x.it.Source.Kind != SourceKind.File).ToList();
        if (discs.Count == 0)
        {
            _discPanel.Children.Add(Muted("Add a Blu-ray / DVD folder or ISO to see its titles, playlists, chapters and segments."));
            return;
        }
        foreach (var (it, idx) in discs)
        {
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(ChipLabel(idx, $"{it.Name}  ·  {it.Source.Titles.Count} titles  ·  {it.Source.Path}{(it.Source.MountedRoot != null ? "  (mounted at " + it.Source.MountedRoot + ")" : "")}"));
            var list = new ListBox { ItemsSource = it.Source.Titles, SelectedItem = it.Title, MaxHeight = 260 };
            var detail = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12 };
            void Show(MediaTitle t)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{t.Name}{(t.IsMain ? "  [main title]" : "")}  ·  {Format.Time(t.Duration)}  ·  {Format.Size(t.SizeBytes)}  ·  {t.Segments.Count} segment(s){(t.Angles > 1 ? $"  ·  {t.Angles} angles" : "")}");
                if (t.StreamLanguages.Count > 0)
                    sb.AppendLine("Languages: " + string.Join(", ", t.StreamLanguages.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")));
                sb.AppendLine();
                foreach (var s in t.Segments.Take(40))
                    sb.AppendLine($"  {Format.Time(s.TitleStart)}  +{Format.Time(s.Duration)}  {s.FileLabel}");
                if (t.Segments.Count > 40)
                    sb.AppendLine($"  … {t.Segments.Count - 40} more");
                if (t.Chapters.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Chapters: " + string.Join("   ", t.Chapters.Select((c, k) => $"{k + 1}: {Format.Time(c.Start, false)}")));
                }
                detail.Text = sb.ToString();
            }
            Show(it.Title);
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is MediaTitle t)
                    Show(t);
            };
            var use = new Button { Content = "Compare this title", Classes = { "accent" } };
            use.Click += async (_, _) =>
            {
                if (list.SelectedItem is MediaTitle t && t != it.Title)
                    await ChangeTitleAsync(it, t);
            };
            _cardControls.Add(use);
            card.Children.Add(list);
            card.Children.Add(use);
            card.Children.Add(detail);
            _discPanel.Children.Add(new Border { Classes = { "card" }, Child = card });
        }
    }
}
