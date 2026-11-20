using Ces.Model;
using Ces.Vfs;
using V = Ces.Vfs.Vfs;

namespace Ces.Disc;

/// <summary>Opens a file, a disc folder (or any file inside one) or an ISO as a <see cref="MediaSource"/>.</summary>
public static class SourceOpener
{
    private static readonly Dictionary<string, IsoMounter.Mount> Mounts = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] MediaExtensions =
    [
        ".mkv", ".mp4", ".m4v", ".mov", ".m2ts", ".mts", ".ts", ".vob", ".mpg", ".mpeg", ".avi", ".webm", ".wmv", ".flv",
        ".mxf", ".y4m", ".hevc", ".h264", ".264", ".265", ".av1", ".ivf", ".iso", ".mka", ".flac", ".wav", ".w64", ".thd",
        ".dts", ".ac3", ".eac3", ".mp3", ".m4a", ".opus", ".ogg",
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp",
    ];

    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".webp";

    /// <summary>A RAR set (first volume) or ISO: the things CES can open from inside.</summary>
    public static bool IsArchive(string path) =>
        Path.GetExtension(path).Equals(".rar", StringComparison.OrdinalIgnoreCase) || IsOldVolume(path);

    private static bool IsOldVolume(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext.Length == 4 && ext[1] == 'r' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]);
    }

    public static async Task<MediaSource> OpenAsync(string path, bool includeShortPlaylists = false, CancellationToken ct = default)
    {
        if (!V.IsVirtual(path))
            path = Path.GetFullPath(path);
        // a release folder: the RAR set in it
        if (!V.IsVirtual(path) && Directory.Exists(path) && FindDiscRoot(path) == null && FirstRarIn(path) is { } rarInFolder)
            path = rarInFolder;
        if (!V.IsVirtual(path) && File.Exists(path) && IsArchive(path))
            return await OpenRarAsync(path, includeShortPlaylists, ct).ConfigureAwait(false);
        if (V.FileExists(path) && Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            return await OpenIsoAsync(path, includeShortPlaylists, ct).ConfigureAwait(false);
        // a file inside an archive or image; a disc structure file (an .mpls picked inside an ISO) opens the disc
        if (V.IsVirtual(path) && V.FileExists(path) && !(IsStructureFile(path) && FindDiscRoot(path) != null))
            return OpenFile(path);

        // a disc folder, or a file inside one (index.bdmv, an .mpls, VIDEO_TS.IFO …)
        var discRoot = FindDiscRoot(path);
        if (discRoot != null)
            return OpenDisc(discRoot.Value.Root, discRoot.Value.Kind, path, includeShortPlaylists);

        if (File.Exists(path))
            return OpenFile(path);
        throw new FileNotFoundException(Directory.Exists(path)
            ? "This folder is not a Blu-ray (BDMV) or DVD (VIDEO_TS) folder."
            : "File not found.", path);
    }

    private static MediaSource OpenFile(string path)
    {
        var problem = V.Problem(path);
        if (problem != null)
            throw new InvalidDataException($"{V.Display(path)}: {problem}.");
        var size = V.Length(path);
        var name = V.FileName(path);
        var src = new MediaSource { Path = path, Kind = SourceKind.File, DisplayName = V.IsVirtual(path) ? V.Display(path) : name };
        src.Titles.Add(new MediaTitle
        {
            Name = name,
            IsMain = true,
            IsStill = IsImage(path),
            Segments = { new TitleSegment { Url = path, FileLabel = name, SizeBytes = size } },
            SizeBytes = size,
        });
        return src;
    }

    /// <summary>
    /// A RAR set: a disc structure inside it, else an ISO inside it, else its biggest media file (samples skipped).
    /// Stored (-m0) sets are read in place, without unpacking.
    /// </summary>
    private static async Task<MediaSource> OpenRarAsync(string rar, bool includeShort, CancellationToken ct)
    {
        List<string> files;
        try
        {
            files = await Task.Run(() => V.ListContainer(rar), ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(rar)}: {ex.Message}", ex);
        }
        var archive = (RarArchive)V.Container(rar);
        if (archive.HeadersEncrypted)
            throw new InvalidDataException(Path.GetFileName(rar) + " is password protected.");
        string Inner(string p) => p[(rar.Length + V.Separator.Length)..];
        // BDMV / VIDEO_TS folders packed in the set
        foreach (var f in files)
        {
            var inner = Inner(f);
            var name = Path.GetFileName(inner);
            if (name.Equals("index.bdmv", StringComparison.OrdinalIgnoreCase) || name.Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase))
            {
                var root = FindDiscRoot(f);
                if (root != null)
                {
                    var disc = OpenDisc(root.Value.Root, root.Value.Kind, null, includeShort);
                    disc.DisplayName = Path.GetFileName(rar) + (disc.Kind == SourceKind.BluRay ? " [BD]" : " [DVD]");
                    return disc;
                }
            }
        }
        bool Sample(string p) => Path.GetFileNameWithoutExtension(p).Contains("sample", StringComparison.OrdinalIgnoreCase)
            || Inner(p).Contains("/sample/", StringComparison.OrdinalIgnoreCase) || Inner(p).StartsWith("sample/", StringComparison.OrdinalIgnoreCase);
        var candidates = files.Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !Sample(f)).ToList();
        if (candidates.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(rar)} holds no media CES can open ({files.Count} files inside).");
        var isos = candidates.Where(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)).ToList();
        var pick = (isos.Count > 0 ? isos : candidates).OrderByDescending(SafeLength).First();
        var problem = V.Problem(pick);
        if (problem != null)
            throw new InvalidDataException($"{V.Display(pick)}: {problem}.");
        return pick.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
            ? await OpenIsoAsync(pick, includeShort, ct).ConfigureAwait(false)
            : OpenFile(pick);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return V.Length(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return 0;
        }
    }

    /// <summary>The first volume of a RAR set directly in <paramref name="folder"/> (a packed release), or null.</summary>
    public static string? FirstRarIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.rar").Where(RarArchive.IsFirstVolumeName).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                ?? Directory.EnumerateFiles(folder, "*.r00").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// An ISO, read with CES's own UDF reader (works for an ISO inside a RAR set and needs no mounting).
    /// Plain ISO files fall back to the operating system's mount when the UDF reader cannot read them.
    /// </summary>
    private static async Task<MediaSource> OpenIsoAsync(string iso, bool includeShort, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => V.Container(iso), ct).ConfigureAwait(false);
            var vroot = iso + V.Separator;
            var vdisc = FindDiscRoot(vroot) ?? throw new InvalidDataException("The ISO has no BDMV or VIDEO_TS folder (data discs and AVCHD are not supported).");
            var disc = OpenDisc(vdisc.Root, vdisc.Kind, null, includeShort);
            var src = new MediaSource
            {
                Path = iso,
                Kind = disc.Kind,
                DisplayName = (V.IsVirtual(iso) ? V.Display(iso) : Path.GetFileName(iso)) + (disc.Kind == SourceKind.BluRay ? " [BD]" : " [DVD]"),
                MountedIso = iso, // an image (read in place, nothing to unmount)
                MountedRoot = vdisc.Root,
            };
            src.Titles.AddRange(disc.Titles);
            return src;
        }
        catch (InvalidDataException) when (!V.IsVirtual(iso))
        {
            // not readable as UDF here: let the operating system mount it
        }
        return await MountIsoAsync(iso, includeShort, ct).ConfigureAwait(false);
    }

    private static async Task<MediaSource> MountIsoAsync(string iso, bool includeShort, CancellationToken ct)
    {
        if (!Mounts.TryGetValue(iso, out var mount))
        {
            mount = await IsoMounter.MountAsync(iso, ct).ConfigureAwait(false);
            Mounts[iso] = mount;
        }
        var root = FindDiscRoot(mount.Root);
        if (root == null)
            throw new InvalidDataException("The ISO has no BDMV or VIDEO_TS folder (data discs and AVCHD are not supported).");
        var disc = OpenDisc(root.Value.Root, root.Value.Kind, null, includeShort);
        var src = new MediaSource
        {
            Path = iso,
            Kind = disc.Kind,
            DisplayName = Path.GetFileName(iso) + (disc.Kind == SourceKind.BluRay ? " [BD]" : " [DVD]"),
            MountedIso = iso,
            MountedRoot = mount.Root,
        };
        src.Titles.AddRange(disc.Titles);
        return src;
    }

    /// <summary>Unmounts ISOs we mounted (call when a source is removed and on exit).</summary>
    public static async Task CloseAsync(MediaSource source)
    {
        if (source.MountedIso != null && Mounts.Remove(source.MountedIso, out var m))
            await IsoMounter.UnmountAsync(source.MountedIso, m).ConfigureAwait(false);
    }

    public static async Task CloseAllAsync()
    {
        foreach (var (iso, m) in Mounts.ToList())
            await IsoMounter.UnmountAsync(iso, m).ConfigureAwait(false);
        Mounts.Clear();
    }

    /// <summary>
    /// A folder of releases → the things to compare in it: media files and ISOs, plus disc folders (BDMV / VIDEO_TS)
    /// one level down. A folder that is itself a disc stays one source.
    /// </summary>
    public static List<string> ExpandFolder(string folder)
    {
        if (!Directory.Exists(folder) || FindDiscRoot(folder) != null)
            return [folder];
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            // video containers only (audio-only files and samples are not releases to rank)
            if (ext is ".mkv" or ".mp4" or ".m4v" or ".m2ts" or ".ts" or ".avi" or ".mov" or ".webm" or ".iso" or ".vob" or ".mpg" &&
                !Path.GetFileNameWithoutExtension(f).Contains("sample", StringComparison.OrdinalIgnoreCase))
                list.Add(f);
        }
        list.AddRange(Directory.EnumerateFiles(folder, "*.rar").Where(V.IsRarSet).Order(StringComparer.OrdinalIgnoreCase));
        foreach (var d in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            // a disc folder, or a release folder with a RAR set
            if (FindDiscRoot(d) != null || FirstRarIn(d) != null)
                list.Add(d);
        }
        return list;
    }

    private static (string Root, SourceKind Kind)? FindDiscRoot(string path)
    {
        // the root of a disc image ("x.iso::") is a directory too
        var dir = path.EndsWith(V.Separator, StringComparison.Ordinal) || V.DirectoryExists(path) ? path : V.Parent(path);
        // walk up a few levels: STREAM/x.m2ts → BDMV → root
        for (var i = 0; i < 4 && dir != null; i++)
        {
            if (BluRayReader.IsBluRayRoot(dir))
                return (dir, SourceKind.BluRay);
            var vts = DvdReader.FindVideoTs(dir);
            if (vts != null)
                return (vts, SourceKind.Dvd);
            // a plain .m2ts/.vob picked by the user is treated as a file unless they picked a structure file
            if (V.FileExists(path) && i == 0 && !IsStructureFile(path))
                return null;
            if (V.IsVirtual(dir) && dir.EndsWith(V.Separator, StringComparison.Ordinal))
                return null; // do not walk out of the image
            dir = V.Parent(dir);
        }
        return null;
    }

    private static bool IsStructureFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".bdmv" or ".mpls" or ".clpi" or ".ifo" or ".bup";
    }

    private static MediaSource OpenDisc(string root, SourceKind kind, string? picked, bool includeShort)
    {
        var src = new MediaSource
        {
            Path = root,
            Kind = kind,
            DisplayName = DiscName(root, kind),
        };
        src.Titles.AddRange(kind == SourceKind.BluRay ? BluRayReader.ReadTitles(root, includeShort) : DvdReader.ReadTitles(root));
        if (src.Titles.Count == 0)
            throw new InvalidDataException("No playable titles found on the disc.");
        // a specific playlist was picked: make it the main one
        if (picked != null && Path.GetExtension(picked).Equals(".mpls", StringComparison.OrdinalIgnoreCase))
        {
            var t = src.Titles.FirstOrDefault(x => x.Name.Equals(Path.GetFileName(picked), StringComparison.OrdinalIgnoreCase));
            if (t != null)
            {
                foreach (var x in src.Titles)
                    x.IsMain = false;
                t.IsMain = true;
            }
        }
        return src;
    }

    private static string DiscName(string root, SourceKind kind)
    {
        var dir = kind == SourceKind.Dvd ? Path.GetDirectoryName(root.TrimEnd('\\', '/')) : root;
        var name = Path.GetFileName(dir?.TrimEnd('\\', '/') ?? "");
        if (string.IsNullOrEmpty(name))
            name = dir ?? root;
        // generic folder names say nothing: use the parent
        if (name.Length <= 3 || name.Equals("DISC", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileName(Path.GetDirectoryName(dir?.TrimEnd('\\', '/') ?? "") ?? "") is { Length: > 0 } p ? p + "/" + name : name;
        return name + (kind == SourceKind.BluRay ? " [BD]" : " [DVD]");
    }
}
