using Ces.Compare;

namespace Ces.Tests;

public class CompareAllPlanTests
{
    private static PlanSource Film(int w = 1920, int h = 1080, double dur = 6000, bool synced = false) => new()
    {
        Duration = dur,
        Width = w,
        Height = h,
        FrameRate = 23.976,
        HasVideo = true,
        AudioTracks = 2,
        HasSubtitles = true,
        SizeBytes = 20_000_000_000,
        Synced = synced,
    };

    [Fact]
    public void AllStepsApplyToTwoFilms()
    {
        var plan = CompareAllPlanner.Plan([Film(), Film()], new PlanOptions());
        Assert.Equal(Enum.GetValues<CompareStep>(), plan.Select(p => p.Step));
        Assert.All(plan, p => Assert.True(p.Applicable, p.Step.ToString()));
        Assert.All(plan, p => Assert.True(p.Seconds > 0, p.Step.ToString()));
        Assert.All(plan, p => Assert.False(p.AlreadyDone));
    }

    [Fact]
    public void OneSourceSkipsThePairSteps()
    {
        var plan = CompareAllPlanner.Plan([Film()], new PlanOptions()).ToDictionary(p => p.Step);
        Assert.False(plan[CompareStep.Sync].Applicable);
        Assert.False(plan[CompareStep.Metrics].Applicable);
        Assert.False(plan[CompareStep.Remaster].Applicable);
        Assert.False(plan[CompareStep.Subtitles].Applicable);
        Assert.True(plan[CompareStep.Video].Applicable);
        Assert.True(plan[CompareStep.Audio].Applicable);
        Assert.Contains("two sources", plan[CompareStep.Sync].WhyNot);
    }

    [Fact]
    public void ExistingResultsMarkStepsDone()
    {
        var done = new PlanSource
        {
            Duration = 6000, Width = 1920, Height = 1080, FrameRate = 24, HasVideo = true, AudioTracks = 1, Synced = true,
            HasVideoResult = true, HasAudioResult = true, HasGradeResult = true, HasMetricsResult = true,
        };
        var plan = CompareAllPlanner.Plan([done, done], new PlanOptions { ShotRows = 8, MissingShots = 0 }).ToDictionary(p => p.Step);
        foreach (var s in new[] { CompareStep.Sync, CompareStep.Screenshots, CompareStep.Video, CompareStep.Audio, CompareStep.Metrics, CompareStep.Remaster })
            Assert.True(plan[s].AlreadyDone, s.ToString());
        Assert.False(plan[CompareStep.Subtitles].Applicable);
    }

    [Fact]
    public void CostScalesWithResolutionAndSyncMode()
    {
        var hd = CompareAllPlanner.Plan([Film(), Film()], new PlanOptions { SyncMode = "Edits" }).ToDictionary(p => p.Step);
        var uhd = CompareAllPlanner.Plan([Film(3840, 2160), Film(3840, 2160)], new PlanOptions { SyncMode = "Edits" }).ToDictionary(p => p.Step);
        Assert.True(uhd[CompareStep.Video].Seconds > 3 * hd[CompareStep.Video].Seconds);
        Assert.True(uhd[CompareStep.Sync].Seconds > 3 * hd[CompareStep.Sync].Seconds);
        var near = CompareAllPlanner.Plan([Film(), Film()], new PlanOptions { SyncMode = "Near" }).ToDictionary(p => p.Step);
        Assert.True(near[CompareStep.Sync].Seconds < hd[CompareStep.Sync].Seconds / 10);
        // a 100-minute 1080p pair mapped for edits: several minutes, not hours (NOTLD-like pairs took ~11 min)
        Assert.InRange(hd[CompareStep.Sync].Seconds, 4 * 60, 20 * 60);
    }

    [Fact]
    public void LearntFactorMovesTowardTheMeasuredTime()
    {
        var factors = new Dictionary<string, double>();
        var est = CompareAllPlanner.Plan([Film(), Film()], new PlanOptions(), factors).Single(p => p.Step == CompareStep.Video).Seconds;
        // this machine took twice as long
        CompareAllPlanner.Learn(factors, CompareStep.Video, est, est * 2);
        var f = factors[nameof(CompareStep.Video)];
        Assert.InRange(f, 1.2, 2);
        var est2 = CompareAllPlanner.Plan([Film(), Film()], new PlanOptions(), factors).Single(p => p.Step == CompareStep.Video).Seconds;
        Assert.Equal(est * f, est2, 6);
        // repeated runs converge on the real ratio
        for (var i = 0; i < 12; i++)
        {
            var e = est * factors[nameof(CompareStep.Video)];
            CompareAllPlanner.Learn(factors, CompareStep.Video, e, est * 2);
        }
        Assert.Equal(2, factors[nameof(CompareStep.Video)], 1);
        // instant runs (cache hits) teach nothing
        CompareAllPlanner.Learn(factors, CompareStep.Audio, 120, 0.5);
        Assert.False(factors.ContainsKey(nameof(CompareStep.Audio)));
    }

    [Theory]
    [InlineData(4, "a few seconds")]
    [InlineData(22, "about 20 s")]
    [InlineData(70, "about a minute")]
    [InlineData(250, "about 4 min")]
    [InlineData(1700, "about 30 min")]
    [InlineData(3600, "about 1 h")]
    [InlineData(5000, "about 1 h 20 min")]
    public void RoughDurations(double seconds, string expected) => Assert.Equal(expected, CompareAllPlanner.Rough(seconds));
}
