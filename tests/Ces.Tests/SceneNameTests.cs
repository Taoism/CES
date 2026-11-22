using Ces.Analysis;
using Ces.Compare;
using Ces.Model;
using Ces.Share;

namespace Ces.Tests;

public class SceneNameTests
{
    [Theory]
    [InlineData("Old.Film.1970.COMPLETE.BLURAY-GRP", "Old Film (1970)")]
    [InlineData("old.film.the.special.collection.1970.complete.bluray-grp.iso", "Old Film The Special Collection (1970)")]
    [InlineData("Some.Movie.2001.2160p.UHD.BluRay.REMUX.HDR.HEVC.DTS-HD.MA.5.1-TEAM.mkv", "Some Movie (2001)")]
    [InlineData("Another_Film_1999_DVD9_NTSC-XYZ", "Another Film (1999)")]
    [InlineData("Film.Without.Year.1080p.BluRay.x264-ABC", "Film Without Year")]
    // what a person typed stays as it is
    [InlineData("My comparison of two discs", "My comparison of two discs")]
    [InlineData("Old Film (1970)", "Old Film (1970)")]
    public void TitleDropsTheReleaseName(string name, string expected) => Assert.Equal(expected, SceneName.Title(name));

    [Theory]
    [InlineData("Old.Film.1970.COMPLETE.BLURAY-GRP", true)]
    [InlineData("film.2001.1080p.bluray.x264-abc.mkv", true)]
    [InlineData("BluRay 1080p HEVC REMUX", true)]
    [InlineData("Criterion disc", false)]
    [InlineData("a.mkv", false)]
    [InlineData("The old transfer", false)]
    public void RecognisesReleaseNames(string s, bool expected) => Assert.Equal(expected, SceneName.LooksLikeRelease(s));

    private static CompareItem Item(string path, SourceKind kind, int w, int h, string codec, string label = "")
    {
        var info = new MediaInfoSummary();
        info.Streams.Add(new StreamInfo { Kind = StreamKind.Video, Codec = codec, Width = w, Height = h });
        return new CompareItem
        {
            Source = new MediaSource { Path = path, Kind = kind, DisplayName = Path.GetFileName(path) },
            Title = new MediaTitle { Name = "00800.mpls", Info = info },
            Label = label,
        };
    }

    [Fact]
    public void UploadCarriesNoReleaseNames()
    {
        var shotDir = Path.Combine(Path.GetTempPath(), "ces-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shotDir);
        try
        {
            var a = Item(@"X:\Media\Old.Film.1970.COMPLETE.BLURAY-GRP\old.film.1970.complete.bluray-grp.iso", SourceKind.BluRay, 1920, 1080, "h264");
            var b = Item(@"X:\Media\Old.Film.1970.REMASTERED.2160p.UHD.BLURAY-TEAM", SourceKind.BluRay, 3840, 2160, "hevc");
            var c = Item(@"X:\Media\old.film.dvd.mkv", SourceKind.File, 720, 480, "mpeg2video", label: "Old DVD");
            var items = new[] { a, b, c };
            var row = new ShotRow { Time = 60, Caption = "worst for " + b.Name + ": 71.2" };
            for (var i = 0; i < items.Length; i++)
            {
                var png = Path.Combine(shotDir, $"{items[i].Name}-{i}.png");
                File.WriteAllBytes(png, [1]);
                row.Shots.Add(new ShotResult { ImagePath = png, PictType = "I" });
            }
            var up = SlowPics.FromRows(items, [row]);
            var text = string.Join("|", up.Select(u => u.Name).Concat(up.SelectMany(u => u.Images.Select(im => im.Name)))) + "|" + SlowPics.SuggestedName(items);
            Assert.DoesNotContain("GRP", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TEAM", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BLURAY-", text, StringComparison.OrdinalIgnoreCase);
            // no dotted release-style names left (times like 0:01:00.000 are fine)
            Assert.DoesNotMatch(@"[A-Za-z]+\.[A-Za-z0-9]+\.[A-Za-z0-9]+", text);
            Assert.Equal(["1 · Blu-ray 1080p AVC (I-frame)", "2 · Blu-ray 2160p HEVC (I-frame)", "3 · Old DVD (I-frame)"], up[0].Images.Select(i => i.Name));
            Assert.Contains("worst for source 2 · Blu-ray 2160p HEVC", up[0].Name);
            Assert.Equal("Old Film (1970)", SlowPics.SuggestedName(items));
        }
        finally
        {
            Directory.Delete(shotDir, true);
        }
    }

    [Fact]
    public void PngTextChunksAreDropped()
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var b = new byte[12 + data.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)data.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            data.CopyTo(b, 8);
            return b; // CRC left zero: chunks are copied as they are
        }
        byte[] sig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        var png = sig.Concat(Chunk("IHDR", new byte[13])).Concat(Chunk("tEXt", "Comment\0Old.Film.1970.BLURAY-GRP"u8.ToArray()))
            .Concat(Chunk("iTXt", new byte[5])).Concat(Chunk("IDAT", [1, 2, 3])).Concat(Chunk("tIME", new byte[7])).Concat(Chunk("IEND", [])).ToArray();
        var clean = SlowPics.StripPngText(png);
        var s = System.Text.Encoding.ASCII.GetString(clean);
        Assert.DoesNotContain("tEXt", s);
        Assert.DoesNotContain("iTXt", s);
        Assert.DoesNotContain("tIME", s);
        Assert.DoesNotContain("GRP", s);
        Assert.Contains("IHDR", s);
        Assert.Contains("IDAT", s);
        Assert.EndsWith("IEND\0\0\0\0", s);
        Assert.Equal(8 + (12 + 13) + (12 + 3) + 12, clean.Length);
    }
}
