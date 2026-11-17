using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ces.SpotUi;

public enum FolderNodeKind
{
    Folder,

    /// <summary>A Blu-ray (BDMV) or DVD (VIDEO_TS) folder: added as one source, not expanded.</summary>
    Disc,
    File,
}

/// <summary>
/// An entry of the directory tree used by both modes. Spot shows folders only; Compare also shows media files and
/// marks disc folders. Children are read the first time a folder is expanded.
/// </summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private readonly Func<string, bool>? _files;
    private bool _expanded;
    private bool _loaded;

    /// <param name="files">Which files to list (null = folders only).</param>
    public FolderNode(string name, string fullPath, bool lazy = true, Func<string, bool>? files = null,
        FolderNodeKind kind = FolderNodeKind.Folder, string tag = "")
    {
        Name = name;
        FullPath = fullPath;
        _files = files;
        Kind = kind;
        Tag = tag;
        if (lazy)
        {
            // one placeholder per node: the tree maps each item to a single container
            Children.Add(new FolderNode("…", "", false));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string FullPath { get; }
    public FolderNodeKind Kind { get; }

    /// <summary>Short type label shown after the name ("BD", "DVD", "MKV", "ISO"…).</summary>
    public string Tag { get; }
    public bool HasTag => Tag.Length > 0;

    /// <summary>A file or disc folder: something Compare can open as a source.</summary>
    public bool IsSource => Kind != FolderNodeKind.Folder && FullPath.Length > 0;
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
        if (_loaded || FullPath.Length == 0 || Kind != FolderNodeKind.Folder)
        {
            return;
        }
        _loaded = true;
        Children.Clear();
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath, "*", opts).Order(StringComparer.OrdinalIgnoreCase))
            {
                string name = System.IO.Path.GetFileName(dir);
                string disc = _files is null ? "" : DiscTag(dir);
                Children.Add(disc.Length > 0
                    ? new FolderNode(name, dir, false, _files, FolderNodeKind.Disc, disc)
                    : new FolderNode(name, dir, HasEntries(dir, opts), _files));
            }
            if (_files is not null)
            {
                foreach (var file in Directory.EnumerateFiles(FullPath, "*", opts).Where(_files).Order(StringComparer.OrdinalIgnoreCase))
                {
                    string ext = System.IO.Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                    Children.Add(new FolderNode(System.IO.Path.GetFileName(file), file, false, _files, FolderNodeKind.File, ext));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable folder: show what was read
        }
    }

    /// <summary>"BD" / "DVD" when the folder is (or holds) a disc structure, else "".</summary>
    public static string DiscTag(string dir)
    {
        string name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) || Directory.Exists(System.IO.Path.Combine(dir, "BDMV")))
        {
            return "BD";
        }
        if (name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase) || Directory.Exists(System.IO.Path.Combine(dir, "VIDEO_TS")))
        {
            return "DVD";
        }
        return "";
    }

    private bool HasEntries(string dir, EnumerationOptions opts)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, "*", opts).Any()
                || (_files is not null && Directory.EnumerateFiles(dir, "*", opts).Any(_files));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Drives (Windows) or the usual mount points (Linux/macOS), plus the home and music/video folders.</summary>
    public static IEnumerable<FolderNode> Roots(Func<string, bool>? files = null)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // the same roots in both modes
        var special = new[] { ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)), ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)) };
        foreach (var (label, path) in special)
        {
            if (path.Length > 0 && Directory.Exists(path))
            {
                yield return new FolderNode(label, path, files: files);
            }
        }
        if (home.Length > 0 && Directory.Exists(home))
        {
            yield return new FolderNode("Home", home, files: files);
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
                yield return new FolderNode(label, d.RootDirectory.FullName, files: files);
            }
            yield break;
        }
        yield return new FolderNode("/", "/", files: files);
        foreach (var mount in new[] { "/media", "/mnt", "/run/media", "/Volumes" })
        {
            if (Directory.Exists(mount))
            {
                yield return new FolderNode(mount, mount, files: files);
            }
        }
    }

    public override string ToString() => FullPath;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
