using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Match = NothingElseCompares2u.Dsp.Match;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Dsp;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

public sealed record SyncProbe(double RefTime, double Offset, double Confidence, double Ncc);

public sealed class SyncOptions
{
    /// <summary>Search ±this many seconds around the same time (ignored with <see cref="WholeTitle"/>).</summary>
    public double MaxShift { get; set; } = 90;

    /// <summary>Search the whole other title (decodes all of its audio once).</summary>
    public bool WholeTitle { get; set; }

    /// <summary>Probe along the whole title and build sections where the offset changes (different edits).</summary>
    public bool DetectEdits { get; set; }

    public int Probes { get; set; } = 3;
    public bool RefineWithVideo { get; set; } = true;
}

public sealed class SyncResult
{
    public List<SyncProbe> Probes { get; } = [];

    /// <summary>Source time at reference time 0 (first section when there are several).</summary>
    public double Offset { get; set; }

    /// <summary>Source seconds per reference second (1, or e.g. 23.976/25 for a PAL speed-up).</summary>
    public double Speed { get; set; } = 1;

    public double Confidence { get; set; }

    /// <summary>Offset change from the first to the last probe without edit detection (different speed or edit).</summary>
    public double Drift { get; set; }

    /// <summary>At least two probes agree (or the only probe is a very strong match): safe to apply.</summary>
    public bool Reliable { get; set; }

    /// <summary>The audio-only answer, before the video refinement.</summary>
    public double AudioOffset { get; set; }

    /// <summary>Seconds the picture comparison moved the offset by, null when not refined.</summary>
    public double? VideoCorrection { get; set; }

    /// <summary>Different edits: piecewise offsets (empty when one offset fits the whole title).</summary>
    public List<SyncSection> Sections { get; } = [];

    /// <summary>Which side got inverse telecine (3:2 pulldown removed), null when none.</summary>
    public string? Ivtc { get; set; }

    /// <summary>How many cut points were placed on the exact frame by the picture.</summary>
    public int FrameExactCuts { get; set; }

    public string Summary
    {
        get
        {
            if (Probes.Count == 0)
                return "No audio match found.";
            if (!Reliable)
                return $"No reliable match (best {Offset:+0.000;-0.000;0.000} s, confidence {Confidence:0.0}) — set the offset by hand or search the whole title";
            var s = $"Offset {Offset:+0.000;-0.000;0.000} s (confidence {Confidence:0.0})";
            if (Math.Abs(Speed - 1) > 1e-6)
                s += $", speed ×{Speed:0.#####} ({SpeedName(Speed)})";
            if (VideoCorrection is { } vc)
                s += Math.Abs(vc) > 0.001 ? $", picture-matched (audio alone said {AudioOffset:+0.000;-0.000;0.000} s)" : ", picture-matched";
            if (Ivtc != null)
                s += $", 3:2 pulldown removed on {Ivtc} (inverse telecine)";
            if (Sections.Count > 0)
            {
                s += $"\nDifferent edit — {Sections.Count} sections" + (FrameExactCuts > 0 ? $" ({FrameExactCuts} cut{(FrameExactCuts == 1 ? "" : "s")} on the exact frame)" : "") + ":";
                for (var i = 0; i < Sections.Count; i++)
                {
                    var end = i + 1 < Sections.Count ? Format.Time(Sections[i + 1].Start) : "end";
                    s += $"\n  {Format.Time(Sections[i].Start)} – {end}: " + (Sections[i].Offset is { } o ? $"{o:+0.000;-0.000;0.000} s" : "not in this source");
                }
            }
            else if (Math.Abs(Drift) > 0.08)
            {
                s += $", drifts {Drift:+0.00;-0.00} s across the title (different speed or edit — try edit detection)";
            }
            return s;
        }
    }

    public static string SpeedName(double speed) => speed switch
    {
        _ when Math.Abs(speed - 24000 / 1001.0 / 25) < 1e-4 => "PAL speed-up of 23.976",
        _ when Math.Abs(speed - 25 / (24000 / 1001.0)) < 1e-4 => "23.976 vs PAL",
        _ when Math.Abs(speed - 24 / 25.0) < 1e-4 => "PAL speed-up of 24",
        _ when Math.Abs(speed - 25 / 24.0) < 1e-4 => "24 vs PAL",
        _ => "custom",
    };
}

