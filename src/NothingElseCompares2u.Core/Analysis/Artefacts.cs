namespace NothingElseCompares2u.Analysis;

/// <summary>Per-frame artefact measures (all on an 8-bit scale so sources of different bit depth compare).</summary>
public sealed class ArtefactStats
{
    /// <summary>Noise / grain level (standard deviation, 8-bit units) in textured-but-not-edge areas.</summary>
    public double Noise { get; set; } = double.NaN;

    /// <summary>Mean run length (pixels) of identical values in smooth, non-black areas: long runs = banding steps.</summary>
    public double BandRun { get; set; } = double.NaN;

    /// <summary>Share of the picture that is smooth gradient (where banding would show).</summary>
    public double SmoothShare { get; set; } = double.NaN;

    /// <summary>Overshoot + undershoot beside strong edges relative to edge height: sharpening halos.</summary>
    public double Halo { get; set; } = double.NaN;

    /// <summary>Chroma high-frequency energy minus luma's (dB), horizontal/vertical; very low = upsampled chroma.</summary>
    public double ChromaHfH { get; set; } = double.NaN;
    public double ChromaHfV { get; set; } = double.NaN;
}

public static class Artefacts
{
    public static ArtefactStats Measure(LumaFrame f, CropBox? crop = null)
    {
        var c = crop ?? new CropBox(0, 0, 0, 0);
        var st = new ArtefactStats
        {
            Noise = Noise(f, c),
            Halo = Halo(f, c),
        };
        (st.BandRun, st.SmoothShare) = Banding(f, c);
        return st;
    }

    /// <summary>
    /// Immerkær's fast noise estimate (Laplacian-difference kernel), skipping strong edges so detail is not
    /// mistaken for grain.
    /// </summary>
    public static double Noise(LumaFrame f, CropBox c)
    {
        var scale = 255.0 / f.MaxCode;
        var w = f.Width;
        double sum = 0;
        long n = 0;
        var d = f.Data;
        var edgeLimit = 24 / scale; // gradient threshold in native codes
        for (var y = c.Top + 1; y < f.Height - c.Bottom - 1; y += 2)
        {
            for (var x = c.Left + 1; x < w - c.Right - 1; x += 2)
            {
                var i = y * w + x;
                var gx = Math.Abs(d[i + 1] - d[i - 1]);
                var gy = Math.Abs(d[i + w] - d[i - w]);
                if (gx + gy > edgeLimit)
                    continue;
                // [1 -2 1; -2 4 -2; 1 -2 1]
                double v = d[i - w - 1] - 2 * d[i - w] + d[i - w + 1]
                           - 2 * d[i - 1] + 4 * d[i] - 2 * d[i + 1]
                           + d[i + w - 1] - 2 * d[i + w] + d[i + w + 1];
                sum += Math.Abs(v);
                n++;
            }
        }
        return n > 1000 ? Math.Sqrt(Math.PI / 2) * sum / (6.0 * n) * scale : double.NaN;
    }

    /// <summary>Run lengths of equal codes in smooth, mid-tone areas (8-bit steps; deep blacks and clipped whites skipped).</summary>
    public static (double MeanRun, double SmoothShare) Banding(LumaFrame f, CropBox c)
    {
        var shift = f.BitDepth - 8;
        var lo = (16 + 6) << shift;
        var hi = (235 - 6) << shift;
        var w = f.Width;
        var d = f.Data;
        long runs = 0, runPixels = 0, smooth = 0, total = 0;
        for (var y = c.Top + 2; y < f.Height - c.Bottom - 2; y += 3)
        {
            var row = y * w;
            var x = c.Left + 2;
            var end = w - c.Right - 2;
            while (x < end)
            {
                total++;
                var v = d[row + x];
                // smooth: neighbours within 2 (8-bit) codes, not near black/white
                var limit = 2 << shift;
                var isSmooth = v > lo && v < hi &&
                               Math.Abs(d[row + x - 2] - v) <= limit && Math.Abs(d[row + x + 2] - v) <= limit &&
                               Math.Abs(d[row - 2 * w + x] - v) <= limit && Math.Abs(d[row + 2 * w + x] - v) <= limit;
                if (!isSmooth)
                {
                    x++;
                    continue;
                }
                smooth++;
                // compare on the 8-bit grid: a real 10-bit gradient changes the low bits constantly
                var q = v;
                var len = 1;
                while (x + len < end && d[row + x + len] == q)
                    len++;
                runs++;
                runPixels += len;
                total += len - 1;
                x += len;
            }
        }
        return (runs > 200 ? (double)runPixels / runs : double.NaN, total > 0 ? (double)smooth / total : 0);
    }

