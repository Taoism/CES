using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ces.SpotUi;

/// <summary>A folder in the tree; sub folders are read the first time it is expanded.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _expanded;
    private bool _loaded;

    public FolderNode(string name, string fullPath, bool lazy = true)
    {
        Name = name;
        FullPath = fullPath;
        if (lazy)
        {
            // one placeholder per node: the tree maps each item to a single container
            Children.Add(new FolderNode("…", "", false));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string FullPath { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }
            _expanded = value;
            if (value)
            {
                Load();
            }
            Raise();
        }
    }

    public void Load()
    {
        if (_loaded || FullPath.Length == 0)
        {
            return;
        }
        _loaded = true;
        Children.Clear();
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            foreach (var dir in Directory.EnumerateDirectories(FullPath, "*", opts).Order(StringComparer.OrdinalIgnoreCase))
            {
                Children.Add(new FolderNode(System.IO.Path.GetFileName(dir), dir, HasSubfolders(dir, opts)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable folder: show it without children
        }
    }

    private static bool HasSubfolders(string dir, EnumerationOptions opts)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, "*", opts).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Drives (Windows) or the usual mount points (Linux/macOS), plus the home and music folders.</summary>
    public static IEnumerable<FolderNode> Roots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (music.Length > 0 && Directory.Exists(music))
        {
            yield return new FolderNode("Music", music);
        }
        if (home.Length > 0 && Directory.Exists(home))
        {
            yield return new FolderNode("Home", home);
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                string label;
                try
                {
                    if (!d.IsReady)
                    {
                        continue;
                    }
                    label = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
                }
                catch (IOException)
                {
                    continue;
                }
                yield return new FolderNode(label, d.RootDirectory.FullName);
            }
            yield break;
        }
        yield return new FolderNode("/", "/");
        foreach (var mount in new[] { "/media", "/mnt", "/run/media", "/Volumes" })
        {
            if (Directory.Exists(mount))
            {
                yield return new FolderNode(mount, mount);
            }
        }
    }

    public override string ToString() => FullPath;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
