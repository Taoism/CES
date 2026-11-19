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
