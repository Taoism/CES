namespace NothingElseCompares2u.Dsp;

/// <summary>In-place iterative radix-2 complex FFT (sizes must be powers of two).</summary>
public static class Fft
{
    public static int NextPow2(int n)
    {
        var p = 1;
        while (p < n)
            p <<= 1;
        return p;
    }

    public static void Transform(double[] re, double[] im, bool inverse = false)
    {
        var n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
            var half = len >> 1;
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < half; k++)
                {
                    var a = i + k;
                    var b = a + half;
                    var xr = re[b] * cr - im[b] * ci;
                    var xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                    var t = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = t;
                }
            }
        }
        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                re[i] /= n;
                im[i] /= n;
            }
        }
    }

    public static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++)
            w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    /// <summary>Welch-averaged power spectrum in dB (n/2+1 bins) of a real signal.</summary>
    public static double[] PowerSpectrumDb(ReadOnlySpan<float> x, int size, int hop = 0)
    {
        if (hop <= 0)
            hop = size / 2;
        var win = Hann(size);
        var acc = new double[size / 2 + 1];
        var re = new double[size];
        var im = new double[size];
        var frames = 0;
        var wsum = win.Sum(v => v * v);
        for (var start = 0; start + size <= x.Length; start += hop)
        {
            for (var i = 0; i < size; i++)
            {
                re[i] = x[start + i] * win[i];
                im[i] = 0;
            }
            Transform(re, im);
            for (var k = 0; k <= size / 2; k++)
                acc[k] += (re[k] * re[k] + im[k] * im[k]) / wsum;
            frames++;
        }
        for (var k = 0; k < acc.Length; k++)
            acc[k] = frames > 0 && acc[k] > 0 ? 10 * Math.Log10(acc[k] / frames) : -200;
        return acc;
    }
}
