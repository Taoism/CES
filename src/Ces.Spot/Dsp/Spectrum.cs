namespace Ces.Dsp;

/// <summary>Spectrum helpers shared by Compare (audio analysis) and Spot (lossless-source checks).</summary>
public static class Spectrum
{
    /// <summary>Average level in dB of the bins between two frequencies (NaN when the band is outside the spectrum).</summary>
    public static double BandDb(double[] db, int rate, double fromHz, double toHz)
    {
        var n = db.Length;
        if (n < 2)
            return double.NaN;
        var binHz = rate / 2.0 / (n - 1);
        var a = (int)Math.Ceiling(fromHz / binHz);
        var b = Math.Min(n - 1, (int)Math.Floor(toHz / binHz));
        if (a > b || a < 0)
            return double.NaN;
        double p = 0;
        for (var i = a; i <= b; i++)
            p += Math.Pow(10, db[i] / 10);
        return 10 * Math.Log10(Math.Max(p / (b - a + 1), 1e-30));
    }

    /// <summary>
    /// For a high-rate file: the old sample rate it was upsampled from (44100 / 48000), else 0. A resampler's lowpass
    /// is about 6 dB down right at the old Nyquist and reaches the floor a few kHz later (soft filters, like ffmpeg's
    /// default, take 3–5 kHz): so the level first falls 6 dB below the 10–18 kHz level near 22 or 24 kHz and is more
    /// than <paramref name="cliffDb"/> down within the next 6 kHz. Real high-resolution recordings fade gently instead.
    /// </summary>
    public static int UpsampledFrom(double[] db, int rate, double cliffDb = 40)
    {
        if (rate <= 48000 || db.Length < 64)
            return 0;
        var reference = BandDb(db, rate, 10000, 18000);
        if (double.IsNaN(reference))
            return 0;
        for (var f = 19000.0; f < 26000; f += 100)
        {
            if (BandDb(db, rate, f, f + 200) >= reference - 6)
                continue;
            var deep = BandDb(db, rate, f + 3000, f + 6500);
            if (double.IsNaN(deep) || deep > reference - cliffDb)
                return 0;
            return f < 23000 ? 44100 : 48000;
        }
        return 0;
    }

    /// <summary>
    /// Highest frequency with real content: where the smoothed spectrum finally falls to the noise floor at the top.
    /// Returns the steepness of that edge (dB lost within ~1 kHz) so a brick-wall lowpass can be told from a natural roll-off.
    /// </summary>
    public static (double Hz, double Steepness) FindCutoff(double[] db, int rate)
    {
        var n = db.Length;
        if (n < 64)
            return (double.NaN, double.NaN);
        var binHz = rate / 2.0 / (n - 1);
        var smoothBins = Math.Max(1, (int)(150 / binHz));
        var sm = new double[n];
        for (var i = 0; i < n; i++)
        {
            double s = 0;
            var c = 0;
            for (var j = Math.Max(0, i - smoothBins); j <= Math.Min(n - 1, i + smoothBins); j++)
            {
                s += db[j];
                c++;
            }
            sm[i] = s / c;
        }
        // noise floor: the quietest 1 kHz band above 5 kHz
        var from = (int)(5000 / binHz);
        var band = Math.Max(4, (int)(1000 / binHz));
        var floor = double.MaxValue;
        for (var i = from; i + band < n; i += band / 2)
            floor = Math.Min(floor, sm.Skip(i).Take(band).Average());
        var reference = sm.Skip((int)(1000 / binHz)).Take((int)(3000 / binHz)).Average();
        if (floor >= double.MaxValue || reference - floor < 20)
            return (rate / 2.0, double.NaN); // content all the way up (or flat noise)
        var threshold = floor + Math.Min(10, (reference - floor) / 4);
        var cut = n - 1;
        while (cut > from && sm[cut] < threshold)
            cut--;
        var hz = cut * binHz;
        var k = Math.Max(1, (int)(1000 / binHz));
        var steep = sm[Math.Max(0, cut - k)] - sm[Math.Min(n - 1, cut + k)];
        return (cut >= n - 1 - smoothBins ? rate / 2.0 : hz, steep);
    }
}
