using System.Formats.Tar;
using System.IO.Compression;
using Ces.Analysis;
using Ces.Compare;
using Ces.Model;
using Ces.Report;

namespace Ces.Tests;

public class ReportExportTests
{
    private static CompareItem Item(string name, int w, int h, string audioCodec, int channels, string transfer = "", long size = 20_000_000_000)
    {
        var info = new MediaInfoSummary { Duration = 6000, SizeBytes = size };
        info.Streams.Add(new StreamInfo { Kind = StreamKind.Video, Codec = "hevc", Width = w, Height = h, ColorTransfer = transfer });
        info.Streams.Add(new StreamInfo { Kind = StreamKind.Audio, Codec = audioCodec, Channels = channels });
        return new CompareItem
        {
            Source = new MediaSource { Path = name + ".mkv", Kind = SourceKind.File },
            Title = new MediaTitle { Name = name, Duration = 6000, Info = info },
            Label = name,
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

    [Fact]
    public void PlainSummaryNamesTheDifferencesInEverydayWords()
    {
        var uhd = Item("UHD", 3840, 2160, "truehd", 8, "smpte2084");
        var bd = Item("BD", 1920, 1080, "ac3", 6, size: 8_000_000_000);
        bd.Synced = true;
        bd.Offset = 1.5;
        var plain = PlainReport.Build([uhd, bd], [(uhd, bd, Vmaf(92))]);
        var text = PlainReport.ToText("UHD vs BD", plain);
        Assert.Contains("4K (Ultra HD)", text);
        Assert.Contains("Full HD (1080p)", text);
        Assert.Contains("HDR (for HDR TVs): only number 1", text);
        Assert.Contains("lossless) sound: only number 1", text);
        Assert.Contains("very close (differences are hard to spot)", text);
        Assert.Contains("starts 1.5 seconds later", text);
        Assert.Contains("much smaller", text);
        // the glossary explains the words that were used, and only those
        var words = plain.Single(s => s.Heading == "Words used in this report").Lines;
        Assert.Contains(words, l => l.StartsWith("VMAF:", StringComparison.Ordinal));
        Assert.Contains(words, l => l.StartsWith("HDR:", StringComparison.Ordinal));
        Assert.DoesNotContain(words, l => l.StartsWith("SSIM:", StringComparison.Ordinal));
        Assert.DoesNotContain(words, l => l.StartsWith("XPSNR:", StringComparison.Ordinal));
        Assert.Contains("In plain words", PlainReport.ToHtml(plain));
    }

    [Theory]
    [InlineData(97, "practically identical")]
    [InlineData(84, "small differences")]
    [InlineData(60, "clearly worse")]
    public void ClosenessInWords(double vmaf, string expected) =>
        Assert.Contains(expected, PlainReport.Closeness(("VMAF", vmaf)));

    [Theory]
    [InlineData("report.zip")]
    [InlineData("report.tar.gz")]
    public void ArchiveRoundTrip(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "ces-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, name);
            ReportArchive.Export(archive, dir =>
            {
                File.WriteAllText(Path.Combine(dir, ReportArchive.SummaryName), "plain words");
                File.WriteAllText(Path.Combine(dir, ReportArchive.TextName), "details");
                File.WriteAllText(Path.Combine(dir, ReportArchive.HtmlName), "<html></html>");
                Directory.CreateDirectory(Path.Combine(dir, "images"));
                File.WriteAllBytes(Path.Combine(dir, "images", "shot-01-1.png"), [1, 2, 3]);
            });
            Assert.True(File.Exists(archive));
            Assert.Single(Directory.GetFiles(root));
            using var b = ReportArchive.Open(archive);
            // the files sit in a folder named after the archive; the bundle hides it
            Assert.Equal("report/", b.Root);
            Assert.Equal("plain words", b.ReadText(ReportArchive.SummaryName));
            Assert.Equal("details", b.ReadText(ReportArchive.TextName));
            Assert.Equal([1, 2, 3], b.Read("images/shot-01-1.png"));
            var html = b.ExtractTo(Path.Combine(root, "out"));
            Assert.NotNull(html);
            Assert.True(File.Exists(Path.Combine(root, "out", "images", "shot-01-1.png")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EntriesThatClimbOutOfTheReportAreIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), "ces-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var zip = Path.Combine(root, "evil.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("r/summary.txt").Open()))
                    w.Write("ok");
                using (var w = new StreamWriter(z.CreateEntry("r/../../escape.txt").Open()))
                    w.Write("bad");
            }
            using (var b = ReportArchive.Open(zip))
            {
                Assert.Equal(["summary.txt"], b.Files);
                b.ExtractTo(Path.Combine(root, "out"));
            }
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));

            var tgz = Path.Combine(root, "evil.tar.gz");
            using (var fs = File.Create(tgz))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var tw = new TarWriter(gz))
            {
                tw.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "r/summary.txt") { DataStream = new MemoryStream("ok"u8.ToArray()) });
                tw.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "r/../x.txt") { DataStream = new MemoryStream("bad"u8.ToArray()) });
            }
            using (var b = ReportArchive.Open(tgz))
                Assert.Equal(["summary.txt"], b.Files);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("a.zip", ArchiveKind.Zip)]
    [InlineData("a.TAR.GZ", ArchiveKind.TarGz)]
    [InlineData("a.tgz", ArchiveKind.TarGz)]
    [InlineData("a.html", null)]
    public void KindFromName(string name, ArchiveKind? kind) => Assert.Equal(kind, ReportArchive.KindOf(name));
}
