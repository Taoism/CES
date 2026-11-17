using System.Text;
using Ces.Spot;
using Ces.Spot.Mp3;

namespace Ces.Tests;

public sealed class AnalyzerTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("encspot-tests").FullName;

    public void Dispose() => Directory.Delete(_tmp, true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private string Copy(string name, string? newName = null)
    {
        string dst = Path.Combine(_tmp, newName ?? name);
        File.Copy(Fixture(name), dst, true);
        return dst;
    }

    private static byte[] WavPcm(string path)
    {
        byte[] wav = File.ReadAllBytes(path);
        int p = 12;
        while (p + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, p, 4);
            int len = BitConverter.ToInt32(wav, p + 4);
            if (id == "data")
            {
                return wav.AsSpan(p + 8, len).ToArray();
            }
            p += 8 + len + (len & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    [Theory]
    [InlineData("ffmpeg-l5.flac")]
    [InlineData("ffmpeg-l12.flac")]
    public void Flac_decodes_bit_exact_to_the_source(string name)
    {
        using var pcm = new MemoryStream();
        var r = AudioAnalyzer.Analyze(Fixture(name), new AnalyzeOptions { PcmOutput = pcm });

        Assert.Null(r.Error);
        Assert.Equal("FLAC", r.Format);
        Assert.Empty(r.Problems);
        Assert.True(r.Flac!.Md5Ok);
        Assert.Equal(WavPcm(Fixture("src.wav")), pcm.ToArray());
        Assert.Equal(QualityRating.Lossless, r.Quality);
        Assert.StartsWith("FFmpeg (Lavf", r.Encoder);
    }

    [Fact]
    public void Flac_level12_reports_high_lpc_orders()
    {
        var r = AudioAnalyzer.Analyze(Fixture("ffmpeg-l12.flac"));
        Assert.True(r.Flac!.MaxLpcOrderUsed > 12);
    }

    [Fact]
    public void Flac_24bit_md5_matches()
    {
        var r = AudioAnalyzer.Analyze(Fixture("ffmpeg-24bit.flac"));
        Assert.Equal(24, r.BitsPerSample);
        Assert.Equal(96000, r.SampleRate);
        Assert.True(r.Flac!.Md5Ok);
        Assert.Equal(r.Flac.TotalSamples, r.Flac.DecodedSamples);
    }

    [Fact]
    public void Flac_quick_mode_reads_metadata_only()
    {
        var r = AudioAnalyzer.Analyze(Fixture("ffmpeg-l5.flac"), new AnalyzeOptions { DecodeFlac = false });
        Assert.Equal(0, r.Flac!.Frames);
        Assert.Null(r.Flac.Md5Ok);
        Assert.Equal(44100, r.SampleRate);
        Assert.InRange(r.DurationSeconds, 3.99, 4.01);
    }

    [Fact]
    public void Flac_corruption_is_detected()
    {
        string path = Copy("ffmpeg-l5.flac", "corrupt.flac");
        byte[] b = File.ReadAllBytes(path);
        for (int i = 0; i < 16; i++)
        {
            b[(b.Length / 2) + i] ^= 0x5A;
        }
        File.WriteAllBytes(path, b);

        var r = AudioAnalyzer.Analyze(path);

        Assert.NotEmpty(r.Problems);
        Assert.Equal(QualityRating.Poor, r.Quality);
        var f = r.Flac!;
        Assert.True(f.FrameCrcErrors + f.SyncErrors + f.DecodeErrors + f.HeaderCrcErrors > 0);
    }

    [Fact]
    public void Lame_vbr_tag_is_read()
    {
        var r = AudioAnalyzer.Analyze(Fixture("lame3100-v2.mp3"));
        var mp3 = r.Mp3!;
        var lame = mp3.Xing!.Lame!;

        Assert.Equal("MP3", r.Format);
        Assert.StartsWith("LAME", r.Encoder);
        Assert.Equal(GuessConfidence.High, r.Confidence);
        Assert.Equal("V2", lame.PresetName);
        Assert.True(lame.TagCrcOk);
        Assert.True(lame.MusicCrcOk);
        Assert.Equal(mp3.Frames, mp3.Xing.Frames);
        Assert.True(mp3.IsVbr);
        Assert.Empty(r.Problems);
        Assert.Equal(0, mp3.Layer3.ReservoirErrors);
    }

    [Fact]
    public void Crc_protected_frames_are_checked()
    {
        var r = AudioAnalyzer.Analyze(Fixture("lame3100-cbr128-crc.mp3"));
        var mp3 = r.Mp3!;
        Assert.Equal(mp3.Frames, mp3.CrcFrames);
        Assert.Equal(0, mp3.CrcErrors);
        Assert.NotNull(mp3.Xing?.Lame);
        Assert.Equal(128, mp3.BitrateHistogram.Single().Key);
    }

    [Fact]
    public void Damaged_side_info_fails_the_crc()
    {
        string path = Copy("lame3100-cbr128-crc.mp3", "damaged.mp3");
        byte[] b = File.ReadAllBytes(path);
        // frame 20: header at 20 * 417/418 bytes is not exact, so find it by walking headers
        int pos = 0;
        for (int i = 0; i < 20; i++)
        {
            Assert.True(MpegHeader.TryParse(b.AsSpan(pos), out var h));
            pos += h.FrameLength;
        }
        b[pos + 8] ^= 0xFF; // inside the side info
        File.WriteAllBytes(path, b);

        var r = AudioAnalyzer.Analyze(path);

        Assert.True(r.Mp3!.CrcErrors >= 1);
        Assert.Contains(r.Problems, p => p.Contains("CRC", StringComparison.Ordinal));
    }

    [Fact]
    public void Truncated_mp3_is_reported()
    {
        string path = Copy("lame3100-v2.mp3", "cut.mp3");
        byte[] b = File.ReadAllBytes(path);
        File.WriteAllBytes(path, b[..(b.Length - 300)]);

        var r = AudioAnalyzer.Analyze(path);

        Assert.True(r.Mp3!.BadLastFrame);
        Assert.False(r.Mp3.Xing!.Lame!.MusicCrcOk);
        Assert.Equal(QualityRating.Poor, r.Quality);
    }

    [Fact]
    public void Ffmpeg_mp3_is_identified()
    {
        var r = AudioAnalyzer.Analyze(Fixture("ffmpeg-cbr192.mp3"));
        Assert.StartsWith("FFmpeg (Lavc", r.Encoder);
        Assert.False(r.IsVbr);
        Assert.InRange(r.BitrateKbps, 191.5, 192.5);
    }

    [Fact]
    public void Ffmpeg_mpeg2_mono_tag_crc_is_accepted()
    {
        var r = AudioAnalyzer.Analyze(Fixture("ffmpeg-mono22k.mp3"));
        Assert.Equal(22050, r.SampleRate);
        Assert.True(r.Mp3!.Xing!.Lame!.TagCrcOk);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Tags_around_the_stream_are_skipped_and_read()
    {
        byte[] audio = File.ReadAllBytes(Fixture("lame3100-v2.mp3"));
        string path = Path.Combine(_tmp, "tagged.mp3");
        using (var f = File.Create(path))
        {
            f.Write(Id3v23("TIT2", "Hello"));
            f.Write(audio);
            f.Write(ApeV2("Artist", "Somebody"));
            var v1 = new byte[128];
            "TAG"u8.CopyTo(v1);
            Encoding.ASCII.GetBytes("Old Album").CopyTo(v1, 63);
            v1[127] = 17;
            f.Write(v1);
        }

        var r = AudioAnalyzer.Analyze(path);
        var plain = AudioAnalyzer.Analyze(Fixture("lame3100-v2.mp3"));

        Assert.Equal("Hello", r.Title);
        Assert.Equal("Somebody", r.Artist);
        Assert.Equal("Old Album", r.Album);
        Assert.Equal("Rock", r.Tags!.Fields["Genre"]);
        Assert.Equal("ID3v2.3, APEv2, ID3v1", r.Tags.Describe());
        Assert.Equal(plain.Mp3!.Frames, r.Mp3!.Frames);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Wrong_extension_is_reported()
    {
        string path = Copy("lame3100-v2.mp3", "actually-mp3.flac");
        var r = AudioAnalyzer.Analyze(path);
        Assert.Equal("MP3", r.Format);
        Assert.Contains(r.Problems, p => p.Contains("named .flac", StringComparison.Ordinal));
    }

    [Fact]
    public void Not_audio_gives_an_error()
    {
        string path = Path.Combine(_tmp, "junk.mp3");
        File.WriteAllBytes(path, Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
        var r = AudioAnalyzer.Analyze(path);
        Assert.NotNull(r.Error);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, 417, 128, 44100)]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x92, 0x64 }, 418, 128, 44100)]
    [InlineData(new byte[] { 0xFF, 0xF3, 0xC8, 0xC4 }, 576, 128, 16000)]
    public void Mpeg_header_frame_length(byte[] header, int length, int bitrate, int rate)
    {
        Assert.True(MpegHeader.TryParse(header, out var h));
        Assert.Equal(length, h.FrameLength);
        Assert.Equal(bitrate, h.BitrateKbps);
        Assert.Equal(rate, h.SampleRate);
    }

    private static byte[] Id3v23(string id, string text)
    {
        var frame = new List<byte>();
        frame.AddRange(Encoding.ASCII.GetBytes(id));
        byte[] body = [0, .. Encoding.Latin1.GetBytes(text)];
        frame.AddRange([0, 0, (byte)(body.Length >> 8), (byte)body.Length, 0, 0]);
        frame.AddRange(body);
        int size = frame.Count + 32; // with padding
        var tag = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, (byte)(size >> 7), (byte)(size & 0x7F) };
        tag.AddRange(frame);
        tag.AddRange(new byte[32]);
        return [.. tag];
    }

    private static byte[] ApeV2(string key, string value)
    {
        var items = new List<byte>();
        byte[] v = Encoding.UTF8.GetBytes(value);
        items.AddRange(BitConverter.GetBytes(v.Length));
        items.AddRange(BitConverter.GetBytes(0));
        items.AddRange(Encoding.ASCII.GetBytes(key));
        items.Add(0);
        items.AddRange(v);
        var footer = new List<byte>();
        footer.AddRange("APETAGEX"u8.ToArray());
        footer.AddRange(BitConverter.GetBytes(2000));
        footer.AddRange(BitConverter.GetBytes(items.Count + 32));
        footer.AddRange(BitConverter.GetBytes(1));
        footer.AddRange(BitConverter.GetBytes(0));
        footer.AddRange(new byte[8]);
        return [.. items, .. footer];
    }
}
