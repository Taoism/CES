namespace Ces.Dsp;

public enum Kernel
{
    Bilinear,
    Bicubic, // Catmull-Rom style (b=0, c=0.5)
    BicubicSws, // ffmpeg/swscale default bicubic (b=0, c=0.6)
    Mitchell, // b=c=1/3
    Lanczos3,
    Spline36,
}

/// <summary>1-D separable resampling of image columns (enough to find a vertical native resolution).</summary>
public static class Resampler
{
    public static double Support(Kernel k) => k switch
    {
        Kernel.Bilinear => 1,
        Kernel.Lanczos3 => 3,
        Kernel.Spline36 => 3,
        _ => 2,
    };

    public static double Weight(Kernel k, double x)
    {
        x = Math.Abs(x);
        switch (k)
        {
            case Kernel.Bilinear:
                return x < 1 ? 1 - x : 0;
            case Kernel.Bicubic:
                return Cubic(x, 0, 0.5);
            case Kernel.BicubicSws:
                return Cubic(x, 0, 0.6);
            case Kernel.Mitchell:
                return Cubic(x, 1 / 3.0, 1 / 3.0);
            case Kernel.Lanczos3:
                if (x < 1e-9)
                    return 1;
                if (x >= 3)
                    return 0;
                var px = Math.PI * x;
                return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
            case Kernel.Spline36:
                if (x < 1)
                    return ((13.0 / 11 * x - 453.0 / 209) * x - 3.0 / 209) * x + 1;
                if (x < 2)
                {
                    x -= 1;
                    return ((-6.0 / 11 * x + 270.0 / 209) * x - 156.0 / 209) * x;
                }
                if (x < 3)
                {
                    x -= 2;
                    return ((1.0 / 11 * x - 45.0 / 209) * x + 26.0 / 209) * x;
                }
                return 0;
            default:
                return 0;
        }
    }

    private static double Cubic(double x, double b, double c)
    {
        if (x < 1)
            return ((12 - 9 * b - 6 * c) * x * x * x + (-18 + 12 * b + 6 * c) * x * x + (6 - 2 * b)) / 6;
        if (x < 2)
            return ((-b - 6 * c) * x * x * x + (6 * b + 30 * c) * x * x + (-12 * b - 48 * c) * x + (8 * b + 24 * c)) / 6;
        return 0;
    }

    /// <summary>Precomputed weights for resizing a length-<paramref name="src"/> axis to <paramref name="dst"/> (centre-aligned like swscale/zimg).</summary>
    public sealed class Plan
    {
        public int[] Start { get; }
        public double[][] W { get; }

        public Plan(int src, int dst, Kernel k)
        {
            Start = new int[dst];
            W = new double[dst][];
            var scale = (double)dst / src;
            var filterScale = Math.Min(1, scale); // widen the kernel when downscaling
            var support = Support(k) / filterScale;
            for (var i = 0; i < dst; i++)
            {
                var center = (i + 0.5) / scale - 0.5;
                var first = (int)Math.Floor(center - support) + 1;
                var last = (int)Math.Floor(center + support);
                var n = last - first + 1;
                var w = new double[n];
                double sum = 0;
                for (var j = 0; j < n; j++)
                {
                    w[j] = Weight(k, (first + j - center) * filterScale);
                    sum += w[j];
                }
                if (Math.Abs(sum) > 1e-12)
                {
                    for (var j = 0; j < n; j++)
                        w[j] /= sum;
                }
                Start[i] = first;
                W[i] = w;
            }
        }
    }

    /// <summary>Resizes every column of a w×h image to height <paramref name="dstH"/>.</summary>
    public static float[] ResizeVertical(float[] img, int w, int h, int dstH, Plan plan)
    {
        var dst = new float[w * dstH];
        Parallel.For(0, dstH, y =>
        {
            var start = plan.Start[y];
            var wts = plan.W[y];
            var row = y * w;
            for (var j = 0; j < wts.Length; j++)
            {
                var sy = Math.Clamp(start + j, 0, h - 1) * w;
                var wt = (float)wts[j];
                for (var x = 0; x < w; x++)
                    dst[row + x] += img[sy + x] * wt;
            }
        });
        return dst;
    }

    /// <summary>Resizes every row of a w×h image to width <paramref name="dstW"/>.</summary>
    public static float[] ResizeHorizontal(float[] img, int w, int h, int dstW, Plan plan)
    {
        var dst = new float[dstW * h];
        Parallel.For(0, h, y =>
        {
            var src = y * w;
            var row = y * dstW;
            for (var x = 0; x < dstW; x++)
            {
                var start = plan.Start[x];
                var wts = plan.W[x];
                double acc = 0;
                for (var j = 0; j < wts.Length; j++)
                    acc += img[src + Math.Clamp(start + j, 0, w - 1)] * wts[j];
                dst[row + x] = (float)acc;
            }
        });
        return dst;
    }

