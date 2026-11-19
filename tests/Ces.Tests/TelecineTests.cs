using Ces.Analysis;

namespace Ces.Tests;

public sealed class TelecineTests
{
    [Fact]
    public void HardTelecineIsFilmNotInterlaced()
    {
        // 2 of 5 frames combed, the rest progressive
        var r = new InterlaceResult { Tff = 200, Progressive = 300 };
        Assert.True(r.IsTelecine);
        Assert.StartsWith("telecined film", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedFieldsAloneMeanTelecine()
    {
        var r = new InterlaceResult { Undetermined = 500, RepeatedFields = 200 };
        Assert.True(r.IsTelecine);
        Assert.StartsWith("telecined film", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void RealInterlaceStaysInterlaced()
    {
        var r = new InterlaceResult { Bff = 490, Progressive = 10 };
        Assert.False(r.IsTelecine);
        Assert.Equal("interlaced (BFF)", r.Verdict);
    }

    [Fact]
    public void ProgressiveStaysProgressive()
    {
        var r = new InterlaceResult { Progressive = 500 };
        Assert.False(r.IsTelecine);
        Assert.Equal("progressive", r.Verdict);
    }
}

public sealed class ChromaOriginTests
{
    [Theory]
    [InlineData(-15, -15, 2, "upsampled from 4:2:0")]     // clean upsampled 4:4:4
    [InlineData(-5.6, -5.0, 1, "probably upsampled from 4:2:0")] // the same after a CRF 35 re-encode
    [InlineData(-5.1, -3.9, 1, "probably upsampled from 4:2:0")] // one direction just above the hint line
    [InlineData(-0.5, -0.6, 0, "No sign")]                 // genuine 4:4:4
    [InlineData(-12, -1, 2, "upsampled from 4:2:2")]       // horizontal only
    public void ChromaVerdicts(double h, double v, int level, string text)
    {
        var f = VideoAnalyzer.ChromaFinding("4:4:4", h, v);
        Assert.Equal(level, f.Level);
        Assert.Contains(text, f.Text, StringComparison.Ordinal);
    }
}

public sealed class DolbyVisionTests
{
    [Fact]
    public void MediaInfoHdrFieldsBecomeOneTidyLine()
    {
        var s = new Ces.Model.StreamInfo();
        var text = Ces.Probe.MediaProber.HdrText("Dolby Vision / SMPTE ST 2086", "dvhe.08 / ", "HDR10 / HDR10", s);
        Assert.Equal("Dolby Vision / SMPTE ST 2086 / profile 8 (dvhe.08) / HDR10", text);
        Assert.Equal(8, s.DvProfile);
    }

    [Fact]
    public void RpuWithoutLevel1IsMasteringOnly()
    {
        // what ffprobe (ffmpeg 9) prints for a real profile 5 frame: no level-1 blocks, the mastering maximum as a number
        const string json = """
            {"frames":[{"pts_time":"5.066667","side_data_list":[{"side_data_type":"Dolby Vision RPU Data"},
            {"side_data_type":"Dolby Vision Metadata","rpu_type":2,"source_min_pq":7,"source_max_pq":3079}]}]}
            """;
        var d = Ces.Analysis.HdrMetadata.Parse(json, 5.1);
        Assert.NotNull(d);
        Assert.True(d.MasteringOnly);
        Assert.InRange(d.MaxNits, 950, 1050);
    }
}

public sealed class FrameRepeatTests
{
    // measured on a real public-domain NTSC DVD (film transferred by repeating one frame in five)
    private static readonly double[] RealDvd =
    [
        0.0, 4.0, 2.8, 2.7, 0.5, 2.6, 2.5, 2.6, 2.5, 0.4, 2.8, 2.9, 3.9, 2.4, 0.2, 2.5, 2.8, 4.5, 3.9, 0.6, 2.9, 10.7, 4.4,
        8.8, 0.3, 2.6, 2.3, 2.2, 2.8, 0.3, 3.3, 3.6, 3.0, 2.1, 0.4, 3.4, 2.6, 2.6, 3.8, 0.4, 2.2, 1.9, 1.4, 1.4, 0.3, 2.4,
        3.4, 2.9, 2.2, 0.5, 2.5, 3.5, 1.7, 1.5, 0.2, 1.8, 2.1, 2.9, 2.7,
    ];

    [Fact]
    public void RealDvdFrameRepeatsAreFound() => Assert.True(Ces.Analysis.SyncDetector.IsFrameRepeatPulldown(RealDvd));

    [Fact]
    public void SteadyMotionIsNotPulldown() =>
        Assert.False(Ces.Analysis.SyncDetector.IsFrameRepeatPulldown(Enumerable.Range(0, 60).Select(i => 2.5 + (i % 3) * 0.4).ToList()));

    [Fact]
    public void StillPictureIsNotPulldown() =>
        Assert.False(Ces.Analysis.SyncDetector.IsFrameRepeatPulldown(Enumerable.Repeat(0.0, 60).ToList()));
}

public sealed class DoviRpuTests
{
    private static byte[] Rpu(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("rpu-p5.bin", 2457, 1419)]
    [InlineData("rpu-p81.bin", 2458, 1710)]
    [InlineData("rpu-p84.bin", 2458, 1710)]
    public void LevelOneFromRealRpus(string file, int maxPq, int avgPq)
    {
        var rpu = Ces.Analysis.DoviRpu.Parse(Rpu(file));
        Assert.NotNull(rpu);
        // the parts FFmpeg prints too: proof that the bit position is right when the extension blocks start
        Assert.Equal(7, rpu.SourceMinPq);
        Assert.Equal(3079, rpu.SourceMaxPq);
        Assert.Equal(42, rpu.SourceDiagonal);
        Assert.Equal((0, maxPq, avgPq), rpu.Level1);
        Assert.Equal([2081, 2851, 3079], rpu.TrimTargetsPq);
        // level 6 repeats the clip's static HDR10 values (MaxCLL 244, MaxFALL 145)
        Assert.Equal((1000, 1, 244, 145), rpu.Level6);
    }

    [Fact]
    public void RpuValuesBecomePerShotNits()
    {
        var d = new Ces.Analysis.DynamicHdr { Kind = "Dolby Vision", MasteringOnly = true };
        Ces.Analysis.HdrMetadata.Apply(d, Ces.Analysis.DoviRpu.Parse(Rpu("rpu-p81.bin"))!);
        Assert.False(d.MasteringOnly);
        Assert.True(d.RpuRead);
        Assert.InRange(d.MaxNits, 230, 250);
        Assert.Equal([100.0, 600, 1000], d.TrimTargetsNits);
    }

    [Fact]
    public void DamagedRpuIsRejected()
    {
        var b = Rpu("rpu-p84.bin");
        Assert.Null(Ces.Analysis.DoviRpu.Parse(b.AsSpan(0, 60)));
        b[2] = 0x18; // not the RPU prefix
        Assert.Null(Ces.Analysis.DoviRpu.Parse(b));
    }

    [Fact]
    public void FindsRpusInAnAnnexBStream()
    {
        var rpu = Rpu("rpu-p81.bin");
        byte[] stream = [0, 0, 0, 1, 0x40, 0x01, 0x0C, 0x01, 0, 0, 1, .. rpu, 0, 0, 0, 1, 0x26, 0x01, 0xAF];
        var found = Ces.Analysis.DoviRpu.RpuNals(stream);
        Assert.Single(found);
        Assert.Equal(rpu, found[0]);
    }

    [Fact]
    public void PicksTheFrameAtTheSeekPoint() =>
        Assert.Equal(3, Ces.Analysis.HdrMetadata.PickFrame([-0.4, -0.43, -0.45, 0, -0.03, 0.06], 6));
}
