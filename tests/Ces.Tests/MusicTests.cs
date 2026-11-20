using System.Diagnostics;
using Ces.Music;
using Ces.Tools;

namespace Ces.Tests;

public sealed class MusicTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ces-music").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(@"K:\x\Some.Artist.Some.Album.1967.COMPLETE.MBLURAY-GROUP", true)]
    [InlineData(@"K:\x\Artist.Album.2025.1080p.PURE.MBluRay.x264-GROUP", true)]
    [InlineData(@"K:\x\Some.Album.Pure.Audio.Blu-ray", true)]
    [InlineData(@"K:\x\a.rar::m-electricladyland.iso", false)]
    [InlineData(@"K:\x\Some.Film.2015.COMPLETE.BLURAY-GROUP", false)]
    [InlineData(@"K:\x\Purely.Fictional.2019.1080p.BluRay", false)]
    public void ReleaseNamesThatSayMusic(string path, bool music) =>
        Assert.Equal(music, MusicAnalyzer.NameSaysMusic(path));

    [Fact]
    public void TrackNumbersAsRanges() =>
        Assert.Equal("1–4, 7, 9–10", MusicAnalyzer.Numbers([1, 2, 3, 4, 7, 9, 10]));

    [Theory]
    [InlineData(@"K:\a.rar::b.iso::BDMV/PLAYLIST/00021.mpls", @"K:\a.rar::b.iso::BDMV/PLAYLIST")]
    [InlineData(@"K:\a.rar::b.iso::BDMV", @"K:\a.rar::b.iso::")]
    [InlineData(@"K:\a.rar::b.iso::", null)]
    [InlineData(@"K:\a.rar::b.iso::\BDMV\PLAYLIST", @"K:\a.rar::b.iso::BDMV")]
    public void ParentStaysInsideTheImage(string path, string? parent) =>
        Assert.Equal(parent, Ces.Vfs.Vfs.Parent(path));

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
    }

    /// <summary>
    /// Integration: a 96 kHz / 24-bit FLAC album in MKV with three songs as chapters — real stereo, mono on two
    /// channels, and one upsampled from 44.1 kHz. Needs ffmpeg; passes without running when it is not installed.
    /// </summary>
    [Fact]
    public async Task SongsAreCheckedOneByOne()
    {
        var (tools, _) = await FfTools.LocateAsync();
        if (tools == null)
            return;
        var mkv = Path.Combine(_dir, "album.mkv");
        var meta = Path.Combine(_dir, "chapters.txt");
        File.WriteAllText(meta, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=20000\ntitle=Stereo Song\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=20000\nEND=40000\ntitle=Mono Song\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=40000\nEND=60000\ntitle=Chapter 03\n");
        // song 1: two independent noises; song 2: one noise on both channels; song 3: noise made at 44.1 kHz then resampled
        const string graph =
            "anoisesrc=d=20:c=pink:r=96000:seed=1[l1];anoisesrc=d=20:c=pink:r=96000:seed=2[r1];[l1][r1]join=inputs=2:channel_layout=stereo[s1];" +
            "anoisesrc=d=20:c=pink:r=96000:seed=3,pan=stereo|c0=c0|c1=c0[s2];" +
            "anoisesrc=d=20:c=white:r=44100:seed=4[l3];anoisesrc=d=20:c=white:r=44100:seed=5[r3];[l3][r3]join=inputs=2:channel_layout=stereo," +
            "aresample=96000,volume=0.5[s3];" +
            "[s1][s2][s3]concat=n=3:v=0:a=1[out]";
        Run(tools.FFmpeg, "-v", "error", "-y", "-filter_complex", graph, "-f", "ffmetadata", "-i", meta, "-map", "[out]", "-map_metadata", "0", "-map_chapters", "0",
            "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-f", "matroska", mkv);

        var r = await MusicAnalyzer.AnalyzePathAsync(tools, mkv, _dir, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal(96000, r.SampleRate);
        var tracks = r.Details.Where(d => d.Group == "Tracks").ToList();
        Assert.Equal(3, tracks.Count);
        Assert.Contains("Stereo Song", tracks[0].Name);
        Assert.StartsWith("ok", tracks[0].Value);
        Assert.Contains("Mono Song", tracks[1].Name);
        Assert.Contains("dual mono", tracks[1].Value);
        Assert.DoesNotContain("Chapter 03", tracks[2].Name);
        Assert.Contains("upsampled", tracks[2].Value);
        Assert.Contains(r.Problems, p => p.Contains("upsampled") && p.Contains("track 3"));
    }
}
