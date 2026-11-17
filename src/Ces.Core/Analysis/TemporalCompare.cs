using Ces.Compare;
using Ces.Model;
using Ces.Tools;

namespace Ces.Analysis;

/// <param name="Until">Set when the event happened somewhere between two checked windows (Time … Until).</param>
public sealed record FrameEvent(double Time, string Kind, int Frames = 1, double? Until = null);

public sealed class TemporalReport
{
    public required CompareItem Item { get; init; }

    /// <summary>Dropped / duplicated frames found (times on the shared timeline).</summary>
    public List<FrameEvent> Events { get; } = [];

    public double SecondsChecked { get; set; }

    /// <summary>Transient specks (dirt, sparkle) per frame, reference and this source.</summary>
    public double RefSpecks { get; set; } = double.NaN;
    public double Specks { get; set; } = double.NaN;

    /// <summary>Frame-to-frame jitter (gate weave, shake) after removing smooth motion, in pixels of a 1920-wide frame.</summary>
    public double RefJitter { get; set; } = double.NaN;
    public double Jitter { get; set; } = double.NaN;

    public List<Finding> Findings { get; } = [];
}

/// <summary>
/// What happens between frames: dropped or duplicated frames against the reference, dirt/specks that a restoration
/// removed (or added), and gate weave / shake that a remaster stabilised.
/// </summary>
public sealed class TemporalCompare(FfTools tools, string tempDir)
{
    internal const int W = 256;
    internal const int H = 144;

