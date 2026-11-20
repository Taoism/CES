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

    /// <summary>Notes at random times: a decaying tone each, its pitch and loudness random.</summary>
    private static float[] Notes(int seed, double seconds, Func<double, double> time)
    {
        const int rate = 16000;
        var rnd = new Random(seed);
        var x = new float[(int)(seconds * rate)];
        for (var t = 0.2; t < seconds - 2; t += 0.1 + rnd.NextDouble() * 0.5)
        {
            var f = 200 + rnd.NextDouble() * 2800;
            var amp = 0.05 + rnd.NextDouble() * 0.2;
            var at = time(t);
            var s0 = (int)(at * rate);
            for (var i = 0; i < rate / 2 && s0 + i < x.Length; i++)
                x[s0 + i] += (float)(amp * Math.Exp(-i / (rate * 0.08)) * Math.Sin(2 * Math.PI * f * i / rate));
        }
        return x;
    }

    [Fact]
    public void AnotherMixOnAnotherTapeSpeedLinesUp()
    {
        // the other mix: the same notes 2 s later and 0.3 % slower, another balance (duller, quieter) and a hiss
        var a = Notes(7, 90, t => t);
        var other = Notes(7, 95, t => t * 1.003 + 2);
        var rnd = new Random(3);
        float lp = 0;
        for (var i = 0; i < other.Length; i++)
        {
            lp += 0.3f * (other[i] - lp);
            other[i] = 0.6f * lp + (float)(rnd.NextDouble() - 0.5) * 0.01f;
        }
        var m = MixAligner.Align(MixAligner.Flux(a), MixAligner.Flux(other), new MusicTrack(1, "", 0, 90), new MusicTrack(1, "", 0, 95), CancellationToken.None);

        Assert.NotNull(m.Offset);
        Assert.Equal(1.003, m.Speed, 4);
        Assert.Equal(2.0, m.Offset!.Value, 1);
        Assert.True(m.Matched >= m.Windows * 0.8, $"{m.Matched} of {m.Windows}");
    }

    [Fact]
    public void AnEditInsideTheSongIsItsOwnStretch()
    {
        // the other mix holds a chord half a second longer at 45 s: everything after it comes 0.5 s later
        var a = Notes(11, 90, t => t);
        var other = Notes(11, 95, t => t + (t < 45 ? 1 : 1.5));
        var m = MixAligner.Align(MixAligner.Flux(a), MixAligner.Flux(other), new MusicTrack(1, "", 0, 90), new MusicTrack(1, "", 0, 95), CancellationToken.None);

        Assert.Equal(2, m.Pieces.Count);
        Assert.Equal(1.0, m.Pieces[0].Offset, 1);
        Assert.Equal(1.5, m.Pieces[1].Offset, 1);
        Assert.InRange(m.Pieces[1].Start, 38, 52);
    }

    [Fact]
    public void AnotherSongDoesNotLineUp()
    {
        var m = MixAligner.Align(MixAligner.Flux(Notes(1, 60, t => t)), MixAligner.Flux(Notes(2, 60, t => t)),
            new MusicTrack(1, "", 0, 60), new MusicTrack(1, "", 0, 60), CancellationToken.None);
        Assert.Null(m.Offset);
    }

    [Fact]
    public void LineThroughTheWindowsThatAgree()
    {
        var pts = Enumerable.Range(0, 20).Select(i => (T: i * 5.0, S: i * 5.0 * 0.997 - 41.3, W: 0.6)).ToList();
        pts[3] = (15, 70, 0.9);  // a match on another chorus
        pts[11] = (55, -3, 0.8);
        var line = MixAligner.FitLine(pts, 0.03);
        Assert.NotNull(line);
        Assert.Equal(18, line.Value.Inliers);
        Assert.Equal(0.997, line.Value.Speed, 6);
        Assert.Equal(-41.3, line.Value.Offset, 6);
    }

    [Fact]
    public void SongSectionsHaveTheirOwnSpeed()
    {
        var src = new Ces.Model.MediaSource { Path = "x", Kind = Ces.Model.SourceKind.File };
        var it = new Ces.Compare.CompareItem { Source = src, Title = new Ces.Model.MediaTitle { Name = "x", Duration = 200 } };
        it.Sections.Add(new Ces.Compare.SyncSection { Start = 0, Offset = null });
        it.Sections.Add(new Ces.Compare.SyncSection { Start = 100, Offset = -40, Speed = 0.99 });
        Assert.True(it.SongMapped);
        Assert.Null(it.SourceTime(50));
        Assert.Equal(150 * 0.99 - 40, it.SourceTime(150)!.Value, 9);
        Assert.Equal(150, it.TimelineOf(150 * 0.99 - 40)!.Value, 9);
    }

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
