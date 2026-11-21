namespace Ces.Report;

/// <summary>
/// Source paths as reports show them: from the release folder down, without the drive and the folders above it
/// (where the files happen to live says nothing about the release and is nobody else's business).
/// <c>X:\Media\Movie.1970.BLURAY-GRP\movie.rar::MOVIE.iso</c> → <c>Movie.1970.BLURAY-GRP\movie.rar::MOVIE.iso</c>.
/// </summary>
public static class ReportPath
{
    private const string Virtual = "::";

    // a disc structure folder is not the release name: keep the folder that holds it too
    private static readonly string[] DiscFolders = ["BDMV", "VIDEO_TS", "AUDIO_TS", "STREAM", "PLAYLIST", "CERTIFICATE"];

    /// <param name="path">A plain path or a virtual one (archive / image members after "::").</param>
    /// <param name="isFolder">The source is a folder itself (a disc folder named after the release); null = look.</param>
    public static string Short(string path, bool? isFolder = null)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        var cut = path.IndexOf(Virtual, StringComparison.Ordinal);
        var outer = cut < 0 ? path : path[..cut];
        var inner = cut < 0 ? "" : path[cut..];
        var sep = outer.Contains('\\') ? '\\' : '/';
        var parts = outer.Split('\\', '/').Where(p => p.Length > 0).ToList();
        // a drive ("X:") is never part of the release
        if (parts.Count > 0 && parts[0].EndsWith(':'))
            parts.RemoveAt(0);
        if (parts.Count <= 1)
            return string.Join(sep, parts) + inner;
        var last = parts.Count - 1;
        // a release folder opened as a disc: its own name is the release
        if (cut < 0 && !IsDisc(parts[last]) && (isFolder ?? Directory.Exists(outer)))
            return parts[last];
        // the file and the folder it is in (the release folder), climbing over disc structure folders
        var start = last - 1;
        while (start > 0 && IsDisc(parts[start]))
            start--;
        return string.Join(sep, parts.Skip(start)) + inner;
    }

    private static bool IsDisc(string name) => Array.Exists(DiscFolders, d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
}
