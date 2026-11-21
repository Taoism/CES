using System.Globalization;

namespace Ces.Compare;

/// <summary>The steps "Compare all" can run, in the order they run.</summary>
public enum CompareStep
{
    Sync,
    Screenshots,
    Video,
    Audio,
    Metrics,
    Remaster,
    Subtitles,
}

/// <summary>What the estimate needs to know about one source (source 0 is the reference).</summary>
public sealed class PlanSource
{
    public double Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public bool HasVideo { get; init; }
    public bool IsStill { get; init; }
    public int AudioTracks { get; init; }
    public bool HasSubtitles { get; init; }
    public long SizeBytes { get; init; }
    public bool Synced { get; init; }

    // results already there (from the cache, a session or an earlier run)
    public bool HasVideoResult { get; init; }
    public bool HasAudioResult { get; init; }
    public bool HasGradeResult { get; init; }
    public bool HasMetricsResult { get; init; }
    public bool HasSubtitleResult { get; init; }

    /// <summary>Pixels relative to 1080p (at least 0.25), the main cost factor of decoding.</summary>
    public double PixelScale => HasVideo && Width > 0 && Height > 0 ? Math.Max(0.25, Width * (double)Height / (1920 * 1080)) : 1;
}

/// <summary>The options the steps will run with.</summary>
public sealed class PlanOptions
{
    /// <summary>"Near", "Whole" or "Edits" (as in the settings).</summary>
    public string SyncMode { get; init; } = "Near";

    /// <summary>Screenshot rows already in the list, and of those the screenshots still missing.</summary>
    public int ShotRows { get; init; }
    public int MissingShots { get; init; }

    /// <summary>Rows taken when there are none yet.</summary>
    public int NewShotCount { get; init; } = 8;

    /// <summary>"Detailed" picks need a keyframe scan of source 1 first.</summary>
    public bool DetailedShots { get; init; }
    public bool KeyframesScanned { get; init; }

    public int VideoPoints { get; init; } = 8;
    public bool AudioFullLength { get; init; }
    public double MetricLength { get; init; } = 60;
    public int MetricSubsample { get; init; } = 1;
    public bool Vmaf { get; init; } = true;
    public bool Xpsnr { get; init; }
    public int GradePoints { get; init; } = 12;
    public bool GradeFrames { get; init; } = true;
}

/// <summary>One step of the plan with its rough cost.</summary>
public sealed record PlannedStep(CompareStep Step, string Name, string Detail, double Seconds, bool Applicable, bool AlreadyDone, string WhyNot = "");

/// <summary>
/// Rough time estimates for "Compare all". The base costs are measured on a typical machine (1080p, CUDA decode,
/// 8 cores) and scaled by duration, resolution and counts; each step's factor is then learnt from the real times
/// of earlier runs (see <see cref="Learn"/>), so estimates get closer the more it is used.
/// </summary>
public static class CompareAllPlanner
{
    public static string StepName(CompareStep s) => s switch
    {
        CompareStep.Sync => "Line up (sync)",
        CompareStep.Screenshots => "Screenshots",
        CompareStep.Video => "Video analysis",
        CompareStep.Audio => "Audio analysis",
        CompareStep.Metrics => "Quality metrics",
        CompareStep.Remaster => "Remaster",
        CompareStep.Subtitles => "Subtitles",
        _ => s.ToString(),
    };

    public static List<PlannedStep> Plan(IReadOnlyList<PlanSource> src, PlanOptions o, IReadOnlyDictionary<string, double>? factors = null)
    {
        var steps = new List<PlannedStep>
        {
            PlanSync(src, o),
            PlanShots(src, o),
            PlanVideo(src, o),
            PlanAudio(src, o),
            PlanMetrics(src, o),
            PlanRemaster(src, o),
            PlanSubtitles(src),
        };
        return steps.Select(s => s with { Seconds = s.Seconds * Factor(factors, s.Step) }).ToList();
    }

    private static double Factor(IReadOnlyDictionary<string, double>? factors, CompareStep s) =>
        factors != null && factors.TryGetValue(s.ToString(), out var f) && double.IsFinite(f) && f > 0 ? Math.Clamp(f, 0.1, 10) : 1;

    /// <summary>Moves the step's factor toward the measured ratio (actual / estimated before the factor).</summary>
    public static void Learn(IDictionary<string, double> factors, CompareStep step, double estimatedSeconds, double actualSeconds)
    {
        // very short runs say little (start-up time, cache hits)
        if (estimatedSeconds < 5 || actualSeconds < 2 || !double.IsFinite(actualSeconds))
            return;
        var key = step.ToString();
        var old = factors.TryGetValue(key, out var f) && f > 0 ? f : 1;
        // estimatedSeconds already includes the old factor: the ratio corrects it
        var target = Math.Clamp(old * actualSeconds / estimatedSeconds, 0.1, 10);
        // geometric moving average: one odd run does not swing it far
        factors[key] = Math.Exp(0.6 * Math.Log(old) + 0.4 * Math.Log(target));
    }

