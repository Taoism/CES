using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Compare;

/// <summary>One column of a comparison: a source, the title picked from it and its time offset.</summary>
public sealed class CompareItem
{
    public required MediaSource Source { get; init; }
    public required MediaTitle Title { get; set; }

    /// <summary>Seconds added to the common timeline to reach the same moment in this source.</summary>
    public double Offset { get; set; }

    public string Label { get; set; } = "";

    public VideoReport? Video { get; set; }
    public List<AudioReport> Audio { get; } = [];

    public string Name => Label.Length > 0 ? Label : Source.DisplayName;

    public override string ToString() => Name;
}

public sealed class ShotRow
{
    public double Time { get; init; }
    public string Caption { get; set; } = "";

    /// <summary>One result per compare item (same order), null while pending.</summary>
    public List<ShotResult?> Shots { get; } = [];
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
        Add("General", "Sync offset", (i, _) => i.Offset != 0 ? $"{i.Offset:+0.000;-0.000} s" : "0");
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
