using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Disc;

/// <summary>Opens a file, a disc folder (or any file inside one) or an ISO as a <see cref="MediaSource"/>.</summary>
public static class SourceOpener
{
    private static readonly Dictionary<string, IsoMounter.Mount> Mounts = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] MediaExtensions =
    [
        ".mkv", ".mp4", ".m4v", ".mov", ".m2ts", ".mts", ".ts", ".vob", ".mpg", ".mpeg", ".avi", ".webm", ".wmv", ".flv",
        ".mxf", ".y4m", ".hevc", ".h264", ".264", ".265", ".av1", ".ivf", ".iso", ".mka", ".flac", ".wav", ".w64", ".thd",
        ".dts", ".ac3", ".eac3", ".mp3", ".m4a", ".opus", ".ogg",
    ];

    public static async Task<MediaSource> OpenAsync(string path, bool includeShortPlaylists = false, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path) && Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            return await OpenIsoAsync(path, includeShortPlaylists, ct).ConfigureAwait(false);

        // a disc folder, or a file inside one (index.bdmv, an .mpls, VIDEO_TS.IFO …)
        var discRoot = FindDiscRoot(path);
        if (discRoot != null)
            return OpenDisc(discRoot.Value.Root, discRoot.Value.Kind, path, includeShortPlaylists);

        if (File.Exists(path))
        {
            var src = new MediaSource { Path = path, Kind = SourceKind.File, DisplayName = Path.GetFileName(path) };
            src.Titles.Add(new MediaTitle
            {
                Name = Path.GetFileName(path),
                IsMain = true,
                Segments = { new TitleSegment { Url = path, FileLabel = Path.GetFileName(path), SizeBytes = new FileInfo(path).Length } },
                SizeBytes = new FileInfo(path).Length,
            });
            return src;
        }
        throw new FileNotFoundException(Directory.Exists(path)
            ? "This folder is not a Blu-ray (BDMV) or DVD (VIDEO_TS) folder."
            : "File not found.", path);
    }

    private static async Task<MediaSource> OpenIsoAsync(string iso, bool includeShort, CancellationToken ct)
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

    private static (string Root, SourceKind Kind)? FindDiscRoot(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        // walk up a few levels: STREAM/x.m2ts → BDMV → root
        for (var i = 0; i < 4 && dir != null; i++)
        {
            if (BluRayReader.IsBluRayRoot(dir))
                return (dir, SourceKind.BluRay);
            var vts = DvdReader.FindVideoTs(dir);
            if (vts != null)
                return (vts, SourceKind.Dvd);
            // a plain .m2ts/.vob picked by the user is treated as a file unless they picked a structure file
            if (File.Exists(path) && i == 0 && !IsStructureFile(path))
                return null;
            dir = Path.GetDirectoryName(dir);
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
