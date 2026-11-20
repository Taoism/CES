using System.Diagnostics;
using Ces.Analysis;
using Ces.Disc;
using Ces.Probe;
using Ces.Tools;

namespace Ces.Tests;

/// <summary>
/// Integration: a remux whose video starts 0.25 s late and whose audio does not match at all (another mix) is still
/// synced, by the picture alone. Needs ffmpeg; passes without running when it is not installed.
/// </summary>
public sealed class SyncRescueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ces-sync").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task PictureRescuesUnmatchedAudio()
    {
        var (tools, _) = await FfTools.LocateAsync();
        if (tools == null)
            return;
        var a = Path.Combine(_dir, "a.mkv");
        var b = Path.Combine(_dir, "b.mkv");
        // a picture that never repeats (Game of Life) + one noise; the other copy: the same picture 0.25 s later, other noise
        Run(tools.FFmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "life=size=320x180:rate=24:seed=5:mold=10,trim=duration=40,format=yuv420p",
            "-f", "lavfi", "-i", "anoisesrc=d=40:c=pink:seed=1", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-c:a", "aac", "-shortest", a);
        Run(tools.FFmpeg, "-v", "error", "-y", "-itsoffset", "0.25", "-i", a, "-f", "lavfi", "-i", "anoisesrc=d=41:c=white:seed=9",
            "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-shortest", b);

        var sa = await SourceOpener.OpenAsync(a);
        var sb = await SourceOpener.OpenAsync(b);
        await new MediaProber(tools).ProbeAsync(sa, sa.MainTitle!);
        await new MediaProber(tools).ProbeAsync(sb, sb.MainTitle!);
        var r = await new SyncDetector(tools, Path.Combine(_dir, "tmp")).DetectAsync(sa.MainTitle!, sb.MainTitle!, new SyncOptions());

        Assert.True(r.Reliable, r.Summary);
        Assert.True(r.PictureOnly, r.Summary);
        Assert.InRange(r.Offset, 0.2, 0.3);
    }

    [Fact]
    public async Task AnotherCutWithAnotherSoundtrackIsMappedByThePicture()
    {
        var (tools, _) = await FfTools.LocateAsync();
        if (tools == null)
            return;
        var a = Path.Combine(_dir, "a.mkv");
        var b = Path.Combine(_dir, "b.mkv");
        // the other cut leaves out 20–40 s and has its own soundtrack (a dub): nothing near the audio's guesses
        Run(tools.FFmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "life=size=320x180:rate=24:seed=7:mold=10,trim=duration=120,format=yuv420p",
            "-f", "lavfi", "-i", "anoisesrc=d=120:c=pink:seed=1", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-c:a", "aac", "-shortest", a);
        Run(tools.FFmpeg, "-v", "error", "-y", "-i", a, "-f", "lavfi", "-i", "anoisesrc=d=100:c=white:seed=9", "-filter_complex",
            "[0:v]trim=0:20,setpts=PTS-STARTPTS[v1];[0:v]trim=40:120,setpts=PTS-STARTPTS[v2];[v1][v2]concat=n=2:v=1:a=0[v]",
            "-map", "[v]", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-c:a", "aac", "-shortest", b);

        var sa = await SourceOpener.OpenAsync(a);
        var sb = await SourceOpener.OpenAsync(b);
        await new MediaProber(tools).ProbeAsync(sa, sa.MainTitle!);
        await new MediaProber(tools).ProbeAsync(sb, sb.MainTitle!);
        var r = await new SyncDetector(tools, Path.Combine(_dir, "tmp")).DetectAsync(sa.MainTitle!, sb.MainTitle!, new SyncOptions());

        Assert.True(r.Reliable, r.Summary);
        Assert.True(r.PictureMapped, r.Summary);
        var gap = r.Sections.FindIndex(s => s.Offset == null);
        Assert.True(gap > 0, r.Summary);
        Assert.InRange(r.Sections[gap].Start, 19.5, 20.5);
        Assert.InRange(r.Sections[gap + 1].Start, 39.5, 40.5);
        Assert.InRange(r.Sections[gap + 1].Offset!.Value, -20.1, -19.9);
    }
}

public sealed class FrameMatchTests
{
    private static Ces.Analysis.GrayFrame Frame(int i, double gain, Random noise)
    {
        // a moving bar on a gradient; gain changes the contrast like another grade, noise like grain
        var px = new float[64];
        // content unique to each frame (seeded by its number), so only one shift is right
        var content = new Random(i);
        for (var k = 0; k < 64; k++)
            px[k] = (float)(gain * content.Next(0, 80) + noise.NextDouble() * 6);
        var mean = px.Average();
        return new Ces.Analysis.GrayFrame(i / 24.0, px.Select(x => x - mean).ToArray(), mean);
    }

    [Fact]
    public void FindsTheShiftUnderAnotherGradeAndGrain()
    {
        var rnd = new Random(5);
        var a = Enumerable.Range(100, 48).Select(i => Frame(i, 1.0, rnd)).ToList();
        var b = Enumerable.Range(93, 72).Select(i => Frame(i, 2.2, rnd)).ToList();
        Assert.Equal(7, Ces.Analysis.SyncDetector.MatchShift(a, b)?.Shift);
    }

    [Fact]
    public void SplitsNoisyCostsWhereTheSectionsChange()
    {
        var rnd = new Random(3);
        // true matches cost ≈25, wrong ones ≈35 (another transfer), with frame-to-frame noise of ±6
        var a = Enumerable.Range(0, 120).Select(k => (k < 70 ? 25 : 35) + rnd.NextDouble() * 12 - 6).ToList();
        var b = Enumerable.Range(0, 120).Select(k => (k < 70 ? 35 : 25) + rnd.NextDouble() * 12 - 6).ToList();
        Assert.InRange(Ces.Analysis.SyncDetector.SplitCut(a, b, true, true), 69, 71);
    }

    [Fact]
    public void SplitsAgainstAMissingSection()
    {
        var rnd = new Random(4);
        var a = Enumerable.Range(0, 120).Select(k => (k < 40 ? 24 : 36) + rnd.NextDouble() * 10 - 5).ToList();
        var missing = Enumerable.Repeat(double.NaN, 120).ToList();
        Assert.InRange(Ces.Analysis.SyncDetector.SplitCut(a, missing, true, false), 39, 41);
    }

    [Fact]
    public void NoSplitWhenNothingChanges()
    {
        var rnd = new Random(6);
        var a = Enumerable.Range(0, 120).Select(_ => 30 + rnd.NextDouble() * 10).ToList();
        var b = Enumerable.Range(0, 120).Select(_ => 30 + rnd.NextDouble() * 10).ToList();
        Assert.Equal(-1, Ces.Analysis.SyncDetector.SplitCut(a, b, true, true));
    }
}
