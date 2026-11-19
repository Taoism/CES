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
