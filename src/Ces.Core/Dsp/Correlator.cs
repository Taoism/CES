namespace Ces.Dsp;

/// <summary>Result of looking for a short snippet inside a long signal.</summary>
public readonly record struct Match(int Lag, double Ncc, double Confidence);

/// <summary>
/// Finds where a short snippet occurs in a long signal: FFT cross-correlation in blocks (overlap-save, so hours of
/// audio need little memory), normalised by the local energy so loud passages do not win by volume alone.
/// </summary>
public static class Correlator
{
    /// <summary>Searches lags [<paramref name="lo"/>, <paramref name="hi"/>] of <paramref name="b"/> for snippet <paramref name="a"/>.</summary>
    public static Match Find(float[] a, float[] b, int lo = 0, int hi = int.MaxValue, CancellationToken ct = default)
    {
        var na = a.Length;
        lo = Math.Max(0, lo);
        hi = Math.Min(hi, b.Length - na);
        if (na < 16 || hi < lo)
            return new Match(0, 0, 0);

        // zero-mean snippet and its norm
        var meanA = 0.0;
        foreach (var v in a)
            meanA += v;
        meanA /= na;
        var normA = 0.0;
        foreach (var v in a)
            normA += (v - meanA) * (v - meanA);
        normA = Math.Sqrt(normA);
        if (normA <= 0)
            return new Match(0, 0, 0);

        var n = Fft.NextPow2(Math.Max(na * 4, 4096));
        var step = n - na + 1;
        var ar = new double[n];
        var ai = new double[n];
        for (var i = 0; i < na; i++)
            ar[i] = a[i] - meanA;
        Fft.Transform(ar, ai);

        var br = new double[n];
        var bi = new double[n];
        var best = new Match(lo, double.MinValue, 0);
        double sumAbs = 0;
        long count = 0;
        for (var start = lo; start <= hi; start += step)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(bi);
            var len = Math.Min(n, b.Length - start);
            for (var i = 0; i < n; i++)
                br[i] = i < len ? b[start + i] : 0;
            Fft.Transform(br, bi);
            for (var i = 0; i < n; i++)
            {
                var r = br[i] * ar[i] + bi[i] * ai[i];
                var im = bi[i] * ar[i] - br[i] * ai[i];
                br[i] = r;
                bi[i] = im;
            }
            Fft.Transform(br, bi, inverse: true);

            // window energy for each lag of this block (sliding sums)
            var lags = Math.Min(step, hi - start + 1);
            double s1 = 0, s2 = 0;
            for (var i = 0; i < na; i++)
            {
                double v = b[start + i];
                s1 += v;
                s2 += v * v;
            }
            for (var j = 0; j < lags; j++)
            {
                if (j > 0)
                {
                    double outV = b[start + j - 1];
                    double inV = b[start + j - 1 + na];
                    s1 += inV - outV;
                    s2 += inV * inV - outV * outV;
                }
                var varB = s2 - s1 * s1 / na;
                var ncc = varB > 1e-12 ? br[j] / (normA * Math.Sqrt(varB)) : 0;
                sumAbs += Math.Abs(ncc);
                count++;
                if (ncc > best.Ncc)
                    best = new Match(start + j, ncc, 0);
            }
        }
        var mean = count > 0 ? sumAbs / count : 0;
        return best with { Confidence = mean > 0 ? best.Ncc / mean : 0 };
    }

    /// <summary>Averages blocks of <paramref name="factor"/> samples (cheap decimation for a coarse search).</summary>
    public static float[] Decimate(float[] x, int factor)
    {
        var y = new float[x.Length / factor];
        for (var i = 0; i < y.Length; i++)
        {
            float s = 0;
            for (var k = 0; k < factor; k++)
                s += x[i * factor + k];
            y[i] = s / factor;
        }
        return y;
    }
}
