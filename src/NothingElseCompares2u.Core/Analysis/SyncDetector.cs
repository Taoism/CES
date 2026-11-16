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

    /// <summary>At least two probes agree (or the only probe is a very strong match): safe to apply.</summary>
    public bool Reliable { get; set; }

    /// <summary>The audio-only answer, before the video refinement.</summary>
    public double AudioOffset { get; set; }

    /// <summary>Seconds the picture comparison moved the offset by (0 = audio was already frame-exact), null when not refined.</summary>
    public double? VideoCorrection { get; set; }

    public string Summary =>
        Probes.Count == 0 ? "No audio match found." :
        !Reliable ? $"No reliable match (best {Offset:+0.000;-0.000} s, confidence {Confidence:0.0}; the probes disagree) — set the offset by hand" :
        $"Offset {Offset:+0.000;-0.000} s (confidence {Confidence:0.0})" +
        (VideoCorrection is { } vc ? Math.Abs(vc) > 0.001 ? $", picture-matched (audio alone said {AudioOffset:+0.000;-0.000} s)" : ", picture-matched" : "") +
        (Math.Abs(Drift) > 0.08 ? $", drifts {Drift:+0.00;-0.00} s across the title (different speed or edit)" : "");
}

/// <summary>Finds the time offset between two sources by cross-correlating their audio.</summary>
public sealed partial class SyncDetector(FfTools tools, string tempDir)
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
        // the largest group of probes that agree on the offset wins; a lone or contradicted peak is noise
        const double agree = 0.1;
        var strong = result.Probes.Where(p => p.Confidence >= 10).ToList();
        var cluster = strong
            .Select(p => strong.Where(q => Math.Abs(q.Offset - p.Offset) <= agree).ToList())
            .OrderByDescending(c => c.Count).ThenByDescending(c => c.Sum(q => q.Confidence))
            .FirstOrDefault() ?? [];
        var pick = cluster.Count > 0 ? cluster : result.Probes.OrderByDescending(p => p.Confidence).Take(1).ToList();
        if (pick.Count > 0)
        {
            var sorted = pick.Select(p => p.Offset).Order().ToList();
            result.Offset = sorted[sorted.Count / 2];
            result.Confidence = pick.Average(p => p.Confidence);
        }
        result.Reliable = cluster.Count >= 2 || (cluster.Count == 1 && result.Probes.Count == 1 && cluster[0].Confidence >= 30);
        if (cluster.Count >= 2)
            result.Drift = cluster.OrderBy(p => p.RefTime).Last().Offset - cluster.OrderBy(p => p.RefTime).First().Offset;
        else if (result.Probes.Count >= 2)
            result.Drift = result.Probes[^1].Offset - result.Probes[0].Offset;
        result.AudioOffset = result.Offset;

        // audio is only as exact as each file's own A/V sync: snap to the frame that really matches
        if (result.Reliable && reference.Info?.Video != null && other.Info?.Video != null)
        {
            progress?.Report("Matching frames…");
            var refined = await RefineWithVideoAsync(reference, other, result.Offset, cluster.Select(p => p.RefTime).ToList(), ct).ConfigureAwait(false);
            if (refined is { } r)
            {
                result.VideoCorrection = r - result.Offset;
                result.Offset = r;
            }
        }
        return result;
    }

    private const int FrameW = 160;
    private const int FrameH = 90;

    /// <summary>
    /// Compares small grey frames around the audio offset (±6 frames) at a few places and returns the offset of the
    /// best-matching frame pair (from real frame timestamps), or null when the picture gives no clear answer.
    /// </summary>
    public async Task<double?> RefineWithVideoAsync(MediaTitle reference, MediaTitle other, double offset, IReadOnlyList<double> times, CancellationToken ct)
    {
        var fr = reference.Info!.Video!.FrameRate;
        var fo = other.Info!.Video!.FrameRate;
        if (fr <= 0 || Math.Abs(fr - fo) > 0.01)
            return null;
        const int n = 36;
        const int m = 6;
        var votes = new List<(double Offset, double Margin)>();
        var points = times.Count > 0 ? times : InputBuilder.EvenPoints(Math.Min(reference.Duration, other.Duration), 3, 20, 20);
        foreach (var t in points)
        {
            ct.ThrowIfCancellationRequested();
            var a = await GrayFramesAsync(reference, t, n, ct).ConfigureAwait(false);
            var b = await GrayFramesAsync(other, t + offset - m / fr, n + 2 * m, ct).ConfigureAwait(false);
            if (a.Count < n / 2 || b.Count < a.Count + m)
                continue;
            // static shots tell nothing about which frame is which
            var motion = Enumerable.Range(1, a.Count - 1).Average(i => Diff(a[i - 1].Pixels, a[i].Pixels));
            if (motion < 0.4)
                continue;
            var costs = new List<(int Shift, double Cost)>();
            for (var s = 0; s + a.Count <= b.Count; s++)
                costs.Add((s, Enumerable.Range(0, a.Count).Average(i => Diff(a[i].Pixels, b[i + s].Pixels))));
            var sorted = costs.OrderBy(c => c.Cost).ToList();
            var best = sorted[0];
            var next = sorted.Count > 1 ? sorted[1].Cost : best.Cost * 2;
            var margin = best.Cost > 0 ? (next - best.Cost) / best.Cost : 10;
            if (margin < 0.15)
                continue;
            // offset from the matched frames' own timestamps
            var pairs = Enumerable.Range(0, a.Count).Select(i => b[i + best.Shift].Time - a[i].Time).Order().ToList();
            votes.Add((pairs[pairs.Count / 2], margin));
        }
        if (votes.Count == 0)
            return null;
        // the most common answer (within half a frame), weighted by how clear it was
        var group = votes.Select(v => votes.Where(w => Math.Abs(w.Offset - v.Offset) < 0.5 / fr).ToList())
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Sum(x => x.Margin)).First();
        if (votes.Count >= 2 && group.Count < 2)
            return null;
        return group.Select(g => g.Offset).Order().ElementAt(group.Count / 2);
    }

    private sealed record GrayFrame(double Time, float[] Pixels);

    private async Task<List<GrayFrame>> GrayFramesAsync(MediaTitle title, double start, int count, CancellationToken ct)
    {
        var frames = new List<GrayFrame>();
        if (start < 0)
            return frames;
        var args = new List<string> { "-hide_banner", "-nostdin" };
        var input = InputBuilder.Input(title, start, count / Math.Max(1, title.Info!.Video!.FrameRate) + 2, tempDir);
        args.AddRange(input.Args);
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", input.TrimFilter + $"scale={FrameW}:{FrameH}:flags=area,format=gray,showinfo",
            "-frames:v", count.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var times = PtsRegex().Matches(res.StdErr).Select(x => double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        const int size = FrameW * FrameH;
        for (var i = 0; i < res.StdOut.Length / size && i < times.Count; i++)
        {
            var px = new float[size];
            double mean = 0;
            for (var k = 0; k < size; k++)
            {
                px[k] = res.StdOut[i * size + k];
                mean += px[k];
            }
            mean /= size;
            // brightness-neutral: HDR vs SDR or a level shift should not decide the match
            for (var k = 0; k < size; k++)
                px[k] -= (float)mean;
            frames.Add(new GrayFrame(start - input.Skip + times[i], px));
        }
        return frames;
    }

    private static double Diff(float[] a, float[] b)
    {
        double d = 0;
        for (var i = 0; i < a.Length; i++)
            d += Math.Abs(a[i] - b[i]);
        return d / a.Length;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\] n:\s*\d+\s+pts:\s*-?\d+\s+pts_time:(-?[\d.]+)")]
    private static partial System.Text.RegularExpressions.Regex PtsRegex();

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
