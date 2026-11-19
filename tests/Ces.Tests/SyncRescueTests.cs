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
}