    /// <summary>Mean relative overshoot/undershoot around strong horizontal edges.</summary>
    public static double Halo(LumaFrame f, CropBox c)
    {
        var scale = 255.0 / f.MaxCode;
        var w = f.Width;
        var d = f.Data;
        double sum = 0;
        var n = 0;
        for (var y = c.Top + 1; y < f.Height - c.Bottom - 1; y += 2)
        {
            var row = y * w;
            for (var x = c.Left + 10; x < w - c.Right - 10; x++)
            {
                var i = row + x;
                double step = (d[i + 1] - d[i - 1]) * scale;
                if (Math.Abs(step) < 40)
                    continue;
                // only clean step edges: both plateaus flat-ish
                double hiPlateau = 0, loPlateau = 0;
                var dir = Math.Sign(step);
                for (var k = 5; k <= 8; k++)
                {
                    hiPlateau += d[i + dir * k];
                    loPlateau += d[i - dir * k];
                }
                hiPlateau /= 4;
                loPlateau /= 4;
                var height = Math.Abs(hiPlateau - loPlateau) * scale;
                if (height < 30)
                    continue;
                double peak = dir > 0 ? Math.Max(d[i + 1], Math.Max(d[i + 2], d[i + 3])) : Math.Min(d[i - 1], Math.Min(d[i - 2], d[i - 3]));
                double trough = dir > 0 ? Math.Min(d[i - 1], Math.Min(d[i - 2], d[i - 3])) : Math.Max(d[i + 1], Math.Max(d[i + 2], d[i + 3]));
                var over = Math.Max(0, dir * (peak - hiPlateau)) * scale;
                var under = Math.Max(0, dir * (loPlateau - trough)) * scale;
                sum += (over + under) / height;
                n++;
                x += 8; // one sample per edge
            }
        }
        return n > 50 ? sum / n : double.NaN;
    }

    /// <summary>
    /// High-frequency energy of a chroma plane relative to luma (dB) along each axis. Real chroma detail is somewhat below
    /// luma; chroma that was upsampled from a lower resolution has almost nothing in its top half of frequencies.
    /// </summary>
    public static (double H, double V) ChromaDetail(LumaFrame luma, LumaFrame chroma)
    {
        var ls = FrameMetrics.Spectrum(luma);
        var cs = FrameMetrics.Spectrum(chroma);
        // upsampled chroma has a cliff at the old Nyquist (0.25 cycles/pixel): compare just above with just below it,
        // relative to how luma behaves across the same frequencies
        static double Cliff(double[] db) => db.Length == 0 ? double.NaN : Band(db, 0.30, 0.45) - Band(db, 0.12, 0.22);
        return (Cliff(cs.Horizontal) - Cliff(ls.Horizontal), Cliff(cs.Vertical) - Cliff(ls.Vertical));
    }

    private static double Band(double[] db, double f0, double f1)
    {
        var a = (int)(f0 / 0.5 * db.Length);
        var b = Math.Max(a + 1, (int)(f1 / 0.5 * db.Length));
        double s = 0;
        for (var i = a; i < Math.Min(b, db.Length); i++)
            s += Math.Pow(10, db[i] / 10);
        return 10 * Math.Log10(Math.Max(1e-30, s / (b - a)));
    }
}