    private static IEnumerable<(int Index, PlanSource S)> Others(IReadOnlyList<PlanSource> src) => src.Select((s, i) => (i, s)).Skip(1);

    private static PlannedStep PlanSync(IReadOnlyList<PlanSource> src, PlanOptions o)
    {
        if (src.Count < 2)
            return new(CompareStep.Sync, StepName(CompareStep.Sync), "", 0, false, false, "needs two sources");
        var r = src[0];
        if (r.IsStill)
            return new(CompareStep.Sync, StepName(CompareStep.Sync), "", 0, false, false, "still images need no lining up");
        var todo = Others(src).Where(x => !x.S.Synced && !x.S.IsStill).ToList();
        var detail = o.SyncMode switch
        {
            "Edits" => "whole title + edits",
            "Whole" => "whole title",
            _ => "near (±90 s)",
        };
        // all lined up already: a re-run would match them all again
        var done = todo.Count == 0;
        if (done)
            todo = Others(src).Where(x => !x.S.IsStill).ToList();
        if (todo.Count == 0)
            return new(CompareStep.Sync, StepName(CompareStep.Sync), "", 0, false, false, "still images need no lining up");
        double sec = 0;
        foreach (var (_, s) in todo)
        {
            sec += o.SyncMode switch
            {
                // audio of both decoded once (~120× real time), then the picture refine
                "Whole" => 10 + (r.Duration + s.Duration) / 120,
                // the motion signatures of both whole titles (~25× real time at 1080p), audio as a fallback
                "Edits" => 15 + (r.Duration * r.PixelScale + s.Duration * s.PixelScale) / 25,
                _ => 5,
            };
        }
        return done
            ? new(CompareStep.Sync, StepName(CompareStep.Sync), $"all sources are lined up ({detail})", sec, true, true)
            : new(CompareStep.Sync, StepName(CompareStep.Sync), $"{todo.Count} source{(todo.Count == 1 ? "" : "s")}, {detail}", sec, true, false);
    }

    private static double ShotCost(PlanSource s) => s.IsStill ? 0.1 : 0.25 * (0.5 + 0.5 * s.PixelScale);

    private static PlannedStep PlanShots(IReadOnlyList<PlanSource> src, PlanOptions o)
    {
        var name = StepName(CompareStep.Screenshots);
        if (src.Count == 0 || !src.Any(s => s.HasVideo || s.IsStill))
            return new(CompareStep.Screenshots, name, "", 0, false, false, "no video");
        var avg = src.Average(ShotCost);
        if (o.ShotRows > 0)
        {
            return o.MissingShots == 0
                ? new(CompareStep.Screenshots, name, $"{o.ShotRows} times taken", 0, true, true)
                : new(CompareStep.Screenshots, name, $"{o.MissingShots} missing of {o.ShotRows} times", o.MissingShots * avg, true, false);
        }
        var rows = src.All(s => s.IsStill) ? 1 : Math.Max(1, o.NewShotCount);
        var sec = rows * src.Sum(ShotCost);
        if (o.DetailedShots && !o.KeyframesScanned && src[0].HasVideo)
            sec += 5 + src[0].Duration * src[0].PixelScale / 90; // keyframe scan of source 1
        return new(CompareStep.Screenshots, name, $"{rows} time{(rows == 1 ? "" : "s")} × {src.Count} source{(src.Count == 1 ? "" : "s")}", sec, true, false);
    }

    private static PlannedStep PlanVideo(IReadOnlyList<PlanSource> src, PlanOptions o)
    {
        var name = StepName(CompareStep.Video);
        var vids = src.Where(s => s.HasVideo && !s.IsStill).ToList();
        if (vids.Count == 0)
            return new(CompareStep.Video, name, "", 0, false, false, "no video");
        var done = vids.All(s => s.HasVideoResult);
        // each sample point: a short decode + the analysis filters
        var sec = vids.Sum(s => 1 + o.VideoPoints * 0.5 * s.PixelScale);
        return new(CompareStep.Video, name, $"{vids.Count} source{(vids.Count == 1 ? "" : "s")} · {o.VideoPoints} points", sec, true, done);
    }

    private static PlannedStep PlanAudio(IReadOnlyList<PlanSource> src, PlanOptions o)
    {
        var name = StepName(CompareStep.Audio);
        var tracks = src.Sum(s => s.AudioTracks);
        if (tracks == 0)
            return new(CompareStep.Audio, name, "", 0, false, false, "no audio");
        var done = src.Where(s => s.AudioTracks > 0).All(s => s.HasAudioResult);
        // the whole track decoded (~40× real time, lossless HD audio is the slow part), or a few excerpts
        var sec = src.Sum(s => s.AudioTracks * (o.AudioFullLength ? 2 + s.Duration / 40 : 2));
        return new(CompareStep.Audio, name, $"{tracks} track{(tracks == 1 ? "" : "s")}{(o.AudioFullLength ? ", full length" : "")}", sec, true, done);
    }

