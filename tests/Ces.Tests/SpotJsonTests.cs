using Ces.Spot;
using Ces.SpotUi;

namespace Ces.Tests;

public sealed class SpotJsonTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("ffmpeg-l5.flac")]
    [InlineData("ffmpeg-24bit.flac")]
    [InlineData("ffmpeg-cbr192.mp3")]
    [InlineData("ffmpeg-mono22k.mp3")]
    [InlineData("lame3100-cbr128-crc.mp3")]
    [InlineData("lame3100-v2.mp3")]
    public void RoundTripKeepsEverything(string name)
    {
        var original = AudioAnalyzer.Analyze(Fixture(name), new AnalyzeOptions { DecodeFlac = true }, CancellationToken.None);
        var json = SpotJson.Serialize(original);
        var back = SpotJson.Deserialize(json);

        Assert.NotNull(back);
        // same JSON again, and the same report (details, settings, problems, tags)
        Assert.Equal(json, SpotJson.Serialize(back));
        Assert.Equal(SpotReport.Text(original), SpotReport.Text(back));
        Assert.Equal(original.Artist, back.Artist);
        if (original.Mp3 is { } m)
        {
            Assert.Equal(m.FrameBitrates, back.Mp3!.FrameBitrates);
            Assert.Equal(m.FrameReservoir, back.Mp3.FrameReservoir);
            Assert.Equal(m.Layer3.BigValuesHistogram, back.Mp3.Layer3.BigValuesHistogram);
            Assert.Equal(m.Layer3.Granules, back.Mp3.Layer3.Granules);
            Assert.Equal(m.FirstHeader, back.Mp3.FirstHeader);
            Assert.Equal(m.Xing?.Lame?.EncoderDelay, back.Mp3.Xing?.Lame?.EncoderDelay);
        }
        if (original.Flac is { } f)
        {
            Assert.Equal(f.LpcOrders, back.Flac!.LpcOrders);
            Assert.Equal(f.StoredMd5, back.Flac.StoredMd5);
            Assert.Equal(f.BlockSizeHistogram, back.Flac.BlockSizeHistogram);
            Assert.Equal(f.Comments, back.Flac.Comments);
        }
    }
}

public sealed class SpotSessionTests
{
    private static readonly string[] Files = ["lame3100-v2.mp3", "ffmpeg-l5.flac"];

    [Fact]
    public async Task SavedResultsComeBack()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var results = Files
            .Select(n => AudioAnalyzer.Analyze(Path.Combine(dir, n), new AnalyzeOptions(), CancellationToken.None)).ToList();
        var path = Environment.GetEnvironmentVariable("CES_TEST_CESS") is { Length: > 0 } keep ? keep : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + SpotSession.Extension);
        await new SpotSession { Inputs = [dir], Results = results }.SaveAsync(path);

        var back = await SpotSession.LoadAsync(path);

        Assert.Equal([dir], back.Inputs);
        Assert.Equal(results.Select(r => SpotJson.Serialize(r)), back.Results.Select(r => SpotJson.Serialize(r)));
        var row = new FileRow(back.Results[0]);
        Assert.True(row.IsDone);
        Assert.Equal(results[0].FileSize, row.Size);
        Assert.Equal(results[0].Encoder, row.Encoder);
        if (Environment.GetEnvironmentVariable("CES_TEST_CESS") is not { Length: > 0 })
            File.Delete(path);
    }
}
