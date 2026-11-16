using System.Text;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Share;

public enum SummaryFormat
{
    BBCode,
    Markdown,
}

/// <summary>A forum-ready text summary: sources, key specs, findings, ranking and the screenshot link.</summary>
public static class Summary
{
    public static string Build(SummaryFormat fmt, IReadOnlyList<CompareItem> items, IReadOnlyList<RankRow>? ranking = null, string? screenshotsUrl = null)
    {
        var md = fmt == SummaryFormat.Markdown;
        var sb = new StringBuilder();
        string B(string s) => md ? $"**{s}**" : $"[b]{s}[/b]";
        string H(string s) => md ? $"### {s}" : $"[size=4][b]{s}[/b][/size]";
        string Link(string url, string text) => md ? $"[{text}]({url})" : $"[url={url}]{text}[/url]";

        sb.AppendLine(H("Comparison: " + string.Join(" vs ", items.Select(i => i.Name))));
        if (screenshotsUrl != null)
            sb.AppendLine(Link(screenshotsUrl, "Screenshots (frame-matched)"));
        sb.AppendLine();

        // key specs, only rows that differ
        var rows = SpecTable.Build(items).Where(r => r.Differs && r.Group != "Analysis").ToList();
        if (rows.Count > 0)
        {
            if (md)
            {
                sb.Append("| |").AppendJoin("", items.Select(i => $" {Esc(i.Name)} |")).AppendLine();
                sb.Append("|---|").AppendJoin("", items.Select(_ => "---|")).AppendLine();
                foreach (var r in rows)
                    sb.Append($"| {Esc(r.Label)} |").AppendJoin("", r.Values.Select(v => $" {Esc(v)} |")).AppendLine();
            }
            else
            {
                sb.AppendLine("[code]");
                var w = rows.Max(r => r.Label.Length) + 2;
                var cw = Math.Max(16, 100 / Math.Max(1, items.Count));
                sb.Append(' ', w).AppendJoin("  ", items.Select(i => Fit(i.Name, cw))).AppendLine();
                foreach (var r in rows)
                    sb.Append(r.Label.PadRight(w)).AppendJoin("  ", r.Values.Select(v => Fit(v, cw))).AppendLine();
                sb.AppendLine("[/code]");
            }
            sb.AppendLine();
        }

        if (ranking is { Count: > 0 })
        {
            sb.AppendLine(B("Ranking") + $" (against {items[0].Name})");
            if (md)
            {
                sb.AppendLine("| # | Release | VMAF | 1% low | PSNR | SSIM |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var r in ranking)
                    sb.AppendLine(r.Result == null ? $"| – | {Esc(r.Item.Name)} | {Esc(r.Error ?? "")} | | | |" : $"| {r.Rank} | {Esc(r.Item.Name)} | {Format.Num(r.Vmaf)} | {Format.Num(r.VmafLow)} | {Format.Num(r.Psnr)} | {Format.Num(r.Ssim, "0.0000")} |");
            }
            else
            {
                sb.AppendLine("[code]").Append(Ranker.Table(ranking)).AppendLine("[/code]");
            }
            sb.AppendLine();
        }

        // findings: notable ones per source, plus what differs from the reference
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var f = new List<Finding>();
            if (it.Video != null)
                f.AddRange(it.Video.Findings.Where(x => x.Level > 0));
            foreach (var a in it.Audio)
                f.AddRange(a.Findings.Where(x => x.Level > 0).Select(x => new Finding { Area = $"Audio {a.TrackIndex + 1} {x.Area}", Text = x.Text, Level = x.Level }));
            if (i > 0)
            {
                if (it.Grade != null)
                    f.AddRange(it.Grade.Findings.Where(x => x.Level > 0));
                f.AddRange(CrossFindings.Video(items[0], it));
                f.AddRange(CrossFindings.Audio(items[0], it));
            }
            if (f.Count == 0)
                continue;
            sb.AppendLine(B(it.Name));
            if (!md)
                sb.AppendLine("[list]");
            foreach (var x in f.OrderByDescending(x => x.Level))
                sb.AppendLine(md ? $"- {(x.Level == 2 ? "⚠ " : "")}{Esc(x.Area)}: {Esc(x.Text)}" : $"[*]{(x.Level == 2 ? "[color=#e06060]⚠[/color] " : "")}{x.Area}: {x.Text}");
            if (!md)
                sb.AppendLine("[/list]");
            sb.AppendLine();
        }
        sb.AppendLine(md ? "_Made with NothingElseCompares2u_" : "[i]Made with NothingElseCompares2u[/i]");
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Fit(string s, int w) => s.Length > w ? s[..(w - 1)] + "…" : s.PadRight(w);
}