/// <summary>
/// Finds how another source's time maps to the reference: audio cross-correlation (optionally over the whole title,
/// for each candidate speed), then the exact frame from the picture; with edit detection, the offset per section.
/// </summary>
public sealed partial class SyncDetector(FfTools tools, string tempDir)
{
    private const int Rate = 4000;
    private const int Coarse = 4; // coarse search at 1 kHz
    private const double SnippetSeconds = 20;

    /// <summary>The other source's audio at <see cref="Rate"/>, either all of it or a window starting at <see cref="Start"/>.</summary>
    private sealed class Track(float[] samples, double start)
    {
        public float[] Samples { get; } = samples;
        public float[] Low { get; } = Correlator.Decimate(samples, Coarse);
        public double Start { get; } = start;
    }

    public Task<SyncResult> DetectAsync(MediaTitle reference, MediaTitle other, double maxShift = 60, int probes = 3, IProgress<string>? progress = null, CancellationToken ct = default) =>
        DetectAsync(reference, other, new SyncOptions { MaxShift = maxShift, Probes = probes }, progress, ct);

    public async Task<SyncResult> DetectAsync(MediaTitle reference, MediaTitle other, SyncOptions opt, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new SyncResult();
        var whole = opt.WholeTitle || opt.DetectEdits;
        Track? full = null;
        if (whole)
        {
            progress?.Report("Decoding the other source's audio…");
            full = new Track(await DecodeAsync(other, 0, null, 1, progress, ct).ConfigureAwait(false), 0);
            if (full.Samples.Length < Rate * 10)
                return result;
        }

        // 29.97 with 3:2 pulldown against 23.976 film: remove the pulldown so frames pair one to one
        foreach (var (tel, film) in new[] { (other, reference), (reference, other) })
        {
            if (!tel.Ivtc && tel.Info?.Video is { } tv && film.Info?.Video is { } fv && InputBuilder.PulldownPair(tv.FrameRate, fv.FrameRate))
            {
                progress?.Report("Checking for 3:2 pulldown…");
                var il = await new VideoAnalyzer(tools, tempDir).DetectInterlaceAsync(tel, tel.Duration / 2, ct).ConfigureAwait(false);
                var frames = il.Tff + il.Bff + il.Progressive + il.Undetermined;
                // telecine: about 2 of 5 frames combed, or repeated fields
                var combed = il.Tff + il.Bff;
                if (frames > 50 && (il.RepeatedFields > frames * 0.15 || (combed > frames * 0.2 && combed < frames * 0.6)))
                {
                    tel.Ivtc = true;
                    result.Ivtc = tel == other ? "this source" : "the reference";
                }
            }
        }

        // speed: same, or the frame-rate ratio (PAL speed-up keeps every frame and plays it faster)
        var speeds = new List<double> { 1 };
        var fr = InputBuilder.FrameRate(reference);
        var fo = InputBuilder.FrameRate(other);
        if (fr > 0 && fo > 0 && Math.Abs(fr - fo) > 0.01 && Math.Abs(fr / fo - 1) < 0.06)
            speeds.Add(fr / fo);
        var speed = 1.0;
        if (speeds.Count > 1)
        {
            var bestScore = double.MinValue;
            foreach (var s in speeds)
            {
                var trial = new List<SyncProbe>();
                foreach (var t in InputBuilder.EvenPoints(reference.Duration, 2, 25, 25))
                {
                    progress?.Report($"Trying speed ×{s:0.####} at {Format.Time(t, false)}…");
                    if (await ProbeAsync(reference, other, t, s, null, full, opt, ct).ConfigureAwait(false) is { } p)
                        trial.Add(p);
                }
                var score = trial.Count > 0 ? trial.Average(p => p.Ncc) : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    speed = s;
                }
            }
        }
        result.Speed = speed;

        if (opt.DetectEdits)
            return await DetectEditsAsync(reference, other, speed, full!, opt, result, progress, ct).ConfigureAwait(false);

