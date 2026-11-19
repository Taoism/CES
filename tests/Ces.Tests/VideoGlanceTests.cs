using Ces.SpotUi;

namespace Ces.Tests;

public sealed class VideoGlanceTests
{
    [Fact]
    public void X264SettingsAtAGlance()
    {
        const string x264 = "cabac=1 / ref=5 / deblock=1:-3:-3 / analyse=0x3:0x113 / me=umh / subme=10 / psy=1 / psy_rd=1.00:0.15 / " +
            "bframes=8 / rc=crf / crf=16.0 / aq=3:0.80";
        Assert.Equal("crf 16.0, ref 5, bframes 8, aq 3:0.80, psy-rd 1.00:0.15, deblock 1:-3:-3", VideoGlance.SettingsSummary(x264));
    }

    [Fact]
    public void X265Keys() =>
        Assert.Equal("crf 20.0, ref 4, bframes 4, aq 2, psy-rd 2.00", VideoGlance.SettingsSummary("ref=4 bframes=4 crf=20.0 aq-mode=2 psy-rd=2.00 rc=crf"));

    [Fact]
    public void TwoPassShowsTheBitrate() =>
        Assert.Equal("2pass 8000 kbps, ref 4", VideoGlance.SettingsSummary("ref=4 / rc=2pass / bitrate=8000 / ratetol=1.0"));

    [Fact]
    public void NoSettingsNoSummary() => Assert.Equal("", VideoGlance.SettingsSummary(""));

    [Fact]
    public void DiscFolders()
    {
        var root = Directory.CreateTempSubdirectory("ces-disc").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Movie", "BDMV"));
            Directory.CreateDirectory(Path.Combine(root, "Show", "VIDEO_TS"));
            Assert.True(VideoGlance.IsDiscFolder(Path.Combine(root, "Movie")));
            Assert.True(VideoGlance.IsDiscFolder(Path.Combine(root, "Movie", "BDMV")));
            Assert.True(VideoGlance.IsDiscFolder(Path.Combine(root, "Show")));
            Assert.False(VideoGlance.IsDiscFolder(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
