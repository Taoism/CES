using System.Globalization;
using Ces.Model;

using V = Ces.Vfs.Vfs;

namespace Ces.Disc;

/// <summary>Turns a BDMV folder into titles (one per distinct movie playlist).</summary>
public static class BluRayReader
{
    /// <summary>Playlists shorter than this are listed only when <c>includeShort</c> is set.</summary>
    public const double ShortPlaylistSeconds = 60;

    public static bool IsBluRayRoot(string dir) =>
        V.DirectoryExists(Path.Combine(dir, "BDMV", "PLAYLIST")) && V.DirectoryExists(Path.Combine(dir, "BDMV", "STREAM"));

    public static List<MediaTitle> ReadTitles(string root, bool includeShort = false)
    {
        var bdmv = Path.Combine(root, "BDMV");
        var playlistDir = Path.Combine(bdmv, "PLAYLIST");
        var streamDir = Path.Combine(bdmv, "STREAM");
        var clipDir = Path.Combine(bdmv, "CLIPINF");
        var clpiCache = new Dictionary<string, Clpi?>(StringComparer.OrdinalIgnoreCase);
        var titles = new List<MediaTitle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in V.EnumerateFiles(playlistDir, ".mpls").Order(StringComparer.OrdinalIgnoreCase))
        {
            Mpls mpls;
            try
            {
                mpls = Mpls.Parse(V.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                continue;
            }
            if (mpls.Items.Count == 0 || (!includeShort && mpls.Duration < ShortPlaylistSeconds))
                continue;
            // obfuscated discs carry many copies of the same playlist: keep the first
            var key = string.Join("|", mpls.Items.Select(i => $"{i.Clip}:{i.InTime}:{i.OutTime}"));
            if (!seen.Add(key))
                continue;

            var title = new MediaTitle { Name = Path.GetFileName(file) };
            double t = 0;
            var itemStarts = new List<double>();
            var missing = false;
            foreach (var item in mpls.Items)
            {
                var m2ts = FindStream(streamDir, item.Clip);
                if (m2ts == null)
                {
                    missing = true;
                    break;
                }
                if (!clpiCache.TryGetValue(item.Clip, out var clpi))
                {
                    var cp = Path.Combine(clipDir, item.Clip + ".clpi");
                    try
                    {
                        clpi = V.FileExists(cp) ? Clpi.Parse(V.ReadAllBytes(cp)) : null;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException)
                    {
                        clpi = null;
                    }
                    clpiCache[item.Clip] = clpi;
                }
                var clipStart = clpi != null && item.StcId < clpi.StcStarts.Count ? clpi.StcStarts[item.StcId] : item.InTime;
                var size = V.Length(m2ts);
                itemStarts.Add(t);
                title.Segments.Add(new TitleSegment
                {
                    Url = m2ts,
                    TitleStart = t,
                    Duration = item.Duration,
                    SeekBase = Math.Max(0, (item.InTime - (double)clipStart) / 45000.0),
                    InPoint = item.InTime / 45000.0,
                    OutPoint = item.OutTime / 45000.0,
                    FileLabel = Path.GetFileName(m2ts),
                    SizeBytes = size,
                });
                title.Angles = Math.Max(title.Angles, item.Angles);
                t += item.Duration;
            }
            if (missing)
                continue;
            title.Duration = t;
            // size: count each clip once, scaled to the part that is played
            title.SizeBytes = title.Segments.GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
                .Sum(g => g.First().SizeBytes);

            var n = 1;
            foreach (var mark in mpls.Marks.Where(mk => mk.Type == 1 && mk.PlayItem < mpls.Items.Count))
            {
                var item = mpls.Items[mark.PlayItem];
                var at = itemStarts[mark.PlayItem] + (mark.Time - (double)item.InTime) / 45000.0;
                if (at < 0 || at > t)
                    continue;
                // marks at the very end are not chapters
                if (t - at < 1 && title.Chapters.Count > 0)
                    continue;
                if (title.Chapters.Count > 0 && Math.Abs(title.Chapters[^1].Start - at) < 0.001)
                    continue;
                title.Chapters.Add(new Chapter(at, $"Chapter {n++:00}"));
            }

            foreach (var s in mpls.Streams)
            {
                if (s.Language.Length > 0)
                    title.StreamLanguages["0x" + s.Pid.ToString("x", CultureInfo.InvariantCulture)] = s.Language;
            }

            var clips = mpls.Items.Select(i => i.Clip).Distinct().ToList();
            title.Description = $"{Format.Time(t, false)}, {(clips.Count == 1 ? clips[0] + ".m2ts" : clips.Count + " clips")}, {title.Chapters.Count} ch"
                + (title.Angles > 1 ? $", {title.Angles} angles" : "");
            titles.Add(title);
        }

        // main title: longest, then most chapters
        var main = titles.OrderByDescending(x => Math.Round(x.Duration / 10)).ThenByDescending(x => x.Chapters.Count).FirstOrDefault();
        if (main != null)
            main.IsMain = true;
        return titles;
    }

    private static string? FindStream(string streamDir, string clip)
    {
        var p = Path.Combine(streamDir, clip + ".m2ts");
        if (V.FileExists(p))
            return p;
        // some file systems/rippers use the short name
        p = Path.Combine(streamDir, clip + ".MTS");
        return V.FileExists(p) ? p : null;
    }
}
