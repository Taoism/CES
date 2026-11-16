using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Tests;

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