        var count = whole ? Math.Max(opt.Probes, 5) : opt.Probes;
        // near search: the probes must fall where both sources have material
        var span = whole ? reference.Duration : Math.Min(reference.Duration, other.Duration / Math.Max(0.5, speed));
        foreach (var t in InputBuilder.EvenPoints(span, count, 12, 12))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Matching audio at {Format.Time(t, false)}…");
            if (await ProbeAsync(reference, other, t, speed, null, full, opt, ct).ConfigureAwait(false) is { } p)
                result.Probes.Add(p);
        }
        // the largest group of probes that agree wins; a lone or contradicted peak is noise
        const double agree = 0.1;
        var strong = result.Probes.Where(p => p.Confidence >= 10).ToList();
        var cluster = strong
            .Select(p => strong.Where(q => Math.Abs(q.Offset - p.Offset) <= agree).ToList())
            .OrderByDescending(c => c.Count).ThenByDescending(c => c.Sum(q => q.Confidence))
            .FirstOrDefault() ?? [];
        var pick = cluster.Count > 0 ? cluster : result.Probes.OrderByDescending(p => p.Confidence).Take(1).ToList();
        if (pick.Count > 0)
        {
            var sorted = pick.Select(p => p.Offset).Order().ToList();
            result.Offset = sorted[sorted.Count / 2];
            result.Confidence = pick.Average(p => p.Confidence);
        }
        result.Reliable = cluster.Count >= 2 || (cluster.Count == 1 && result.Probes.Count == 1 && cluster[0].Confidence >= 30);
        // drift from the confident probes only: a failed probe (no material there) is not a drift
        if (strong.Count >= 2)
            result.Drift = strong[^1].Offset - strong[0].Offset;
        result.AudioOffset = result.Offset;

        // audio is only as exact as each file's own A/V sync: snap to the frame that really matches
        if (opt.RefineWithVideo && result.Reliable && reference.Info?.Video != null && other.Info?.Video != null)
        {
            progress?.Report("Matching frames…");
            var refined = await RefineWithVideoAsync(reference, other, result.Offset, speed, cluster.Select(p => p.RefTime).ToList(), ct).ConfigureAwait(false);
            if (refined is { } r)
            {
                result.VideoCorrection = r - result.Offset;
                result.Offset = r;
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the reference snippet at <paramref name="t"/> in the other source. With a whole-title track: near
    /// <paramref name="expected"/> first (cheap), then everywhere; otherwise in a ±MaxShift window.
    /// </summary>
    private async Task<SyncProbe?> ProbeAsync(MediaTitle reference, MediaTitle other, double t, double speed, double? expected, Track? full, SyncOptions opt, CancellationToken ct, double seconds = SnippetSeconds)
    {
        // decoding at Rate·speed and reading it as Rate plays the snippet at the other source's speed
        var a = await DecodeAsync(reference, t, seconds, speed, null, ct).ConfigureAwait(false);
        if (a.Length < Rate * seconds * speed * 0.5)
            return null;
        var track = full;
        if (track == null)
        {
            var from = Math.Max(0, t * speed - opt.MaxShift);
            var samples = await DecodeAsync(other, from, seconds * speed + opt.MaxShift * 2, 1, null, ct).ConfigureAwait(false);
            if (samples.Length < a.Length)
                return null;
            track = new Track(samples, from);
        }
        Match m;
        if (expected is { } e)
        {
            // local look first: most probes of an edit-detection pass sit where the previous one said
            var at = (int)((t * speed + e - track.Start) * Rate);
            m = await Task.Run(() => Correlator.Find(a, track.Samples, at - Rate / 2, at + Rate / 2, ct), ct).ConfigureAwait(false);
            if (m.Ncc >= 0.5)
                return ToProbe(m, t, speed, track, 30);
        }
        var low = Correlator.Decimate(a, Coarse);
        var coarse = await Task.Run(() => Correlator.Find(low, track.Low, 0, int.MaxValue, ct), ct).ConfigureAwait(false);
        // fine: around the coarse hit at the full rate
        var c = coarse.Lag * Coarse;
        m = await Task.Run(() => Correlator.Find(a, track.Samples, c - Coarse * 4, c + Coarse * 4, ct), ct).ConfigureAwait(false);
        return ToProbe(m with { Confidence = coarse.Confidence }, t, speed, track, coarse.Confidence);
    }

    private static SyncProbe ToProbe(Match m, double t, double speed, Track track, double confidence) =>
        new(t, track.Start + m.Lag / (double)Rate - t * speed, confidence, m.Ncc);

    /// <summary>Probes along the whole title and turns runs of equal offsets into sections (cuts found to ±2 s).</summary>
    private async Task<SyncResult> DetectEditsAsync(MediaTitle reference, MediaTitle other, double speed, Track full, SyncOptions opt, SyncResult result, IProgress<string>? progress, CancellationToken ct)
    {
        var dur = reference.Duration;
        var step = Math.Clamp(dur / 60, 20, 120);
        double? last = null;
        for (var t = Math.Min(10, dur / 4); t + SnippetSeconds < dur; t += step)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Mapping edits: {Format.Time(t, false)} of {Format.Time(dur, false)}…");
            if (await ProbeAsync(reference, other, t, speed, last, full, opt, ct).ConfigureAwait(false) is { } p)
            {
                result.Probes.Add(p);
                if (Matched(p))
                    last = p.Offset;
            }
        }
        // runs: consecutive probes with the same offset; unmatched probes = missing
        var runs = new List<(double? Offset, List<SyncProbe> Probes)>();
        foreach (var p in result.Probes)
        {
            double? off = Matched(p) ? p.Offset : null;
            if (runs.Count > 0 && Same(runs[^1].Offset, off))
                runs[^1].Probes.Add(p);
            else
                runs.Add((off, [p]));
        }
        // a single odd probe between two runs that agree is noise (silence, music cue), not an edit
        for (var i = 1; i + 1 < runs.Count; i++)
        {
            if (runs[i].Probes.Count == 1 && Same(runs[i - 1].Offset, runs[i + 1].Offset) && !(runs[i].Probes[0].Ncc > 0.6))
            {
                runs[i - 1].Probes.AddRange(runs[i].Probes);
                runs[i - 1].Probes.AddRange(runs[i + 1].Probes);
                runs.RemoveRange(i, 2);
                i--;
            }
        }
        var matchedRuns = runs.Where(r => r.Offset != null).ToList();
        result.Reliable = matchedRuns.Any(r => r.Probes.Count >= 2);
        if (matchedRuns.Count == 0)
            return result;
        // offset of each run: median of its probes
        var offsets = runs.Select(r => r.Offset == null ? (double?)null : r.Probes.Select(p => p.Offset).Order().ElementAt(r.Probes.Count / 2)).ToList();
        result.Confidence = matchedRuns.SelectMany(r => r.Probes).Average(p => p.Confidence);

        // locate each cut: where the earlier part stops matching and where the later one starts (to ~1 s);
        // a gap between the two is a part of the reference that is not in the other source
        var sections = new List<SyncSection> { new() { Start = 0, Offset = offsets[0] } };
        for (var i = 1; i < runs.Count; i++)
        {
            var lo = runs[i - 1].Probes[^1].RefTime;
            var hi = runs[i].Probes[0].RefTime;
            double endA = lo + SnippetSeconds, startB = hi;
            // a probe's 20 s snippet can straddle the cut and still match: search a snippet beyond each side
            var dur0 = reference.Duration - CutSnippet;
            if (offsets[i - 1] is { } oa)
            {
                double a0 = Math.Max(0, lo - SnippetSeconds), b0 = Math.Min(dur0, hi + SnippetSeconds);
                while (b0 - a0 > 1)
                {
                    var mid = (a0 + b0) / 2;
                    progress?.Report($"Locating the cut near {Format.Time(mid, false)}…");
                    if (await MatchesAsync(reference, mid, speed, oa, full, ct).ConfigureAwait(false))
                        a0 = mid;
                    else
                        b0 = mid;
                }
                endA = a0 + CutSnippet;
            }
            if (offsets[i] is { } ob)
            {
                double a0 = Math.Max(0, lo - SnippetSeconds), b0 = Math.Min(dur0, hi + SnippetSeconds);
                while (b0 - a0 > 1)
                {
                    var mid = (a0 + b0) / 2;
                    progress?.Report($"Locating the cut near {Format.Time(mid, false)}…");
                    if (await MatchesAsync(reference, mid, speed, ob, full, ct).ConfigureAwait(false))
                        b0 = mid;
                    else
                        a0 = mid;
                }
                startB = b0;
            }
            if (offsets[i - 1] == null)
                endA = startB;
            if (offsets[i] == null)
                startB = endA;
            if (offsets[i - 1] != null && offsets[i] != null && startB - endA > 2)
            {
                sections.Add(new SyncSection { Start = endA, Offset = null });
                sections.Add(new SyncSection { Start = startB, Offset = offsets[i] });
            }
            else
            {
                sections.Add(new SyncSection { Start = Math.Min(startB, Math.Max(endA, lo)), Offset = offsets[i] });
            }
        }
        // keep the probes of each section for the frame matching below
        var sectionProbes = sections.Select((sec, k) =>
        {
            var end = k + 1 < sections.Count ? sections[k + 1].Start : double.MaxValue;
            return result.Probes.Where(p => p.RefTime >= sec.Start && p.RefTime + SnippetSeconds <= end && Matched(p)).Select(p => p.RefTime).ToList();
        }).ToList();

        if (sections.Count == 1)
            result.Offset = offsets[0]!.Value;
        else
        {
            result.Sections.AddRange(sections);
            result.Offset = sections.First(x => x.Offset != null).Offset!.Value;
        }
        result.AudioOffset = result.Offset;

        // the exact frame, per section
        if (opt.RefineWithVideo && reference.Info?.Video != null && other.Info?.Video != null)
        {
            if (result.Sections.Count == 0)
            {
                var times = runs[0].Probes.Select(p => p.RefTime).Where((_, k) => k % Math.Max(1, runs[0].Probes.Count / 3) == 0).Take(3).ToList();
                if (await RefineWithVideoAsync(reference, other, result.Offset, speed, times, ct).ConfigureAwait(false) is { } r)
                {
                    result.VideoCorrection = r - result.Offset;
                    result.Offset = r;
                }
            }
            else
            {
                for (var i = 0; i < result.Sections.Count; i++)
                {
                    if (result.Sections[i].Offset is not { } o)
                        continue;
                    progress?.Report($"Matching frames in section {i + 1}…");
                    var times = sectionProbes[i].Take(2).ToList();
                    if (times.Count == 0)
                        times = [result.Sections[i].Start + 2];
                    if (await RefineWithVideoAsync(reference, other, o, speed, times, ct).ConfigureAwait(false) is { } r)
                        result.Sections[i].Offset = r;
                }
                // with exact offsets, put every cut on its frame
                for (var i = 1; i < result.Sections.Count; i++)
                {
                    progress?.Report($"Placing cut {i} on the exact frame…");
                    if (await RefineCutAsync(reference, other, speed, result.Sections, i, ct).ConfigureAwait(false))
                        result.FrameExactCuts++;
                }
                result.Offset = result.Sections.First(s => s.Offset != null).Offset!.Value;
            }
        }
        return result;
    }

    private static bool Matched(SyncProbe p) => p.Ncc >= 0.3 && p.Confidence >= 8;

    private static bool Same(double? a, double? b) => a == null ? b == null : b != null && Math.Abs(a.Value - b.Value) <= 0.15;

    private const double CutSnippet = 3;

    /// <summary>Whether a short reference snippet at <paramref name="t"/> is found at offset <paramref name="off"/>.</summary>
    private async Task<bool> MatchesAsync(MediaTitle reference, double t, double speed, double off, Track full, CancellationToken ct)
    {
        var a = await DecodeAsync(reference, t, CutSnippet, speed, null, ct).ConfigureAwait(false);
        if (a.Length < 64)
            return false;
        var at = (int)((t * speed + off) * Rate);
        var m = await Task.Run(() => Correlator.Find(a, full.Samples, at - Rate / 10, at + Rate / 10, ct), ct).ConfigureAwait(false);
        return m.Ncc >= 0.4;
    }

    // ---------- video refinement ----------

    private const int FrameW = 160;
    private const int FrameH = 90;

    /// <summary>
    /// Compares small grey frames around the audio offset (±12 frames) at a few places and returns the offset of the
    /// best-matching frame pair (from real frame timestamps), or null when the picture gives no clear answer.
    /// </summary>
    public async Task<double?> RefineWithVideoAsync(MediaTitle reference, MediaTitle other, double offset, double speed, IReadOnlyList<double> times, CancellationToken ct)
    {
        var fr = InputBuilder.FrameRate(reference);
        var fo = InputBuilder.FrameRate(other);
        // frames pair one to one only when the other source shows the same frames per reference second
        if (fr <= 0 || fo <= 0 || Math.Abs(fr - fo * speed) > 0.01)
            return null;
        const int n = 36;
        const int m = 12;
        var votes = new List<(double Offset, double Margin)>();
        var points = times.Count > 0 ? times : InputBuilder.EvenPoints(Math.Min(reference.Duration, other.Duration), 3, 20, 20);
        foreach (var t in points)
        {
            ct.ThrowIfCancellationRequested();
            var a = await GrayFramesAsync(reference, t, n, ct).ConfigureAwait(false);
            var b = await GrayFramesAsync(other, t * speed + offset - m / fo, n + 2 * m, ct).ConfigureAwait(false);
            if (a.Count < n / 2 || b.Count < a.Count + m)
                continue;
            // static shots tell nothing about which frame is which
            var motion = Enumerable.Range(1, a.Count - 1).Average(i => Diff(a[i - 1].Pixels, a[i].Pixels));
            if (motion < 0.4)
                continue;
            var costs = new List<(int Shift, double Cost)>();
            for (var s = 0; s + a.Count <= b.Count; s++)
                costs.Add((s, Enumerable.Range(0, a.Count).Average(i => Diff(a[i].Pixels, b[i + s].Pixels))));
            var sorted = costs.OrderBy(c => c.Cost).ToList();
            var best = sorted[0];
            var next = sorted.Count > 1 ? sorted[1].Cost : best.Cost * 2;
            var margin = best.Cost > 0 ? (next - best.Cost) / best.Cost : 10;
            if (margin < 0.15)
                continue;
            // offset from the matched frames' own timestamps
            var pairs = Enumerable.Range(0, a.Count).Select(i => b[i + best.Shift].Time - a[i].Time * speed).Order().ToList();
            votes.Add((pairs[pairs.Count / 2], margin));
        }
        if (votes.Count == 0)
            return null;
        // the most common answer (within half a frame), weighted by how clear it was
        var group = votes.Select(v => votes.Where(w => Math.Abs(w.Offset - v.Offset) < 0.5 / fo).ToList())
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Sum(x => x.Margin)).First();
        if (votes.Count >= 2 && group.Count < 2)
            return null;
        return group.Select(g => g.Offset).Order().ElementAt(group.Count / 2);
    }

    // brightness-neutral: HDR vs SDR or a level shift should not decide the match
    private Task<List<GrayFrame>> GrayFramesAsync(MediaTitle title, double start, int count, CancellationToken ct) =>
        FrameSequence.GrabAsync(tools, title, start, count, FrameW, FrameH, tempDir, normalize: true, ct);

    private static double Diff(float[] a, float[] b) => FrameSequence.Diff(a, b);

    /// <summary>
    /// Moves the cut between section <paramref name="i"/>−1 and <paramref name="i"/> onto the exact frame: every
    /// reference frame around the audio estimate is matched against the other source under both sections' offsets
    /// (or "not there" for a missing section); the cut goes where the labels switch. Returns false when unclear.
    /// </summary>
    private async Task<bool> RefineCutAsync(MediaTitle reference, MediaTitle other, double speed, List<SyncSection> sections, int i, CancellationToken ct)
    {
        var fr = InputBuilder.FrameRate(reference);
        var fo = InputBuilder.FrameRate(other);
        if (fr <= 0 || fo <= 0 || Math.Abs(fr - fo * speed) > 0.01)
            return false;
        var approx = sections[i].Start;
        const double half = 4;
        var start = Math.Max(0, approx - half);
        var count = (int)(2 * half * fr);
        var refFrames = await GrayFramesAsync(reference, start, count, ct).ConfigureAwait(false);
        if (refFrames.Count < count / 2)
            return false;
        async Task<List<GrayFrame>?> Under(double? off) =>
            off is { } o ? await GrayFramesAsync(other, Math.Max(0, start * speed + o - 1.0 / fo), count + 4, ct).ConfigureAwait(false) : null;
        var a = await Under(sections[i - 1].Offset).ConfigureAwait(false);
        var b = await Under(sections[i].Offset).ConfigureAwait(false);

        // cost of each label per reference frame: diff to the matching frame, or "missing" when that section is absent
        double Cost(List<GrayFrame>? list, double? off, GrayFrame r) =>
            list != null && off is { } o && FrameSequence.At(list, r.Time * speed + o, fo) is { } m ? Diff(r.Pixels, m.Pixels) : double.NaN;
        var costA = refFrames.Select(r => Cost(a, sections[i - 1].Offset, r)).ToList();
        var costB = refFrames.Select(r => Cost(b, sections[i].Offset, r)).ToList();
        // a frame "matches" a section when it is as close as true matches are (near the best costs seen)
        var all = costA.Concat(costB).Where(x => !double.IsNaN(x)).Order().ToList();
        if (all.Count < 10)
            return false;
        // true matches sit near zero; mismatches are many times higher. Anchor on the best costs, not the bulk.
        var low = all[Math.Max(0, all.Count / 10 - 1)];
        if (low > 10)
            return false;
        var thr = Math.Max(4.0, 2.5 * low);
        var n = refFrames.Count;
        var mA = costA.Select(x => !double.IsNaN(x) && x <= thr).ToArray();
        var mB = costB.Select(x => !double.IsNaN(x) && x <= thr).ToArray();
        // penalties: before the cut a frame should be section A (or, when A is missing, not B);
        // from the cut on it should be section B (or, when B is missing, not A)
        bool BadBefore(int k) => a != null ? !mA[k] : mB[k];
        bool BadAfter(int k) => b != null ? !mB[k] : mA[k];
        var bestK = -1;
        var bestBad = int.MaxValue;
        var before = 0;
        var afterTotal = Enumerable.Range(0, n).Count(BadAfter);
        for (var k = 1; k < n; k++)
        {
            before += BadBefore(k - 1) ? 1 : 0;
            afterTotal -= BadAfter(k - 1) ? 1 : 0;
            var bad = before + afterTotal;
            if (bad < bestBad)
            {
                bestBad = bad;
                bestK = k;
            }
        }
        // both sides must be clearly what they claim: few contradicting frames, real matches on the present side(s)
        var matchesBefore = Enumerable.Range(0, Math.Max(0, bestK)).Count(k => a != null ? mA[k] : !mB[k]);
        var matchesAfter = Enumerable.Range(Math.Max(0, bestK), n - Math.Max(0, bestK)).Count(k => b != null ? mB[k] : !mA[k]);
        if (bestK <= 0 || bestBad > n * 0.15 || matchesBefore < 3 || matchesAfter < 3)
            return false;
        // halfway between the last frame of A and the first of B
        sections[i].Start = (refFrames[bestK - 1].Time + refFrames[bestK].Time) / 2;
        return true;
    }

    [GeneratedRegex(@"out_time_us=(\d+)")]
    private static partial Regex ProgressRegex();

    /// <summary>
    /// Mono audio at <see cref="Rate"/>. <paramref name="speed"/> ≠ 1 resamples to Rate·speed but is read as Rate,
    /// which plays the audio at the other source's speed (pitch included, like a PAL speed-up).
    /// </summary>
    private async Task<float[]> DecodeAsync(MediaTitle title, double start, double? length, double speed, IProgress<string>? progress, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        var input = InputBuilder.Input(title, start, length, tempDir);
        args.AddRange(input.Args);
        var rate = (int)Math.Round(Rate * speed);
        if (progress != null)
            args.AddRange(["-progress", "pipe:2", "-nostats"]);
        args.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-af", input.ATrimFilter + "highpass=f=80", "-f", "f32le", "-acodec", "pcm_f32le", "-"]);
        var total = length ?? title.Duration;
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct, progress == null ? null : line =>
        {
            var m = ProgressRegex().Match(line);
            if (m.Success && total > 0)
                progress.Report($"Decoding audio: {long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1e6 / total:P0}");
        }).ConfigureAwait(false);
        return MemoryMarshal.Cast<byte, float>(res.StdOut.AsSpan(0, res.StdOut.Length / 4 * 4)).ToArray();
    }

    /// <summary>Normalised cross-correlation of a inside b: (best lag in samples, peak-to-mean ratio).</summary>
    internal static (int Lag, double Confidence) Correlate(float[] a, float[] b)
    {
        var m = Correlator.Find(a, b);
        return (m.Lag, m.Confidence);
    }
}
