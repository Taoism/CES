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

    [Fact]
    public void FraunhoferByPreflagWithoutScfsi() => Assert.Equal("FhG (untagged)", Analyze("fastenc102-128.mp3").Encoder);

    [Fact]
    public void LameBefore390HasNoTagCrcToCheck()
    {
        var r = Analyze("lame380-vbr.mp3");
        Assert.Equal("LAME 3.80", r.Encoder);
        Assert.DoesNotContain(r.Problems, p => p.Contains("CRC", StringComparison.Ordinal));
        Assert.False(r.Mp3!.Xing!.Lame!.HasExtendedInfo);
    }

    [Theory]
    [InlineData("LAME3.80", true)]
    [InlineData("LAME3.88 (beta)", true)]
    [InlineData("LAME3.90", false)]
    [InlineData("LAME3.100", false)]
    [InlineData("LAMEH5.24", false)]
    public void ExtendedTagFrom390(string encoder, bool before) => Assert.Equal(before, Ces.Spot.Mp3.LameTag.BeforeExtendedTag(encoder));
}
