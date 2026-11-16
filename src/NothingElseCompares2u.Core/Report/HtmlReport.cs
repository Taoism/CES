using System.Globalization;
using System.Net;
using System.Text;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Report;

/// <summary>Writes a self-contained comparison report: index.html plus an images folder.</summary>
public static class HtmlReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Write(string outDir, string title, IReadOnlyList<CompareItem> items, IReadOnlyList<ShotRow> shots, IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics,
        IReadOnlyList<SubtitleTimingReport>? subtitles = null, string? slowPicsUrl = null)
    {
        Directory.CreateDirectory(outDir);
        var imgDir = Path.Combine(outDir, "images");
        Directory.CreateDirectory(imgDir);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(H(title)).Append("</title><style>").Append(Css).Append("</style></head><body>");
        sb.Append("<header><h1>").Append(H(title)).Append("</h1><p class=\"muted\">NothingElseCompares2u · ")
          .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv));
        if (slowPicsUrl != null)
            sb.Append(" · <a href=\"").Append(H(slowPicsUrl)).Append("\" style=\"color:var(--accent)\">screenshots on slow.pics</a>");
        sb.Append("</p></header>");

        // sources
        sb.Append("<section><h2>Sources</h2><ol class=\"sources\">");
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            sb.Append("<li><span class=\"chip c").Append(i % 6).Append("\">").Append(i + 1).Append("</span> <b>").Append(H(it.Name)).Append("</b> <span class=\"muted\">")
              .Append(H(it.Source.Path)).Append(it.Source.Kind != SourceKind.File ? " · " + H(it.Title.Name) : "").Append("</span></li>");
        }
        sb.Append("</ol></section>");

        // specs
        var rows = SpecTable.Build(items);
        sb.Append("<section><h2>Specs</h2><div class=\"scroll\"><table class=\"specs\"><thead><tr><th></th>");
        for (var i = 0; i < items.Count; i++)
            sb.Append("<th><span class=\"chip c").Append(i % 6).Append("\">").Append(i + 1).Append("</span> ").Append(H(items[i].Name)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        string? group = null;
        foreach (var r in rows)
        {
            if (r.Group != group)
            {
                group = r.Group;
                sb.Append("<tr class=\"group\"><td colspan=\"").Append(items.Count + 1).Append("\">").Append(H(group)).Append("</td></tr>");
            }
            sb.Append(r.Differs ? "<tr class=\"diff\">" : "<tr>").Append("<td class=\"lbl\">").Append(H(r.Label)).Append("</td>");
            foreach (var v in r.Values)
                sb.Append("<td>").Append(H(v)).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div><p class=\"muted\">Highlighted rows differ between sources.</p></section>");

        // findings
        if (items.Any(i => i.Video != null || i.Audio.Count > 0 || i.Grade != null) || subtitles is { Count: > 0 })
        {
            sb.Append("<section><h2>Findings</h2><div class=\"cards\">");
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var findings = (it.Video?.Findings ?? []).Concat(it.Audio.SelectMany(a => a.Findings.Select(f => new Finding { Area = $"Audio {a.TrackIndex + 1} · {f.Area}", Text = f.Text, Level = f.Level }))).ToList();
                if (i > 0)
                {
                    if (it.Grade != null)
                        findings.AddRange(it.Grade.Findings.Select(f => new Finding { Area = "Grade · " + f.Area, Text = f.Text, Level = f.Level }));
                    findings.AddRange(CrossFindings.Video(items[0], it).Concat(CrossFindings.Audio(items[0], it)).Select(f => new Finding { Area = "vs 1 · " + f.Area, Text = f.Text, Level = f.Level }));
                    foreach (var sr in subtitles?.Where(s => s.Item == it) ?? [])
                        findings.AddRange(sr.Findings.Select(f => new Finding { Area = "Subtitles · " + f.Area, Text = f.Text, Level = f.Level }));
                }
                if (findings.Count == 0)
                    continue;
                sb.Append("<div class=\"card\"><h3><span class=\"chip c").Append(i % 6).Append("\">").Append(i + 1).Append("</span> ").Append(H(it.Name)).Append("</h3><ul class=\"findings\">");
                foreach (var f in findings.OrderByDescending(f => f.Level))
                    sb.Append("<li class=\"l").Append(f.Level).Append("\"><b>").Append(H(f.Area)).Append("</b> ").Append(H(f.Text)).Append("</li>");
                sb.Append("</ul>");
                foreach (var a in it.Audio.Where(a => a.SpectrogramPng != null && File.Exists(a.SpectrogramPng)))
                {
                    var name = $"spectrogram-{i + 1}-{a.TrackIndex + 1}.png";
                    File.Copy(a.SpectrogramPng!, Path.Combine(imgDir, name), true);
                    sb.Append("<figure><img loading=\"lazy\" src=\"images/").Append(name).Append("\" alt=\"spectrogram\"><figcaption>Audio track ").Append(a.TrackIndex + 1).Append(" spectrogram (60 s, middle)</figcaption></figure>");
                }
                sb.Append("</div>");
            }
            sb.Append("</div></section>");
        }

        // metrics
        foreach (var (rf, ds, res) in metrics)
        {
            sb.Append("<section><h2>Quality: ").Append(H(ds.Name)).Append(" vs ").Append(H(rf.Name)).Append("</h2><p class=\"muted\">")
              .Append(res.Frames).Append(" frames from ").Append(Format.Time(res.Start)).Append(", reference ").Append(H(res.ReferenceGeometry))
              .Append(res.Notes.Length > 0 ? " · " + H(res.Notes) : "").Append("</p><table class=\"specs small\"><tr><th>Metric</th><th>Mean</th><th>1% low</th><th>Min</th></tr>");
            foreach (var (m, s) in res.Series)
                sb.Append("<tr><td>").Append(H(s.Name)).Append("</td><td>").Append(Format.Num(s.Mean, m == Metric.Ssim ? "0.0000" : "0.00")).Append("</td><td>")
                  .Append(Format.Num(s.Low1, m == Metric.Ssim ? "0.0000" : "0.00")).Append("</td><td>").Append(Format.Num(s.Min, m == Metric.Ssim ? "0.0000" : "0.00")).Append("</td></tr>");
            sb.Append("</table>");
            foreach (var (_, s) in res.Series)
                sb.Append(Chart(s, res));
            sb.Append("</section>");
        }

        // screenshots
        if (shots.Count > 0)
        {
            sb.Append("<section><h2>Screenshots</h2><p class=\"muted\">Click a picture or press 1–").Append(items.Count)
              .Append(" (or ←/→) to switch sources at the same frame; press S for side by side.</p>");
            for (var r = 0; r < shots.Count; r++)
            {
                var row = shots[r];
                sb.Append("<div class=\"shot\" data-n=\"").Append(items.Count).Append("\"><div class=\"tabs\"><span class=\"time\">").Append(H(Format.Time(row.Time))).Append("</span>");
                if (row.Caption.Length > 0)
                    sb.Append("<span class=\"muted\">").Append(H(row.Caption)).Append("</span>");
                for (var i = 0; i < items.Count; i++)
                {
                    var s = row.Shots.ElementAtOrDefault(i);
                    sb.Append("<button class=\"tab").Append(i == 0 ? " on" : "").Append("\" data-i=\"").Append(i).Append("\"><span class=\"chip c").Append(i % 6).Append("\">").Append(i + 1)
                      .Append("</span> ").Append(H(items[i].Name)).Append(s is { PictType: var pt } && pt != "?" ? $" <em>{H(pt)}</em>" : "").Append("</button>");
                }
                sb.Append("</div><div class=\"frames\">");
                for (var i = 0; i < items.Count; i++)
                {
                    var s = row.Shots.ElementAtOrDefault(i);
                    sb.Append("<figure class=\"f").Append(i == 0 ? " on" : "").Append("\">");
                    if (s != null && s.Error == null && File.Exists(s.ImagePath))
                    {
                        var name = $"shot-{r + 1:00}-{i + 1}.png";
                        File.Copy(s.ImagePath, Path.Combine(imgDir, name), true);
                        sb.Append("<img loading=\"lazy\" src=\"images/").Append(name).Append("\" alt=\"\">");
                        sb.Append("<figcaption>").Append(H(items[i].Name)).Append(" · ").Append(s.Width).Append('×').Append(s.Height).Append(" · frame type ").Append(H(s.PictType));
                        if (s.Stats is { } st)
                            sb.Append(" · Y ").Append(st.YMin.ToString("0", Inv)).Append('–').Append(st.YMax.ToString("0", Inv)).Append(" avg ").Append(st.YAvg.ToString("0.0", Inv))
                              .Append(double.IsNaN(st.Blur) ? "" : " · blur " + st.Blur.ToString("0.00", Inv)).Append(double.IsNaN(st.Block) ? "" : " · block " + st.Block.ToString("0.00", Inv));
                        sb.Append("</figcaption>");
                    }
                    else
                    {
                        sb.Append("<div class=\"missing\">").Append(H(s?.Error ?? "no frame")).Append("</div>");
                    }
                    sb.Append("</figure>");
                }
                sb.Append("</div></div>");
            }
            sb.Append("</section>");
        }
        sb.Append("<script>").Append(Js).Append("</script></body></html>");
        var path = Path.Combine(outDir, "index.html");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Chart(MetricSeries s, MetricResult res)
    {
        if (s.Values.Count < 2)
            return "";
        const int w = 1000, h = 160;
        var min = Math.Min(s.Values.Min(), s.Name == "SSIM" ? 0.9 : s.Name == "VMAF" ? 60 : 30);
        var max = s.Name == "SSIM" ? 1.0 : s.Name == "VMAF" ? 100 : Math.Max(50, s.Values.Where(v => v < 99).DefaultIfEmpty(50).Max());
        var step = Math.Max(1, s.Values.Count / w);
        var pts = new StringBuilder();
        for (var i = 0; i < s.Values.Count; i += step)
        {
            var x = (double)i / (s.Values.Count - 1) * w;
            var y = h - (Math.Clamp(s.Values[i], min, max) - min) / (max - min) * h;
            pts.Append(x.ToString("0.#", Inv)).Append(',').Append(y.ToString("0.#", Inv)).Append(' ');
        }
        return $"<figure class=\"chart\"><svg viewBox=\"0 0 {w} {h}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"{H(s.Name)} per frame\"><polyline fill=\"none\" stroke=\"var(--accent)\" stroke-width=\"1.5\" vector-effect=\"non-scaling-stroke\" points=\"{pts}\"/></svg>" +
               $"<figcaption>{H(s.Name)} per frame ({Format.Num(min, "0.##")}–{Format.Num(max, "0.##")}), {Format.Time(res.Start, false)} → {Format.Time(res.TimeOf(res.Frames), false)}</figcaption></figure>";
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private const string Css = """
        :root{--bg:#141518;--panel:#1d1f23;--edge:#33363c;--text:#e6e6e8;--muted:#9a9ca1;--accent:#4a8ce3;--diff:#2c2a1c;--warn:#e0a040;--bad:#e06060}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,Segoe UI,Roboto,sans-serif}
        header,section{max-width:1800px;margin:0 auto;padding:12px 20px}h1{margin:8px 0 0;font-size:22px}h2{font-size:17px;border-bottom:1px solid var(--edge);padding-bottom:6px}
        .muted{color:var(--muted)}.scroll{overflow-x:auto}table.specs{border-collapse:collapse;width:100%;font-size:13px}
        .specs th,.specs td{border-bottom:1px solid var(--edge);padding:4px 8px;text-align:left;vertical-align:top}.specs th{background:var(--panel);position:sticky;top:0}
        .specs td.lbl{color:var(--muted);white-space:nowrap}.specs tr.group td{font-weight:600;padding-top:12px;color:var(--accent)}.specs tr.diff td{background:var(--diff)}
        table.small{width:auto}.sources{padding-left:0;list-style:none}.sources li{margin:4px 0}
        .chip{display:inline-block;min-width:20px;padding:0 5px;border-radius:4px;text-align:center;font-weight:700;color:#111}
        .c0{background:#4a8ce3}.c1{background:#e0a040}.c2{background:#6cc070}.c3{background:#d070c0}.c4{background:#50c8c8}.c5{background:#e06060}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(420px,1fr));gap:12px}.card{background:var(--panel);border:1px solid var(--edge);border-radius:6px;padding:10px 14px}
        .card h3{margin:4px 0 8px;font-size:15px}.findings{padding-left:18px;margin:0}.findings li{margin:3px 0}.findings .l1 b{color:var(--warn)}.findings .l2 b{color:var(--bad)}.findings .l2{color:#f0c8c8}
        figure{margin:8px 0}figure img{max-width:100%;display:block}figcaption{color:var(--muted);font-size:12px;margin-top:3px}
        .shot{margin:18px 0;background:var(--panel);border:1px solid var(--edge);border-radius:6px;padding:8px}
        .tabs{display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin-bottom:6px}.time{font-weight:700;margin-right:6px;font-variant-numeric:tabular-nums}
        .tab{background:#26282d;color:var(--text);border:1px solid var(--edge);border-radius:4px;padding:3px 8px;cursor:pointer;font:inherit}.tab.on{border-color:var(--accent);background:#2b3442}.tab em{color:var(--muted);font-style:normal}
        .frames figure{display:none;margin:0}.frames figure.on{display:block}.frames img{cursor:pointer;width:100%;image-rendering:auto}
        .shot.sbs .frames{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:6px}.shot.sbs .frames figure{display:block}
        .missing{padding:40px;text-align:center;color:var(--muted);border:1px dashed var(--edge)}.chart svg{width:100%;height:160px;background:#17181b;border:1px solid var(--edge)}
        """;

    private const string Js = """
        let active=null;
        function show(s,i){const n=+s.dataset.n;i=((i%n)+n)%n;s.querySelectorAll('.tab').forEach((t,k)=>t.classList.toggle('on',k===i));s.querySelectorAll('.frames figure').forEach((f,k)=>f.classList.toggle('on',k===i));s.dataset.cur=i;}
        document.querySelectorAll('.shot').forEach(s=>{s.dataset.cur=0;s.addEventListener('mouseenter',()=>active=s);
          s.querySelectorAll('.tab').forEach(t=>t.addEventListener('click',()=>show(s,+t.dataset.i)));
          s.querySelectorAll('.frames img').forEach(im=>im.addEventListener('click',()=>{if(!s.classList.contains('sbs'))show(s,+s.dataset.cur+1)}));});
        document.addEventListener('keydown',e=>{if(!active)return;const k=e.key;
          if(k>='1'&&k<='9'){show(active,+k-1);e.preventDefault();}
          else if(k==='ArrowRight'){show(active,+active.dataset.cur+1);e.preventDefault();}
          else if(k==='ArrowLeft'){show(active,+active.dataset.cur-1);e.preventDefault();}
          else if(k==='s'||k==='S'){document.querySelectorAll('.shot').forEach(x=>x.classList.toggle('sbs'));}});
        """;
}
