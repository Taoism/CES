using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ces.Model;
using Ces.Tools;

namespace Ces.Analysis;

/// <summary>Per-frame dynamic HDR metadata: what HDR10+ or Dolby Vision tells the display about this shot.</summary>
public sealed class DynamicHdr
{
    public string Kind { get; set; } = "";

    /// <summary>Shot maximum (HDR10+ maxscl / DV L1 max) in nits.</summary>
    public double MaxNits { get; set; } = double.NaN;

    /// <summary>Shot average (HDR10+ average maxRGB / DV L1 avg) in nits.</summary>
    public double AvgNits { get; set; } = double.NaN;

    public double TargetNits { get; set; } = double.NaN;

    /// <summary>Shot minimum (DV L1 min) in nits.</summary>
    public double MinNits { get; set; } = double.NaN;

    /// <summary>Dolby Vision level-2 trims: the target displays (nits) the grade was trimmed for.</summary>
    public List<double> TrimTargetsNits { get; set; } = [];

    /// <summary>
    /// Dolby Vision without level-1 values: MaxNits is the RPU's mastering-display maximum, the same in every frame —
    /// not a per-shot value.
    /// </summary>
    public bool MasteringOnly { get; set; }

    /// <summary>Dolby Vision: the RPU itself was read (so a missing level 1 really is missing).</summary>
    public bool RpuRead { get; set; }

    /// <summary>The RPU's source maximum (12-bit PQ) as ffprobe read it: our own RPU parse has to agree.</summary>
    internal int SourceMaxPq { get; set; } = -1;
}

