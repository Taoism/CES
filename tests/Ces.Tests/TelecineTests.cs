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
