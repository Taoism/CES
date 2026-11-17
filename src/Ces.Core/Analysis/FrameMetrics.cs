using Ces.Dsp;

namespace Ces.Analysis;

public sealed class LevelStats
{
    public int Min { get; set; }
    public int Max { get; set; }
    public double Mean { get; set; }
    public int DistinctCodes { get; set; }

    /// <summary>Share of samples whose low (BitDepth-8) bits are all zero: ~1.0 means 8-bit data padded to 10/12 bits.</summary>
    public double PaddedShare { get; set; }

    /// <summary>Share of samples that sit exactly on a code an 8-bit value would map to (x·1023/255 rounding).</summary>
    public double EightBitGridShare { get; set; }

    public double BelowBlackShare { get; set; }
    public double AboveWhiteShare { get; set; }

    /// <summary>Estimated effective bit depth of the luma values.</summary>
    public int EffectiveBits { get; set; }
}

public sealed class SpectrumStats
{
    /// <summary>Average power (dB) per normalised frequency bin 0…0.5 cycles/pixel.</summary>
    public double[] Horizontal { get; set; } = [];
    public double[] Vertical { get; set; } = [];

    /// <summary>Energy in the top quarter of frequencies relative to the low-mid band (dB). Lower = softer / upscaled.</summary>
    public double HfRatioH { get; set; }
    public double HfRatioV { get; set; }

    /// <summary>Estimated resolution where detail stops (pixels), NaN when there is no clear cut.</summary>
    public double CutoffWidth { get; set; } = double.NaN;
    public double CutoffHeight { get; set; } = double.NaN;
}

public sealed record NativeResPoint(int Height, double Error, Kernel Kernel);

public sealed class NativeResResult
{
    public List<NativeResPoint> Curve { get; } = [];
    public int? NativeHeight { get; set; }
    public Kernel? Kernel { get; set; }

    /// <summary>How far the best dip is below its neighbours (0…1, higher = clearer).</summary>
    public double Confidence { get; set; }
}

public sealed record CropBox(int Left, int Top, int Right, int Bottom)
{
    public int Width(int w) => w - Left - Right;
    public int Height(int h) => h - Top - Bottom;
}

/// <summary>Pure computations on a luma plane.</summary>
public static class FrameMetrics
{
    public static LevelStats Levels(LumaFrame f)
    {
        var hist = new long[f.MaxCode + 1];
        foreach (var v in f.Data)
            hist[Math.Min(v, f.MaxCode)]++;
        var st = new LevelStats();
        long n = f.Data.Length;
        st.Min = Array.FindIndex(hist, c => c > 0);
        st.Max = Array.FindLastIndex(hist, c => c > 0);
        double sum = 0;
        for (var i = 0; i < hist.Length; i++)
            sum += (double)i * hist[i];
        st.Mean = sum / n;
        st.DistinctCodes = hist.Count(c => c > 0);
        var shift = f.BitDepth - 8;
        var black = 16 << shift;
        var white = 235 << shift;
        long below = 0, above = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            if (i < black)
                below += hist[i];
            else if (i > white)
                above += hist[i];
        }
        st.BelowBlackShare = (double)below / n;
        st.AboveWhiteShare = (double)above / n;

