using Ces.Analysis;
using Ces.Compare;
using Ces.Model;
using Ces.Report;

namespace Ces.Tests;

public class ArchivedSessionTests
{
    [Fact]
    public void ReportSessionKeepsPathsFromTheReleaseFolderDown()
    {
        var s = new SessionFile();
        s.Sources.Add(new SessionSource { Path = @"X:\Media\Old.Film.1970.COMPLETE.BLURAY-ALPHA\old.film.rar::OLDFILM.iso" });
        s.Sources.Add(new SessionSource { Path = @"X:\Media\Old.Film.1970.1080p.BluRay.x264-BETA\old.film.mkv" });
        s.WithReportPaths();
        Assert.Equal(@"Old.Film.1970.COMPLETE.BLURAY-ALPHA\old.film.rar::OLDFILM.iso", s.Sources[0].Path);
        Assert.Equal(@"Old.Film.1970.1080p.BluRay.x264-BETA\old.film.mkv", s.Sources[1].Path);
    }

    [Fact]
    public async Task SubtitleResultsRoundTrip()
    {
        var item = new CompareItem { Source = new MediaSource { Path = "b.mkv", Kind = SourceKind.File }, Title = new MediaTitle { Name = "b" } };
        var r = new SubtitleTimingReport { Item = item, Track = 2, TrackLabel = "English", Events = 98, ReferenceEvents = 100, Matched = 56, MedianDelta = -0.063, DriftPerHour = double.NaN };
        r.Deltas.Add((12.5, -0.06));
        r.Findings.Add(new Finding { Area = "Lines", Text = "56 of 100", Level = 1 });
        var s = new SessionFile();
        s.Subtitles.Add(SessionSubtitles.From(r, 1));
        var path = Path.Combine(Path.GetTempPath(), "ces-sub-" + Guid.NewGuid().ToString("N")[..8] + ".cesc");
        try
        {
            s.Save(path);
            var back = (await SessionFile.LoadAsync(path)).Subtitles.Single().ToReport(item);
            Assert.Equal(56, back.Matched);
            Assert.Equal(100, back.ReferenceEvents);
            Assert.Equal(-0.063, back.MedianDelta, 6);
            Assert.True(double.IsNaN(back.DriftPerHour));
            Assert.Equal((12.5, -0.06), back.Deltas.Single());
            Assert.Equal("Lines", back.Findings.Single().Area);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ArchiveFindsAndUnpacksTheSavedComparison()
    {
        var root = Path.Combine(Path.GetTempPath(), "ces-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "old.film.1970.complete.bluray-alpha.zip");
            ReportArchive.Export(archive, dir =>
            {
                File.WriteAllText(Path.Combine(dir, "old.film.1970.complete.bluray-alpha-summary.txt"), "plain");
                new SessionFile().Save(Path.Combine(dir, "old.film.1970.complete.bluray-alpha.cesc"));
            });
            using var b = ReportArchive.Open(archive);
            Assert.Equal("old.film.1970.complete.bluray-alpha.cesc", b.Session);
            var file = b.ExtractFile(b.Session!, Path.Combine(root, "out"));
            Assert.True(File.Exists(file));
            Assert.Contains("\"App\":\"CES\"", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
