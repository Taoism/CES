using System.Globalization;
using System.Runtime.InteropServices;
using NothingElseCompares2u.Dsp;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

public sealed record SyncProbe(double RefTime, double Offset, double Confidence);

public sealed class SyncResult
{
    public List<SyncProbe> Probes { get; } = [];

    /// <summary>Seconds to add to reference time to get the same moment in the other source.</summary>
    public double Offset { get; set; }

    public double Confidence { get; set; }

    /// <summary>Offset drifts along the title: different speed (PAL speed-up) or different cuts.</summary>
    public double Drift { get; set; }

    public string Summary =>
        Probes.Count == 0 ? "No audio match found." :
        $"Offset {Offset:+0.000;-0.000} s (confidence {Confidence:0.0}){(Math.Abs(Drift) > 0.08 ? $", drifts {Drift:+0.00;-0.00} s across the title (different speed or edit)" : "")}";
}

/// <summary>Finds the time offset between two sources by cross-correlating their audio.</summary>
public sealed class SyncDetector(FfTools tools, string tempDir)
{
    private const int Rate = 4000;

    public async Task<SyncResult> DetectAsync(MediaTitle reference, MediaTitle other, double maxShift = 60, int probes = 3, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new SyncResult();
        var dur = Math.Min(reference.Duration, other.Duration);
        const double refLen = 20;
        foreach (var t in InputBuilder.EvenPoints(dur, probes, 15, 15))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Matching audio at {Format.Time(t, false)}…");
            var a = await DecodeAsync(reference, t, refLen, ct).ConfigureAwait(false);
            var start = Math.Max(0, t - maxShift);
            var b = await DecodeAsync(other, start, refLen + maxShift * 2, ct).ConfigureAwait(false);
            if (a.Length < Rate * 5 || b.Length < a.Length)
                continue;
            var (lag, conf) = await Task.Run(() => Correlate(a, b), ct).ConfigureAwait(false);
            // a starts at t, matches b at index lag (b starts at 'start')
            var offset = start + lag / (double)Rate - t;
            result.Probes.Add(new SyncProbe(t, offset, conf));
        }
        var good = result.Probes.Where(p => p.Confidence >= 4).ToList();
        if (good.Count == 0)
            good = result.Probes;
        if (good.Count > 0)
        {
            var sorted = good.Select(p => p.Offset).Order().ToList();
            result.Offset = sorted[sorted.Count / 2];
            result.Confidence = good.Average(p => p.Confidence);
            result.Drift = good[^1].Offset - good[0].Offset;
        }
        return result;
    }

    private async Task<float[]> DecodeAsync(MediaTitle title, double start, double length, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        var input = InputBuilder.Input(title, start, length, tempDir);
        args.AddRange(input.Args);
        args.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-af", input.ATrimFilter + "highpass=f=80", "-f", "f32le", "-acodec", "pcm_f32le", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        return MemoryMarshal.Cast<byte, float>(res.StdOut.AsSpan(0, res.StdOut.Length / 4 * 4)).ToArray();
    }

    /// <summary>Normalised cross-correlation of a inside b: (best lag in samples, peak-to-mean ratio).</summary>
    internal static (int Lag, double Confidence) Correlate(float[] a, float[] b)
    {
        var n = Fft.NextPow2(a.Length + b.Length);
        var ar = new double[n];
        var ai = new double[n];
        var br = new double[n];
        var bi = new double[n];
        var ma = a.Average(x => (double)x);
        var mb = b.Average(x => (double)x);
        for (var i = 0; i < a.Length; i++)
            ar[i] = a[i] - ma;
        for (var i = 0; i < b.Length; i++)
            br[i] = b[i] - mb;
        Fft.Transform(ar, ai);
        Fft.Transform(br, bi);
        // B · conj(A)
        for (var i = 0; i < n; i++)
        {
            var r = br[i] * ar[i] + bi[i] * ai[i];
            var im = bi[i] * ar[i] - br[i] * ai[i];
            br[i] = r;
            bi[i] = im;
        }
        Fft.Transform(br, bi, inverse: true);
        var maxLag = b.Length - a.Length;
        var best = 0;
        var bestV = double.MinValue;
        double sumAbs = 0;
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var v = br[lag];
            sumAbs += Math.Abs(v);
            if (v > bestV)
            {
                bestV = v;
                best = lag;
            }
        }
        var mean = sumAbs / (maxLag + 1);
        return (best, mean > 0 ? bestV / mean : 0);
    }
}
