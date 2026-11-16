using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Compare;

/// <summary>
/// A stretch of the reference timeline (from <see cref="Start"/> to the next section) and where it is in a source:
/// source time = reference time × speed + <see cref="Offset"/>. Offset null = this part is not in the source (cut).
/// </summary>
public sealed class SyncSection
{
    public double Start { get; set; }
    public double? Offset { get; set; }
}

/// <summary>One column of a comparison: a source, the title picked from it and how its time maps to the reference.</summary>
public sealed class CompareItem
{
    public required MediaSource Source { get; init; }
    public required MediaTitle Title { get; set; }

    /// <summary>Source time at reference time 0 (for the first section when there are several).</summary>
    public double Offset
    {
        get => Sections.Count > 0 ? Sections.FirstOrDefault(s => s.Offset != null)?.Offset ?? field : field;
        set
        {
            // moving the base offset moves every section with it (frame nudges keep the edit map)
            var delta = value - Offset;
            field = value;
            foreach (var s in Sections)
            {
                if (s.Offset is { } o)
                    s.Offset = o + delta;
            }
        }
    }

    /// <summary>Source seconds per reference second: 1 normally, 23.976/25 for a PAL speed-up of a film reference.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Different edits: piecewise offsets along the reference timeline (empty = one offset for everything).</summary>
    public List<SyncSection> Sections { get; } = [];

    public string Label { get; set; } = "";

    /// <summary>The time map was set by auto-sync or by hand (ranking syncs the others first).</summary>
    public bool Synced { get; set; }

    public VideoReport? Video { get; set; }
    public List<AudioReport> Audio { get; } = [];

    /// <summary>Grade and framing compared with the reference (null until measured).</summary>
    public GradeReport? Grade { get; set; }

    public string Name => Label.Length > 0 ? Label : Source.DisplayName;

    /// <summary>Where reference time <paramref name="t"/> is in this source, or null when that part was cut from it.</summary>
    public double? SourceTime(double t)
    {
        if (Title.IsStill)
            return 0;
        var off = Offset;
        if (Sections.Count > 0)
        {
            var sec = Sections.LastOrDefault(x => x.Start <= t) ?? Sections[0];
            if (sec.Offset is not { } o)
                return null;
            off = o;
        }
        return t * Speed + off;
    }

    /// <summary>Reference time of a moment in this source (inverse of <see cref="SourceTime"/>, using the base offset).</summary>
    public double TimelineTime(double sourceTime) => (sourceTime - Offset) / Speed;

    /// <summary>Timeline time of a moment in this source, section-aware; null when it is in none of the sections.</summary>
    public double? TimelineOf(double sourceTime)
    {
        if (Sections.Count == 0)
            return (sourceTime - Offset) / Speed;
        for (var i = 0; i < Sections.Count; i++)
        {
            if (Sections[i].Offset is not { } o)
                continue;
            var t = (sourceTime - o) / Speed;
            var end = i + 1 < Sections.Count ? Sections[i + 1].Start : double.MaxValue;
            if (t >= Sections[i].Start - 0.5 && t < end + 0.5)
                return t;
        }
        return null;
    }

    /// <summary>Part of the reference timeline this source covers.</summary>
    public (double Start, double End) Coverage()
    {
        if (Title.IsStill)
            return (double.NegativeInfinity, double.PositiveInfinity);
        var first = Sections.Count > 0 ? Sections.First(s => s.Offset != null).Offset!.Value : Offset;
        var last = Sections.Count > 0 ? Sections.Last(s => s.Offset != null).Offset!.Value : Offset;
        return (-first / Speed, (Title.Duration - last) / Speed);
    }

    /// <summary>Short description of the time map for cards, specs and reports.</summary>
    public string MapSummary
    {
        get
        {
            var parts = new List<string> { $"{Offset:+0.000;-0.000;0.000} s" };
            if (Math.Abs(Speed - 1) > 1e-6)
                parts.Add($"speed ×{Speed:0.#####}");
            if (Sections.Count > 1 || Sections.Any(s => s.Offset == null))
                parts.Add($"{Sections.Count} sections ({Sections.Count(s => s.Offset == null)} missing)");
            return string.Join(", ", parts);
        }
    }