    /// <summary>
    /// Inverse of an upscale ("descale"): for an image upscaled from height <c>h</c> to <c>H</c> with kernel <c>k</c>, finds
    /// the h-row image that upscales back to it best (least squares) and returns the RMS error of that reconstruction.
    /// Real upscales come back nearly exact at their true height and kernel; native footage does not.
    /// </summary>
    public static double DescaleErrorVertical(float[] img, int w, int bigH, int h, Kernel k, int margin, CancellationToken ct = default)
    {
        var plan = new Plan(h, bigH, k);
        // sparse rows of U (bigH × h), edge taps folded onto the border rows
        var cols = new int[bigH][];
        var wts = new double[bigH][];
        var bw = 0;
        for (var i = 0; i < bigH; i++)
        {
            var map = new SortedDictionary<int, double>();
            for (var j = 0; j < plan.W[i].Length; j++)
            {
                var c = Math.Clamp(plan.Start[i] + j, 0, h - 1);
                map[c] = map.GetValueOrDefault(c) + plan.W[i][j];
            }
            cols[i] = map.Keys.ToArray();
            wts[i] = map.Values.ToArray();
            bw = Math.Max(bw, cols[i][^1] - cols[i][0]);
        }
        // A = UᵀU, banded: band[r * (bw+1) + d] = A[r, r+d]
        var stride = bw + 1;
        var band = new double[h * stride];
        for (var i = 0; i < bigH; i++)
        {
            var c = cols[i];
            var wv = wts[i];
            for (var a = 0; a < c.Length; a++)
            {
                for (var b = a; b < c.Length; b++)
                    band[c[a] * stride + (c[b] - c[a])] += wv[a] * wv[b];
            }
        }
        for (var r = 0; r < h; r++)
            band[r * stride] += 1e-9; // keeps the factorisation stable for degenerate edge rows
        // banded Cholesky A = L Lᵀ, stored in place as L[r+d, r] at band[r * stride + d]
        for (var j = 0; j < h; j++)
        {
            var sum = band[j * stride];
            for (var kk = Math.Max(0, j - bw); kk < j; kk++)
            {
                var l = band[kk * stride + (j - kk)];
                sum -= l * l;
            }
            var diag = Math.Sqrt(Math.Max(sum, 1e-12));
            band[j * stride] = diag;
            for (var d = 1; d <= bw && j + d < h; d++)
            {
                var i = j + d;
                var s = band[j * stride + d];
                for (var kk = Math.Max(0, i - bw); kk < j; kk++)
                    s -= band[kk * stride + (i - kk)] * band[kk * stride + (j - kk)];
                band[j * stride + d] = s / diag;
            }
        }
        ct.ThrowIfCancellationRequested();
        double total = 0;
        var lockObj = new object();
        Parallel.For(0, w, () => (Err: 0.0, B: new double[h], Y: new double[bigH]), (x, _, st) =>
        {
            var b = st.B;
            var y = st.Y;
            Array.Clear(b);
            for (var i = 0; i < bigH; i++)
            {
                var v = img[i * w + x];
                y[i] = v;
                var c = cols[i];
                var wv = wts[i];
                for (var a = 0; a < c.Length; a++)
                    b[c[a]] += wv[a] * v;
            }
            // forward: L z = b
            for (var i = 0; i < h; i++)
            {
                var s = b[i];
                for (var kk = Math.Max(0, i - bw); kk < i; kk++)
                    s -= band[kk * stride + (i - kk)] * b[kk];
                b[i] = s / band[i * stride];
            }
            // back: Lᵀ x = z
            for (var i = h - 1; i >= 0; i--)
            {
                var s = b[i];
                for (var d = 1; d <= bw && i + d < h; d++)
                    s -= band[i * stride + d] * b[i + d];
                b[i] = s / band[i * stride];
            }
            var err = 0.0;
            for (var i = margin; i < bigH - margin; i++)
            {
                var c = cols[i];
                var wv = wts[i];
                double r = 0;
                for (var a = 0; a < c.Length; a++)
                    r += wv[a] * b[c[a]];
                var d = r - y[i];
                err += d * d;
            }
            return (st.Err + err, b, y);
        }, st =>
        {
            lock (lockObj)
                total += st.Err;
        });
        return Math.Sqrt(total / ((bigH - 2.0 * margin) * w));
    }
}
