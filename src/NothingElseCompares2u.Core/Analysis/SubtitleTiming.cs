using System.Globalization;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

public sealed record SubtitleEvent(double Start, double Duration);

public sealed class SubtitleTimingReport
{
    public required CompareItem Item { get; init; }
    public int Track { get; init; }
    public string TrackLabel { get; init; } = "";
    public int Events { get; set; }
    public int ReferenceEvents { get; set; }
    public int Matched { get; set; }

    /// <summary>Other − reference start time per matched line (seconds), with the line's timeline time.</summary>
    public List<(double Time, double Delta)> Deltas { get; } = [];

    public double MedianDelta { get; set; } = double.NaN;
    public double P90AbsDelta { get; set; } = double.NaN;
    public double WithinFrameShare { get; set; } = double.NaN;

    /// <summary>Change of the shift per hour of running time (seconds/hour).</summary>
    public double DriftPerHour { get; set; } = double.NaN;

    public List<Finding> Findings { get; } = [];
}

/// <summary>Reads subtitle events from the container and compares their timing between releases.</summary>
public sealed class SubtitleTiming(FfTools tools)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Events of subtitle stream <paramref name="track"/> (index among subtitle streams), in title time.</summary>
    public async Task<List<SubtitleEvent>> ReadAsync(MediaTitle title, int track, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var codec = title.Info?.Subtitles.ElementAtOrDefault(track)?.Codec ?? "";
        var bitmap = codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle";
        var events = new List<SubtitleEvent>();
        foreach (var seg in title.Segments)
        {
            ct.ThrowIfCancellationRequested();
            if (seg.Format == "mpeg")
                throw new InvalidOperationException("Subtitle timing is not available for DVD titles yet.");
            var args = new List<string> { "-v", "error", "-select_streams", $"s:{track}", "-show_entries", "packet=pts_time,duration_time,size", "-of", "csv=p=0" };
            args.Add(seg.Url);
            var res = await ProcessRunner.RunAsync(tools.FFprobe, args, ct).ConfigureAwait(false);
            // title time of a packet: segment start + (packet time − where the segment's play range begins)
            var origin = seg.InPoint ?? await StartTimeAsync(seg.Url, ct).ConfigureAwait(false);
            foreach (var line in res.StdOutText.Split('\n'))
            {
                var p = line.Trim().Split(',');
                if (p.Length < 3 || !double.TryParse(p[0], NumberStyles.Float, Inv, out var pts))
                    continue;
                double.TryParse(p[1], NumberStyles.Float, Inv, out var dur);
                int.TryParse(p[2], NumberStyles.Integer, Inv, out var size);
                // PGS/VobSub: the "clear screen" sets are tiny; they end a line, they are not lines
                if (bitmap && size < 64)
                    continue;
                // MP4 text tracks fill the gaps between lines with empty samples
                if (!bitmap && size <= 2)
                    continue;
                var t = seg.TitleStart + pts - origin;
                if (t >= seg.TitleStart - 0.5 && t <= seg.TitleStart + seg.Duration + 0.5)
                    events.Add(new SubtitleEvent(t, dur));
            }
            progress?.Report((title.Segments.IndexOf(seg) + 1.0) / title.Segments.Count);
        }
        return events.OrderBy(e => e.Start).ToList();
    }

    private async Task<double> StartTimeAsync(string url, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(tools.FFprobe, ["-v", "error", "-show_entries", "format=start_time", "-of", "csv=p=0", url], ct).ConfigureAwait(false);
        return double.TryParse(res.StdOutText.Trim(), NumberStyles.Float, Inv, out var s) ? s : 0;
    }

    /// <summary>Matches every reference line to the nearest line of the other source (within 1.5 s) on the shared timeline.</summary>
    public static SubtitleTimingReport Compare(CompareItem reference, IReadOnlyList<SubtitleEvent> refEvents, CompareItem item, int track, IReadOnlyList<SubtitleEvent> events)
    {
        var rep = new SubtitleTimingReport
        {
            Item = item,
            Track = track,
            TrackLabel = item.Title.Info?.Subtitles.ElementAtOrDefault(track)?.Label ?? "",
            Events = events.Count,
            ReferenceEvents = refEvents.Count,
        };
        var other = events.Select(e => item.TimelineOf(e.Start)).OfType<double>().Order().ToArray();
        var fps = reference.Title.Info?.Video?.FrameRate is > 0 and var f ? f : 24;
        var used = new bool[other.Length];
        foreach (var e in refEvents)
        {
            if (reference.TimelineOf(e.Start) is not { } t)
                continue;
            var i = Array.BinarySearch(other, t);
            if (i < 0)
                i = ~i;
            var best = -1;
            var bestD = double.MaxValue;
            for (var k = Math.Max(0, i - 2); k <= Math.Min(other.Length - 1, i + 2); k++)
            {
                var d = Math.Abs(other[k] - t);
                if (!used[k] && d < bestD)
                {
                    bestD = d;
                    best = k;
                }
            }
            if (best >= 0 && bestD <= 1.5)
            {
                used[best] = true;
                rep.Deltas.Add((t, other[best] - t));
            }
        }
        rep.Matched = rep.Deltas.Count;
        if (rep.Matched > 0)
        {
            var d = rep.Deltas.Select(x => x.Delta).Order().ToList();
            rep.MedianDelta = d[d.Count / 2];
            var abs = rep.Deltas.Select(x => Math.Abs(x.Delta - rep.MedianDelta)).Order().ToList();
            rep.P90AbsDelta = abs[(int)(abs.Count * 0.9)];
            rep.WithinFrameShare = rep.Deltas.Count(x => Math.Abs(x.Delta) <= 1 / fps) / (double)rep.Matched;
            if (rep.Matched >= 10)
            {
                // least-squares slope of the shift over time
                var mt = rep.Deltas.Average(x => x.Time);
                var md = rep.Deltas.Average(x => x.Delta);
                var num = rep.Deltas.Sum(x => (x.Time - mt) * (x.Delta - md));
                var den = rep.Deltas.Sum(x => (x.Time - mt) * (x.Time - mt));
                rep.DriftPerHour = den > 0 ? num / den * 3600 : 0;
            }
        }
        BuildFindings(rep, fps);
        return rep;
    }

    private static void BuildFindings(SubtitleTimingReport r, double fps)
    {
        var f = r.Findings;
        if (r.ReferenceEvents == 0)
        {
            f.Add(new Finding { Area = "Subtitles", Level = 1, Text = "The reference track has no lines in this title." });
            return;
        }
        var share = (double)r.Matched / r.ReferenceEvents;
        f.Add(new Finding { Area = "Lines", Level = share < 0.8 ? 2 : 0, Text = $"{r.Matched} of {r.ReferenceEvents} reference lines found ({share:P0}); this track has {r.Events}." + (share < 0.8 ? " A different subtitle edit or translation, or a cut." : "") });
        if (r.Matched == 0)
            return;
        var shiftLevel = Math.Abs(r.MedianDelta) > 0.25 ? 2 : Math.Abs(r.MedianDelta) > 1.5 / fps ? 1 : 0;
        f.Add(new Finding { Area = "Shift", Level = shiftLevel, Text = $"Median shift {r.MedianDelta * 1000:+0;-0;0} ms, 90 % of lines within ±{r.P90AbsDelta * 1000:0} ms of that, {r.WithinFrameShare:P0} within one frame." });
        if (!double.IsNaN(r.DriftPerHour) && Math.Abs(r.DriftPerHour) > 0.1)
            f.Add(new Finding { Area = "Drift", Level = Math.Abs(r.DriftPerHour) > 0.5 ? 2 : 1, Text = $"Timing drifts {r.DriftPerHour:+0.00;-0.00} s per hour (made for another speed or frame rate?)." });
    }
}
