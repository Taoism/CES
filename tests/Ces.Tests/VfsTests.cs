using System.Net.Http.Headers;
using Ces.Disc;
using Ces.Tools;
using Ces.Vfs;
using V = Ces.Vfs.Vfs;

namespace Ces.Tests;

public sealed class VfsTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Rar4 => Fx(Path.Combine("rar", "set4.rar"));
    private static string Rar5 => Fx(Path.Combine("rar", "set5.part1.rar"));

    [Fact]
    public void Rar4_old_style_volumes_are_read_across_volumes()
    {
        var a = RarArchive.Open(Rar4);
        Assert.Equal(4, a.Version);
        Assert.Equal(4, a.Volumes.Count); // .rar .r00 .r01 .r02
        var files = V.ListContainer(Rar4).Select(V.FileName).Order().ToList();
        Assert.Equal(["lame397-v2.mp3", "udf102.iso"], files);
        Assert.Equal(File.ReadAllBytes(Fx("udf102.iso")), V.ReadAllBytes(V.Combine(Rar4, "udf102.iso")));
    }

    [Fact]
    public void Rar5_part_volumes_are_read_and_seekable()
    {
        var a = RarArchive.Open(Rar5);
        Assert.Equal(5, a.Version);
        Assert.Equal(5, a.Volumes.Count);
        var original = File.ReadAllBytes(Fx("udf250.iso"));
        using var s = V.OpenRead(V.Combine(Rar5, "udf250.iso"));
        Assert.Equal(original.Length, s.Length);
        var rnd = new Random(5);
        for (int i = 0; i < 50; i++)
        {
            int at = rnd.Next(original.Length - 5000);
            var buf = new byte[5000];
            s.Position = at;
            s.ReadExactly(buf);
            Assert.True(original.AsSpan(at, 5000).SequenceEqual(buf), $"bytes differ at {at}");
        }
    }

    [Theory]
    [InlineData("udf102.iso")]
    [InlineData("udf250.iso")] // UDF 2.50: metadata partition, like Blu-ray
    public void Udf_images_list_and_read_files(string iso)
    {
        var img = UdfImage.Open(Fx(iso));
        Assert.True(img.DirectoryExists("VIDEO_TS"));
        Assert.True(img.FileExists("BDMV/STREAM/00001.m2ts"));
        using var r = new StreamReader(img.OpenRead("readme.txt"));
        Assert.Equal("hello udf", r.ReadToEnd().Trim());
        Assert.Equal(File.ReadAllBytes(Fx("lame397-v2.mp3")), V.ReadAllBytes(Fx(iso) + "::VIDEO_TS/VTS_01_1.VOB"));
        Assert.Equal(File.ReadAllBytes(Fx("ffmpeg-l5.flac")), V.ReadAllBytes(Fx(iso) + "::BDMV/STREAM/00001.m2ts"));
    }

    [Fact]
    public void Iso_inside_a_rar_set_is_read_in_place()
    {
        string inner = V.Combine(V.Combine(Rar5, "udf250.iso"), "BDMV/STREAM/00001.m2ts");
        Assert.True(V.FileExists(inner));
        Assert.True(V.DirectoryExists(V.Combine(Rar5, "udf250.iso") + "::BDMV"));
        Assert.Equal(File.ReadAllBytes(Fx("ffmpeg-l5.flac")), V.ReadAllBytes(inner));
        var listed = V.EnumerateFiles(V.Combine(Rar4, "udf102.iso") + "::VIDEO_TS").ToList();
        Assert.Equal(["VTS_01_1.VOB"], listed.Select(V.FileName));
        Assert.Equal("set5.part1.rar › udf250.iso › BDMV/STREAM/00001.m2ts", V.Display(inner));
    }

    [Fact]
    public void Compressed_rar_entries_are_reported_not_misread()
    {
        string packed = V.Combine(Fx(Path.Combine("rar", "packed.rar")), "lame397-v2.mp3");
        Assert.StartsWith("compressed", V.Problem(packed));
        var ex = Assert.Throws<InvalidDataException>(() => V.OpenRead(packed));
        Assert.Contains("compressed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Media_server_serves_byte_ranges()
    {
        string path = V.Combine(Rar5, "ffmpeg-l5.flac");
        string url = MediaServer.Shared.UrlFor(path);
        Assert.EndsWith("/ffmpeg-l5.flac", url, StringComparison.Ordinal);
        var original = File.ReadAllBytes(Fx("ffmpeg-l5.flac"));
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(1000, 1999);
        using var res = await http.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, res.StatusCode);
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(original.AsSpan(1000, 1000).ToArray(), body);
        var whole = await http.GetByteArrayAsync(url);
        Assert.Equal(original, whole);
    }

    [Fact]
    public void Tool_arguments_are_rewritten_with_the_whitelist()
    {
        string vob = V.Combine(V.Combine(Rar4, "udf102.iso"), "VIDEO_TS/VTS_01_1.VOB");
        var args = ProcessRunner.ExternalizeArgs("ffmpeg", ["-f", "mpeg", "-i", $"concat:subfile,,start,0,end,100,,:{vob}|{vob}", "-y", "out.png"]);
        int i = args.IndexOf("-i");
        Assert.Equal("-protocol_whitelist", args[i - 2]);
        Assert.Contains("http", args[i - 1], StringComparison.Ordinal);
        Assert.StartsWith("concat:subfile,,start,0,end,100,,:http://127.0.0.1:", args[i + 1], StringComparison.Ordinal);
        Assert.DoesNotContain("::", args[i + 1], StringComparison.Ordinal);
        Assert.Equal("out.png", args[^1]);
        // plain files are left alone
        Assert.Equal(["-i", @"C:\x.mkv"], ProcessRunner.ExternalizeArgs("ffmpeg", ["-i", @"C:\x.mkv"]));
    }

    [Fact]
    public void Spot_analyzes_audio_inside_rar_sets_and_images()
    {
        var flac = Spot.AudioAnalyzer.Analyze(V.Combine(Rar5, "ffmpeg-l5.flac"));
        Assert.Null(flac.Error);
        Assert.True(flac.Flac!.Md5Ok);
        var mp3 = Spot.AudioAnalyzer.Analyze(V.Combine(V.Combine(Rar4, "udf102.iso"), "VIDEO_TS/VTS_01_1.VOB"));
        Assert.Equal("MP3", mp3.Format);
        Assert.StartsWith("LAME", mp3.Encoder, StringComparison.Ordinal);
        Assert.True(mp3.Mp3!.Xing!.Lame!.MusicCrcOk);
    }

    [Fact]
    public async Task Source_opener_explains_what_it_cannot_open()
    {
        var packed = await Assert.ThrowsAsync<InvalidDataException>(() => SourceOpener.OpenAsync(Fx(Path.Combine("rar", "packed.rar"))));
        Assert.Contains("compressed", packed.Message, StringComparison.Ordinal);
        // the ISO in set4 has no IFO files: it is found and read, but is not a DVD
        var notDisc = await Assert.ThrowsAsync<InvalidDataException>(() => SourceOpener.OpenAsync(Rar4));
        Assert.Contains("BDMV or VIDEO_TS", notDisc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Volume_names_identify_whole_sets()
    {
        Assert.True(V.IsRarSet(@"C:\r\x.rar"));
        Assert.True(V.IsRarSet(@"C:\r\x.part01.rar"));
        Assert.False(V.IsRarSet(@"C:\r\x.part02.rar"));
        Assert.False(V.IsRarSet(@"C:\r\x.r00"));
    }
}
