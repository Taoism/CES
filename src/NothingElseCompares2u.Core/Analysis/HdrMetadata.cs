using System.Globalization;
using System.Text.Json;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

/// <summary>Per-frame dynamic HDR metadata: what HDR10+ or Dolby Vision tells the display about this shot.</summary>
public sealed class DynamicHdr
{
    public string Kind { get; set; } = "";

    /// <summary>Shot maximum (HDR10+ maxscl / DV L1 max) in nits.</summary>
    public double MaxNits { get; set; } = double.NaN;

    /// <summary>Shot average (HDR10+ average maxRGB / DV L1 avg) in nits.</summary>
    public double AvgNits { get; set; } = double.NaN;

    public double TargetNits { get; set; } = double.NaN;
}

/// <summary>Reads HDR10+ / Dolby Vision frame metadata with ffprobe at a title time.</summary>
public sealed class HdrMetadata(FfTools tools)
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
            "-v", "error", "-select_streams", "v:0", "-read_intervals", $"{at.ToString("0.###", Inv)}%+#1",
            "-show_frames", "-show_entries", "frame=side_data_list", "-of", "json", seg.Url,
        };
        var res = await ProcessRunner.RunAsync(tools.FFprobe, args, ct).ConfigureAwait(false);
        return Parse(res.StdOutText);
    }

    internal static DynamicHdr? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("frames", out var frames))
                return null;
            foreach (var fr in frames.EnumerateArray())
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
            d.MaxNits = PqNits(m / 4095);
        else if (srcMax is { } s)
            d.MaxNits = PqNits(s / 4095);
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