    private static PlannedStep PlanMetrics(IReadOnlyList<PlanSource> src, PlanOptions o)
    {
        var name = StepName(CompareStep.Metrics);
        if (src.Count < 2 || !src[0].HasVideo)
            return new(CompareStep.Metrics, name, "", 0, false, false, src.Count < 2 ? "needs two sources" : "source 1 has no video");
        var others = Others(src).Where(x => x.S.HasVideo).Select(x => x.S).ToList();
        if (others.Count == 0)
            return new(CompareStep.Metrics, name, "", 0, false, false, "no other source with video");
        var done = others.All(s => s.HasMetricsResult);
        var r = src[0];
        var fps = r.FrameRate > 1 ? r.FrameRate : 24;
        var frames = o.MetricLength * fps / Math.Max(1, o.MetricSubsample);
        // frames per second through decode + scale + metrics at 1080p on the CPU: VMAF is the slow one
        var rate = (o.Vmaf ? 90 : 250) / (o.Xpsnr ? 1.6 : 1);
        var sec = others.Sum(s => 1 + frames * Math.Max(r.PixelScale, s.PixelScale) / rate);
        return new(CompareStep.Metrics, name, $"{others.Count} pair{(others.Count == 1 ? "" : "s")} · {Format(o.MetricLength)}{(o.Vmaf ? " · VMAF" : "")}", sec, true, done);
    }

    private static PlannedStep PlanRemaster(IReadOnlyList<PlanSource> src, PlanOptions o)
    {
        var name = StepName(CompareStep.Remaster);
        if (src.Count < 2)
            return new(CompareStep.Remaster, name, "", 0, false, false, "needs two sources");
        var others = Others(src).Select(x => x.S).ToList();
        var done = others.All(s => s.HasGradeResult);
        var pts = src.All(s => s.IsStill) ? 1 : Math.Max(1, o.GradePoints);
        var sec = others.Sum(s =>
        {
            var px = Math.Max(src[0].PixelScale, s.PixelScale);
            var t = 3 + pts * 3.0 * px;
            if (o.GradeFrames && !s.IsStill && !src[0].IsStill)
                t += 40 * px; // frame timing, dirt and stabilisation: 5 short clips of each
            return t;
        });
        return new(CompareStep.Remaster, name, $"{others.Count} pair{(others.Count == 1 ? "" : "s")} · {pts} points", sec, true, done);
    }

    private static PlannedStep PlanSubtitles(IReadOnlyList<PlanSource> src)
    {
        var name = StepName(CompareStep.Subtitles);
        if (src.Count < 2 || !src[0].HasSubtitles)
            return new(CompareStep.Subtitles, name, "", 0, false, false, src.Count < 2 ? "needs two sources" : "source 1 has no subtitles");
        var others = Others(src).Where(x => x.S.HasSubtitles).Select(x => x.S).ToList();
        if (others.Count == 0)
            return new(CompareStep.Subtitles, name, "", 0, false, false, "no other source has subtitles");
        var done = others.All(s => s.HasSubtitleResult);
        // reading a subtitle track demuxes the whole file (~1 GB/s from a fast disk, slower from RAR / ISO / HDD)
        var sec = new[] { src[0] }.Concat(others).Sum(s => 0.5 + s.SizeBytes / 1e9);
        return new(CompareStep.Subtitles, name, $"{others.Count} pair{(others.Count == 1 ? "" : "s")} · timing", sec, true, done);
    }

    private static string Format(double seconds) =>
        seconds >= 60 ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:0.#} min") : string.Create(CultureInfo.InvariantCulture, $"{seconds:0} s");

    /// <summary>A rough duration for people: "under a minute", "about 4 min", "about 1 h 20 min".</summary>
    public static string Rough(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return "unknown";
        if (seconds < 10)
            return "a few seconds";
        if (seconds < 45)
            return string.Create(CultureInfo.InvariantCulture, $"about {Math.Round(seconds / 5) * 5:0} s");
        if (seconds < 90)
            return "about a minute";
        if (seconds < 3600)
        {
            // round to 1 min below 10 min, then 5 min
            var m = seconds < 600 ? Math.Round(seconds / 60) : Math.Round(seconds / 300) * 5;
            return string.Create(CultureInfo.InvariantCulture, $"about {m:0} min");
        }
        var total = (int)(Math.Round(seconds / 600) * 10); // nearest 10 min
        var h = total / 60;
        var mm = total % 60;
        return mm == 0 ? string.Create(CultureInfo.InvariantCulture, $"about {h} h") : string.Create(CultureInfo.InvariantCulture, $"about {h} h {mm} min");
    }

    /// <summary>A countdown for the job area: "about 3 min left", "less than a minute left".</summary>
    public static string Left(double seconds) =>
        seconds < 10 ? "a few seconds left" : Rough(seconds) + " left";
}
