using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ces.Cache;
using Ces.Dsp;
using Ces.Model;
using Ces.Tools;

namespace Ces.Analysis;

/// <summary>Where one reference window of the signature was found in the other: the frame shift and how clearly.</summary>
public sealed record SignatureWindow(int RefFrame, int Shift, double Ncc, double Next)
{
    /// <summary>A clear match: strong, and well above the best match anywhere else.</summary>
    public bool Good => Ncc >= 0.6 && Ncc - Next >= 0.2;
}

/// <summary>One part of a frame-level edit map: from <see cref="RefFrame"/> on, the other's frame = reference frame + shift.</summary>
public sealed record SignatureSection(int RefFrame, int? Shift, bool ExactCut);

/// <summary>
/// The motion signature of a whole title: how much each tiny grey frame (32×24, brightness and contrast removed)
/// differs from the one before. Shot changes and movement put the same pattern on the same frames whatever the
/// transfer (grade, framing, grain, resolution), so two releases of one film line up by it even when their audio was
/// restored differently. A whole-title alignment of the two signatures gives the edit map: where the offset steps
/// (splices, extra or missing scenes) and, from the signature around each step, the frame it happens on.
/// </summary>
public sealed partial class MotionSignature(float[] values, double fps, double firstTime)
{
    private const int W = 32;
    private const int H = 24;

    /// <summary>values[i] = change from frame i to frame i + 1.</summary>
    public float[] Values { get; } = values;

    /// <summary>Frames per second as the filters deliver them (after inverse telecine).</summary>
    public double Fps { get; } = fps;

    /// <summary>Title time of frame 0.</summary>
    public double FirstTime { get; } = firstTime;

    public int Frames => Values.Length + 1;

    private const string CacheKind = "signature";

    // set after CUDA could not start at all (no NVIDIA card or driver): later titles go straight to the CPU
    private static volatile bool _cudaFailed;

