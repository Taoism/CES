using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Ces.Report;

public enum ArchiveKind
{
    Zip,
    TarGz,
}

/// <summary>
/// Exported reports travel as one compressed file (.zip or .tar.gz) holding a folder with index.html, images/,
/// summary.txt (plain words) and report.txt (the technical details). CES reads them straight from the archive.
/// </summary>
public static class ReportArchive
{
    public const string SummaryName = "summary.txt";
    public const string TextName = "report.txt";
    public const string NotesName = "notes.txt";
    public const string HtmlName = "index.html";

    /// <summary>
    /// The release a Compare report is named after: the reference's release name as it is laid out (disc folder or
    /// file name, lowercased): "old.film.1970.complete.bluray-grp". Files inside are "&lt;that&gt;-summary.txt" etc.
    /// </summary>
    public static string ReleaseBase(IReadOnlyList<Compare.CompareItem> items) => items.Count == 0 ? "" : SourceBase(items[0]);

    /// <summary>One source's release name for file names ("old.film.1970.1080p.bluray.x264-grp").</summary>
    public static string SourceBase(Compare.CompareItem it)
    {
        // the release folder first ("Movie.1970.1080p.BluRay.x264-GRP\movie.rar::movie.mkv" → its first part), then
        // the parts of the shown name ("movie.rar › movie.mkv"), then the file name
        var shortPath = ReportPath.Short(it.Source.Path);
        var candidates = shortPath.Split("::")[0].Split('\\', '/')
            .Concat(it.Name.Split(" › ")).Concat(it.Source.DisplayName.Split(" › "))
            .Append(Path.GetFileName(it.Source.Path.TrimEnd('/', '\\')))
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (candidates.Length == 0)
            return "";
        var name = candidates.FirstOrDefault(n => n.Length > 0 && Share.SceneName.LooksLikeRelease(n)) ?? candidates.FirstOrDefault(n => n.Length > 0) ?? "";
        var ext = Path.GetExtension(name);
        if (ext.ToLowerInvariant() is ".mkv" or ".mp4" or ".m2ts" or ".ts" or ".iso" or ".rar" or ".avi" or ".mpls" or ".vob" or ".ifo" or ".png" or ".jpg")
            name = name[..^ext.Length];
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            var c = char.IsWhiteSpace(ch) ? '.' : ch;
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                continue;
            if (c == '.' && sb.Length > 0 && sb[^1] == '.')
                continue;
            sb.Append(c);
        }
        var clean = sb.ToString().Trim('.', '-');
        return clean.Length > 120 ? clean[..120].TrimEnd('.', '-') : clean;
    }

    /// <summary>"&lt;release&gt;-summary.txt", or plain "summary.txt" when there is no name.</summary>
    public static string Named(string release, string file) => release.Length > 0 ? release + "-" + file : file;

    /// <summary>The archive kind from a file name, null when it is neither .zip nor .tar.gz / .tgz.</summary>
    public static ArchiveKind? KindOf(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.Zip
        : path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.TarGz
        : null;

    public static string Extension(ArchiveKind kind) => kind == ArchiveKind.Zip ? ".zip" : ".tar.gz";

    /// <summary>The file name without .zip / .tar.gz (the folder name inside the archive).</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".zip" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        }
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Packs <paramref name="folder"/> (with the folder itself as the top entry) into <paramref name="archivePath"/>.</summary>
    public static void Pack(string folder, string archivePath)
    {
        var kind = KindOf(archivePath) ?? throw new ArgumentException("Report archives end in .zip or .tar.gz: " + archivePath);
        var temp = archivePath + ".part";
        File.Delete(temp);
        try
        {
            if (kind == ArchiveKind.Zip)
            {
                ZipFile.CreateFromDirectory(folder, temp, CompressionLevel.Optimal, includeBaseDirectory: true, Encoding.UTF8);
            }
            else
            {
                using var fs = File.Create(temp);
                using var gz = new GZipStream(fs, CompressionLevel.Optimal);
                TarFile.CreateFromDirectory(folder, gz, includeBaseDirectory: true);
            }
            File.Move(temp, archivePath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// Writes a report into a scratch folder named after the archive (via <paramref name="write"/>), packs it to
    /// <paramref name="archivePath"/> and removes the scratch folder.
    /// </summary>
    public static void Export(string archivePath, Action<string> write)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "ces-export-" + Guid.NewGuid().ToString("N")[..8]);
        var folder = Path.Combine(scratch, BaseName(archivePath));
        try
        {
            Directory.CreateDirectory(folder);
            write(folder);
            Pack(folder, archivePath);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, true);
            }
            catch (IOException)
            {
                // a viewer still holding a file: the temp folder is cleaned up by the system later
            }
        }
    }

    /// <summary>Opens an exported report for reading without unpacking it to disk.</summary>
    public static ReportBundle Open(string archivePath) => KindOf(archivePath) switch
    {
        ArchiveKind.Zip => ReportBundle.FromZip(archivePath),
        ArchiveKind.TarGz => ReportBundle.FromTarGz(archivePath),
        _ => throw new InvalidDataException("Not a .zip or .tar.gz report: " + archivePath),
    };
}

