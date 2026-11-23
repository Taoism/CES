using Ces.Analysis;
using Ces.Compare;
using Ces.Model;
using Ces.Report;
using Xunit.Abstractions;

namespace Ces.Tests;

public class ReportNotesTests(ITestOutputHelper output)
{
    private static CompareItem Item(string name, long size, double noise, params (string Codec, int Channels)[] audio)
    {
        var info = new MediaInfoSummary { Duration = 6300, SizeBytes = size };
        info.Streams.Add(new StreamInfo { Kind = StreamKind.Video, Codec = "h264", Width = 1920, Height = 1080 });
        foreach (var (codec, ch) in audio)
            info.Streams.Add(new StreamInfo { Kind = StreamKind.Audio, Codec = codec, Channels = ch });
        var video = new VideoReport { Width = 1920, Height = 1080, BitDepth = 8 };
        video.Frames.Add(new VideoFrameResult
        {
            Time = 100,
            Levels = new LevelStats { Min = 16, Max = 235, EffectiveBits = 8 },
            Spectrum = new SpectrumStats { Horizontal = [1, 2, 3], CutoffHeight = double.NaN },
            Crop = new CropBox(0, 0, 0, 0),
            Sharpness = 10,
            Artefacts = new ArtefactStats { Noise = noise },
        });
        return new CompareItem
        {
            Source = new MediaSource { Path = @"X:\Media\" + name + ".mkv", Kind = SourceKind.File, DisplayName = name + ".mkv" },
            Title = new MediaTitle { Name = name + ".mkv", Duration = 6300, Info = info },
            Video = video,
        };
    }

    private static MetricResult Vmaf(double value)
    {
        var r = new MetricResult { Frames = 1440, FrameRate = 24 };
        var s = new MetricSeries { Name = "VMAF" };
        for (var i = 0; i < 1440; i++)
            s.Values.Add(value);
        r.Series[Metric.Vmaf] = s;
        return r;
    }

    private static (CompareItem Ref, CompareItem Close, CompareItem Regraded, List<(CompareItem, CompareItem, MetricResult)> Metrics, List<SubtitleTimingReport> Subs) ThreeReleases()
    {
        var a = Item("Old.Film.1970.COMPLETE.BLURAY-ALPHA", 8_214_000_000, 1.0, ("ac3", 2), ("ac3", 2));
        var b = Item("Old.Film.1970.1080p.BluRay.x264-BETA", 9_000_000_000, 1.1, ("dts", 2));
        var c = Item("Old.Film.1970.REMASTERED.1080p.BluRay.FLAC.x264-GAMMA", 17_500_000_000, 2.3, ("flac", 1));
        var grade = new GradeReport { Item = c };
        for (var t = 0; t < 5; t++)
        {
            grade.Frames.Add(new GradeFrame
            {
                Time = 600 * t,
                Match = 0.6,
                RefL = [10, 20, 30, 50, 70, 85, 95],
                OtherL = [8, 18.5, 30, 45, 72, 90, 97],
                ContrastRatio = 1.31,
                SaturationRatio = 1.02,
                DeltaA = -4.5,
                DeltaB = 0.5,
            });
        }
        grade.Findings.Add(new Finding { Area = "Grade", Level = 2, Text = "Re-graded." });
        c.Grade = grade;
        b.Temporal = new TemporalReport { Item = b, SecondsChecked = 20 };
        b.Temporal.Events.Add(new FrameEvent(1200, "duplicated / inserted frame"));
        c.Temporal = new TemporalReport { Item = c, SecondsChecked = 20 };
        c.Temporal.Events.Add(new FrameEvent(1800, "duplicated / inserted frame"));
        c.Temporal.Events.Add(new FrameEvent(2400, "dropped frame"));
        var subs = new List<SubtitleTimingReport> { new() { Item = b, TrackLabel = "English", ReferenceEvents = 100, Matched = 56, Events = 98 } };
        return (a, b, c, [(a, b, Vmaf(42)), (a, c, Vmaf(11))], subs);
    }

    [Fact]
    public void NotesReadLikeAReviewOfTheReleases()
    {
        var (a, b, c, metrics, subs) = ThreeReleases();
        var notes = ReportNotes.Build([a, b, c], metrics, subs);
        var text = string.Join("\n\n", notes);
        output.WriteLine(text);
        Assert.Equal(2, notes.Count);
        Assert.StartsWith("Comparing the three 1080p Blu-ray releases of Old Film (1970) from ALPHA, BETA, and GAMMA reveals substantial differences in picture quality, mastering, and compression.", notes[0]);
        Assert.Contains("Using ALPHA as the reference, BETA delivers the closest visual match with a VMAF score of 42, while GAMMA scores considerably lower at just 11.", text);
        Assert.Contains("rather than compression quality alone", text);
        Assert.Contains("BETA appears to originate from a different master but maintains a closer resemblance to ALPHA's presentation.", text);
        Assert.Contains("GAMMA's remastered release features significantly altered colour grading, with 31% stronger contrast, darker midtones, brighter highlights, and a noticeable green tint.", text);
        Assert.Contains("more than twice the noise of the reference", text);
        Assert.Contains("At 16.30 GiB, GAMMA is substantially larger than ALPHA's 7.65 GiB", text);
        Assert.Contains("ALPHA includes two audio tracks with lossy Dolby Digital stereo, while BETA offers a single lossy DTS stereo track.", text);
        Assert.Contains("GAMMA stands out as the only release featuring lossless FLAC audio, although its soundtrack is mono rather than stereo.", text);
        Assert.Contains("Both BETA and GAMMA show minor playback irregularities, including isolated duplicated frames, with GAMMA also registering one dropped frame during testing.", text);
        Assert.Contains("only 56% matching lines against the reference in the limited sample", text);
        Assert.Contains("Ultimately, ALPHA is the safest choice", text);
        Assert.Contains("lossless original mono soundtrack", text);
    }

    [Fact]
    public void OneSourceGetsNoNotes() =>
        Assert.Empty(ReportNotes.Build([Item("A", 1, 1, ("ac3", 2))], []));

    [Fact]
    public void ArchiveFilesAreNamedAfterTheReleases()
    {
        var (a, b, c, metrics, subs) = ThreeReleases();
        Assert.Equal("old.film.1970.complete.bluray-alpha", ReportArchive.ReleaseBase([a, b, c]));
        Assert.Equal("old.film.1970.1080p.bluray.x264-beta", ReportArchive.SourceBase(b));
        var root = Path.Combine(Path.GetTempPath(), "ces-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var archive = Path.Combine(root, "report.zip");
            Directory.CreateDirectory(root);
            var png = Path.Combine(root, "frame.png");
            File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]);
            var row = new ShotRow { Time = 100 };
            foreach (var _ in new[] { a, b, c })
                row.Shots.Add(new ShotResult { ImagePath = png, Width = 1920, Height = 1080 });
            ReportArchive.Export(archive, dir => HtmlReport.WriteAll(dir, "Old Film", [a, b, c], [row], metrics, subs));
            using var bundle = ReportArchive.Open(archive);
            Assert.Equal("old.film.1970.complete.bluray-alpha-notes.txt", bundle.Notes);
            Assert.Equal("old.film.1970.complete.bluray-alpha-summary.txt", bundle.Summary);
            Assert.Equal("old.film.1970.complete.bluray-alpha-report.txt", bundle.Text);
            Assert.Contains("Comparing the three", bundle.ReadText(bundle.Notes!));
            Assert.Contains("Comparing the three", bundle.ReadText(bundle.Summary!));
            Assert.Contains("NOTES", bundle.ReadText(bundle.Text!));
            Assert.Contains("<h2>Notes</h2>", bundle.ReadText(ReportArchive.HtmlName));
            Assert.Contains("images/old.film.1970.complete.bluray-alpha-shot-01-1.png", bundle.Files);
            Assert.Contains("images/old.film.1970.1080p.bluray.x264-beta-shot-01-2.png", bundle.Files);
            Assert.Contains("images/old.film.1970.remastered.1080p.bluray.flac.x264-gamma-shot-01-3.png", bundle.Files);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
