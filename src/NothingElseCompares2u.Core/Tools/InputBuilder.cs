using System.Globalization;
using System.Text;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Tools;

/// <summary>Builds ffmpeg input arguments that start at a given title time, for files and disc titles alike.</summary>
/// <summary>ffmpeg input arguments plus the seconds to discard after decoding (see <see cref="InputBuilder.Input"/>).</summary>
public sealed record InputSpec(List<string> Args, double Skip)
{
    /// <summary>Output-side "-ss" that drops the preroll (empty when there is none).</summary>
    public List<string> OutputSkip => Skip > 0 ? ["-ss", InputBuilder.S(Skip)] : [];

    /// <summary>Filter prefix that drops the preroll inside a filter graph.</summary>
    public string TrimFilter => Skip > 0 ? $"trim=start={InputBuilder.S(Skip)}," : "";

    /// <summary>Audio filter prefix that drops the preroll.</summary>
    public string ATrimFilter => Skip > 0 ? $"atrim=start={InputBuilder.S(Skip)}," : "";
}

public static class InputBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string S(double v) => v.ToString("0.######", Inv);

    /// <summary>Seconds decoded before the target on inputs whose seeking can overshoot (MPEG-TS/PS).</summary>
    public const double Preroll = 3;

    /// <summary>ffmpeg -hwaccel value for video decoding (e.g. "d3d11va", "auto"), null = software.</summary>
    public static string? HardwareDecode { get; set; }

    /// <summary>Filter that brings hardware-decoded frames back to the stream's own pixel format (empty without hwaccel).</summary>
    public static string SoftwareFormat(MediaTitle title) =>
        HardwareDecode != null && title.Info?.Video?.PixFmt is { Length: > 0 } pf ? $"format={pf}," : "";

    /// <summary>
    /// Input arguments ("-ss …", "-i …") positioned at <paramref name="time"/> (title seconds), able to read
    /// <paramref name="length"/> seconds (null = to the end). Short reads inside one segment open that segment
    /// directly; longer reads go through a concat list in <paramref name="tempDir"/>.
    /// <para>MPEG-TS/PS seeking can land on a keyframe after the target, so those inputs are opened a little early:
    /// <see cref="InputSpec.Skip"/> seconds must then be dropped after decoding (output -ss, or trim in a filter).</para>
    /// </summary>
    public static InputSpec Input(MediaTitle title, double time, double? length, string tempDir, bool exactSeek = true, double minPreroll = 0)
    {
        if (title.IsStill)
            return new InputSpec(["-i", title.Segments[0].Url], 0);
        time = Math.Max(0, time);
        var seg = SegmentAt(title, time);
        var args = new List<string>();
        var end = length.HasValue ? time + length.Value : double.MaxValue;
        var single = title.Segments.Count == 1 || seg.TitleStart + seg.Duration >= Math.Min(end, title.Duration) - 0.001 || seg.Duration <= 0;
        var seek = single ? seg.SeekBase + (time - seg.TitleStart) : time - seg.TitleStart;
        var skip = 0.0;
        if (exactSeek && (IsTsOrPs(seg) || minPreroll > 0) && seek > 0.0005)
        {
            skip = Math.Min(Math.Max(IsTsOrPs(seg) ? Preroll : 0, minPreroll), seek);
            seek -= skip;
        }
        if (HardwareDecode != null && title.Info?.Video != null)
            args.AddRange(["-hwaccel", HardwareDecode]);
        if (seg.Format == "mpeg")
            args.AddRange(["-analyzeduration", "50M", "-probesize", "50M"]);
        if (seek > 0.0005)
            args.AddRange(["-ss", S(seek)]);
        if (!exactSeek)
            args.Add("-noaccurate_seek");
        if (length.HasValue)
            args.AddRange(["-t", S(length.Value + skip)]);

        if (single)
        {
            if (seg.Format != null)
                args.AddRange(["-f", seg.Format]);
            args.AddRange(["-i", seg.Url]);
            return new InputSpec(args, skip);
        }

        // spans segments: concat demuxer list starting at the segment that holds 'time'
        var first = title.Segments.IndexOf(seg);
        var list = new StringBuilder("ffconcat version 1.0\n");
        foreach (var s in title.Segments.Skip(first))
        {
            if (s.TitleStart >= end)
                break;
            list.Append("file '").Append(s.Url.Replace("'", "'\\''")).Append("'\n");
            if (s.InPoint.HasValue)
                list.Append("inpoint ").Append(S(s.InPoint.Value)).Append('\n');
            if (s.OutPoint.HasValue)
                list.Append("outpoint ").Append(S(s.OutPoint.Value)).Append('\n');
            list.Append("duration ").Append(S(s.Duration)).Append('\n');
        }
        Directory.CreateDirectory(tempDir);
        var listPath = Path.Combine(tempDir, $"concat-{Guid.NewGuid():N}.ffconcat");
        File.WriteAllText(listPath, list.ToString(), new UTF8Encoding(false));
        args.AddRange(["-f", "concat", "-safe", "0", "-protocol_whitelist", "file,subfile,concat,crypto,data", "-i", listPath]);
        return new InputSpec(args, skip);
    }

    private static bool IsTsOrPs(TitleSegment seg)
    {
        if (seg.Format == "mpeg")
            return true;
        var ext = Path.GetExtension(seg.Url).ToLowerInvariant();
        return ext is ".m2ts" or ".mts" or ".ts" or ".m2t" or ".vob" or ".mpg" or ".mpeg" or ".evo";
    }

    public static TitleSegment SegmentAt(MediaTitle title, double time)
    {
        TitleSegment? best = null;
        foreach (var s in title.Segments)
        {
            if (s.TitleStart <= time + 0.0005)
                best = s;
            else
                break;
        }
        return best ?? title.Segments[0];
    }

    /// <summary>Evenly spread points that avoid the first and last few percent (logos, credits).</summary>
    public static List<double> EvenPoints(double duration, int count, double skipStartPct = 5, double skipEndPct = 8)
    {
        var pts = new List<double>();
        if (duration <= 0 || count <= 0)
            return pts;
        var a = duration * skipStartPct / 100;
        var b = duration * (1 - skipEndPct / 100);
        if (b <= a)
        {
            a = 0;
            b = duration;
        }
        for (var i = 0; i < count; i++)
            pts.Add(count == 1 ? (a + b) / 2 : a + (b - a) * i / (count - 1));
        return pts;
    }

    /// <summary>Random but reproducible points (same seed, same points) so a re-run picks the same frames.</summary>
    public static List<double> RandomPoints(double duration, int count, int seed)
    {
        var rnd = new Random(seed);
        var a = duration * 0.05;
        var b = duration * 0.92;
        return Enumerable.Range(0, count).Select(_ => a + rnd.NextDouble() * (b - a)).Order().ToList();
    }
}