/// <summary>The files of an exported report, read from the archive (names relative to the report folder, with '/').</summary>
public sealed class ReportBundle : IDisposable
{
    // more than this unpacked is not a CES report (guards against archive bombs)
    private const long MaxTotal = 2L << 30;

    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _zipEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _data = new(StringComparer.OrdinalIgnoreCase);

    public required string Path { get; init; }

    /// <summary>The folder name inside the archive ("" when the files are at the top).</summary>
    public string Root { get; private set; } = "";

    public List<string> Files { get; } = [];

    private ReportBundle(ZipArchive? zip) => _zip = zip;

    internal static ReportBundle FromZip(string path)
    {
        var zip = ZipFile.OpenRead(path);
        var b = new ReportBundle(zip) { Path = path };
        try
        {
            var names = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName.Replace('\\', '/')).ToList();
            b.Root = FindRoot(names);
            foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                var rel = b.Relative(e.FullName.Replace('\\', '/'));
                if (rel != null && b._zipEntries.TryAdd(rel, e))
                    b.Files.Add(rel);
            }
            return b;
        }
        catch
        {
            b.Dispose();
            throw;
        }
    }

    internal static ReportBundle FromTarGz(string path)
    {
        var b = new ReportBundle(null) { Path = path };
        var raw = new List<(string Name, byte[] Data)>();
        long total = 0;
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
        {
            while (tar.GetNextEntry() is { } e)
            {
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || e.DataStream == null)
                    continue;
                total += e.Length;
                if (total > MaxTotal)
                    throw new InvalidDataException("The archive unpacks to more than 2 GB; it is not a CES report.");
                using var ms = new MemoryStream();
                e.DataStream.CopyTo(ms);
                raw.Add((e.Name.Replace('\\', '/'), ms.ToArray()));
            }
        }
        b.Root = FindRoot(raw.Select(r => r.Name).ToList());
        foreach (var (name, data) in raw)
        {
            var rel = b.Relative(name);
            if (rel != null && b._data.TryAdd(rel, data))
                b.Files.Add(rel);
        }
        return b;
    }

    /// <summary>The folder holding index.html or summary.txt (the report's own folder).</summary>
    private static string FindRoot(List<string> names)
    {
        var marker = names.Where(n => IsFile(n, ReportArchive.HtmlName) || IsFile(n, ReportArchive.SummaryName) || IsFile(n, ReportArchive.TextName))
            .OrderBy(n => n.Count(c => c == '/')).FirstOrDefault();
        if (marker == null)
            return "";
        var cut = marker.LastIndexOf('/');
        return cut < 0 ? "" : marker[..(cut + 1)];
    }

    /// <summary>"x/summary.txt", "summary.txt" or "x/&lt;release&gt;-summary.txt".</summary>
    private static bool IsFile(string path, string file)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        return leaf.Equals(file, StringComparison.OrdinalIgnoreCase) || (file != ReportArchive.HtmlName && leaf.EndsWith("-" + file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The report-folder file for "summary.txt" / "report.txt" / "notes.txt": the plain name or "&lt;release&gt;-name".</summary>
    public string? Find(string file) =>
        Files.Where(f => !f.Contains('/') && IsFile(f, file)).OrderBy(f => f.Length).FirstOrDefault();

    public string? Summary => Find(ReportArchive.SummaryName);
    public string? Text => Find(ReportArchive.TextName);
    public string? Notes => Find(ReportArchive.NotesName);

    /// <summary>The saved comparison (.cesc) next to the report, which CES can open with its results.</summary>
    public string? Session => Files.Where(f => !f.Contains('/') && f.EndsWith(".cesc", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Length).FirstOrDefault();

    /// <summary>Writes one file of the report to <paramref name="folder"/> and returns its path.</summary>
    public string ExtractFile(string name, string folder)
    {
        var data = Read(name) ?? throw new FileNotFoundException(name + " is not in the report.");
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(name));
        File.WriteAllBytes(target, data);
        return target;
    }

    private string? Relative(string name)
    {
        name = name.TrimStart('/');
        if (!name.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            return null;
        var rel = name[Root.Length..];
        // nothing that climbs out of the report folder
        return rel.Length == 0 || rel.Split('/').Any(p => p is ".." or "") || rel.Contains(':') ? null : rel;
    }

    public bool Has(string name) => Files.Contains(name, StringComparer.OrdinalIgnoreCase);

    public byte[]? Read(string name)
    {
        if (_data.TryGetValue(name, out var d))
            return d;
        if (!_zipEntries.TryGetValue(name, out var e))
            return null;
        if (e.Length > MaxTotal)
            throw new InvalidDataException(name + " is too big for a report file.");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public string? ReadText(string name) => Read(name) is { } b ? Encoding.UTF8.GetString(b).TrimStart('﻿') : null;

    /// <summary>Unpacks the report into <paramref name="folder"/> (for the browser) and returns the path of index.html, or null.</summary>
    public string? ExtractTo(string folder)
    {
        var full = System.IO.Path.GetFullPath(folder);
        foreach (var f in Files)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(full, f));
            if (!target.StartsWith(full + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, Read(f)!);
        }
        var html = System.IO.Path.Combine(full, ReportArchive.HtmlName);
        return File.Exists(html) ? html : null;
    }

    public void Dispose() => _zip?.Dispose();
}