    public override string ToString() => Name;
}

public sealed class ShotRow
{
    public double Time { get; init; }
    public string Caption { get; set; } = "";

    /// <summary>One result per compare item (same order), null while pending.</summary>
    public List<ShotResult?> Shots { get; } = [];
}

/// <summary>Which time to ask each source for so every screenshot in a row shows the same frame.</summary>
public static class FrameMatch
{
    /// <summary>
    /// ffmpeg returns the first frame at or after the requested time, so a few milliseconds of offset error (or frame
    /// grids with a different phase) can pick the next frame. Once the reference frame's real time is known, ask the
    /// other source for half a frame before its matching frame: that always lands on the nearest one.
    /// Returns null when the moment is not in that source (a cut in a different edit).
    /// </summary>
    public static double? TargetTime(CompareItem reference, double? referenceFrameTime, double rowTime, CompareItem item)
    {
        if (item == reference || referenceFrameTime is not { } rt || double.IsNaN(rt))
            return item.SourceTime(rowTime);
        var t = reference.TimelineTime(rt);
        if (item.SourceTime(t) is not { } st)
            return null;
        var fps = item.Title.Info?.Video?.FrameRate is > 0 and var f ? f : 24000 / 1001.0;
        return Math.Max(0, st - 0.5 / fps);
    }
}

/// <summary>Picks the same subtitle in every source: by language (first full, non-forced track) or by track number.</summary>
public static class SubtitleChoice
{
    /// <summary>Index among the title's subtitle streams, or null when there is no match.</summary>
    public static int? Pick(MediaTitle title, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || title.Info == null)
            return null;
        var subs = title.Info.Subtitles.ToList();
        if (int.TryParse(spec, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n >= 1 && n <= subs.Count ? n - 1 : null;
        var matches = subs.Select((s, i) => (s, i)).Where(x => LangMatches(x.s.Language, spec)).ToList();
        if (matches.Count == 0)
            return null;
        // prefer a full track over a forced-only one
        return matches.OrderBy(x => x.s.Forced ? 1 : 0).First().i;
    }

    /// <summary>"en" matches "eng", "fre" matches "fra" and "fr" …</summary>
    public static bool LangMatches(string lang, string spec)
    {
        if (lang.Length == 0)
            return false;
        if (lang.Equals(spec, StringComparison.OrdinalIgnoreCase))
            return true;
        static string Norm(string l) => l.ToLowerInvariant() switch
        {
            "en" or "eng" => "en",
            "fr" or "fre" or "fra" => "fr",
            "de" or "ger" or "deu" => "de",
            "es" or "spa" => "es",
            "it" or "ita" => "it",
            "nl" or "dut" or "nld" => "nl",
            "pt" or "por" => "pt",
            "ja" or "jpn" => "ja",
            "zh" or "chi" or "zho" => "zh",
            "sv" or "swe" => "sv",
            "da" or "dan" => "da",
            "no" or "nor" or "nob" => "no",
            "fi" or "fin" => "fi",
            "pl" or "pol" => "pl",
            var x => x,
        };
        return Norm(lang) == Norm(spec);
    }
}

/// <summary>"Specs" table: one row per property, one value per source.</summary>
public static class SpecTable
{
    public sealed record Row(string Group, string Label, string[] Values)
    {
        /// <summary>True when the sources disagree (empty values ignored).</summary>
        public bool Differs => Values.Where(v => v.Length > 0 && v != "—").Distinct().Count() > 1;
    }

