using Ces.Analysis;
using Ces.Compare;
using Ces.Model;

namespace Ces.Tests;

public class HdrMetadataTests
{
    [Fact]
    public void ParsesHdr10PlusWithRepeatedKeys()
    {
        const string json = """
            {"frames":[{"pts_time":"0.000","side_data_list":[{"side_data_type":"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)",
            "targeted_system_display_maximum_luminance":"400/1","maxscl":"4000/100000","maxscl":"12000/100000","maxscl":"3000/100000",
            "average_maxrgb":"1000/100000"}]},
            {"pts_time":"1.000","side_data_list":[{"side_data_type":"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)","maxscl":"500/100000","average_maxrgb":"100/100000"}]}]}
            """;
        var d = HdrMetadata.Parse(json, 0.5)!;
        Assert.Equal("HDR10+", d.Kind);
        Assert.Equal(1200, d.MaxNits, 3);
        Assert.Equal(100, d.AvgNits, 3);
        Assert.Equal(50, HdrMetadata.Parse(json, 1.2)!.MaxNits, 3);
    }

    [Fact]
    public void ParsesDolbyVisionLevel1()
    {
        const string json = """
            {"frames":[{"side_data_list":[{"side_data_type":"Dolby Vision Metadata","source_max_pq":3079,
            "ext_blocks":[{"level":1,"min_pq":0,"max_pq":2081,"avg_pq":1000}]}]}]}
            """;
        var d = HdrMetadata.Parse(json)!;
        Assert.Equal("Dolby Vision", d.Kind);
        Assert.InRange(d.MaxNits, 95, 105); // PQ 2081/4095 ≈ 100 nits
    }
}

public class GradeTests
{
    private static RgbFrame Frame(int w, int h, Func<double, double, (double R, double G, double B)> f)
    {
        var d = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = f(x / (double)w, y / (double)h);
                var i = (y * w + x) * 3;
                d[i] = (byte)Math.Clamp(r, 0, 255);
                d[i + 1] = (byte)Math.Clamp(g, 0, 255);
                d[i + 2] = (byte)Math.Clamp(b, 0, 255);
            }
        }
        return new RgbFrame { Data = d, Width = w, Height = h };
    }

    // a detailed, non-repeating picture: random soft blobs
    private static readonly (double X, double Y, double R, double A)[] Blobs = MakeBlobs();

    private static (double X, double Y, double R, double A)[] MakeBlobs()
    {
        var rnd = new Random(42);
        return Enumerable.Range(0, 160).Select(_ => (rnd.NextDouble(), rnd.NextDouble(), 0.01 + rnd.NextDouble() * 0.05, rnd.NextDouble() * 160 - 80)).ToArray();
    }

    private static (double, double, double) Picture(double u, double v)
    {
        var t = 120.0;
        foreach (var (x, y, r, a) in Blobs)
        {
            var d2 = ((u - x) * (u - x) + (v - y) * (v - y) * 0.316) / (r * r);
            if (d2 < 9)
                t += a * Math.Exp(-d2);
        }
        return (t, t * 0.9 + 10, t * 0.7 + 30);
    }

    [Fact]
    public void FindsZoomAndColourCast()
    {
        const int w = 480, h = 270;
        var a = Frame(w, h, Picture);
        // remaster: 10 % zoomed in (shows the centre 1/1.1), warmer
        var b = Frame(w, h, (u, v) =>
        {
            var (r, g, bl) = Picture(0.5 + (u - 0.5) / 1.1, 0.5 + (v - 0.5) / 1.1);
            return (r + 12, g + 4, bl - 14);
        });
        var gf = GradeCompare.CompareFrames(a, b, 0);
        Assert.InRange(1 / gf.Scale, 1.08, 1.12);
        Assert.True(gf.DeltaB > 4, $"b* {gf.DeltaB}");
    }

    [Fact]
    public void SameFrameMatchesExactly()
    {
        var a = Frame(320, 180, Picture);
        var gf = GradeCompare.CompareFrames(a, a, 0);
        Assert.Equal(1.0, gf.Scale, 2);
        Assert.InRange(gf.ShiftX, -0.5, 0.5);
        Assert.InRange(gf.SaturationRatio, 0.99, 1.01);
    }
}

public class SubtitleTimingTests
{
    private static CompareItem Item(string name, double offset = 0) => new()
    {
        Source = new MediaSource { Path = name, Kind = SourceKind.File, DisplayName = name },
        Title = new MediaTitle { Name = name, Duration = 7200 },
        Offset = offset,
    };

