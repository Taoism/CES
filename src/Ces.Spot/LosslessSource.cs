using System.Globalization;

namespace Ces.Spot;

/// <summary>
/// Is a lossless file really lossless? From its decoded audio: a brick-wall lowpass well below Nyquist is what an
/// MP3/AAC encoder leaves behind, nothing above 22–24 kHz in a 88.2/96/192 kHz file means it was upsampled, and
/// zero low bits in every sample mean 16-bit audio padded to 24 bits. Adds problems, details and a short verdict.
/// </summary>
public static class LosslessSource
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A lowpass is a "brick wall" when the level drops by more than this within ~1 kHz.</summary>
    public const double SteepEdgeDb = 25;

    /// <summary>Highest brick-wall cut that still counts as a lossy encoder's lowpass (LAME 320 kbps cuts at 20.5 kHz).</summary>
    public const double LossyMaxHz = 20800;

    public static void Check(AnalysisResult r, int rate, int bits, int usedBits, double[] spectrum, double cutoffHz, double steepnessDb)
    {
        var verdicts = new List<string>();
        var nyquist = rate / 2.0;
        if (!double.IsNaN(cutoffHz) && rate > 0)
        {
            var steep = !double.IsNaN(steepnessDb) && steepnessDb > SteepEdgeDb;
            r.Details.Add(new InfoItem("Source", "Content up to",
                cutoffHz >= nyquist * 0.97 ? string.Create(Inv, $"{nyquist / 1000:0.#} kHz (Nyquist)") : string.Create(Inv, $"{cutoffHz / 1000:0.0} kHz")));
            if (!double.IsNaN(steepnessDb))
                r.Details.Add(new InfoItem("Source", "Edge", string.Create(Inv, $"{steepnessDb:0} dB within 1 kHz{(steep ? " (brick-wall lowpass)" : " (natural roll-off)")}")));
            var from = Dsp.Spectrum.UpsampledFrom(spectrum, rate);
            if (from > 0)
            {
                r.Problems.Add(string.Create(Inv, $"upsampled from {from / 1000.0:0.0#} kHz: the level drops off a cliff at {from / 2000.0:0.0#} kHz in a {rate / 1000.0:0.#} kHz file"));
                verdicts.Add(string.Create(Inv, $"upsampled ({from / 1000.0:0.0#} kHz)"));
            }
            else if (steep && cutoffHz < LossyMaxHz && cutoffHz < nyquist * 0.97)
            {
                r.Problems.Add(string.Create(Inv, $"lossy source? brick-wall lowpass at {cutoffHz / 1000:0.0} kHz ({steepnessDb:0} dB edge), typical of {LossyHint(cutoffHz)}"));
                verdicts.Add(string.Create(Inv, $"lossy source? ({cutoffHz / 1000:0.0} kHz)"));
            }
        }
        if (usedBits > 0 && bits > 0)
        {
            r.Details.Add(new InfoItem("Source", "Bits used", string.Create(Inv, $"{usedBits} of {bits}")));
            if (usedBits < bits && bits > 16 && usedBits <= 16)
            {
                r.Problems.Add(string.Create(Inv, $"{usedBits}-bit audio padded to {bits} bits (the low {bits - usedBits} bits are zero in every sample)"));
                verdicts.Add(string.Create(Inv, $"{usedBits}-bit in {bits}"));
            }
        }
        r.SourceCheck = verdicts.Count > 0 ? string.Join(", ", verdicts) : usedBits > 0 || !double.IsNaN(cutoffHz) ? "ok" : "";
    }

    /// <summary>Which lossy encodes typically cut at this frequency.</summary>
    public static string LossyHint(double hz) => hz switch
    {
        < 15500 => "a low-bitrate MP3/AAC (≤ 112 kbps)",
        < 16700 => "a 128 kbps MP3",
        < 17700 => "a 160 kbps MP3 or LAME V4–V5",
        < 19300 => "a 192 kbps MP3 or LAME V2–V3",
        _ => "LAME V0 / 256–320 kbps MP3 or AAC",
    };
}
