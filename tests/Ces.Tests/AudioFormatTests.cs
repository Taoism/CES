using Ces.Spot;

namespace Ces.Tests;

/// <summary>WAV, Ogg Vorbis/Opus and M4A (AAC/ALAC) in Spot, including damaged files.</summary>
public sealed class AudioFormatTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("ces-formats").FullName;

    public void Dispose() => Directory.Delete(_tmp, true);

    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static AnalysisResult Analyze(string path) => AudioAnalyzer.Analyze(path, new AnalyzeOptions(), CancellationToken.None);

    [Fact]
    public void Wav()
    {
        var r = Analyze(Fx("src.wav"));
        Assert.Null(r.Error);
        Assert.Equal("WAV", r.Format);
        Assert.Equal(44100, r.SampleRate);
        Assert.Equal(16, r.BitsPerSample);
        Assert.Equal(16, r.Wav!.UsedBits);
        Assert.NotEmpty(r.Wav.Spectrum);
        Assert.Equal(QualityRating.Lossless, r.Quality);
    }

    [Fact]
    public void OggVorbis()
    {
        var r = Analyze(Fx("ffmpeg-vorbis-q4.ogg"));
        Assert.Equal("Vorbis", r.Format);
        Assert.StartsWith("FFmpeg (Lavf", r.Encoder, StringComparison.Ordinal);
        Assert.Contains("q4", r.EncoderSettings, StringComparison.Ordinal);
        Assert.InRange(r.DurationSeconds, 1.9, 2.1);
        Assert.Equal(0, r.Ogg!.CrcErrors);
        Assert.True(r.Ogg.EndOfStream);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Opus()
    {
        var r = Analyze(Fx("ffmpeg-opus-96k.opus"));
        Assert.Equal("Opus", r.Format);
        Assert.Equal(48000, r.SampleRate);
        Assert.InRange(r.DurationSeconds, 1.9, 2.1);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void DamagedOggPageIsFound()
    {
        var path = Path.Combine(_tmp, "bad.ogg");
        var b = File.ReadAllBytes(Fx("ffmpeg-vorbis-q4.ogg"));
        b[b.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, b);
        var r = Analyze(path);
        Assert.Equal(1, r.Ogg!.CrcErrors);
        Assert.Equal(QualityRating.Poor, r.Quality);
    }

    [Fact]
    public void AacInM4a()
    {
        var r = Analyze(Fx("ffmpeg-aac-128k.m4a"));
        Assert.Equal("AAC (M4A)", r.Format);
        Assert.Equal("AAC-LC", r.Mp4!.Profile);
        Assert.StartsWith("FFmpeg (Lavf", r.Encoder, StringComparison.Ordinal);
        Assert.InRange(r.DurationSeconds, 1.9, 2.1);
        Assert.Equal(QualityRating.Good, r.Quality);
    }

    [Fact]
    public void AlacInM4a()
    {
        var r = Analyze(Fx("ffmpeg-alac.m4a"));
        Assert.Equal("ALAC (M4A)", r.Format);
        Assert.Equal(16, r.BitsPerSample);
        Assert.Equal(44100, r.SampleRate);
        Assert.Equal(QualityRating.Lossless, r.Quality);
    }

    [Fact]
    public void TruncatedM4aIsFound()
    {
        var path = Path.Combine(_tmp, "cut.m4a");
        File.WriteAllBytes(path, File.ReadAllBytes(Fx("ffmpeg-aac-128k.m4a"))[..20000]);
        var r = Analyze(path);
        Assert.Contains(r.Problems, p => p.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void Ec3AtmosConfig()
    {
        // data rate 768, one independent substream: 5.1 (acmod 7 + LFE), no dependents, then Atmos JOC complexity 16
        var a = new Ces.Spot.Mp4.Mp4Analysis();
        Ces.Spot.Mp4.Mp4Analyzer.ReadDec3([0x18, 0x00, 0x20, 0x0F, 0x00, 0x01, 0x10], a);
        Assert.Equal("5.1", a.ChannelLayout);
        Assert.Equal(6, a.Channels);
        Assert.Equal(768000, a.AverageBitrate);
        Assert.True(a.Atmos);
        Assert.Equal(16, a.AtmosComplexity);
    }

    [Fact]
    public void Ec3SevenOneWithoutAtmos()
    {
        // 5.1 core + a dependent substream carrying Lrs/Rrs (chan_loc bit 7) = 7.1; no extension bytes
        var a = new Ces.Spot.Mp4.Mp4Analysis();
        Ces.Spot.Mp4.Mp4Analyzer.ReadDec3([0x0C, 0x00, 0x20, 0x0F, 0x02, 0x80], a);
        Assert.Equal("7.1", a.ChannelLayout);
        Assert.False(a.Atmos);
    }

    [Theory]
    [InlineData("Xiph.Org libVorbis I 20200704 (Reducing Environment)", "libVorbis 1.3.7")]
    [InlineData("AO; aoTuV b6.03 [20110424] (based on Xiph.Org's libVorbis)", "aoTuV b6.03")]
    [InlineData("libopus 1.3.1", "libopus 1.3.1")]
    [InlineData("Lavf61.7.100", "FFmpeg (Lavf61.7.100)")]
    [InlineData("iTunes 12.13.2.3", "iTunes 12.13.2.3")]
    public void EncoderNamesAreReadable(string vendor, string expected) => Assert.Equal(expected, EncoderNames.Normalize(vendor));
}