/// <summary>Reads HDR10+ / Dolby Vision frame metadata with ffprobe at a title time.</summary>
public sealed partial class HdrMetadata(FfTools tools)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<DynamicHdr?> ReadAsync(MediaTitle title, double time, CancellationToken ct = default)
    {
        var seg = InputBuilder.SegmentAt(title, time);
        if (seg.Format == "mpeg")
            return null;
        // read_intervals takes stream timestamps: absolute (in point based) for Blu-ray clips, from 0 for files
        var at = seg.InPoint.HasValue ? seg.InPoint.Value + (time - seg.TitleStart) : seg.SeekBase + (time - seg.TitleStart);
        var args = new List<string>
        {
            // the read starts at the keyframe before 'at': read up to it and keep the frame at that time
            "-v", "error", "-select_streams", "v:0", "-read_intervals", $"{at.ToString("0.###", Inv)}%{(at + 0.1).ToString("0.###", Inv)}",
            "-show_frames", "-show_entries", "frame=pts_time,side_data_list", "-of", "json", seg.Url,
        };
        var res = await ProcessRunner.RunAsync(tools.FFprobe, args, ct).ConfigureAwait(false);
        var d = Parse(res.StdOutText, at);
        // ffprobe stops at the mastering values: the per-shot level-1 block is in the RPU's extension blocks
        if (d is { Kind: "Dolby Vision", MasteringOnly: true })
            await AddRpuAsync(d, seg, seg.SeekBase + (time - seg.TitleStart), ct).ConfigureAwait(false);
        return d;
    }

    private async Task AddRpuAsync(DynamicHdr d, TitleSegment seg, double seek, CancellationToken ct)
    {
        // a dual-layer (profile 7) Blu-ray keeps the RPU in the enhancement-layer stream
        foreach (var stream in new[] { 0, 1 })
        {
            var rpu = await ReadRpuAsync(seg, seek, stream, ct).ConfigureAwait(false);
            if (rpu is null)
                continue;
            if (d.SourceMaxPq >= 0 && rpu.SourceMaxPq != d.SourceMaxPq)
                return; // our parse and FFmpeg's disagree: not trusted
            Apply(d, rpu);
            return;
        }
    }

    internal static void Apply(DynamicHdr d, DoviRpuData rpu)
    {
        d.RpuRead = true;
        d.TrimTargetsNits = rpu.TrimTargetsPq.Select(p => NominalNits(PqNits(p / 4095.0))).ToList();
        if (rpu.Level1 is not var (min, max, avg))
            return;
        d.MaxNits = PqNits(max / 4095.0);
        d.AvgNits = PqNits(avg / 4095.0);
        d.MinNits = PqNits(min / 4095.0);
        d.MasteringOnly = false;
    }

    /// <summary>
    /// The RPU of the frame at <paramref name="seek"/> (segment seconds): the packets are copied, not decoded, from
    /// the keyframe before it; their times (relative to the seek point, so the wanted frame is the last one at or
    /// before 0) come from FFmpeg's timestamp log in decode order, the same order as the RPUs in the stream.
    /// </summary>
    private async Task<DoviRpuData?> ReadRpuAsync(TitleSegment seg, double seek, int stream, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "info" };
        if (seek > 0.0005)
            args.AddRange(["-ss", seek.ToString("0.######", Inv)]);
        args.AddRange(["-t", "0.1"]);
        if (seg.Format != null)
            args.AddRange(["-f", seg.Format]);
        args.AddRange(["-i", seg.Url, "-map", $"0:v:{stream}", "-c", "copy", "-bsf:v", "hevc_mp4toannexb", "-debug_ts", "-f", "hevc", "pipe:1"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var rpus = DoviRpu.RpuNals(res.StdOut);
        if (rpus.Count == 0)
            return null;
        var times = MuxerTimeRegex().Matches(res.StdErr)
            .Select(m => double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out var t) ? t : double.NaN).ToList();
        return DoviRpu.Parse(rpus[PickFrame(times, rpus.Count)]);
    }

    /// <summary>A trim target as the display class it stands for: 12-bit PQ codes land just off 100 / 600 / 1000 nits.</summary>
    internal static double NominalNits(double nits) => nits >= 100 ? Math.Round(nits / 10) * 10 : Math.Round(nits);

    /// <summary>The packet shown at the seek point: the latest time at or before 0, else the earliest one.</summary>
    internal static int PickFrame(IReadOnlyList<double> times, int count)
    {
        int best = -1, first = 0;
        for (var i = 0; i < Math.Min(times.Count, count); i++)
        {
            var t = times[i];
            if (double.IsNaN(t))
                continue;
            if (t <= 0.0005 && (best < 0 || t > times[best]))
                best = i;
            if (t < times[first] || double.IsNaN(times[first]))
                first = i;
        }
        return best >= 0 ? best : Math.Min(first, count - 1);
    }

    [GeneratedRegex(@"muxer <- pts:\S+ pts_time:(\S+)")]
    private static partial Regex MuxerTimeRegex();

    internal static DynamicHdr? Parse(string json, double at = double.NaN)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("frames", out var frames))
                return null;
            // the last frame at or before the wanted time (or the first one when none is)
            var list0 = frames.EnumerateArray().ToList();
            var pick = list0;
            if (!double.IsNaN(at) && list0.Count > 1)
            {
                var before = list0.Where(fr => fr.TryGetProperty("pts_time", out var pt) && double.TryParse(pt.GetString(), NumberStyles.Float, Inv, out var t) && t <= at + 0.001).ToList();
                pick = before.Count > 0 ? [before[^1]] : [list0[0]];
            }
            foreach (var fr in pick)
            {
                if (!fr.TryGetProperty("side_data_list", out var list))
                    continue;
                foreach (var sd in list.EnumerateArray())
                {
                    var type = sd.TryGetProperty("side_data_type", out var t) ? t.GetString() ?? "" : "";
                    if (type.Contains("2094-40", StringComparison.Ordinal))
                        return ParseHdr10Plus(sd);
                    if (type.Contains("Dolby Vision Metadata", StringComparison.OrdinalIgnoreCase) || type.Contains("DOVI", StringComparison.OrdinalIgnoreCase) && type.Contains("Metadata", StringComparison.OrdinalIgnoreCase))
                        return ParseDovi(sd);
                }
            }
        }
        catch (JsonException)
        {
            // no metadata
        }
        return null;
    }

    /// <summary>HDR10+: maxscl / average_maxrgb are linear fractions of 10000 nits (ffprobe prints them as rationals, keys repeat).</summary>
    private static DynamicHdr ParseHdr10Plus(JsonElement sd)
    {
        var d = new DynamicHdr { Kind = "HDR10+" };
        var maxscl = new List<double>();
        foreach (var p in sd.EnumerateObject())
        {
            switch (p.Name)
            {
                case "maxscl":
                    maxscl.Add(Rational(p.Value) * 10000);
                    break;
                case "average_maxrgb":
                    d.AvgNits = Rational(p.Value) * 10000;
                    break;
                case "targeted_system_display_maximum_luminance":
                    d.TargetNits = Rational(p.Value);
                    break;
            }
        }
        if (maxscl.Count > 0)
            d.MaxNits = maxscl.Max();
        return d;
    }

    /// <summary>Dolby Vision: level 1 min/max/avg PQ (12-bit) when ffprobe prints the extension blocks, else the RPU's source max.</summary>
    private static DynamicHdr ParseDovi(JsonElement sd)
    {
        var d = new DynamicHdr { Kind = "Dolby Vision" };
        double? max = null, avg = null, srcMax = null;
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var isL1 = e.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number && lv.GetInt32() == 1;
                foreach (var p in e.EnumerateObject())
                {
                    if (isL1 && p.Name == "max_pq")
                        max = Rational(p.Value);
                    else if (isL1 && p.Name == "avg_pq")
                        avg = Rational(p.Value);
                    else if (p.Name == "source_max_pq")
                        srcMax = Rational(p.Value);
                    else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray())
                    Walk(x);
            }
        }
        Walk(sd);
        if (max is { } m)
        {
            d.MaxNits = PqNits(m / 4095);
        }
        else if (srcMax is { } s)
        {
            d.MaxNits = PqNits(s / 4095);
            d.MasteringOnly = true;
            d.SourceMaxPq = (int)Math.Round(s);
        }
        if (avg is { } a)
            d.AvgNits = PqNits(a / 4095);
        return d;
    }

    /// <summary>PQ signal (0–1) to nits.</summary>
    public static double PqNits(double e)
    {
        e = Math.Clamp(e, 0, 1);
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var p = Math.Pow(e, 1 / m2);
        return 10000 * Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
    }

    private static double Rational(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        var s = v.GetString() ?? "";
        var i = s.IndexOf('/');
        if (i < 0)
            return double.TryParse(s, NumberStyles.Float, Inv, out var x) ? x : double.NaN;
        return double.TryParse(s[..i], NumberStyles.Float, Inv, out var a) && double.TryParse(s[(i + 1)..], NumberStyles.Float, Inv, out var b) && b != 0 ? a / b : double.NaN;
    }
}
