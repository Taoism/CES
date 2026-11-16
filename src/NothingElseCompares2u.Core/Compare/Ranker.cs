using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Compare;

public sealed class RankRow
{
    public required CompareItem Item { get; init; }
    public MetricResult? Result { get; set; }
    public string? Error { get; set; }
    public int Rank { get; set; }

    public double Vmaf => Get(Metric.Vmaf, s => s.Mean);
    public double VmafLow => Get(Metric.Vmaf, s => s.Low1);
    public double Psnr => Get(Metric.Psnr, s => s.Mean);
    public double Ssim => Get(Metric.Ssim, s => s.Mean);
    public double Xpsnr => Get(Metric.Xpsnr, s => s.Mean);

    /// <summary>The number the ranking sorts by: VMAF when measured, else SSIM, else PSNR.</summary>
    public double Score => !double.IsNaN(Vmaf) ? Vmaf : !double.IsNaN(Ssim) ? Ssim * 100 : Psnr;

    private double Get(Metric m, Func<MetricSeries, double> f) =>
        Result != null && Result.Series.TryGetValue(m, out var s) ? f(s) : double.NaN;
}

/// <summary>
/// "Which release is best": every other source is measured against the reference in several windows spread over
/// the title (inside the parts each release really has), then ranked.
/// </summary>
public static class Ranker
{
    /// <summary>Window start times (reference timeline) that all map into <paramref name="item"/> without crossing a cut.</summary>
    public static List<double> Windows(CompareItem reference, CompareItem item, int count, double length)
    {
        var (s0, e0) = item.Coverage();
        var start = Math.Max(Math.Max(0, s0), Math.Max(0, reference.Coverage().Start));
        var end = Math.Min(Math.Min(e0, reference.Title.Duration), reference.Coverage().End) - length;
        var list = new List<double>();
        if (end <= start)
            return list;
        foreach (var t in InputBuilder.EvenPoints(end - start + length, count, 4, 8).Select(t => t + start))
        {
            var w = Math.Min(t, end);
            // the whole window must be one section of the item
            if (item.SourceTime(w) is { } a && item.SourceTime(w + length) is { } b && Math.Abs(b - a - length * item.Speed) < 0.5)
                list.Add(w);
        }
        return list;
    }

    public static async Task<List<RankRow>> RankAsync(FfTools tools, string temp, CompareItem reference, IReadOnlyList<CompareItem> others, MetricOptions opt, int windows,
        IProgress<(double Fraction, string Text)>? progress = null, CancellationToken ct = default)
    {
        var qm = new QualityMetrics(tools, temp);
        var rows = new List<RankRow>();
        for (var k = 0; k < others.Count; k++)
        {
            var item = others[k];
            var row = new RankRow { Item = item };
            rows.Add(row);
            if (item.Title.Info?.Video == null)
            {
                row.Error = "no video";
                continue;
            }
            var starts = Windows(reference, item, windows, opt.Length);
            if (starts.Count == 0)
            {
                row.Error = "does not overlap the reference (check the sync)";
                continue;
            }
            var parts = new List<MetricResult>();
            for (var w = 0; w < starts.Count; w++)
            {
                ct.ThrowIfCancellationRequested();
                var ws = starts[w];
                var o = new MetricOptions
                {
                    Metrics = opt.Metrics,
                    Length = opt.Length,
                    Subsample = opt.Subsample,
                    DeinterlaceReference = opt.DeinterlaceReference,
                    AutoCrop = opt.AutoCrop,
                    ReferenceCrop = opt.ReferenceCrop,
                    // reference title time of the window
                    Start = reference.SourceTime(ws) ?? ws,
                };
                // the item's offset for this window's section, relative to the reference title
                var off = item.SourceTime(ws)!.Value - (reference.SourceTime(ws) ?? ws) * item.Speed;
                var kk = k;
                var ww = w;
                var prog = new Progress<(double Fraction, string Text)>(p =>
                    progress?.Report(((kk + (ww + p.Fraction) / starts.Count) / others.Count, $"{item.Name}: window {ww + 1}/{starts.Count} · {p.Text}")));
                try
                {
                    var r = await qm.RunAsync(reference.Title, item.Title, off, o, prog, ct, item.Speed).ConfigureAwait(false);
                    // report frame times on the shared timeline
                    for (var i = 0; i < r.FrameTimes.Count; i++)
                        r.FrameTimes[i] = reference.TimelineTime(r.FrameTimes[i]);
                    parts.Add(r);
                }
                catch (IOException ex)
                {
                    row.Error = ex.Message;
                }
            }
            if (parts.Count > 0)
            {
                row.Result = MetricResult.Merge(parts);
                row.Error = null;
            }
        }
        var ranked = rows.Where(r => r.Result != null).OrderByDescending(r => r.Score).ThenByDescending(r => r.VmafLow).ToList();
        for (var i = 0; i < ranked.Count; i++)
            ranked[i].Rank = i + 1;
        return [.. ranked, .. rows.Where(r => r.Result == null)];
    }

    /// <summary>Plain-text ranking table (CLI, clipboard).</summary>
    public static string Table(IReadOnlyList<RankRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"#",-3} {"Release",-44} {"VMAF",7} {"1% low",7} {"PSNR",7} {"SSIM",7} {"Video",11} {"Resolution",11}  Audio");
        foreach (var r in rows)
        {
            var v = r.Item.Title.Info?.Video;
            var name = r.Item.Name.Length > 44 ? r.Item.Name[..43] + "…" : r.Item.Name;
            if (r.Result == null)
            {
                sb.AppendLine($"{"-",-3} {name,-44} {r.Error}");
                continue;
            }
            var audio = r.Item.Title.Info?.Audio.FirstOrDefault()?.CodecDisplay ?? "";
            sb.AppendLine($"{r.Rank,-3} {name,-44} {Format.Num(r.Vmaf),7} {Format.Num(r.VmafLow),7} {Format.Num(r.Psnr),7} {Format.Num(r.Ssim, "0.0000"),7} {(v != null ? Format.BitRate(v.BitRate > 0 ? v.BitRate : r.Item.Title.Info!.OverallBitRate) : ""),11} {(v != null ? $"{v.Width}×{v.Height}" : ""),11}  {audio}");
        }
        return sb.ToString();
    }
}
