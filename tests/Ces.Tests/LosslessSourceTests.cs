using Ces.Spot;

namespace Ces.Tests;

/// <summary>Spot's lossless-source checks on white noise: genuine, from a 128 kbps MP3, upsampled, padded bits.</summary>
public sealed class LosslessSourceTests
{
    private static AnalysisResult Analyze(string name) =>
        AudioAnalyzer.Analyze(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), new AnalyzeOptions { DecodeFlac = true }, CancellationToken.None);

    [Fact]
    public void GenuineLosslessPasses()
    {
        var r = Analyze("noise-genuine.flac");
        Assert.Empty(r.Problems);
        Assert.Equal(QualityRating.Lossless, r.Quality);
        Assert.Equal("ok", r.SourceCheck);
        Assert.Equal(16, r.Flac!.UsedBits);
    }

    [Fact]
    public void FlacFromMp3IsALossySource()
    {
        var r = Analyze("noise-from-mp3.flac");
        Assert.Contains(r.Problems, p => p.StartsWith("lossy source?", StringComparison.Ordinal));
        Assert.Equal(QualityRating.Fair, r.Quality);
        Assert.InRange(r.Flac!.CutoffHz, 15000, LosslessSource.LossyMaxHz);
    }

    [Fact]
    public void UpsampledHighRateFileIsFound()
    {
        var r = Analyze("noise-upsampled-96k.flac");
        Assert.Contains(r.Problems, p => p.StartsWith("upsampled from 44.1 kHz", StringComparison.Ordinal));
        Assert.Equal(QualityRating.Fair, r.Quality);
    }

    [Fact]
    public void PaddedBitsAreFound()
    {
        var r = Analyze("noise-16in24.flac");
        Assert.Equal(16, r.Flac!.UsedBits);
        Assert.Contains(r.Problems, p => p.StartsWith("16-bit audio padded to 24 bits", StringComparison.Ordinal));
    }

    [Fact]
    public void RealHighRateContentIsNotUpsampled()
    {
        // the 96 kHz fixture has content up to ~45 kHz
        Assert.DoesNotContain(Analyze("ffmpeg-24bit.flac").Problems, p => p.StartsWith("upsampled", StringComparison.Ordinal));
    }
}