    [Fact]
    public void MeasuresShiftAndDrift()
    {
        var rf = Item("ref");
        var other = Item("other", 5);
        var refEv = Enumerable.Range(0, 200).Select(k => new SubtitleEvent(30 + k * 30, 2)).ToList();
        // same lines, 5 s later in the file, 80 ms late, drifting 0.4 s over the two hours
        var ev = refEv.Select(e => new SubtitleEvent(e.Start + 5 + 0.08 + e.Start / 7200 * 0.4, 2)).ToList();
        var r = SubtitleTiming.Compare(rf, refEv, other, 0, ev);
        Assert.Equal(200, r.Matched);
        // 80 ms + drift at mid-film (≈0.17 s)
        Assert.InRange(r.MedianDelta, 0.22, 0.28);
        Assert.InRange(r.DriftPerHour, 0.18, 0.22);
    }
}

public class GradeOverTimeTests
{
    private static GradeReport Report(params (double Mid, double Sat, double B)[] frames)
    {
        var item = new CompareItem
        {
            Source = new MediaSource { Path = "x.mkv", Kind = SourceKind.File },
            Title = new MediaTitle { Name = "x" },
        };
        var r = new GradeReport { Item = item };
        for (var i = 0; i < frames.Length; i++)
        {
            var (mid, sat, b) = frames[i];
            r.Frames.Add(new GradeFrame
            {
                Time = i * 10,
                Match = 0.6,
                RefL = [5, 15, 30, 50, 70, 85, 95],
                OtherL = [5, 15, 30, 50 + mid, 70, 85, 95],
                SaturationRatio = sat,
                ContrastRatio = 1,
                DeltaA = 0,
                DeltaB = b,
            });
        }
        return r;
    }

    [Fact]
    public void FlagsShotByShotGrade()
    {
        var r = Report((0, 1, 0), (0, 1, 0), (9, 1.3, 10), (9, 1.3, 11), (-9, 0.8, -5), (-9, 0.8, -5));
        GradeCompare.BuildFindings(r);
        var f = Assert.Single(r.Findings, x => x.Area == "Over time");
        Assert.Equal(1, f.Level);
        Assert.DoesNotContain(r.Findings, x => x.Text == "Same saturation.");
    }

    [Fact]
    public void GlobalBoostIsSteady()
    {
        // one saturation boost reads as +19 … +53 % between pale and colourful shots: still one global grade
        var r = Report((5, 1.19, 6), (5, 1.3, 6), (6, 1.53, 7), (5, 1.25, 6), (5, 1.4, 6), (6, 1.35, 7));
        GradeCompare.BuildFindings(r);
        Assert.Equal(0, Assert.Single(r.Findings, x => x.Area == "Over time").Level);
        Assert.Contains(r.Findings, x => x.Area == "Grade" && x.Text.Contains("consistent", StringComparison.Ordinal));
    }
}

public class TemporalTests
{
    private const int W = TemporalCompare.W;
    private const int H = TemporalCompare.H;

    private static float[] Texture(double dx, double dy)
    {
        var p = new float[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                double u = x + dx, v = y + dy;
                p[y * W + x] = (float)(128 + 40 * Math.Sin(u * 0.21) * Math.Cos(v * 0.17) + 30 * Math.Sin((u + v) * 0.09) + 20 * Math.Cos(u * 0.05 - v * 0.11));
            }
        }
        return p;
    }

    private static List<GrayFrame> Frames(int n, Func<int, (double X, double Y)> shift) =>
        Enumerable.Range(0, n).Select(i =>
        {
            var (x, y) = shift(i);
            var px = Texture(x, y);
            return new GrayFrame(i / 24.0, px, px.Average());
        }).ToList();

    [Fact]
    public void WeaveIsJitterButAPanIsNot()
    {
        var pan = TemporalCompare.Jitter(Frames(24, i => (i * 2.0, 0)));
        var weave = TemporalCompare.Jitter(Frames(24, i => (i * 2.0 + (i % 2 == 0 ? 1.5 : -1.5), i % 3 - 1.0)));
        Assert.InRange(pan, 0, 0.3);
        Assert.True(weave > 1, $"weave {weave}");
    }

    [Fact]
    public void CountsTransientSpecks()
    {
        var clean = Frames(10, _ => (0, 0));
        Assert.Equal(0, TemporalCompare.Specks(clean));
        var rnd = new Random(1);
        var dirty = clean.Select((f, i) =>
        {
            var px = (float[])f.Pixels.Clone();
            if (i % 2 == 1)
            {
                for (var k = 0; k < 40; k++)
                    px[rnd.Next(px.Length)] = 250;
            }
            return f with { Pixels = px };
        }).ToList();
        Assert.InRange(TemporalCompare.Specks(dirty), 15, 45);
    }
}
