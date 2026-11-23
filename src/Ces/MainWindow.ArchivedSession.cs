using Avalonia.Platform.Storage;
using Ces.Dialogs;
using Ces.Report;

namespace Ces;

/// <summary>
/// The comparison saved inside an exported report (&lt;release&gt;.cesc): written with source paths from the release
/// folder down (reports never show the drive or the folders above a release), found again on opening.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Opens the comparison saved inside <paramref name="archive"/> (unpacked to <paramref name="cescPath"/>): not
    /// remembered as a recent file and not saved back there — Save results asks where to keep it.
    /// </summary>
    internal async Task OpenArchivedSessionAsync(string cescPath, string archive)
    {
        SetMode(false);
        if (_tools == null)
        {
            await MessageBox.ShowAsync(this, "Opening a comparison needs ffmpeg: set its folder in File › Settings.", MessageKind.Warning);
            return;
        }
        await LoadSessionAsync(cescPath, fromArchive: true, archive: archive);
        SetStatus("Opened the comparison saved in " + Path.GetFileName(archive) + "; Save results (Ctrl+S) asks where to keep it.");
    }

    /// <summary>An exported report: its saved comparison when it has one, else the report viewer.</summary>
    private async Task OpenReportOrResultsAsync(string archive)
    {
        string? file = null;
        try
        {
            using var b = ReportArchive.Open(archive);
            if (b.Session is { } name)
                file = b.ExtractFile(name, Path.Combine(Path.GetTempPath(), "ces-reports", SafeName(ReportArchive.BaseName(archive))));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            await MessageBox.ShowAsync(this, "Could not open the report:\n" + ex.Message, MessageKind.Error);
            return;
        }
        if (file != null)
            await OpenArchivedSessionAsync(file, archive);
        else
            await ReportViewer.ShowAsync(this, archive, OpenArchivedSessionAsync);
    }

    /// <summary>
    /// Turns release-relative source paths back into real ones: next to the report, in the last folders used, in the
    /// folders of recent comparisons, then wherever an earlier source was found; asks for the release folder when
    /// none has it. False when the user gives up.
    /// </summary>
    private async Task<bool> ResolveSourcesAsync(SessionFile s, string? archive)
    {
        var roots = new List<string>();
        void AddRoot(string? dir)
        {
            for (var d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                if (Directory.Exists(d) && !roots.Contains(d, StringComparer.OrdinalIgnoreCase))
                    roots.Add(d);
                if (roots.Count > 40)
                    return;
            }
        }
        foreach (var src in s.Sources)
        {
            var (outer, inner) = Split(src.Path);
            if (Path.IsPathRooted(outer))
            {
                if (Exists(outer))
                    continue;
                // a full path from another machine: look for it from its release folder down
                outer = ReportPath.Short(outer, isFolder: false);
            }
            var found = Find(outer);
            if (found == null && roots.Count == 0)
            {
                // the likely places, gathered once
                AddRoot(archive != null ? Path.GetDirectoryName(Path.GetFullPath(archive)) : null);
                AddRoot(_settings.LastFolder);
                AddRoot(_settings.ReportFolder);
                foreach (var r in _settings.RecentSessions)
                    AddRoot(Path.GetDirectoryName(r));
                found = Find(outer);
            }
            while (found == null)
            {
                var release = outer.Split('\\', '/')[0];
                var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = $"Where is “{release}”? (pick it, or the folder that holds it)",
                    SuggestedStartLocation = await StartFolderAsync(),
                });
                if ((picked.Count > 0 ? picked[0].TryGetLocalPath() : null) is not { } dir)
                    return false;
                dir = dir.TrimEnd('\\', '/');
                // the release folder itself, or the folder it sits in
                roots.Insert(0, string.Equals(Path.GetFileName(dir), release, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir) ?? dir : dir);
                found = Find(outer);
                if (found == null)
                    await MessageBox.ShowAsync(this, $"“{outer}” is not in that folder.", MessageKind.Info);
            }
            src.Path = found + inner;
            // the other releases often sit next to this one
            if (!Path.IsPathRooted(outer))
                roots.Insert(0, found[..^outer.Length].TrimEnd('\\', '/'));
        }
        return true;

        string? Find(string rel)
        {
            foreach (var r in roots)
            {
                var p = Path.Combine(r, rel);
                if (Exists(p))
                    return p;
            }
            return null;
        }
    }

    private static (string Outer, string Inner) Split(string path)
    {
        var cut = path.IndexOf("::", StringComparison.Ordinal);
        return cut < 0 ? (path, "") : (path[..cut], path[cut..]);
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
}
