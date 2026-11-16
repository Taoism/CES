using NothingElseCompares2u.Analysis;

namespace NothingElseCompares2u.Compare;

/// <summary>
/// What a release does differently from the reference: grain removed, sharpening added, more banding, softer picture,
/// louder or more compressed audio, a different master or a fake 5.1.
/// </summary>
public static class CrossFindings
{
    public static List<Finding> Video(CompareItem reference, CompareItem item)
    {
        var f = new List<Finding>();
        if (reference.Video is not { } r || item.Video is not { } v || reference == item)
            return f;
        if (r.AvgNoise > 0.4 && v.AvgNoise < r.AvgNoise * 0.7)
            f.Add(new Finding { Area = "Grain", Level = v.AvgNoise < r.AvgNoise * 0.5 ? 2 : 1, Text = $"Grain/noise {v.AvgNoise:0.00} vs {r.AvgNoise:0.00} in the reference ({v.AvgNoise / r.AvgNoise:P0}): grain filtered away by noise reduction or smoothed by the encode." });
        else if (r.AvgNoise > 0.3 && v.AvgNoise > r.AvgNoise * 1.6)
            f.Add(new Finding { Area = "Grain", Level = 1, Text = $"Noise {v.AvgNoise:0.00} vs {r.AvgNoise:0.00} in the reference: added grain/dither or noisy encode." });
        if (!double.IsNaN(r.AvgHalo) && !double.IsNaN(v.AvgHalo) && v.AvgHalo > r.AvgHalo * 1.25 + 0.05)
            f.Add(new Finding { Area = "Halos", Level = 2, Text = $"Edge overshoot {v.AvgHalo:P0} vs {r.AvgHalo:P0} in the reference: sharpening added (halos/ringing)." });
        if (!double.IsNaN(r.AvgBandRun) && !double.IsNaN(v.AvgBandRun) && v.AvgBandRun > r.AvgBandRun * 1.8 && v.AvgBandRun > 8)
            f.Add(new Finding { Area = "Banding", Level = 2, Text = $"Smooth areas step in runs of {v.AvgBandRun:0.0} px vs {r.AvgBandRun:0.0} px in the reference: more banding." });
        if (r.AvgSharpness > 0 && v.AvgSharpness < r.AvgSharpness * 0.6 && v.Height >= r.Height * 0.9)
            f.Add(new Finding { Area = "Detail", Level = 1, Text = $"Sharpness {v.AvgSharpness:0} vs {r.AvgSharpness:0} in the reference: a softer picture at the same size (upscale, filtering or a starved encode)." });
        return f;
    }

    public static List<Finding> Audio(CompareItem reference, CompareItem item)
    {
        var f = new List<Finding>();
        var r = reference.Audio.FirstOrDefault();
        var a = item.Audio.FirstOrDefault();
        if (r == null || a == null || reference == item)
            return f;
        if (!double.IsNaN(r.IntegratedLufs) && !double.IsNaN(a.IntegratedLufs) && Math.Abs(a.IntegratedLufs - r.IntegratedLufs) >= 1)
            f.Add(new Finding { Area = "Loudness", Level = Math.Abs(a.IntegratedLufs - r.IntegratedLufs) >= 3 ? 1 : 0, Text = $"{a.IntegratedLufs - r.IntegratedLufs:+0.0;-0.0} LU louder than the reference ({a.IntegratedLufs:0.0} vs {r.IntegratedLufs:0.0} LUFS)." });
        var crestDrop = r.MeanCrestDb - a.MeanCrestDb;
        var lraDrop = r.LoudnessRange - a.LoudnessRange;
        if (!double.IsNaN(crestDrop) && crestDrop >= 3 && (double.IsNaN(lraDrop) || lraDrop >= 1.5))
            f.Add(new Finding { Area = "Dynamics", Level = 2, Text = $"Crest factor {crestDrop:0.0} dB lower and loudness range {lraDrop:0.0} LU smaller than the reference: compressed/limited (brick-walled remaster or downmix)." });
        else if (!double.IsNaN(a.TruePeakDb) && a.TruePeakDb > -0.3 && r.TruePeakDb < -1)
            f.Add(new Finding { Area = "Peaks", Level = 1, Text = $"True peak {a.TruePeakDb:0.0} dBTP (reference {r.TruePeakDb:0.0}): pushed to full scale, possible clipping." });
        if (Eq(r, a) is { } eq && eq.Spread >= 3)
            f.Add(new Finding { Area = "Master", Level = 1, Text = $"Spectral balance differs from the reference by up to {eq.Spread:0} dB ({eq.Where}) after level matching: a different master/EQ, not just a different encode." });
        return f;
    }

    /// <summary>Level-matched spectrum difference in octave-ish bands below both cutoffs: (largest spread, where).</summary>
    internal static (double Spread, string Where)? Eq(AudioReport r, AudioReport a)
    {
        if (r.Spectrum.Length == 0 || a.Spectrum.Length == 0)
            return null;
        double[] edges = [60, 120, 250, 500, 1000, 2000, 4000, 8000, 12000];
        var top = Math.Min(Math.Min(double.IsNaN(r.CutoffHz) ? 16000 : r.CutoffHz, double.IsNaN(a.CutoffHz) ? 16000 : a.CutoffHz) * 0.9, 16000);
        var diffs = new List<(double Hz, double D)>();
        for (var b = 0; b + 1 < edges.Length && edges[b + 1] <= top; b++)
        {
            var dr = BandDb(r, edges[b], edges[b + 1]);
            var da = BandDb(a, edges[b], edges[b + 1]);
            if (!double.IsNaN(dr) && !double.IsNaN(da))
                diffs.Add((Math.Sqrt(edges[b] * edges[b + 1]), da - dr));
        }
        if (diffs.Count < 4)
            return null;
        // remove the overall level difference: what is left is EQ
        var mean = diffs.Average(x => x.D);
        var worst = diffs.OrderByDescending(x => Math.Abs(x.D - mean)).First();
        var spread = diffs.Max(x => x.D - mean) - diffs.Min(x => x.D - mean);
        var where = worst.D - mean > 0 ? $"more around {Hz(worst.Hz)}" : $"less around {Hz(worst.Hz)}";
        return (spread, where);
    }

    private static string Hz(double hz) => hz >= 1000 ? $"{hz / 1000:0.#} kHz" : $"{hz:0} Hz";

    private static double BandDb(AudioReport r, double f0, double f1)
    {
        var n = r.Spectrum.Length;
        var binHz = r.SpectrumRate / 2.0 / (n - 1);
        var a = (int)(f0 / binHz);
        var b = Math.Min(n - 1, (int)(f1 / binHz));
        if (b <= a)
            return double.NaN;
        double s = 0;
        for (var i = a; i < b; i++)
            s += Math.Pow(10, r.Spectrum[i] / 10);
        return 10 * Math.Log10(s / (b - a));
    }
}
