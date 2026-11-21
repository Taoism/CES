using Ces.Report;

namespace Ces.Tests;

public class ReportPathTests
{
    [Theory]
    // a RAR set with an ISO inside: from the release folder on
    [InlineData(@"X:\Media\Old.Film.1970.COMPLETE.BLURAY-GRP\old.film.1970.complete.bluray-grp.rar::OLD_FILM.iso",
        @"Old.Film.1970.COMPLETE.BLURAY-GRP\old.film.1970.complete.bluray-grp.rar::OLD_FILM.iso", false)]
    [InlineData(@"E:\Movies\HD\Some.Film.1999.1080p\some.film.mkv", @"Some.Film.1999.1080p\some.film.mkv", false)]
    // a file right at the drive root
    [InlineData(@"D:\film.mkv", "film.mkv", false)]
    // a disc folder: the release folder itself, or the folder above BDMV / VIDEO_TS
    [InlineData(@"X:\Media\Film.2001.COMPLETE.BLURAY-GRP", "Film.2001.COMPLETE.BLURAY-GRP", true)]
    [InlineData(@"X:\Media\Film.2001.COMPLETE.BLURAY-GRP\BDMV", @"Film.2001.COMPLETE.BLURAY-GRP\BDMV", true)]
    [InlineData(@"X:\Media\Film.2001.COMPLETE.BLURAY-GRP\BDMV\PLAYLIST\00800.mpls", @"Film.2001.COMPLETE.BLURAY-GRP\BDMV\PLAYLIST\00800.mpls", false)]
    [InlineData(@"X:\Media\Film.DVD-GRP\VIDEO_TS", @"Film.DVD-GRP\VIDEO_TS", true)]
    // Linux / macOS paths
    [InlineData("/mnt/media/Film.2001.BLURAY-GRP/film.iso", "Film.2001.BLURAY-GRP/film.iso", false)]
    public void ShowsFromTheReleaseFolder(string path, string expected, bool folder) =>
        Assert.Equal(expected, ReportPath.Short(path, folder));
}
