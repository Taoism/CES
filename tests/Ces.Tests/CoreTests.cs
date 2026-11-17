using Ces.Analysis;
using Ces.Disc;
using Ces.Dsp;
using Ces.Model;
using Ces.Probe;
using Ces.Tools;

namespace Ces.Tests;

public class FormatTests
{
    [Theory]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData("62:03", 3723)]
    [InlineData("3723.5", 3723.5)]
    [InlineData("3723.5s", 3723.5)]
    [InlineData("-5.042", -5.042)]
    public void ParsesTimes(string text, double expected)
    {
        Assert.True(Format.TryParseTime(text, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    public void RejectsBadTimes(string text) => Assert.False(Format.TryParseTime(text, out _));

    [Fact]
    public void FormatsTime() => Assert.Equal("1:02:03.500", Format.Time(3723.5));
}

public class DiscTests
{
    [Fact]
    public void DvdBcdTime()
    {
        // 01:23:45, 12 frames at 25 fps (frame-rate bits 01)
        byte[] d = [0x01, 0x23, 0x45, 0x40 | 0x12];
        Assert.Equal(1 * 3600 + 23 * 60 + 45 + 12 / 25.0, DvdReader.BcdTime(d, 0), 6);
    }

    [Fact]
    public void ParsesSyntheticMpls()
    {
        var mpls = BuildMpls(("00055", 45000u * 10, 45000u * 70), ("00056", 45000u * 5, 45000u * 35), marks: [(0, 45000u * 10), (0, 45000u * 40), (1, 45000u * 20)]);
        var m = Mpls.Parse(mpls);
        Assert.Equal(2, m.Items.Count);
        Assert.Equal("00055", m.Items[0].Clip);
        Assert.Equal(60, m.Items[0].Duration, 6);
        Assert.Equal(90, m.Duration, 6);
        Assert.Equal(3, m.Marks.Count);
        Assert.Contains(m.Streams, s => s.Kind == "audio" && s.Pid == 0x1100 && s.Language == "eng");
    }

    /// <summary>Builds a minimal MPLS: two play items with one video + one audio stream, entry marks.</summary>
    private static byte[] BuildMpls((string Clip, uint In, uint Out) a, (string Clip, uint In, uint Out) b, (int Item, uint Time)[] marks)
    {
        var body = new List<byte>();
        void U8(int v) => body.Add((byte)v);
        void U16(int v) { body.Add((byte)(v >> 8)); body.Add((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        void Str(string s) => body.AddRange(System.Text.Encoding.ASCII.GetBytes(s));

        Str("MPLS0200");
        U32(40); // playlist start
        U32(0); // mark start (patched)
        U32(0);
        while (body.Count < 40)
            U8(0);
        U32(0); // length (unused by the parser)
        U16(0);
        U16(2);
        U16(0);
        foreach (var (clip, inT, outT) in new[] { a, b })
        {
            var item = new List<byte>();
            var save = body;
            body = item;
            Str(clip);
            Str("M2TS");
            U16(0x0001); // connection condition 1, no multi-angle
            U8(0);
            U32(inT);
            U32(outT);
            for (var i = 0; i < 8; i++)
                U8(0);
            U8(0);
            U8(0);
            U16(0);
            // STN table: 1 video, 1 audio
            var stn = new List<byte>();
            body = stn;
            U16(0);
            U8(1); U8(1); U8(0); U8(0); U8(0); U8(0); U8(0);
            for (var i = 0; i < 5; i++)
                U8(0);
            U8(3); U8(1); U16(0x1011); // video entry: type 1, PID
            U8(2); U8(0x1B); U8(0x61); // video attrs
            U8(3); U8(1); U16(0x1100); // audio entry
            U8(5); U8(0x83); U8(0x61); Str("eng"); // audio attrs
            body = item;
            U16(stn.Count);
            item.AddRange(stn);
            body = save;
            U16(item.Count);
            body.AddRange(item);
        }
        var markStart = body.Count;
        U32(0);
        U16(marks.Length);
        foreach (var (it, t) in marks)
        {
            U8(0); U8(1); U16(it); U32(t); U16(0xFFFF); U32(0);
        }
        var arr = body.ToArray();
        arr[12] = (byte)(markStart >> 24);
        arr[13] = (byte)(markStart >> 16);
        arr[14] = (byte)(markStart >> 8);
        arr[15] = (byte)markStart;
        return arr;
    }
}

public class DspTests
{
    [Fact]
    public void FftFindsSine()
    {
        const int n = 1024;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
            re[i] = Math.Sin(2 * Math.PI * 64 * i / n);
        Fft.Transform(re, im);
        var mags = Enumerable.Range(0, n / 2).Select(k => Math.Sqrt(re[k] * re[k] + im[k] * im[k])).ToList();
        Assert.Equal(64, mags.IndexOf(mags.Max()));
    }

    [Fact]
    public void FindsBrickWallCutoff()
    {
        // 44.1 kHz spectrum: music-like slope up to 16 kHz, then the encoder's lowpass
        const int bins = 4097;
        const int rate = 44100;
        var db = new double[bins];
        var rnd = new Random(1);
        for (var k = 0; k < bins; k++)
        {
            var hz = k * (rate / 2.0) / (bins - 1);
            db[k] = hz < 16000 ? -40 - hz / 1000.0 + rnd.NextDouble() * 3 : -125 + rnd.NextDouble() * 2;
        }
        var (cut, steep) = AudioAnalyzer.FindCutoff(db, rate);
        Assert.InRange(cut, 15500, 16500);
        Assert.True(steep > 25);
    }

    [Fact]
    public void FullBandHasNoCutoff()
    {
        const int bins = 4097;
        var db = Enumerable.Range(0, bins).Select(k => -40 - k / 300.0).ToArray();
        var (cut, _) = AudioAnalyzer.FindCutoff(db, 48000);
        Assert.True(cut > 23000);
    }

    [Fact]
    public void CorrelationFindsShift()
    {
        var rnd = new Random(7);
        var b = Enumerable.Range(0, 40000).Select(_ => (float)(rnd.NextDouble() - 0.5)).ToArray();
        var a = b.Skip(12345).Take(8000).ToArray();
        var (lag, conf) = SyncDetector.Correlate(a, b);
        Assert.Equal(12345, lag);
        Assert.True(conf > 20);
    }
}

public class FrameTests
{
    private static LumaFrame Frame(int w, int h, int depth, Func<int, int, int> f)
    {
        var d = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
                d[y * w + x] = (ushort)f(x, y);
        }
        return new LumaFrame { Data = d, Width = w, Height = h, BitDepth = depth };
    }

    [Fact]
    public void DetectsPadded8BitIn10Bit()
    {
        var rnd = new Random(3);
        var f = Frame(320, 180, 10, (_, _) => rnd.Next(16, 236) << 2);
        var st = FrameMetrics.Levels(f);
        Assert.Equal(8, st.EffectiveBits);
        Assert.True(st.PaddedShare > 0.99);
    }

    [Fact]
    public void Real10BitStays10Bit()
    {
        var rnd = new Random(4);
        var f = Frame(320, 180, 10, (_, _) => rnd.Next(64, 941));
        Assert.Equal(10, FrameMetrics.Levels(f).EffectiveBits);
    }

    [Fact]
    public void DetectsLetterbox()
    {
        var rnd = new Random(5);
        var f = Frame(400, 300, 8, (_, y) => y < 40 || y >= 260 ? 16 : rnd.Next(30, 230));
        var c = FrameMetrics.DetectCrop(f);
        Assert.Equal(40, c.Top);
        Assert.Equal(40, c.Bottom);
        Assert.Equal(0, c.Left);
    }

    [Fact]
    public void FindsUpscaleNativeHeight()
    {
        // detailed 540-line picture, upscaled to 1080 with bicubic: the descale test must point at 540
        var rnd = new Random(11);
        const int w = 256;
        const int h = 540;
        var small = new float[w * h];
        for (var i = 0; i < small.Length; i++)
            small[i] = (float)(0.2 + 0.6 * rnd.NextDouble());
        var big = Resampler.ResizeVertical(small, w, h, 1080, new Resampler.Plan(h, 1080, Kernel.Bicubic));
        var f = Frame(w, 1080, 8, (x, y) => (int)Math.Clamp(Math.Round(big[y * w + x] * 255), 0, 255));
        var r = FrameMetrics.NativeResolution(f);
        Assert.Equal(540, r.NativeHeight);
    }

    [Fact]
    public void NativeNoiseHasNoNativeHeight()
    {
        var rnd = new Random(12);
        var f = Frame(256, 1080, 8, (_, _) => rnd.Next(40, 220));
        Assert.Null(FrameMetrics.NativeResolution(f).NativeHeight);
    }

    [Fact]
    public void PqCodeToNits()
    {
        // 10-bit limited range: 940 = 10000 nits, 509 ≈ 100 nits (PQ 0.508)
        Assert.Equal(10000, FrameMetrics.PqToNits(940, 10), 0);
        Assert.InRange(FrameMetrics.PqToNits(509, 10), 95, 105);
    }
}

public class ParserTests
{
    [Fact]
    public void ParsesPsnrStats()
    {
        const string log = "n:1 mse_avg:2.10 mse_y:2.5 mse_u:1 mse_v:1 psnr_avg:44.9 psnr_y:44.15 psnr_u:48 psnr_v:48\nn:2 mse_avg:1 mse_y:1 psnr_avg:inf psnr_y:inf\n";
        Assert.Equal([44.15, 100], QualityMetrics.ParseKeyed(log, "psnr_y"));
    }

    [Fact]
    public void ParsesSsimStats()
    {
        const string log = "n:1 Y:0.986321 U:0.99 V:0.99 All:0.988 (19.2)\n";
        Assert.Equal(0.986321, QualityMetrics.ParseKeyed(log, "Y").Single(), 6);
    }

    [Fact]
    public void ParsesLoudnessSummary()
    {
        const string err = "[Parsed_ebur128_1 @ 0x1]   Summary:\n\n  Integrated loudness:\n    I:         -18.1 LUFS\n    Threshold: -28.4 LUFS\n\n  Loudness range:\n    LRA:         4.3 LU\n\n  True peak:\n    Peak:       -0.7 dBFS\n";
        var (i, lra, tp) = AudioAnalyzer.ParseLoudness(err);
        Assert.Equal(-18.1, i, 3);
        Assert.Equal(4.3, lra, 3);
        Assert.Equal(-0.7, tp, 3);
    }

    [Fact]
    public void ParsesAstats()
    {
        const string err = """
            [Parsed_astats_0 @ 0x1] Channel: 1
            [Parsed_astats_0 @ 0x1] Peak level dB: -3.0
            [Parsed_astats_0 @ 0x1] RMS level dB: -23.4
            [Parsed_astats_0 @ 0x1] Bit depth: 23/24/24/24
            [Parsed_astats_0 @ 0x1] Overall
            [Parsed_astats_0 @ 0x1] Peak level dB: -0.7
            [Parsed_astats_0 @ 0x1] Bit depth: 23/24/24/24
            """;
        var (overall, ch) = AudioAnalyzer.ParseAstats(err);
        Assert.Single(ch);
        Assert.Equal(-3.0, ch[0].PeakDb);
        Assert.Equal("23 of 24", ch[0].BitsUsedText);
        Assert.Equal(-0.7, overall.PeakDb);
    }

    [Fact]
    public void PixFmtDepths()
    {
        Assert.Equal((10, "4:2:0"), MediaProber.PixFmtInfo("yuv420p10le", 0));
        Assert.Equal((8, "4:2:2"), MediaProber.PixFmtInfo("yuv422p", 0));
        Assert.Equal("23.976", MediaProber.FrameRateText(24000 / 1001.0));
    }
}

public class TimelineTests
{
    [Fact]
    public void EvenPointsSkipEdges()
    {
        var p = InputBuilder.EvenPoints(1000, 5);
        Assert.Equal(5, p.Count);
        Assert.Equal(50, p[0], 6);
        Assert.Equal(920, p[^1], 6);
    }

    [Fact]
    public void SegmentLookup()
    {
        var t = new MediaTitle { Name = "x", Duration = 30 };
        t.Segments.Add(new TitleSegment { Url = "a", TitleStart = 0, Duration = 10 });
        t.Segments.Add(new TitleSegment { Url = "b", TitleStart = 10, Duration = 20 });
        Assert.Equal("a", InputBuilder.SegmentAt(t, 9.99).Url);
        Assert.Equal("b", InputBuilder.SegmentAt(t, 10).Url);
    }

    [Fact]
    public void TsInputsGetPreroll()
    {
        var t = new MediaTitle { Name = "x", Duration = 100 };
        t.Segments.Add(new TitleSegment { Url = "movie.m2ts", Duration = 100 });
        var spec = InputBuilder.Input(t, 50, 2, Path.GetTempPath());
        Assert.Equal(InputBuilder.Preroll, spec.Skip);
        Assert.Contains("47", spec.Args);
        var mkv = new MediaTitle { Name = "y", Duration = 100 };
        mkv.Segments.Add(new TitleSegment { Url = "movie.mkv", Duration = 100 });
        Assert.Equal(0, InputBuilder.Input(mkv, 50, 2, Path.GetTempPath()).Skip);
    }
}
