using BDInfo;
using Ces.Model;
using V = Ces.Vfs.Vfs;

namespace Ces.Disc;

/// <summary>
/// A Blu-ray read by the BDInfo engine (BDInfo.dll, LGPL 2.1, see external/BDInfo): playlists with exact stream
/// details and the BDInfo report. Opening reads the playlists, clip info and the start of each stream (quick);
/// <see cref="Scan"/> reads the streams in full for exact bitrates. Works on BD folders, ISO images and ISOs inside
/// RAR sets. Not thread-safe: use one instance from one thread at a time.
/// </summary>
public sealed class BdInfoDisc : IDisposable
{
    private readonly BDROM _bdrom;

    private BdInfoDisc(BDROM bdrom, string displayName)
    {
        _bdrom = bdrom;
        DisplayName = displayName;
        Playlists = BDInfoScanner.GetPlaylists(bdrom);
    }

    public string DisplayName { get; }

    /// <summary>The disc's playlists in BDInfo's order (grouped, longest first; short and looping ones left out).</summary>
    public IReadOnlyList<TSPlaylistFile> Playlists { get; }

    /// <summary>The longest playlist, normally the main feature.</summary>
    public TSPlaylistFile? MainPlaylist => Playlists.Count > 0 ? Playlists[0] : null;

    public string VolumeLabel => _bdrom.VolumeLabel;
    public ulong Size => _bdrom.Size;
    public bool IsUhd => _bdrom.IsUHD;
    public bool Is3D => _bdrom.Is3D;

    /// <summary>The last full scan, or null when only the quick read was done.</summary>
    public ScanBDROMResult? ScanResult { get; private set; }

    /// <summary>Opens <paramref name="path"/> (BD folder, ISO, RAR set or release folder) with BDInfo.</summary>
    public static async Task<BdInfoDisc> OpenAsync(string path, CancellationToken ct = default)
    {
        var source = await SourceOpener.OpenAsync(path, ct: ct).ConfigureAwait(false);
        return await Task.Run(() => FromSource(source), ct).ConfigureAwait(false);
    }

    /// <summary>Opens an already opened Blu-ray source with BDInfo.</summary>
    public static BdInfoDisc FromSource(MediaSource source)
    {
        if (source.Kind != SourceKind.BluRay)
            throw new InvalidDataException("BDInfo reads Blu-ray discs only (BDMV folder or ISO).");
        var bdrom = OpenBdrom(source);
        var problems = new List<string>();
        try
        {
            // as BDInfo does after "continue scanning?": note the damaged file and go on
            bdrom.StreamClipFileScanError += (f, ex) => Note(problems, f.Name, ex);
            bdrom.StreamFileScanError += (f, ex) => Note(problems, f.Name, ex);
            bdrom.PlaylistFileScanError += (f, ex) => Note(problems, f.Name, ex);
            bdrom.Scan();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bdrom.CloseDiscImage();
            throw new InvalidDataException("BDInfo: " + ex.Message, ex);
        }
        var disc = new BdInfoDisc(bdrom, source.DisplayName);
        disc.Problems.AddRange(problems);
        return disc;
    }

    private static bool Note(List<string> problems, string file, Exception ex)
    {
        problems.Add($"{file}: {ex.Message}");
        return true;
    }

    /// <summary>Files BDInfo could not read while opening the disc (copy-protected or damaged), with the reason.</summary>
    public List<string> Problems { get; } = [];

    private static BDROM OpenBdrom(MediaSource source)
    {
        try
        {
            if (source.MountedIso is { } iso)
            {
                if (V.IsVirtual(iso))
                    return new BDROM(V.Display(iso), V.OpenRead(iso)); // an ISO inside a RAR set
                if (source.MountedRoot is { } root && !V.IsVirtual(root) && Directory.Exists(root))
                    return new BDROM(root); // mounted by the operating system
                return new BDROM(iso);
            }
            if (V.IsVirtual(source.Path))
                throw new InvalidDataException("BDInfo can't read a BDMV folder packed in a RAR set; extract it or use an ISO.");
            return new BDROM(source.Path);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not IOException and not OperationCanceledException)
        {
            // BDInfo throws plain Exception for "Unable to locate BD structure." and similar
            throw new InvalidDataException("BDInfo: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Full scan of the streams of <paramref name="playlists"/> (all playlists when null): reads them completely.
    /// <paramref name="progress"/> gets 0…1.
    /// </summary>
    public void Scan(IList<TSPlaylistFile>? playlists, IProgress<double>? progress, CancellationToken ct)
    {
        var list = playlists ?? [.. Playlists];
        // BDInfo clears every playlist's bitrates when a scan starts
        ScanResult = null;
        _scanned.Clear();
        ScanResult = BDInfoScanner.Scan(_bdrom, list,
            progress == null ? null : (done, total) => progress.Report(total > 0 ? (double)done / total : 1), ct);
        foreach (var p in list)
            _scanned.Add(p.Name);
    }

    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the last full scan covered every playlist in <paramref name="playlists"/>.</summary>
    public bool IsScanned(IEnumerable<TSPlaylistFile> playlists) => ScanResult != null && playlists.All(p => _scanned.Contains(p.Name));

    public Task ScanAsync(IList<TSPlaylistFile>? playlists, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(playlists, progress, ct), ct);

    /// <summary>
    /// The BDInfo report for <paramref name="playlists"/> (all when null). Without a full scan BDInfo marks the
    /// report as incomplete and the bitrates are estimates from the clip info.
    /// </summary>
    public string Report(IList<TSPlaylistFile>? playlists = null)
    {
        List<TSPlaylistFile> list = playlists is null ? [.. Playlists] : [.. playlists];
        return BDInfoReport.Generate(_bdrom, list, IsScanned(list) ? ScanResult! : new ScanBDROMResult());
    }

    /// <summary>
    /// The "QUICK SUMMARY" lines of a BDInfo report (the last one when it covers several playlists) as
    /// label/value pairs, in order; Audio and Subtitle repeat once per track.
    /// </summary>
    public static List<(string Label, string Value)> QuickSummary(string report)
    {
        var result = new List<(string, string)>();
        var at = report.LastIndexOf("QUICK SUMMARY:", StringComparison.Ordinal);
        if (at < 0)
            return result;
        foreach (var raw in report[(at + "QUICK SUMMARY:".Length)..].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (result.Count > 0)
                    break;
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                result.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return result;
    }

    /// <summary>A playlist by file name (e.g. "00800.MPLS"; case-insensitive, extension optional).</summary>
    public TSPlaylistFile? FindPlaylist(string name)
    {
        var n = name.Trim();
        if (!n.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase))
            n += ".MPLS";
        return _bdrom.PlaylistFiles.Values.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _bdrom.CloseDiscImage();
}