        if (shift > 0)
        {
            var mask = (1 << shift) - 1;
            long padded = 0, grid = 0;
            var gridSet = new bool[hist.Length];
            for (var k = 0; k <= 255; k++)
            {
                gridSet[Math.Min(f.MaxCode, (int)Math.Round(k * (double)f.MaxCode / 255))] = true;
                gridSet[Math.Min(f.MaxCode, k << shift)] = true;
            }
            for (var i = 0; i < hist.Length; i++)
            {
                if ((i & mask) == 0)
                    padded += hist[i];
                if (gridSet[i])
                    grid += hist[i];
            }
            st.PaddedShare = (double)padded / n;
            st.EightBitGridShare = (double)grid / n;
            // codes used inside the occupied range: real 10-bit content fills far more than 256 levels
            var span = Math.Max(1, st.Max - st.Min + 1);
            var fill = (double)st.DistinctCodes / span;
            st.EffectiveBits = st.PaddedShare > 0.97 || st.EightBitGridShare > 0.97 || (fill < 0.30 && st.DistinctCodes <= 256)
                ? 8
                : f.BitDepth;
        }
        else
        {
            st.PaddedShare = double.NaN;
            st.EightBitGridShare = double.NaN;
            st.EffectiveBits = 8;
        }
        return st;
    }

    /// <summary>Variance of the Laplacian on an 8-bit scale: a simple sharpness / detail number.</summary>
    public static double Sharpness(LumaFrame f, CropBox? crop = null)
    {
        var c = crop ?? new CropBox(0, 0, 0, 0);
        var scale = 255.0 / f.MaxCode;
        double sum = 0, sum2 = 0;
        long n = 0;
        var w = f.Width;
        for (var y = c.Top + 1; y < f.Height - c.Bottom - 1; y++)
        {
            var row = y * w;
            for (var x = c.Left + 1; x < w - c.Right - 1; x++)
            {
                var i = row + x;
                double lap = 4.0 * f.Data[i] - f.Data[i - 1] - f.Data[i + 1] - f.Data[i - w] - f.Data[i + w];
                lap *= scale;
                sum += lap;
                sum2 += lap * lap;
                n++;
            }
        }
        if (n == 0)
            return 0;
        var mean = sum / n;
        return sum2 / n - mean * mean;
    }

    /// <summary>Black borders: rows/columns from each edge whose 98th percentile is at or below near-black.</summary>
    public static CropBox DetectCrop(LumaFrame f)
    {
        var shift = f.BitDepth - 8;
        var limit = (16 + 12) << shift;
        bool RowBlack(int y)
        {
            var bright = 0;
            for (var x = 0; x < f.Width; x++)
            {
                if (f.Data[y * f.Width + x] > limit)
                    bright++;
            }
            return bright < f.Width / 50;
        }
        bool ColBlack(int x)
        {
            var bright = 0;
            for (var y = 0; y < f.Height; y++)
            {
                if (f.Data[y * f.Width + x] > limit)
                    bright++;
            }
            return bright < f.Height / 50;
        }
        int top = 0, bottom = 0, left = 0, right = 0;
        while (top < f.Height / 2 && RowBlack(top))
            top++;
        while (bottom < f.Height / 2 && RowBlack(f.Height - 1 - bottom))
            bottom++;
        while (left < f.Width / 2 && ColBlack(left))
            left++;
        while (right < f.Width / 2 && ColBlack(f.Width - 1 - right))
            right++;
        // all black frame: no crop
        if (top >= f.Height / 2 || left >= f.Width / 2)
            return new CropBox(0, 0, 0, 0);
        return new CropBox(left, top, right, bottom);
    }

    public static SpectrumStats Spectrum(LumaFrame f, CropBox? crop = null)
    {
        var c = crop ?? new CropBox(0, 0, 0, 0);
        var w = c.Width(f.Width);
        var h = c.Height(f.Height);
        var st = new SpectrumStats();
        if (w < 64 || h < 64)
            return st;
        st.Horizontal = AxisSpectrum(f, c, w, h, horizontal: true);
        st.Vertical = AxisSpectrum(f, c, w, h, horizontal: false);
        st.HfRatioH = HfRatio(st.Horizontal);
        st.HfRatioV = HfRatio(st.Vertical);
        st.CutoffWidth = Cutoff(st.Horizontal) is var cw && !double.IsNaN(cw) ? cw * 2 * w : double.NaN;
        st.CutoffHeight = Cutoff(st.Vertical) is var ch && !double.IsNaN(ch) ? ch * 2 * h : double.NaN;
        return st;
    }

    private static double[] AxisSpectrum(LumaFrame f, CropBox c, int w, int h, bool horizontal)
    {
        var len = horizontal ? w : h;
        var lines = horizontal ? h : w;
        var n = Fft.NextPow2(len);
        var win = Fft.Hann(len);
        var acc = new double[n / 2 + 1];
        var step = Math.Max(1, lines / 256);
        var lockObj = new object();
        Parallel.For(0, (lines + step - 1) / step, () => new double[n / 2 + 1], (li, _, local) =>
        {
            var line = li * step;
            var re = new double[n];
            var im = new double[n];
            double mean = 0;
            for (var i = 0; i < len; i++)
            {
                var v = horizontal ? f.Data[(c.Top + line) * f.Width + c.Left + i] : f.Data[(c.Top + i) * f.Width + c.Left + line];
                re[i] = v;
                mean += v;
            }
            mean /= len;
            for (var i = 0; i < len; i++)
                re[i] = (re[i] - mean) * win[i] / f.MaxCode;
            Fft.Transform(re, im);
            for (var k = 0; k <= n / 2; k++)
                local[k] += re[k] * re[k] + im[k] * im[k];
            return local;
        }, local =>
        {
            lock (lockObj)
            {
                for (var k = 0; k < acc.Length; k++)
                    acc[k] += local[k];
            }
        });
        var lineCount = (lines + step - 1) / step;
        // resample to 256 bins over 0…0.5 cycles/pixel so different sizes compare
        var outBins = new double[256];
        for (var b = 0; b < outBins.Length; b++)
        {
            var from = (int)((double)b / outBins.Length * (acc.Length - 1));
            var to = Math.Max(from + 1, (int)((double)(b + 1) / outBins.Length * (acc.Length - 1)));
            double s = 0;
            for (var k = from; k < to; k++)
                s += acc[k];
            s /= (to - from) * Math.Max(1, lineCount);
            outBins[b] = s > 0 ? 10 * Math.Log10(s) : -200;
        }
        return outBins;
    }

    private static double BandMean(double[] db, double f0, double f1)
    {
        var a = (int)(f0 / 0.5 * db.Length);
        var b = Math.Max(a + 1, (int)(f1 / 0.5 * db.Length));
        double s = 0;
        for (var i = a; i < Math.Min(b, db.Length); i++)
            s += Math.Pow(10, db[i] / 10);
        return 10 * Math.Log10(Math.Max(1e-30, s / (b - a)));
    }

    private static double HfRatio(double[] db) => db.Length == 0 ? double.NaN : BandMean(db, 0.375, 0.5) - BandMean(db, 0.08, 0.2);

    /// <summary>
    /// Frequency (cycles/pixel) of the steepest sustained drop in the upper part of the spectrum — the edge of the
    /// detail an upscaler could not invent. NaN when the spectrum falls off smoothly.
    /// </summary>
    internal static double Cutoff(double[] db)
    {
        if (db.Length < 32)
            return double.NaN;
        var n = db.Length;
        var sm = new double[n];
        for (var i = 0; i < n; i++)
        {
            double s = 0;
            var c = 0;
            for (var j = Math.Max(0, i - 3); j <= Math.Min(n - 1, i + 3); j++)
            {
                s += db[j];
                c++;
            }
            sm[i] = s / c;
        }
        // look for the biggest drop over a short window, from 25 % to 95 % of Nyquist
        var best = 0.0;
        var at = -1;
        var win = Math.Max(4, n / 40);
        for (var i = n / 4; i + win < n * 95 / 100; i++)
        {
            var drop = sm[i] - sm[i + win];
            if (drop > best)
            {
                best = drop;
                at = i + win / 2;
            }
        }
        // a natural spectrum falls ~1 dB per window there; an upscale cliff is many dB, flat-ish after
        if (at < 0 || best < 8)
            return double.NaN;
        var after = sm.Skip(at + win).Take(n / 8).DefaultIfEmpty(sm[^1]).Average();
        var before = sm.Skip(Math.Max(0, at - win - n / 8)).Take(n / 8).Average();
        if (before - after < 10)
            return double.NaN;
        return at / (double)n * 0.5;
    }

    /// <summary>
    /// Round-trip test for the vertical native resolution: downscale to each candidate height and back; an upscaled
    /// source comes back almost unchanged at the height (and kernel) it was upscaled from.
    /// </summary>
    public static NativeResResult NativeResolution(LumaFrame f, CropBox? crop = null, CancellationToken ct = default)
    {
        var c = crop ?? new CropBox(0, 0, 0, 0);
        var res = new NativeResResult();
        var h = c.Height(f.Height);
        var fullW = c.Width(f.Width);
        if (h < 200 || fullW < 64)
            return res;
        // central strip keeps it quick
        var w = Math.Min(fullW, 512);
        var x0 = c.Left + (fullW - w) / 2;
        var img = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
                img[y * w + x] = f.Data[(c.Top + y) * f.Width + x0 + x] / (float)f.MaxCode;
        }
        var heights = Candidates(f.Height, h);
        var kernels = new[] { Kernel.Bilinear, Kernel.Bicubic, Kernel.BicubicSws, Kernel.Mitchell, Kernel.Lanczos3, Kernel.Spline36 };
        foreach (var ch in heights)
        {
            ct.ThrowIfCancellationRequested();
            NativeResPoint? best = null;
            foreach (var k in kernels)
            {
                var err = Resampler.DescaleErrorVertical(img, w, h, ch, k, 8, ct);
                if (best == null || err < best.Error)
                    best = new NativeResPoint(ch, err, k);
            }
            res.Curve.Add(best!);
        }
        ScoreDips(res, h, f.Height);
        return res;
    }

    /// <summary>Averages the curves of several frames (same candidates) and scores them again.</summary>
    public static NativeResResult Combine(IReadOnlyList<NativeResResult> parts, int activeHeight, int frameHeight)
    {
        var res = new NativeResResult();
        if (parts.Count == 0 || parts[0].Curve.Count == 0)
            return res;
        for (var i = 0; i < parts[0].Curve.Count; i++)
        {
            var pts = parts.Where(p => i < p.Curve.Count).Select(p => p.Curve[i]).ToList();
            // geometric mean keeps a dip that shows in every frame
            var err = Math.Exp(pts.Average(p => Math.Log(Math.Max(p.Error, 1e-12))));
            var kernel = pts.GroupBy(p => p.Kernel).OrderByDescending(g => g.Count()).First().Key;
            res.Curve.Add(new NativeResPoint(pts[0].Height, err, kernel));
        }
        ScoreDips(res, activeHeight, frameHeight);
        return res;
    }

    /// <summary>
    /// A dip is a candidate whose error is well below the log-linear interpolation of its two neighbours; native
    /// footage gives a smooth curve (a few % wiggle), an upscale one clear notch at its source height.
    /// </summary>
    private static void ScoreDips(NativeResResult res, int activeHeight, int frameHeight)
    {
        var pts = res.Curve;
        var scores = new double[pts.Count];
        for (var i = 1; i + 1 < pts.Count; i++)
        {
            var (a, b, c) = (pts[i - 1], pts[i], pts[i + 1]);
            if (b.Height >= activeHeight * 0.97 || a.Error <= 0 || c.Error <= 0)
                continue;
            var t = (double)(b.Height - a.Height) / (c.Height - a.Height);
            var expected = Math.Exp(Math.Log(a.Error) * (1 - t) + Math.Log(c.Error) * t);
            scores[i] = expected > 0 ? 1 - b.Error / expected : 0;
        }
        var order = scores.Select((v, i) => (v, i)).OrderByDescending(x => x.v).ToList();
        res.NativeHeight = null;
        res.Kernel = null;
        res.Confidence = order.Count > 0 ? Math.Max(0, order[0].v) : 0;
        if (order.Count == 0 || order[0].v < 0.15)
            return;
        var best = order[0];
        var bestH = pts[best.i].Height;
        // an image upscaled from h also descales well to simple multiples of h (3/2·h, 4/3·h …): those are not rivals,
        // and the lowest strong member of that family is the real source height
        var family = order.Where(x => x.v >= best.v * 0.5 && IsHarmonic(pts[x.i].Height, bestH)).ToList();
        var rival = order.FirstOrDefault(x => x.i != best.i && !IsHarmonic(pts[x.i].Height, bestH)).v;
        if (best.v < Math.Max(0, rival) * 1.8)
            return;
        var native = family.OrderBy(x => pts[x.i].Height).First();
        var hgt = pts[native.i].Height;
        res.Confidence = native.v;
        // report as a full-frame height (the test ran on the picture without black bars)
        res.NativeHeight = activeHeight != frameHeight ? (int)Math.Round(hgt * (double)frameHeight / activeHeight) : hgt;
        res.Kernel = pts[native.i].Kernel;
    }

    /// <summary>True when a and b are related by a small ratio (k/m with k, m ≤ 4), within 1 %.</summary>
    private static bool IsHarmonic(int a, int b)
    {
        if (a == b)
            return true;
        for (var k = 1; k <= 4; k++)
        {
            for (var m = 1; m <= 4; m++)
            {
                if (k != m && Math.Abs(a * (double)m / k - b) <= b * 0.01)
                    return true;
            }
        }
        return false;
    }

    private static List<int> Candidates(int frameHeight, int activeHeight)
    {
        var common = new[] { 360, 480, 540, 576, 600, 630, 648, 714, 720, 768, 800, 810, 864, 873, 900, 945, 960, 1008, 1080, 1200, 1440, 1600, 1620, 1800, 2160 };
        var set = new SortedSet<int>();
        var ratio = (double)activeHeight / frameHeight;
        foreach (var hgt in common)
        {
            var a = (int)Math.Round(hgt * ratio);
            if (a >= activeHeight * 0.45 && a < activeHeight)
                set.Add(a);
        }
        var step = Math.Max(4, activeHeight / 40);
        for (var hgt = (int)(activeHeight * 0.45); hgt < activeHeight * 0.98; hgt += step)
            set.Add(hgt);
        return set.ToList();
    }

    /// <summary>SMPTE ST 2084 (PQ) code value (limited range) to nits.</summary>
    public static double PqToNits(int code, int bitDepth)
    {
        var shift = bitDepth - 8;
        var e = Math.Clamp((code - (16 << shift)) / (double)((235 - 16) << shift), 0, 1);
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var p = Math.Pow(e, 1 / m2);
        return 10000 * Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
    }

    /// <summary>Luma-based brightness estimate for PQ video: (99.9th percentile nits, average nits).</summary>
    public static (double Peak, double Average) PqBrightness(LumaFrame f)
    {
        var lut = new double[f.MaxCode + 1];
        for (var i = 0; i < lut.Length; i++)
            lut[i] = PqToNits(i, f.BitDepth);
        var hist = new long[f.MaxCode + 1];
        double sum = 0;
        foreach (var v in f.Data)
        {
            hist[Math.Min(v, f.MaxCode)]++;
            sum += lut[Math.Min(v, f.MaxCode)];
        }
        var target = f.Data.Length * 0.999;
        long acc = 0;
        var peak = 0.0;
        for (var i = 0; i < hist.Length; i++)
        {
            acc += hist[i];
            if (acc >= target)
            {
                peak = lut[i];
                break;
            }
        }
        return (peak, sum / f.Data.Length);
    }
}
