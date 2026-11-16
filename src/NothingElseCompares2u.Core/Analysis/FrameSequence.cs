using System.Globalization;
using System.Text.RegularExpressions;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

/// <summary>A small grey frame with its title time.</summary>
public sealed record GrayFrame(double Time, float[] Pixels, double Mean);

/// <summary>Runs of small grey frames (with real timestamps) for sync, cut detection and temporal checks.</summary>
public static partial class FrameSequence
{
    /// <summary>
    /// <paramref name="count"/> consecutive frames from title time <paramref name="start"/>, scaled to w×h grey.
    /// With <paramref name="normalize"/> each frame has its mean removed (brightness-neutral matching).
    /// </summary>
    public static async Task<List<GrayFrame>> GrabAsync(FfTools tools, MediaTitle title, double start, int count, int w, int h, string tempDir, bool normalize, CancellationToken ct)
    {
        var frames = new List<GrayFrame>();
        if (start < 0)
            return frames;
        var fps = Math.Max(1, InputBuilder.FrameRate(title));
        var args = new List<string> { "-hide_banner", "-nostdin" };
        var input = InputBuilder.Input(title, start, count / fps + 2, tempDir);
        args.AddRange(input.Args);
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", InputBuilder.VideoPrefix(title) + input.TrimFilter + $"scale={w}:{h}:flags=area,format=gray,showinfo",
            "-frames:v", count.ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough", "-f", "rawvideo", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var times = PtsRegex().Matches(res.StdErr).Select(x => double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        var size = w * h;
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
            if (normalize)
            {
                for (var k = 0; k < size; k++)
                    px[k] -= (float)mean;
            }
            frames.Add(new GrayFrame(start - input.Skip + times[i] - (input.VideoTrimStart - input.Skip), px, mean));
        }
        return frames;
    }

    /// <summary>Mean absolute difference of two equally sized frames.</summary>
    public static double Diff(float[] a, float[] b)
    {
        double d = 0;
        for (var i = 0; i < a.Length; i++)
            d += Math.Abs(a[i] - b[i]);
        return d / a.Length;
    }

    /// <summary>The frame whose time is nearest to <paramref name="t"/> (within half a frame), or null.</summary>
    public static GrayFrame? At(IReadOnlyList<GrayFrame> frames, double t, double fps)
    {
        GrayFrame? best = null;
        var bestD = 0.5 / Math.Max(1, fps);
        foreach (var f in frames)
        {
            var d = Math.Abs(f.Time - t);
            if (d <= bestD)
            {
                bestD = d;
                best = f;
            }
        }
        return best;
    }

    [GeneratedRegex(@"\] n:\s*\d+\s+pts:\s*-?\d+\s+pts_time:(-?[\d.]+)")]
    private static partial Regex PtsRegex();
}
