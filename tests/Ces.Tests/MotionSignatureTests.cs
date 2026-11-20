using Ces.Analysis;

namespace Ces.Tests;

/// <summary>Edit maps from motion signatures: synthetic films whose other release is spliced, extended and re-transferred.</summary>
public sealed class MotionSignatureTests
{
    private const int Win = 240;
    private const int Step = 120;

    /// <summary>A frame feature that drifts within a shot and jumps at each shot change (every 2–6 s).</summary>
    private static double[] Film(int frames, int seed)
    {
        var rnd = new Random(seed);
        var f = new double[frames];
        var v = 0.0;
        var next = 0;
        for (var k = 0; k < frames; k++)
        {
            if (k == next)
            {
                v = rnd.NextDouble() * 10;
                next = k + rnd.Next(48, 150);
            }
            v += (rnd.NextDouble() - 0.5) * 0.6;
            f[k] = v;
        }
        return f;
    }

    /// <summary>The signature of the film frames listed in <paramref name="frames"/>: another transfer scales and adds noise.</summary>
    private static float[] Signature(double[] film, List<int> frames, double scale, double noise, int seed)
    {
        var rnd = new Random(seed);
        var s = new float[frames.Count - 1];
        for (var i = 1; i < frames.Count; i++)
            s[i - 1] = (float)(Math.Abs(film[frames[i]] - film[frames[i - 1]]) * scale + rnd.NextDouble() * noise);
        return s;
    }

    private static List<int> Range(int from, int count) => Enumerable.Range(from, count).ToList();

    [Fact]
    public void MapsSplicesInsertsAndALeadAcrossTransfers()
    {
        var film = Film(20000, 1);
        // reference: film frames 100 … 9099
        var refFrames = Range(100, 9000);
        // other: 20 frames earlier start, 6 frames spliced out at film frame 3100, 240 frames of extra material at 6100
        var other = Range(80, 3100 - 80).Concat(Range(3106, 6100 - 3106)).Concat(Range(12000, 240)).Concat(Range(6100, 9100 - 6100)).ToList();
        var r = Signature(film, refFrames, 1, 0.05, 2);
        var o = Signature(film, other, 1.7, 0.4, 3);

        var windows = MotionSignature.AlignWindows(r, o, Win, Step);
        var map = MotionSignature.Map(r, o, windows, Win);

        Assert.NotNull(map);
        Assert.Equal(4, map.Count);
        Assert.Equal(0, map[0].RefFrame);
        Assert.Equal(20, map[0].Shift);
        // ref frame 3000 = film 3100: the six spliced frames are not in the other. Inside a shot the signature
        // places the step only roughly (the pixels finish it) and must not claim the exact frame.
        Assert.Null(map[1].Shift);
        Assert.InRange(map[1].RefFrame, 2992, 3008);
        Assert.Equal(14, map[2].Shift);
        Assert.Equal(6, map[2].RefFrame - map[1].RefFrame);
        // the inserted material moves everything after film 6100 (ref 6000) by 240 frames
        Assert.Equal(254, map[3].Shift);
        Assert.InRange(map[3].RefFrame, 5992, 6008);
        Assert.True(map[1].RefFrame == 3000 || !map[1].ExactCut);
    }

    [Fact]
    public void StepAtAShotChangeLandsOnItsFrame()
    {
        var film = Film(12000, 11);
        // the last 30 frames of a shot cut out: the shot change after them puts the step on its frame
        var shot = Enumerable.Range(4000, 1000).First(k => Math.Abs(film[k] - film[k - 1]) > 2);
        var r = Signature(film, Range(0, 9000), 1, 0.05, 12);
        var o = Signature(film, Range(0, shot - 30).Concat(Range(shot, 9000 - shot)).ToList(), 1.4, 0.2, 13);
        var map = MotionSignature.Map(r, o, MotionSignature.AlignWindows(r, o, Win, Step), Win);
        Assert.NotNull(map);
        Assert.Equal(3, map.Count);
        Assert.Null(map[1].Shift);
        Assert.InRange(map[1].RefFrame, shot - 31, shot - 30);
        Assert.Equal(map[1].RefFrame + 30, map[2].RefFrame);
        Assert.Equal(-30, map[2].Shift);
    }

    [Fact]
    public void MissingTailIsReported()
    {
        var film = Film(9000, 4);
        var r = Signature(film, Range(0, 8000), 1, 0.05, 5);
        var o = Signature(film, Range(0, 6000), 1.3, 0.3, 6);
        var map = MotionSignature.Map(r, o, MotionSignature.AlignWindows(r, o, Win, Step), Win);
        Assert.NotNull(map);
        Assert.Equal(0, map[0].Shift);
        Assert.Null(map[^1].Shift);
        Assert.InRange(map[^1].RefFrame, 5999, 6001);
    }

    [Fact]
    public void AFewFramesShorterIsNotAnEdit()
    {
        // a UHD disc 14 frames shorter than the Blu-ray (black at the end)
        var film = Film(9000, 14);
        var r = Signature(film, Range(0, 8000), 1, 0.05, 15);
        var o = Signature(film, Range(0, 7986), 1.2, 0.2, 16);
        var map = MotionSignature.Map(r, o, MotionSignature.AlignWindows(r, o, Win, Step), Win);
        Assert.NotNull(map);
        Assert.Single(map);
    }

    [Fact]
    public void UnrelatedFilmsGiveNoMap()
    {
        var r = Signature(Film(6000, 7), Range(0, 6000), 1, 0.05, 8);
        var o = Signature(Film(6000, 9), Range(0, 6000), 1, 0.05, 10);
        Assert.Null(MotionSignature.Map(r, o, MotionSignature.AlignWindows(r, o, Win, Step), Win));
    }
}

/// <summary>Blu-ray main title on discs with decoy playlists.</summary>
public sealed class BluRayMainTitleTests
{
    private static Ces.Model.MediaTitle T(string name, double minutes, long gb10) =>
        new() { Name = name, Duration = minutes * 60, SizeBytes = gb10 * 100_000_000 };

    [Fact]
    public void DecoyLoopPlaylistIsNotTheFilm()
    {
        // Age of Ultron Blu-ray: a 6:09 playlist of small clips looped, the film is 2:21 in one 35 GB clip
        var titles = new[] { T("00182.MPLS", 369.6, 5), T("00800.MPLS", 141.3, 349), T("00210.MPLS", 20.9, 31) };
        Assert.Equal("00800.MPLS", Ces.Disc.BluRayReader.PickMain(titles)!.Name);
    }

    [Fact]
    public void ExtendedCutSharingClipsWins()
    {
        var titles = new[] { T("00001.MPLS", 120, 300), T("00002.MPLS", 135, 320) };
        Assert.Equal("00002.MPLS", Ces.Disc.BluRayReader.PickMain(titles)!.Name);
    }
}
