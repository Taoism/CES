using System.Globalization;
using System.Text.Json;
using Ces.Analysis;
using Ces.Cache;
using Ces.Compare;

namespace Ces;

/// <summary>
/// Compare results in the results cache: a source's own analyses (video, audio) and, per pair with the reference,
/// the time map, grade &amp; frame checks, quality metrics and BDInfo full scans. Adding the same file again (same
/// path, size and mtime, same title) brings them back without re-running anything.
/// </summary>
public sealed partial class MainWindow
{
    private const string CacheSource = "compare-source";
    private const string CachePair = "compare-pair";
    private const string CacheBdInfo = "bdinfo";

    /// <summary>JSON last written per cache key, so unchanged results are not written again.</summary>
    private readonly Dictionary<string, string> _cacheWritten = [];

    /// <summary>What the cache keeps for a source next to the reference.</summary>
    public sealed class CachedPair
    {
        public SessionSource TimeMap { get; set; } = new();
        public MetricResult? Metric { get; set; }
    }

    private static string? SourceId(CompareItem it)
    {
        if (!ResultsCache.Shared.Enabled)
            return null;
        var path = it.Source.Path;
        // files and ISOs by size + mtime; disc folders by the title's total size
        var id = ResultsCache.TryFileIdentity(path)
            ?? (Directory.Exists(path) ? Path.GetFullPath(path) + "|" + it.Title.SizeBytes.ToString(CultureInfo.InvariantCulture) : null);
        return id == null ? null : ResultsCache.Key("compare", "1", ResultsCache.BuildOf(typeof(SyncDetector)), id, it.Title.Name, it.Title.Duration.ToString("R", CultureInfo.InvariantCulture));
    }

    private static string PairKey(string referenceId, string id) => ResultsCache.Key("pair", referenceId, id);

    /// <summary>Restores what the cache has for <paramref name="item"/> (only what is not there yet). Returns what came back.</summary>
    private async Task<List<string>> RestoreFromCacheAsync(CompareItem item, CancellationToken ct)
    {
        var restored = new List<string>();
        if (SourceId(item) is not { } id)
            return restored;
        if (item.Video == null && item.Audio.Count == 0 && ResultsCache.Shared.Get(CacheSource, id, Info<SessionSource>()) is { } s)
        {
            item.Video = s.Video;
            await RestoreAudioAsync(s, item, ct);
            if (s.Video != null)
                restored.Add("video analysis");
            if (s.Audio.Count > 0)
                restored.Add("audio analysis");
        }
        var idx = _items.IndexOf(item);
        if (idx > 0 && SourceId(_items[0]) is { } refId && ResultsCache.Shared.Get(CachePair, PairKey(refId, id), Info<CachedPair>()) is { } p)
        {
            if (!item.Synced && p.TimeMap.Synced)
            {
                ApplyTimeMap(p.TimeMap, item);
                restored.Add("sync");
            }
            if (item.Grade == null && item.Temporal == null)
            {
                ApplyPairResults(p.TimeMap, item);
                if (item.Grade != null || item.Temporal != null)
                    restored.Add("remaster checks");
            }
            if (p.Metric != null && !_metrics.Any(m => m.Distorted == item && m.Reference == _items[0]))
            {
                _metrics.Add((_items[0], item, p.Metric));
                restored.Add("quality metrics");
            }
        }
        return restored;
    }

    /// <summary>Keeps every source's current results in the cache (after each finished job).</summary>
    private async Task StoreInCacheAsync()
    {
        if (!ResultsCache.Shared.Enabled || _items.Count == 0)
            return;
        var refId = SourceId(_items[0]);
        foreach (var it in _items.ToList())
        {
            if (SourceId(it) is not { } id)
                continue;
            var all = await ToSessionSourceAsync(it);
            if (it.Video != null || it.Audio.Count > 0)
            {
                var own = new SessionSource { Path = all.Path, Title = all.Title, Video = all.Video, Audio = all.Audio, Spectrograms = all.Spectrograms };
                Put(CacheSource, id, own);
            }
            if (refId == null || it == _items[0])
                continue;
            var pair = new CachedPair
            {
                TimeMap = new SessionSource
                {
                    Path = all.Path, Title = all.Title, Offset = all.Offset, Speed = all.Speed, Synced = all.Synced, Sections = all.Sections, Ivtc = all.Ivtc,
                    GradeFrames = all.GradeFrames, GradeFindings = all.GradeFindings, TemporalFindings = all.TemporalFindings, FrameEvents = all.FrameEvents,
                },
                Metric = _metrics.FirstOrDefault(m => m.Distorted == it && m.Reference == _items[0]).Result,
            };
            if (pair.TimeMap.Synced || pair.Metric != null || pair.TimeMap.GradeFindings.Count > 0 || pair.TimeMap.TemporalFindings.Count > 0)
                Put(CachePair, PairKey(refId, id), pair);
        }
    }

    private void Put<T>(string kind, string key, T value)
    {
        var json = JsonSerializer.Serialize(value, SessionFile.Json);
        var slot = kind + "/" + key;
        if (_cacheWritten.TryGetValue(slot, out var last) && last == json)
            return;
        _cacheWritten[slot] = json;
        ResultsCache.Shared.Put(kind, key, value, Info<T>());
    }

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> Info<T>() =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)SessionFile.Json.GetTypeInfo(typeof(T));

    // ---------- BDInfo full scans ----------

    private readonly Dictionary<(Model.MediaSource Source, string Playlist), string?> _bdCached = [];

    private static string? BdKey(CompareItem it, string playlist) => SourceId(it) is { } id ? ResultsCache.Key("bdinfo", id, playlist) : null;

    /// <summary>A full-scan report of this title from an earlier visit, or null.</summary>
    private string? CachedBdReport(CompareItem it, string playlist)
    {
        if (_bdCached.TryGetValue((it.Source, playlist), out var r))
            return r;
        r = BdKey(it, playlist) is { } key ? ResultsCache.Shared.Get(CacheBdInfo, key, Info<string>()) : null;
        _bdCached[(it.Source, playlist)] = r;
        return r;
    }

    private void StoreBdReport(CompareItem it, string playlist, string report)
    {
        _bdCached[(it.Source, playlist)] = report;
        if (BdKey(it, playlist) is { } key)
            ResultsCache.Shared.Put(CacheBdInfo, key, report, Info<string>());
    }
}
