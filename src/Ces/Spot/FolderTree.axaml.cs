using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Ces.SpotUi;

/// <summary>The directory tree shared by Spot and Compare (same look, same keys, same context menu).</summary>
public sealed partial class FolderTree : UserControl
{
    public FolderTree()
    {
        InitializeComponent();
        Menu.Opening += (_, e) =>
        {
            ActivateItem.IsVisible = ActivateText is not null && Tree.SelectedItem is FolderNode { IsSource: true };
            if (ActivateText is not null)
            {
                ActivateItem.Header = ActivateText;
            }
            e.Cancel = Tree.SelectedItem is not FolderNode { FullPath.Length: > 0 };
        };
    }

    /// <summary>A folder (or file) was selected.</summary>
    public event Action<FolderNode>? NodeSelected;

    /// <summary>A file or disc folder was double-clicked, Enter was pressed, or the context menu's first item chosen.</summary>
    public event Action<FolderNode>? NodeActivated;

    /// <summary>Context menu text for <see cref="NodeActivated"/> (null hides it).</summary>
    public string? ActivateText { get; set; }

    /// <summary>The inner tree (for selecting and scrolling to a node).</summary>
    public TreeView View => Tree;

    public IEnumerable<FolderNode>? Roots
    {
        get => Tree.ItemsSource as IEnumerable<FolderNode>;
        set => Tree.ItemsSource = value;
    }

    /// <summary>Suppresses <see cref="NodeSelected"/> while the code selects a node.</summary>
    public bool Quiet { get; set; }

    /// <summary>Expands the tree down to <paramref name="path"/> and selects it without raising <see cref="NodeSelected"/>.</summary>
    /// <param name="expand">Also expand the folder itself (Compare: show the files in it).</param>
    public void Reveal(string path, bool expand = false)
    {
        if (Roots is not { } roots || path.Length == 0)
        {
            return;
        }
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Norm(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool Under(string node) => full.Equals(Norm(node), cmp) || full.StartsWith(Norm(node) + Path.DirectorySeparatorChar, cmp);
        var root = roots.Where(r => r.FullPath.Length > 0 && Under(r.FullPath)).OrderByDescending(r => r.FullPath.Length).FirstOrDefault();
        if (root is null)
        {
            return;
        }
        Quiet = true;
        var node = root;
        while (!Norm(node.FullPath).Equals(full, cmp))
        {
            node.IsExpanded = true;
            var next = node.Children.FirstOrDefault(c => c.FullPath.Length > 0 && Under(c.FullPath));
            if (next is null)
            {
                break;
            }
            node = next;
        }
        if (expand)
        {
            node.IsExpanded = true;
        }
        Tree.SelectedItem = node;
        Dispatcher.UIThread.Post(() =>
        {
            if (Tree.TreeContainerFromItem(node) is Control c)
            {
                c.BringIntoView();
            }
            Quiet = false;
        }, DispatcherPriority.Background);
    }

    private void OnSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (!Quiet && Tree.SelectedItem is FolderNode { FullPath.Length: > 0 } node)
        {
            NodeSelected?.Invoke(node);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Activate();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Activate())
        {
            e.Handled = true;
        }
    }

    private void OnActivateClick(object? sender, RoutedEventArgs e) => Activate();

    private void OnShowClick(object? sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is FolderNode { FullPath.Length: > 0 } node)
        {
            SpotView.ShowInFileManager(node.FullPath);
        }
    }

    private bool Activate()
    {
        if (Tree.SelectedItem is FolderNode { IsSource: true } node)
        {
            NodeActivated?.Invoke(node);
            return true;
        }
        return false;
    }
}
