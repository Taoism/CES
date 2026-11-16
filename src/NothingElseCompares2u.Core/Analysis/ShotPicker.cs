using System.Globalization;
using System.Text.RegularExpressions;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

public sealed record KeyframeInfo(double Time, double Mean, double Detail);

public sealed record ShotPick(double Time, string Caption);

/// <summary>
/// Picks screenshot times worth comparing: keyframes sampled across the title (small, grey) are scored by detail and
/// exposure; each part of the timeline gets its most detailed, well-lit shot — no black frames, fades or flat credits.
/// </summary>
public sealed partial class ShotPicker(FfTools tools, string tempDir)
{
    private const int W = 96;
    private const int H = 54;

    /// <summary>
    /// Samples <paramref name="samples"/> keyframes spread over the title (a fast seek each, a few in parallel):
    /// seconds even for a 50 GB disc, where decoding every keyframe would have to read the whole file.
    /// </summary>
    public async Task<List<KeyframeInfo>> ScanAsync(MediaTitle title, IProgress<double>? progress = null, int samples = 150, CancellationToken ct = default)
    {
        var points = InputBuilder.EvenPoints(title.Duration, samples, 1, 3);
        var results = new KeyframeInfo?[points.Count];
        var done = 0;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(points.Select(async (t, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[i] = await GrabAsync(title, t, ct).ConfigureAwait(false);
                progress?.Report((double)Interlocked.Increment(ref done) / points.Count);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        var list = results.OfType<KeyframeInfo>().GroupBy(k => Math.Round(k.Time, 2)).Select(g => g.First()).OrderBy(k => k.Time).ToList();
        return list;
    }

    private async Task<KeyframeInfo?> GrabAsync(MediaTitle title, double t, CancellationToken ct)
    {
        // no accurate seek: the keyframe at or before t is what we want, and it needs no decoding of other frames
        var input = InputBuilder.Input(title, t, 5, tempDir, exactSeek: false);
        var args = new List<string> { "-hide_banner", "-nostdin" };
        args.AddRange(input.Args);
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", $"scale={W}:{H}:flags=area,format=gray,showinfo", "-frames:v", "1", "-f", "rawvideo", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var m = PtsRegex().Match(res.StdErr);
        const int size = W * H;
        if (!m.Success || res.StdOut.Length < size)
            return null;
        var time = t + double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var px = res.StdOut;
        double mean = 0, detail = 0;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var k = y * W + x;
                mean += px[k];
                if (x + 1 < W)
                    detail += Math.Abs(px[k] - px[k + 1]);
                if (y + 1 < H)
                    detail += Math.Abs(px[k] - px[k + W]);
            }
        }
        return new KeyframeInfo(Math.Max(0, time), mean / size, detail / size);
    }

    /// <summary>
    /// <paramref name="count"/> times between <paramref name="start"/> and <paramref name="end"/>, one per equal part,
    /// each the best-scoring keyframe of its part, nudged a third of a second in so the reference is not always an I-frame.
    /// </summary>
    public static List<ShotPick> Pick(IReadOnlyList<KeyframeInfo> frames, int count, double start, double end)
    {
        var picks = new List<ShotPick>();
        if (frames.Count == 0 || count <= 0 || end <= start)
            return picks;
        // skip the very start and end (logos, credits)
        var a = start + (end - start) * 0.03;
        var b = end - (end - start) * 0.06;
        var details = frames.Select(f => f.Detail).Order().ToList();
        var floor = details[details.Count / 4];
        for (var k = 0; k < count; k++)
        {
            var from = a + (b - a) * k / count;
            var to = a + (b - a) * (k + 1) / count;
            var best = frames.Where(f => f.Time >= from && f.Time < to)
                .Select(f => (f, Score: Score(f, floor)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best.f == null)
            {
                picks.Add(new ShotPick((from + to) / 2, "no keyframe here"));
                continue;
            }
            var f = best.f;
            picks.Add(new ShotPick(Math.Min(f.Time + 0.33, to), $"detailed shot · detail {f.Detail:0.0} · brightness {f.Mean:0}"));
        }
        return picks;
    }

    private static double Score(KeyframeInfo f, double detailFloor)
    {
        // well exposed, detailed; dark/blown/flat frames lose
        var exposure = f.Mean switch
        {
            < 18 => 0.05,
            < 40 => 0.5,
            > 235 => 0.1,
            > 210 => 0.6,
            _ => 1.0,
        };
        var flat = f.Detail < detailFloor ? 0.5 : 1.0;
        return f.Detail * exposure * flat;
    }

    [GeneratedRegex(@"\] n:\s*\d+\s+pts:\s*-?\d+\s+pts_time:(-?[\d.]+)")]
    private static partial Regex PtsRegex();
}
