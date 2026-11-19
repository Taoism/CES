using Ces.Spot;

namespace Ces.Tests;

/// <summary>Real encoders (RareWares / Doom9 builds) on the fixture tone: what Spot must recognise.</summary>
public sealed class EncoderSpotTests
{
    private static AnalysisResult Analyze(string name) =>
        AudioAnalyzer.Analyze(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), new AnalyzeOptions(), CancellationToken.None);

    [Fact]
    public void GogoVbrByItsXingQualityField()
    {
        var r = Analyze("gogo313-vbr2.mp3");
        Assert.Equal("GOGO", r.Encoder);
        Assert.Equal(GuessConfidence.High, r.Confidence);
    }

    [Fact]
    public void GogoCbrIsAnHonestLowGuess()
    {
        var r = Analyze("gogo313-cbr128.mp3");
        Assert.StartsWith("GOGO", r.Encoder, StringComparison.Ordinal);
        Assert.Equal(GuessConfidence.Low, r.Confidence);
    }

    [Fact]
    public void HelixFromItsLameStyleTag()
    {
        var r = Analyze("helix524-cbr128.mp3");
        Assert.Equal("Helix hmp3 5.2.4", r.Encoder);
        Assert.StartsWith("CBR", r.EncoderSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void OldLameWithPreset()
    {
        var r = Analyze("lame3951-aps.mp3");
        Assert.Equal("LAME 3.95", r.Encoder);
        Assert.Contains("V2", r.EncoderSettings, StringComparison.Ordinal);
    }
}

public sealed class EncoderFingerprintTests
{
    private static AnalysisResult Analyze(string name) =>
        AudioAnalyzer.Analyze(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), new AnalyzeOptions(), CancellationToken.None);

    [Fact]
    public void BladeByItsUnusedReservoir()
    {
        var r = Analyze("blade0942-128.mp3");
        Assert.Equal("Blade", r.Encoder);
        Assert.Equal(GuessConfidence.Medium, r.Confidence);
    }

    [Theory]
    [InlineData("fastenc102-128.mp3")]
    [InlineData("fhgmp3s15-128.mp3")]
    [InlineData("fhgacm34-128.mp3")]
    public void FraunhoferByPreflagWithoutScfsi(string file) => Assert.Equal("FhG (untagged)", Analyze(file).Encoder);

    [Fact]
    public void LameBefore390HasNoTagCrcToCheck()
    {
        var r = Analyze("lame380-vbr.mp3");
        Assert.Equal("LAME 3.80", r.Encoder);
        Assert.DoesNotContain(r.Problems, p => p.Contains("CRC", StringComparison.Ordinal));
        Assert.False(r.Mp3!.Xing!.Lame!.HasExtendedInfo);
    }

    [Fact]
    public void XingCountsItsHeaderFrameWithoutComplaint()
    {
        var r = Analyze("xing15-vbr50.mp3");
        Assert.Equal("Xing (new)", r.Encoder);
        Assert.Equal(r.Mp3!.Frames + 1, r.Mp3.Xing!.Frames);
        Assert.DoesNotContain(r.Problems, p => p.Contains("frames", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("LAME3.80", true)]
    [InlineData("LAME3.88 (beta)", true)]
    [InlineData("LAME3.90", false)]
    [InlineData("LAME3.100", false)]
    [InlineData("LAMEH5.24", false)]
    public void ExtendedTagFrom390(string encoder, bool before) => Assert.Equal(before, Ces.Spot.Mp3.LameTag.BeforeExtendedTag(encoder));
}

public sealed class ITunesSpotTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunes12-160.mp3");

    [Fact]
    public void NamedByEncodedByWithGaplessValues()
    {
        var r = AudioAnalyzer.Analyze(Fixture, new AnalyzeOptions(), CancellationToken.None);
        Assert.Equal("iTunes 12.13.12.3", r.Encoder);
        Assert.Equal(GuessConfidence.Medium, r.Confidence);
        Assert.Equal("delay 528, padding 2784", r.EncoderSettings);
        // iTunes' own comment frames are not the user's comment
        Assert.False(r.Tags!.Fields.ContainsKey("Comment"));
    }

    [Fact]
    public void WithoutTagsTheStreamStillPointsAtITunes()
    {
        var bytes = File.ReadAllBytes(Fixture);
        // ID3v2 size: 4 syncsafe bytes after the 6-byte header start
        var size = 10 + ((bytes[6] & 0x7F) << 21 | (bytes[7] & 0x7F) << 14 | (bytes[8] & 0x7F) << 7 | (bytes[9] & 0x7F));
        var path = Path.Combine(Path.GetTempPath(), $"ces-itunes-untagged-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, bytes[size..]);
        try
        {
            var r = AudioAnalyzer.Analyze(path, new AnalyzeOptions(), CancellationToken.None);
            Assert.Equal("iTunes (untagged)", r.Encoder);
            Assert.Equal(GuessConfidence.Low, r.Confidence);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(" 00000000 00000210 000008A0 00000000000D7550 00000000", "delay 528, padding 2208")]
    [InlineData("garbage", null)]
    public void GaplessComment(string smpb, string? expected) => Assert.Equal(expected, Ces.Spot.Mp3.Mp3Report.ITunesGapless(smpb));
}
