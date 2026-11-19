using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Ces.SpotUi;

/// <summary>Spot list columns: choose which are shown, keep widths, order and the sort between runs.</summary>
public sealed partial class SpotView
{
    private readonly List<(DataGridColumn Column, DataGridLength Width)> _defaultColumns;

    private static string HeaderOf(DataGridColumn c) => c.Header as string ?? "";

    /// <summary>Menu items to show / hide each column (Name always stays), plus Reset.</summary>
    public List<Control> ColumnMenuItems()
    {
        var items = new List<Control>();
        foreach (var c in Grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            var header = HeaderOf(c);
            if (header == "Name")
            {
                continue;
            }
            var mi = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = c.IsVisible };
            var col = c;
            mi.Click += (_, _) =>
            {
                col.IsVisible = !col.IsVisible;
                mi.IsChecked = col.IsVisible;
                SaveColumnLayout();
            };
            items.Add(mi);
        }
        items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset columns" };
        reset.Click += (_, _) => ResetColumns();
        items.Add(reset);
        return items;
    }

    private void OnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || e.Source is not Avalonia.Visual v
            || v.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is not { } header)
        {
            return;
        }
        var menu = new ContextMenu { ItemsSource = ColumnMenuItems() };
        menu.Open(header);
        e.Handled = true;
    }

    private void ApplyColumnLayout()
    {
        var saved = _settings.SpotColumns;
        if (saved.Count > 0)
        {
            var byHeader = Grid.Columns.ToDictionary(HeaderOf);
            var index = 0;
            foreach (var s in saved)
            {
                if (!byHeader.TryGetValue(s.Header, out var c))
                {
                    continue; // a column that no longer exists
                }
                c.IsVisible = s.Visible || s.Header == "Name";
                if (s.Width > 20)
                {
                    c.Width = new DataGridLength(s.Width);
                }
                c.DisplayIndex = Math.Min(index++, Grid.Columns.Count - 1);
            }
        }
        if (_settings.SpotSortColumn.Length > 0 && Grid.Columns.FirstOrDefault(c => HeaderOf(c) == _settings.SpotSortColumn) is { } sortBy)
        {
            // straight on the view: DataGridColumn.Sort does nothing before the grid is shown
            _view.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription.FromPath(sortBy.SortMemberPath,
                _settings.SpotSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
        }
    }

    private void SaveColumnLayout()
    {
        _settings.SpotColumns = Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => new SpotColumn
        {
            Header = HeaderOf(c),
            Visible = c.IsVisible,
            Width = c.ActualWidth > 0 ? c.ActualWidth : c.Width.DisplayValue,
        }).ToList();
    }

    private void RememberSort(DataGridColumn column)
    {
        var path = column.SortMemberPath;
        var sort = _view.SortDescriptions.FirstOrDefault(d => d.PropertyPath == path);
        if (sort is null)
        {
            _settings.SpotSortColumn = "";
            return;
        }
        _settings.SpotSortColumn = HeaderOf(column);
        _settings.SpotSortDescending = sort.Direction == ListSortDirection.Descending;
    }

    private void ResetColumns()
    {
        for (var i = 0; i < _defaultColumns.Count; i++)
        {
            var (c, w) = _defaultColumns[i];
            c.IsVisible = true;
            c.Width = w;
            c.DisplayIndex = i;
        }
        _view.SortDescriptions.Clear();
        _settings.SpotSortColumn = "";
        SaveColumnLayout();
    }
}
