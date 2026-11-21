using System.Globalization;
using System.Text;
using Ces.Analysis;
using Ces.Compare;
using Ces.Disc;
using Ces.Model;

namespace Ces.Report;

/// <summary>
/// Plain-text versions of the Compare tabs (what the GUI shows), for "Copy" / "Save as text" and for scripts:
/// one function per tab, each starting with a header naming the sources.
/// </summary>
public static class TextReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string Rule = new('-', 80);

    /// <summary>Title line, date and the numbered sources.</summary>
    public static string Header(string title, IReadOnlyList<CompareItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title).AppendLine(new string('=', Math.Max(title.Length, 8)));
        sb.AppendLine("CES · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)).AppendLine();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            sb.AppendLine(string.Create(Inv, $"[{i + 1}] {it.Name}"));
            sb.AppendLine("    " + ReportPath.Short(it.Source.Path) + (it.Source.Kind != SourceKind.File ? " · " + it.Title.Name : ""));
            var sped = Math.Abs(it.Speed - 1) > 1e-9;
            if (i > 0 && (Math.Abs(it.Offset) > 1e-9 || sped || it.Sections.Count > 0))
                sb.AppendLine(string.Create(Inv, $"    offset {it.Offset:+0.000;-0.000;0.000} s{(sped ? $", speed {it.Speed:0.#####}" : "")}{(it.SongMapped ? $", song by song ({it.Sections.Count} stretches, another mix)" : it.Sections.Count > 0 ? $", {it.Sections.Count} sections (different edit)" : "")}"));
        }
        return sb.AppendLine().ToString();
    }

    /// <summary>One block of findings the way the cards show them (⚠ problem, • note).</summary>
    public static string Findings(string title, IEnumerable<Finding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title).AppendLine(Rule);
        var any = false;
        foreach (var f in findings.OrderByDescending(f => f.Level))
        {
            sb.AppendLine((f.Level == 2 ? "⚠ " : f.Level == 1 ? "• " : "  ") + f.Area + ": " + f.Text);
            any = true;
        }
        if (!any)
            sb.AppendLine("  (nothing found)");
        return sb.AppendLine().ToString();
    }

    private static IEnumerable<Finding> Prefixed(IEnumerable<Finding> findings, string prefix) =>
        findings.Select(f => new Finding { Area = prefix + f.Area, Text = f.Text, Level = f.Level });

    public static string Video(IReadOnlyList<CompareItem> items)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Video is not { } rep)
                continue;
            var all = rep.Findings.Concat(i > 0 ? Prefixed(CrossFindings.Video(items[0], items[i]), "vs 1 · ") : []);
            sb.Append(Findings(string.Create(Inv, $"[{i + 1}] {items[i].Name} · {rep.Frames.Count} frames"), all));
        }
        return sb.Length > 0 ? sb.ToString() : "No video analysis yet." + Environment.NewLine;
    }

    public static string Audio(IReadOnlyList<CompareItem> items)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var rep in items[i].Audio)
            {
                var first = items[i].Audio[0] == rep;
                var all = rep.Findings.Concat(i > 0 && first ? Prefixed(CrossFindings.Audio(items[0], items[i]), "vs 1 · ") : []);
                var block = Findings(string.Create(Inv, $"[{i + 1}] {items[i].Name} · track {rep.TrackIndex + 1}: {rep.Stream.Label}"), all);
                sb.Append(block.TrimEnd()).AppendLine();
                if (rep.Channels.Count > 0)
                {
                    var lossy = Probe.MediaProber.IsLossy(rep.Stream.Codec) && !(rep.Stream.Codec == "dts" && rep.Stream.Profile.Contains("MA", StringComparison.Ordinal));
                    sb.AppendLine();
                    sb.AppendLine(string.Create(Inv, $"  {"Ch",-6}{"Peak",-12}{"RMS",-12}{"Noise floor",-14}{"Crest",-12}Bits used"));
                    foreach (var ch in rep.Channels)
                        sb.AppendLine(string.Create(Inv, $"  {ch.Name,-6}{Format.Db(ch.PeakDb),-12}{Format.Db(ch.RmsDb),-12}{Format.Db(ch.NoiseFloorDb),-14}{Format.Db(ch.CrestDb),-12}{(lossy ? "n/a (lossy)" : ch.BitsUsedText)}"));
                }
                sb.AppendLine();
            }
        }
        return sb.Length > 0 ? sb.ToString() : "No audio analysis yet." + Environment.NewLine;
    }

    public static string Quality(IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics, IReadOnlyList<RankRow> ranking)
    {
        var sb = new StringBuilder();
        if (ranking.Count > 0)
            sb.AppendLine("Ranking").AppendLine(Rule).AppendLine(Ranker.Table(ranking)).AppendLine();
        foreach (var (rf, ds, res) in metrics)
        {
            sb.AppendLine($"{ds.Name}  vs  {rf.Name}").AppendLine(Rule);
            sb.AppendLine(string.Create(Inv, $"{res.Frames} frames from {Format.Time(res.Start)} · compared at {res.ReferenceGeometry}{(res.Notes.Length > 0 ? " · " + res.Notes : "")}"));
            sb.AppendLine(string.Create(Inv, $"  {"Metric",-8}{"Mean",-12}{"1 % low",-12}{"Min",-12}Harmonic"));
            foreach (var (m, s) in res.Series)
            {
                var fmt = m == Metric.Ssim ? "0.0000" : "0.00";
                var harmonic = m == Metric.Vmaf ? Format.Num(s.HarmonicMean, fmt) : "";
                sb.AppendLine(string.Create(Inv, $"  {s.Name,-8}{Format.Num(s.Mean, fmt),-12}{Format.Num(s.Low1, fmt),-12}{Format.Num(s.Min, fmt),-12}{harmonic}").TrimEnd());
            }
            var worst = res.Worst(6);
            if (worst.Count > 0)
                sb.AppendLine("Worst frames: " + string.Join(",  ", worst.Select(w => string.Create(Inv, $"{Format.Time(res.TimeOf(w.Frame))} ({w.Value:0.###})"))));
            sb.AppendLine();
        }
        return sb.Length > 0 ? sb.ToString() : "No quality metrics yet." + Environment.NewLine;
    }

    public static string Remaster(IReadOnlyList<CompareItem> items)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].Grade is not { } g)
                continue;
            var findings = g.Findings.Concat(items[i].Temporal?.Findings ?? []);
            sb.Append(Findings(string.Create(Inv, $"[{i + 1}] {items[i].Name} compared with {items[0].Name} · {g.Frames.Count} frames"), findings));
        }
        return sb.Length > 0 ? sb.ToString() : "No remaster comparison yet." + Environment.NewLine;
    }

    public static string Subtitles(IReadOnlyList<SubtitleTimingReport> reports, IReadOnlyList<CompareItem> items)
    {
        var sb = new StringBuilder();
        foreach (var r in reports)
        {
            var i = items.ToList().IndexOf(r.Item);
            var title = string.Create(Inv, $"[{i + 1}] {r.Item.Name} · {r.TrackLabel} · {r.Matched} of {r.ReferenceEvents} reference events matched");
            sb.Append(Findings(title, r.Findings));
        }
        return sb.Length > 0 ? sb.ToString() : "No subtitle timing check yet." + Environment.NewLine;
    }

    public static string Discs(IReadOnlyList<CompareItem> items)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Source.Kind == SourceKind.File)
                continue;
            sb.AppendLine(string.Create(Inv, $"[{i + 1}] {it.Name} · {it.Source.Titles.Count} titles · {ReportPath.Short(it.Source.Path)}")).AppendLine(Rule);
            foreach (var t in it.Source.Titles)
            {
                var mark = t == it.Title ? "» " : "  ";
                sb.AppendLine(string.Create(Inv, $"{mark}{t.Name}{(t.IsMain ? "  [main title]" : "")}  ·  {Format.Time(t.Duration)}  ·  {Format.Size(t.SizeBytes)}  ·  {t.Segments.Count} segment(s){(t.Angles > 1 ? $"  ·  {t.Angles} angles" : "")}"));
            }
            var c = it.Title;
            sb.AppendLine().AppendLine("Compared title " + c.Name + ":");
            foreach (var s in c.Segments)
                sb.AppendLine($"  {Format.Time(s.TitleStart)}  +{Format.Time(s.Duration)}  {s.FileLabel}");
            if (c.Chapters.Count > 0)
                sb.AppendLine("Chapters: " + string.Join("   ", c.Chapters.Select((ch, k) => string.Create(Inv, $"{k + 1}: {Format.Time(ch.Start, false)}"))));
            sb.AppendLine();
        }
        return sb.Length > 0 ? sb.ToString() : "No Blu-ray or DVD sources." + Environment.NewLine;
    }

    /// <summary>Screenshot times with frame type and picture statistics per source.</summary>
    public static string Screenshots(IReadOnlyList<CompareItem> items, IReadOnlyList<ShotRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine(Format.Time(row.Time) + (row.Caption.Length > 0 ? "  · " + row.Caption : ""));
            for (var i = 0; i < items.Count; i++)
            {
                var s = row.Shots.ElementAtOrDefault(i);
                if (s == null)
                    continue;
                if (s.Error != null)
                {
                    sb.AppendLine(string.Create(Inv, $"  [{i + 1}] {s.Error}"));
                    continue;
                }
                var line = string.Create(Inv, $"  [{i + 1}] {(double.IsNaN(s.FrameTime) ? "" : Format.Time(s.FrameTime) + "  ")}{s.PictType}-frame  {s.Width}×{s.Height}");
                if (s.Stats is { } st)
                {
                    line += string.Create(Inv, $"  Y {st.YMin:0}–{st.YMax:0} avg {st.YAvg:0.0}  sat {st.SatAvg:0.0}");
                    if (!double.IsNaN(st.Blur))
                        line += string.Create(Inv, $"  blur {st.Blur:0.00}");
                    if (!double.IsNaN(st.Block))
                        line += string.Create(Inv, $"  blockiness {st.Block:0.00}");
                }
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }
        return sb.Length > 0 ? sb.ToString() : "No screenshots yet." + Environment.NewLine;
    }
}