    public async Task<TemporalReport> CompareAsync(CompareItem reference, CompareItem item, int windows = 5, double seconds = 4, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rep = new TemporalReport { Item = item };
        var fr = InputBuilder.FrameRate(reference.Title);
        var fo = InputBuilder.FrameRate(item.Title);
        if (fr <= 0 || fo <= 0 || item.Title.IsStill || reference.Title.IsStill)
            return rep;
        var oneToOne = Math.Abs(fr - fo * item.Speed) < 0.01;
        var (s0, e0) = item.Coverage();
        var (s1, e1) = reference.Coverage();
        var start = Math.Max(Math.Max(0, s0), Math.Max(0, s1));
        var end = Math.Min(e0, e1) - seconds - 1;
        if (end <= start)
            return rep;
        var n = (int)(seconds * fr);
        var refSpecks = new List<double>();
        var specks = new List<double>();
        var refJ = new List<double>();
        var jit = new List<double>();
        var k = 0;
        (double End, int Offset)? previous = null;
        foreach (var t in InputBuilder.EvenPoints(end - start, windows, 6, 6).Select(x => x + start))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{item.Name}: frames {++k}/{windows}");
            if (reference.SourceTime(t) is not { } rt || item.SourceTime(t) is not { } it || item.SourceTime(t + seconds) is null)
                continue;
            var a = await FrameSequence.GrabAsync(tools, reference.Title, rt, n, W, H, tempDir, false, ct).ConfigureAwait(false);
            var b = await FrameSequence.GrabAsync(tools, item.Title, Math.Max(0, it - 10 / fo), n + 20, W, H, tempDir, false, ct).ConfigureAwait(false);
            if (a.Count < n / 2 || b.Count < n / 2)
                continue;
            rep.SecondsChecked += a.Count / fr;
            refSpecks.Add(Specks(a));
            specks.Add(Specks(b));
            refJ.Add(Jitter(a));
            jit.Add(Jitter(b));
            if (!oneToOne)
                continue;
            var (first, last) = FindTimingEvents(reference, item, a, b, fo, rep.Events);
            // a step between two windows: the frame offset changed somewhere in the unchecked stretch
            if (previous is { } p && first is { } f0 && f0 != p.Offset)
            {
                var d = f0 - p.Offset;
                rep.Events.Add(new FrameEvent(p.End, d > 0 ? "duplicated / inserted frame" : "dropped frame", Math.Abs(d), reference.TimelineTime(a[0].Time)));
            }
            if (last is { } l0)
                previous = (reference.TimelineTime(a[^1].Time), l0);
            else if (first is null)
                previous = null;
        }
        rep.RefSpecks = Median(refSpecks);
        rep.Specks = Median(specks);
        rep.RefJitter = Median(refJ) * 1920.0 / W;
        rep.Jitter = Median(jit) * 1920.0 / W;
        BuildFindings(rep, oneToOne);
        return rep;
    }

    private static double Median(List<double> v)
    {
        var l = v.Where(x => !double.IsNaN(x)).Order().ToList();
        return l.Count == 0 ? double.NaN : l[l.Count / 2];
    }

    /// <summary>
    /// For every reference frame, which nearby frame of the other source shows the same picture (brightness-neutral).
    /// A lasting step in that frame offset is a dropped (−1) or duplicated/inserted (+1) frame in the other source.
    /// </summary>
    /// <returns>The frame offset at the start and at the end of the window (null when unclear).</returns>
    private static (int? First, int? Last) FindTimingEvents(CompareItem reference, CompareItem item, List<GrayFrame> a, List<GrayFrame> b, double fo, List<FrameEvent> events)
    {
        static float[] Norm(GrayFrame f)
        {
            var p = new float[f.Pixels.Length];
            for (var i = 0; i < p.Length; i++)
                p[i] = (float)(f.Pixels[i] - f.Mean);
            return p;
        }
        var an = a.Select(Norm).ToList();
        var bn = b.Select(Norm).ToList();
        var offsets = new List<(int Index, int Offset)>();
        for (var i = 0; i < a.Count; i++)
        {
            // expected partner from the time map, then the best within ±8 frames (start-time shifts of remuxes)
            if (item.SourceTime(reference.TimelineTime(a[i].Time)) is not { } st)
                continue;
            var j0 = -1;
            var best = double.MaxValue;
            for (var j = 0; j < b.Count; j++)
            {
                var d = Math.Abs(b[j].Time - st);
                if (d < best)
                {
                    best = d;
                    j0 = j;
                }
            }
            if (j0 < 0 || best > 1 / fo)
                continue;
            var bestOff = 0;
            var bestCost = double.MaxValue;
            for (var o = -8; o <= 8; o++)
            {
                var j = j0 + o;
                if (j < 0 || j >= b.Count)
                    continue;
                var c = FrameSequence.Diff(an[i], bn[j]);
                if (c < bestCost)
                {
                    bestCost = c;
                    bestOff = o;
                }
            }
            offsets.Add((i, bestOff));
        }
        // the clear majority of a run of frames (null when the picture does not say)
        static int? Mode(IEnumerable<int> v)
        {
            var g = v.GroupBy(x => x).OrderByDescending(x => x.Count()).ToList();
            var total = g.Sum(x => x.Count());
            return total >= 6 && g[0].Count() >= total * 0.75 ? g[0].Key : null;
        }
        // steps that last: the runs of frames before and after a frame agree on different offsets
        const int run = 8;
        for (var m = run; m + run <= offsets.Count; m++)
        {
            if (offsets[m - 1].Offset == offsets[m].Offset)
                continue;
            var before = Mode(offsets.Skip(m - run).Take(run).Select(x => x.Offset));
            var after = Mode(offsets.Skip(m).Take(run).Select(x => x.Offset));
            if (before is not { } b0 || after is not { } a0 || b0 == a0 || offsets[m - 1].Offset != b0 || offsets[m].Offset != a0)
                continue;
            var t = reference.TimelineTime(a[offsets[m].Index].Time);
            if (events.All(e => Math.Abs(e.Time - t) > 0.5))
                events.Add(new FrameEvent(t, a0 > b0 ? "duplicated / inserted frame" : "dropped frame", Math.Abs(a0 - b0)));
        }
        // the window's offset at either end
        const int edge = 12;
        return (Mode(offsets.Take(edge).Select(x => x.Offset)), Mode(offsets.TakeLast(edge).Select(x => x.Offset)));
    }

    /// <summary>Transient specks per frame: pixels that jump away from both neighbours while the neighbours agree.</summary>
    internal static double Specks(List<GrayFrame> f)
    {
        if (f.Count < 3)
            return double.NaN;
        const float t = 28;
        double total = 0;
        var frames = 0;
        for (var i = 1; i + 1 < f.Count; i++)
        {
            var p = f[i - 1].Pixels;
            var c = f[i].Pixels;
            var nx = f[i + 1].Pixels;
            var count = 0;
            for (var k = 0; k < c.Length; k++)
            {
                var d1 = c[k] - p[k];
                var d2 = c[k] - nx[k];
                if (Math.Abs(d1) > t && Math.Abs(d2) > t && Math.Sign(d1) == Math.Sign(d2) && Math.Abs(p[k] - nx[k]) < t / 2)
                    count++;
            }
            // a flash or a cut makes large areas "transient": those frames are not dirt
            if (count > c.Length / 50)
                continue;
            total += count;
            frames++;
        }
        return frames > 0 ? total / frames : double.NaN;
    }

    /// <summary>Std-dev of the frame-to-frame global shift after removing its smooth part (camera moves).</summary>
    internal static double Jitter(List<GrayFrame> f)
    {
        if (f.Count < 8)
            return double.NaN;
        var dx = new List<double>();
        var dy = new List<double>();
        for (var i = 1; i < f.Count; i++)
        {
            var (x, y, peak) = GradeCompare.PhaseCorrelate(f[i - 1].Pixels, f[i].Pixels, W, H);
            if (peak < 0.05 || Math.Abs(x) > 20 || Math.Abs(y) > 20)
            {
                dx.Add(double.NaN);
                dy.Add(double.NaN);
                continue;
            }
            dx.Add(x);
            dy.Add(y);
        }
        double Resid(List<double> v)
        {
            var r = new List<double>();
            for (var i = 2; i + 2 < v.Count; i++)
            {
                var win = v.Skip(i - 2).Take(5).Where(z => !double.IsNaN(z)).ToList();
                if (win.Count < 4 || double.IsNaN(v[i]))
                    continue;
                r.Add(v[i] - win.Average());
            }
            return r.Count < 4 ? double.NaN : Math.Sqrt(r.Average(z => z * z));
        }
        var rx = Resid(dx);
        var ry = Resid(dy);
        return double.IsNaN(rx) || double.IsNaN(ry) ? double.NaN : Math.Sqrt(rx * rx + ry * ry);
    }

    private static void BuildFindings(TemporalReport r, bool oneToOne)
    {
        var f = r.Findings;
        if (r.SecondsChecked <= 0)
        {
            f.Add(new Finding { Area = "Frames", Level = 0, Text = "No overlapping material to check." });
            return;
        }
        if (oneToOne)
        {
            var drops = r.Events.Where(e => e.Kind.StartsWith("dropped", StringComparison.Ordinal)).Sum(e => e.Frames);
            var dups = r.Events.Sum(e => e.Frames) - drops;
            f.Add(r.Events.Count == 0
                ? new Finding { Area = "Frames", Level = 0, Text = $"No dropped or duplicated frames in {r.SecondsChecked:0} s checked." }
                : new Finding
                {
                    Area = "Frames",
                    Level = 2,
                    Text = $"{drops} dropped and {dups} duplicated frame(s) in {r.SecondsChecked:0} s checked, at " +
                           string.Join(", ", r.Events.Take(6).Select(e => e.Until is { } u ? $"{Format.Time(e.Time)}–{Format.Time(u)}" : Format.Time(e.Time))) + (r.Events.Count > 6 ? " …" : "") + ".",
                });
        }
        else
        {
            f.Add(new Finding { Area = "Frames", Level = 0, Text = "Different frame rates: dropped/duplicated frames are not checked (set the speed or inverse telecine to pair frames)." });
        }
        if (!double.IsNaN(r.RefSpecks) && !double.IsNaN(r.Specks))
        {
            var refS = r.RefSpecks;
            var s = r.Specks;
            if (refS >= 3 && s < refS * 0.35)
                f.Add(new Finding { Area = "Restoration", Level = 1, Text = $"Dirt/specks removed: {s:0.0} transient specks per frame vs {refS:0.0} in the reference (cleaned up — or grain smoothed away)." });
            else if (s >= 3 && s > refS * 2.5)
                f.Add(new Finding { Area = "Restoration", Level = 1, Text = $"More transient specks: {s:0.0} per frame vs {refS:0.0} in the reference (dirt, sparkle or noisier encode)." });
            else
                f.Add(new Finding { Area = "Restoration", Level = 0, Text = $"Transient specks per frame: {s:0.0} (reference {refS:0.0})." });
        }
        if (!double.IsNaN(r.RefJitter) && !double.IsNaN(r.Jitter))
        {
            if (r.RefJitter >= 0.6 && r.Jitter < r.RefJitter * 0.5)
                f.Add(new Finding { Area = "Stabilisation", Level = 1, Text = $"Stabilised: frame jitter {r.Jitter:0.00} px vs {r.RefJitter:0.00} px in the reference (gate weave / shake removed)." });
            else if (r.Jitter >= 0.6 && r.Jitter > r.RefJitter * 2)
                f.Add(new Finding { Area = "Stabilisation", Level = 1, Text = $"More frame jitter: {r.Jitter:0.00} px vs {r.RefJitter:0.00} px in the reference." });
            else
                f.Add(new Finding { Area = "Stabilisation", Level = 0, Text = $"Frame jitter {r.Jitter:0.00} px (reference {r.RefJitter:0.00} px)." });
        }
    }
}