    public static List<Row> Build(IReadOnlyList<CompareItem> items)
    {
        var rows = new List<Row>();
        void Add(string group, string label, Func<CompareItem, MediaInfoSummary, string> f)
        {
            var vals = items.Select(i => i.Title.Info is { } inf ? f(i, inf) : "").ToArray();
            if (vals.Any(v => v.Length > 0))
                rows.Add(new Row(group, label, vals));
        }

        Add("General", "Source", (i, _) => i.Source.Kind switch
        {
            SourceKind.BluRay => "Blu-ray" + (i.Source.MountedIso != null ? " ISO" : " folder"),
            SourceKind.Dvd => "DVD" + (i.Source.MountedIso != null ? " ISO" : " folder"),
            _ => System.IO.Path.GetExtension(i.Source.Path).TrimStart('.').ToUpperInvariant() + " file",
        });
        Add("General", "Title", (i, _) => i.Source.Kind == SourceKind.File ? "" : i.Title.Name);
        Add("General", "Container", (_, m) => m.Container);
        Add("General", "Duration", (_, m) => Format.Time(m.Duration));
        Add("General", "Size", (_, m) => Format.Size(m.SizeBytes));
        Add("General", "Overall bitrate", (_, m) => Format.BitRate(m.OverallBitRate));
        Add("General", "Chapters", (_, m) => m.Chapters.Count > 0 ? m.Chapters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none");
        Add("General", "Time map", (i, _) => i.MapSummary);
        Add("General", "Encoder", (_, m) => m.Encoder);

        Add("Video", "Codec", (_, m) => m.Video?.CodecDisplay ?? "");
        Add("Video", "Resolution", (_, m) => m.Video is { Width: > 0 } v ? $"{v.Width}×{v.Height}" : "");
        Add("Video", "Aspect (DAR / SAR)", (_, m) => m.Video is { } v ? $"{v.Dar} / {v.Sar}".Trim(' ', '/') : "");
        Add("Video", "Frame rate", (_, m) => m.Video?.FrameRateText ?? "");
        Add("Video", "Scan", (_, m) => m.Video is { } v ? (v.ScanType.Length > 0 ? v.ScanType : v.FieldOrder) : "");
        Add("Video", "Bit depth", (_, m) => m.Video is { BitDepth: > 0 } v ? $"{v.BitDepth}-bit" : "");
        Add("Video", "Chroma", (_, m) => m.Video?.ChromaSubsampling ?? "");
        Add("Video", "Pixel format", (_, m) => m.Video?.PixFmt ?? "");
        Add("Video", "Bitrate", (_, m) => m.Video is { BitRate: > 0 } v ? Format.BitRate(v.BitRate) : "");
        Add("Video", "Color range", (_, m) => m.Video?.ColorRange ?? "");
        Add("Video", "Primaries / transfer / matrix", (_, m) => m.Video is { } v ? string.Join(" / ", new[] { v.ColorPrimaries, v.ColorTransfer, v.ColorSpace }.Where(x => x.Length > 0)) : "");
        Add("Video", "HDR", (_, m) => m.Video is { } v ? string.Join(" · ", new[] { v.HdrShort, v.HdrFormat }.Where(x => x.Length > 0).Distinct()) : "");
        Add("Video", "Mastering display", (_, m) => m.Video?.MasteringDisplay ?? "");
        Add("Video", "Content light", (_, m) => m.Video?.ContentLight ?? "");
        Add("Video", "Encoding settings", (_, m) => m.EncodingSettings.Length > 160 ? m.EncodingSettings[..160] + "…" : m.EncodingSettings);

        var maxAudio = items.Max(i => i.Title.Info?.Audio.Count() ?? 0);
        for (var a = 0; a < maxAudio; a++)
        {
            var idx = a;
            Add("Audio", $"Track {a + 1}", (_, m) => m.Audio.ElementAtOrDefault(idx)?.Label ?? "");
        }
        var maxSubs = items.Max(i => i.Title.Info?.Subtitles.Count() ?? 0);
        if (maxSubs > 0)
            Add("Subtitles", "Tracks", (_, m) => string.Join(", ", m.Subtitles.Select(s => (s.Language.Length > 0 ? s.Language : "und") + (s.Forced ? " (forced)" : "") + " " + s.Codec.Replace("hdmv_pgs_subtitle", "PGS").Replace("dvd_subtitle", "VobSub").Replace("subrip", "SRT"))));

        // analysis results, when they exist
        if (items.Any(i => i.Video != null))
        {
            Add("Analysis", "Sharpness", (i, _) => i.Video is { } r ? Format.Num(r.AvgSharpness, "0.0") : "");
            Add("Analysis", "HF energy (H / V)", (i, _) => i.Video is { } r ? $"{r.AvgHfRatioH:0.0} / {r.AvgHfRatioV:0.0} dB" : "");
            Add("Analysis", "Grain / noise", (i, _) => i.Video is { } r && !double.IsNaN(r.AvgNoise) ? $"{r.AvgNoise:0.00}" : "");
            Add("Analysis", "Flat-area step length (banding)", (i, _) => i.Video is { } r && !double.IsNaN(r.AvgBandRun) ? $"{r.AvgBandRun:0.0} px" : "");
            Add("Analysis", "Edge overshoot (halos)", (i, _) => i.Video is { } r && !double.IsNaN(r.AvgHalo) ? $"{r.AvgHalo:P0}" : "");
            Add("Analysis", "Chroma detail vs luma (H / V)", (i, _) => i.Video is { } r && !double.IsNaN(r.ChromaHfH) ? $"{r.ChromaHfH:0} / {r.ChromaHfV:0} dB" : "");
            Add("Analysis", "Dynamic HDR metadata", (i, _) => i.Video is { } r && r.Frames.FirstOrDefault(f => f.DynamicHdr != null)?.DynamicHdr is { } d
                ? $"{d.Kind}: max {r.Frames.Where(f => f.DynamicHdr != null).Min(f => f.DynamicHdr!.MaxNits):0}–{r.Frames.Where(f => f.DynamicHdr != null).Max(f => f.DynamicHdr!.MaxNits):0} nits" : "");
            Add("Analysis", "Native resolution", (i, _) => i.Video?.NativeRes is { } n ? n.NativeHeight is { } h ? $"≈{h}p ({n.Kernel}, {n.Confidence:P0})" : "native / unknown" : "");
            Add("Analysis", "Effective bit depth", (i, _) => i.Video is { Frames.Count: > 0 } r ? $"{r.Frames.Min(f => f.Levels.EffectiveBits)}-bit" : "");
            Add("Analysis", "Active picture", (i, _) => i.Video is { Crop: { } c } r ? $"{c.Width(r.Width)}×{c.Height(r.Height)}" : "");
            Add("Analysis", "Scan (detected)", (i, _) => i.Video?.Interlace?.Verdict ?? "");
            Add("Analysis", "Peak brightness", (i, _) => i.Video is { Frames.Count: > 0 } r && r.Frames.Any(f => !double.IsNaN(f.PeakNits)) ? $"{r.Frames.Max(f => f.PeakNits):0} nits" : "");
        }
        if (items.Any(i => i.Audio.Count > 0))
        {
            Add("Analysis", "Loudness (track 1)", (i, _) => i.Audio.FirstOrDefault() is { } a && !double.IsNaN(a.IntegratedLufs) ? $"{a.IntegratedLufs:0.0} LUFS, LRA {a.LoudnessRange:0.0}, TP {a.TruePeakDb:0.0}" : "");
            Add("Analysis", "Audio cutoff (track 1)", (i, _) => i.Audio.FirstOrDefault() is { } a && !double.IsNaN(a.CutoffHz) ? $"{a.CutoffHz / 1000:0.0} kHz" : "");
            Add("Analysis", "Bits used (track 1)", (i, _) => i.Audio.FirstOrDefault()?.Overall.BitDepth ?? "");
        }
        return rows;
    }
}