    /// <summary>
    /// Decodes the whole video once at 32×24 grey and keeps only the signature (null without video). Kept in the
    /// results cache, so one film compared with several releases is read once.
    /// </summary>
    public static async Task<MotionSignature?> ComputeAsync(FfTools tools, MediaTitle title, string tempDir, IProgress<string>? progress, string label, CancellationToken ct)
    {
        var fps = InputBuilder.FrameRate(title);
        if (fps <= 0 || title.Info?.Video == null || title.IsStill)
            return null;
        var key = CacheKey(title);
        if (key != null && ResultsCache.Shared.Get(CacheKind, key, SignatureJson.Default.SignatureEntry) is { } hit)
            return new MotionSignature(MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(hit.Data)).ToArray(), hit.Fps, hit.FirstTime);
        var sig = await DecodeAsync(tools, title, fps, tempDir, progress, label, ct).ConfigureAwait(false);
        if (sig != null && key != null)
            ResultsCache.Shared.Put(CacheKind, key, new SignatureEntry { Data = Convert.ToBase64String(MemoryMarshal.AsBytes(sig.Values.AsSpan())), Fps = sig.Fps, FirstTime = sig.FirstTime }, SignatureJson.Default.SignatureEntry);
        return sig;
    }

    /// <summary>The title's files (size + mtime), cut points, name and filters; null when a file cannot be read.</summary>
    private static string? CacheKey(MediaTitle title)
    {
        if (!ResultsCache.Shared.Enabled || title.Segments.Count == 0)
            return null;
        var parts = new List<string> { CacheKind, "1", ResultsCache.BuildOf(typeof(MotionSignature)), title.Name, title.Ivtc ? "ivtc" : "", InputBuilder.S(title.Duration) };
        foreach (var seg in title.Segments)
        {
            if (ResultsCache.TryFileIdentity(seg.Url) is not { } id)
                return null;
            parts.Add(id + "|" + InputBuilder.S(seg.InPoint ?? -1) + "|" + InputBuilder.S(seg.OutPoint ?? -1));
        }
        return ResultsCache.Key([.. parts]);
    }

    private static async Task<MotionSignature?> DecodeAsync(FfTools tools, MediaTitle title, double fps, string tempDir, IProgress<string>? progress, string label, CancellationToken ct)
    {
        // the same filters as every other frame grab, so frame 0's time is the one the other checks see
        var first = await FrameSequence.GrabAsync(tools, title, 0, 1, 16, 9, tempDir, normalize: false, ct).ConfigureAwait(false);
        if (first.Count == 0)
            return null;
        var input = InputBuilder.Input(title, 0, null, tempDir);
        var total = title.Duration;
        // NVIDIA's decoder shrinks the picture on the card: a whole 4K film in about a tenth of its running time,
        // three times faster than decoding it on the CPU (inverse telecine needs software frames, and is SD anyway)
        var tries = _cudaFailed || title.Ivtc ? new[] { false } : [true, false];
        foreach (var gpu in tries)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
            if (gpu)
                args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
            args.AddRange(InputBuilder.WithoutHwaccel(input.Args));
            var vf = (gpu ? "scale_cuda=128:72:format=yuv420p,hwdownload,format=yuv420p," : InputBuilder.IvtcPrefix(title))
                + input.TrimFilter + $"scale={W}:{H}:flags=area,format=gray";
            args.AddRange(["-progress", "pipe:2", "-nostats", "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", vf, "-fps_mode", "passthrough", "-f", "rawvideo", "-"]);
            await using var sink = new SignatureSink(W * H);
            var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct, line =>
            {
                var m = ProgressRegex().Match(line);
                if (progress != null && m.Success && total > 0)
                    progress.Report($"Reading the motion of {label}: {Math.Min(1, long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1e6 / total):P0}");
            }, null, sink).ConfigureAwait(false);
            // no NVIDIA card, a codec it cannot decode, or it gave up part-way: once more on the CPU
            var complete = res.Ok && sink.Count >= 100 && (total <= 0 || sink.Count >= total * fps * 0.97);
            if (gpu && !complete)
            {
                _cudaFailed = res.StdErr.Contains("cuda", StringComparison.OrdinalIgnoreCase) && sink.Count == 0;
                continue;
            }
            return sink.Count < 100 ? null : new MotionSignature(sink.ToArray(), fps, first[0].Time);
        }
        return null;
    }

    /// <summary>
    /// Each reference window (<paramref name="win"/> frames, every <paramref name="step"/>) located anywhere in
    /// <paramref name="other"/> by normalised cross-correlation (one FFT of the other, one per window).
    /// </summary>
    internal static List<SignatureWindow> AlignWindows(float[] reference, float[] other, int win, int step, CancellationToken ct = default)
    {
        var n = other.Length;
        if (reference.Length < win || n < win)
            return [];
        // shot changes dwarf movement: compressed, a window is matched by all of its motion, not by its two or three cuts
        reference = Compressed(reference);
        other = Compressed(other);
        var nfft = Fft.NextPow2(n + win);
        var ore = new double[nfft];
        var oim = new double[nfft];
        for (var i = 0; i < n; i++)
            ore[i] = other[i];
        Fft.Transform(ore, oim);
        // running mean / variance of every other window
        var c1 = new double[n + 1];
        var c2 = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            c1[i + 1] = c1[i] + other[i];
            c2[i + 1] = c2[i] + (double)other[i] * other[i];
        }
        var lags = n - win + 1;
        var scale = new double[lags];
        for (var j = 0; j < lags; j++)
        {
            var m = (c1[j + win] - c1[j]) / win;
            var v = (c2[j + win] - c2[j]) / win - m * m;
            scale[j] = v > 1e-12 ? 1 / (win * Math.Sqrt(v)) : 0;
        }

        var starts = new List<int>();
        for (var s = 0; s + win <= reference.Length; s += step)
            starts.Add(s);
        var found = new SignatureWindow?[starts.Count];
        Parallel.For(0, starts.Count, new ParallelOptions { CancellationToken = ct }, () => (new double[nfft], new double[nfft]), (w, _, buf) =>
        {
            var (re, im) = buf;
            Array.Clear(re);
            Array.Clear(im);
            var s = starts[w];
            double mean = 0, sq = 0;
            for (var i = 0; i < win; i++)
                mean += reference[s + i];
            mean /= win;
            for (var i = 0; i < win; i++)
                sq += (reference[s + i] - mean) * (reference[s + i] - mean);
            var sd = Math.Sqrt(sq / win);
            if (sd < 1e-6)
                return buf; // a still stretch says nothing
            for (var i = 0; i < win; i++)
                re[i] = (reference[s + i] - mean) / sd;
            Fft.Transform(re, im);
            // cross-correlation: IFFT(O · conj(X))
            for (var k = 0; k < nfft; k++)
            {
                double ar = ore[k], ai = oim[k], br = re[k], bi = -im[k];
                re[k] = ar * br - ai * bi;
                im[k] = ar * bi + ai * br;
            }
            Fft.Transform(re, im, inverse: true);
            var best = 0;
            for (var j = 1; j < lags; j++)
            {
                if (re[j] * scale[j] > re[best] * scale[best])
                    best = j;
            }
            // best match more than a second away (0 when the other is too short to have one)
            var next = 0.0;
            var any = false;
            for (var j = 0; j < lags; j++)
            {
                if (Math.Abs(j - best) > 24 && (!any || re[j] * scale[j] > next))
                {
                    next = re[j] * scale[j];
                    any = true;
                }
            }
            found[w] = new SignatureWindow(s, best - s, re[best] * scale[best], next);
            return buf;
        }, _ => { });
        return found.OfType<SignatureWindow>().ToList();
    }

    /// <summary>
    /// The frame-level edit map of <paramref name="reference"/> against <paramref name="other"/>, or null when too
    /// few windows match clearly (not the same film, or too little motion). Each run of windows with the same shift
    /// is a section; between two runs the step is placed on a frame from the signatures; a shift that drops by d
    /// frames means d reference frames the other does not have, and reference frames before the other starts or
    /// after it ends are missing too.
    /// </summary>
    internal static List<SignatureSection>? Map(float[] reference, float[] other, IReadOnlyList<SignatureWindow> windows, int win)
    {
        if (windows.Count < 3)
            return null;
        var good = windows.Where(w => w.Good).ToList();
        if (good.Count < Math.Max(3, windows.Count * 0.4))
            return null;

        // runs of the same shift (within a frame)
        var runs = new List<List<SignatureWindow>>();
        foreach (var w in good)
        {
            if (runs.Count > 0 && Math.Abs(Median(runs[^1]) - w.Shift) <= 1)
                runs[^1].Add(w);
            else
                runs.Add([w]);
        }
        // a lone window between two runs that agree is a near-miss (a splice inside it), not an edit
        for (var i = 1; i + 1 < runs.Count; i++)
        {
            if (runs[i].Count == 1 && Math.Abs(Median(runs[i - 1]) - Median(runs[i + 1])) <= 1)
            {
                runs[i - 1].AddRange(runs[i + 1]);
                runs.RemoveRange(i, 2);
                i--;
            }
        }
        var shifts = runs.Select(r => (int)Math.Round(Median(r))).ToList();

        var rn = Normalized(reference);
        var on = Normalized(other);
        var sections = new List<SignatureSection>();
        // before the other starts: reference frames with no counterpart
        var lead = -shifts[0];
        if (lead > 0)
            sections.Add(new SignatureSection(0, null, false));
        sections.Add(new SignatureSection(Math.Max(0, lead), shifts[0], false));
        for (var i = 1; i < runs.Count; i++)
        {
            int a = shifts[i - 1], b = shifts[i];
            var lo = runs[i - 1][^1].RefFrame;
            var hi = Math.Min(reference.Length, runs[i][0].RefFrame + win);
            var gap = Math.Max(0, a - b);
            var (cut, exact) = PlaceCut(rn, on, lo, hi, a, b, gap);
            if (gap > 0)
            {
                sections.Add(new SignatureSection(cut, null, exact));
                sections.Add(new SignatureSection(cut + gap, b, exact));
            }
            else
            {
                sections.Add(new SignatureSection(cut, b, exact));
            }
        }
        // after the other ends
        var tail = other.Length - shifts[^1];
        if (tail < reference.Length - 1 && tail > sections[^1].RefFrame)
            sections.Add(new SignatureSection(tail + 1, null, false));
        return sections;

        static double Median(List<SignatureWindow> r) => r.Select(x => (double)x.Shift).Order().ElementAt(r.Count / 2);
    }

    /// <summary>
    /// The first reference frame after section A for a step from shift <paramref name="a"/> to <paramref name="b"/>
    /// somewhere in [lo, hi): the split where A's signature mismatch before it plus B's after the
    /// <paramref name="gap"/> (frames in neither) is least. Exact when that split is clearly cheaper than any other.
    /// </summary>
    internal static (int Cut, bool Exact) PlaceCut(float[] r, float[] o, int lo, int hi, int a, int b, int gap)
    {
        lo = Math.Max(lo, 0);
        hi = Math.Min(hi, r.Length);
        if (hi - lo - gap < 2)
            return (Math.Clamp((lo + hi - gap) / 2, 0, Math.Max(0, r.Length - 1)), false);
        double Cost(int i, int shift) => i + shift >= 0 && i + shift < o.Length ? Math.Abs(r[i] - o[i + shift]) : 2;
        var n = hi - lo;
        var prefA = new double[n + 1];
        var sufB = new double[n + 1];
        for (var k = 0; k < n; k++)
            prefA[k + 1] = prefA[k] + Cost(lo + k, a);
        for (var k = n - 1; k >= 0; k--)
            sufB[k] = sufB[k + 1] + Cost(lo + k, b);
        var best = 1;
        var bestS = double.MaxValue;
        for (var c = 1; c + gap < n; c++)
        {
            var s = prefA[c] + sufB[c + gap];
            if (s < bestS)
            {
                bestS = s;
                best = c;
            }
        }
        // exact only when every placement two or more frames away costs clearly more: a shot change or a burst of
        // movement next to the step decides it; inside an even stretch of motion the frames cannot tell
        var runnerUp = double.MaxValue;
        for (var c = 1; c + gap < n; c++)
        {
            if (Math.Abs(c - best) >= 2)
                runnerUp = Math.Min(runnerUp, prefA[c] + sufB[c + gap]);
        }
        var exact = runnerUp - bestS >= 3; // values are in units of the median frame change
        return (lo + best, exact);
    }

    /// <summary>log(1 + value / median): every transfer on one scale, shot-change spikes no longer dominating.</summary>
    internal static float[] Compressed(float[] x) => Normalized(x).Select(v => (float)Math.Log(1 + v)).ToArray();

    /// <summary>The signature divided by its median, so transfers with more or less grain compare on one scale.</summary>
    private static float[] Normalized(float[] x)
    {
        var med = x.Where(v => v > 0).Order().ElementAtOrDefault(x.Count(v => v > 0) / 2);
        var s = med > 1e-6 ? 1 / med : 1;
        return x.Select(v => v * s).ToArray();
    }

    [GeneratedRegex(@"out_time_us=(\d+)")]
    private static partial Regex ProgressRegex();

    /// <summary>Turns ffmpeg's raw grey frames into the signature as they arrive (no whole-film buffer).</summary>
    private sealed class SignatureSink(int size) : Stream
    {
        private readonly byte[] frame = new byte[size];
        private float[]? prev;
        private float[] cur = new float[size];
        private int fill;
        private readonly List<float> values = [];

        public int Count => values.Count;
        public float[] ToArray() => [.. values];

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var take = Math.Min(size - fill, buffer.Length);
                buffer[..take].CopyTo(frame.AsSpan(fill));
                fill += take;
                buffer = buffer[take..];
                if (fill == size)
                {
                    Frame();
                    fill = 0;
                }
            }
        }

        private void Frame()
        {
            double mean = 0, sq = 0;
            for (var i = 0; i < size; i++)
                mean += frame[i];
            mean /= size;
            for (var i = 0; i < size; i++)
                sq += (frame[i] - mean) * (frame[i] - mean);
            // contrast-neutral, but noise in a near-black frame is not blown up into motion
            var sd = Math.Max(Math.Sqrt(sq / size), 4);
            for (var i = 0; i < size; i++)
                cur[i] = (float)((frame[i] - mean) / sd);
            if (prev != null)
            {
                double d = 0;
                for (var i = 0; i < size; i++)
                    d += Math.Abs(cur[i] - prev[i]);
                values.Add((float)(d / size));
            }
            (prev, cur) = (cur, prev ?? new float[size]);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class SignatureEntry
{
    public string Data { get; set; } = "";
    public double Fps { get; set; }
    public double FirstTime { get; set; }
}

[JsonSerializable(typeof(SignatureEntry))]
internal sealed partial class SignatureJson : JsonSerializerContext;
