using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ces.Analysis;
using Ces.Compare;
using Ces.Model;
using Ces.Share;

namespace Ces.Report;

/// <summary>
/// Release notes: the comparison written out as two paragraphs of prose, the way a reviewer would sum it up —
/// the picture (closeness, master, grade, grain, size), then sound, frames, subtitles and a verdict.
/// </summary>
public static partial class ReportNotes
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] Numbers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    [GeneratedRegex(@"-([A-Za-z0-9]{2,})$", RegexOptions.CultureInvariant)]
    private static partial Regex Group();

    [GeneratedRegex(@"(?:^|[._ -])(BLU-?RAY|BDRIP|BRRIP|BDREMUX|REMUX|BD25|BD50|BD66|BD100)(?:$|[._ -])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluRayTag();

    [GeneratedRegex(@"(?:^|[._ -])(WEB-?DL|WEBRIP|WEB)(?:$|[._ -])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebTag();

    [GeneratedRegex(@"(?:^|[._ -])(DVD\w*)(?:$|[._ -])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DvdTag();

    /// <summary>The notes as paragraphs; empty for fewer than two sources.</summary>
    public static List<string> Build(IReadOnlyList<CompareItem> items,
        IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics,
        IReadOnlyList<SubtitleTimingReport>? subtitles = null)
    {
        if (items.Count < 2)
            return [];
        var ctx = new Ctx(items, metrics, subtitles ?? []);
        var first = Picture(ctx);
        var second = Rest(ctx);
        return [.. new[] { first, second }.Where(p => p.Length > 0)];
    }

    public static string ToText(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
            return "";
        var sb = new StringBuilder();
        foreach (var p in notes)
            sb.AppendLine(Wrap(p, 100)).AppendLine();
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
            return "";
        var sb = new StringBuilder("<section class=\"notes\"><h2>Notes</h2>");
        foreach (var p in notes)
            sb.Append("<p>").Append(WebUtility.HtmlEncode(p)).Append("</p>");
        return sb.Append("</section>").ToString();
    }

    // ---------- what is known about each source ----------

    private sealed class Ctx
    {
        public IReadOnlyList<CompareItem> Items { get; }
        public CompareItem Ref => Items[0];
        public List<string> Names { get; }
        public Dictionary<CompareItem, double> Vmaf { get; } = [];
        public Dictionary<CompareItem, string> ScoreName { get; } = [];
        public IReadOnlyList<SubtitleTimingReport> Subtitles { get; }

        public Ctx(IReadOnlyList<CompareItem> items, IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics, IReadOnlyList<SubtitleTimingReport> subtitles)
        {
            Items = items;
            Subtitles = subtitles;
            Names = ShortNames(items);
            foreach (var (r, d, res) in metrics)
            {
                if (r != items[0] || !items.Contains(d))
                    continue;
                var s = PlainReport.Score(res);
                if (!double.IsNaN(s.Value))
                {
                    Vmaf[d] = s.Value;
                    ScoreName[d] = s.Name;
                }
            }
        }

        public string Name(CompareItem it) => Names[Items.ToList().IndexOf(it)];

        public string Possessive(CompareItem it) => Name(it).EndsWith('s') ? Name(it) + "'" : Name(it) + "'s";

        public IEnumerable<CompareItem> Others => Items.Skip(1);

        /// <summary>Colour/tone re-grade (the grade check's own verdict).</summary>
        public static bool Regraded(CompareItem it) => it.Grade?.Findings.Any(f => f.Area == "Grade" && f.Level >= 2) == true;
    }

    /// <summary>The release group when every source has a distinct one, else the label (or "source n").</summary>
    private static List<string> ShortNames(IReadOnlyList<CompareItem> items)
    {
        var groups = items.Select(it =>
        {
            var n = StripExtension(it.Name);
            return SceneName.LooksLikeRelease(n) && Group().Match(n) is { Success: true } m ? m.Groups[1].Value : null;
        }).ToList();
        if (groups.All(g => g != null) && groups.Distinct(StringComparer.OrdinalIgnoreCase).Count() == groups.Count)
            return groups!;
        var names = items.Select(it => it.Name).ToList();
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count && names.All(n => n.Length <= 40)
            ? names
            : [.. items.Select((_, i) => "source " + (i + 1).ToString(Inv))];
    }

    private static string StripExtension(string s)
    {
        var ext = Path.GetExtension(s);
        return ext is ".mkv" or ".mp4" or ".m2ts" or ".ts" or ".iso" or ".rar" or ".avi" ? s[..^ext.Length] : s;
    }

    // ---------- paragraph 1: the picture ----------

    private static string Picture(Ctx c)
    {
        var s = new List<string> { Opening(c) };
        s.AddRange(Scores(c));
        foreach (var it in c.Others)
            s.AddRange(PictureOf(c, it));
        return string.Join(' ', s.Where(x => x.Length > 0));
    }

    private static string Opening(Ctx c)
    {
        var items = c.Items;
        var count = items.Count < Numbers.Length ? Numbers[items.Count] : items.Count.ToString(Inv);
        var res = items.Select(i => Res(i)).Distinct().ToList();
        var kinds = items.Select(Kind).Distinct().ToList();
        var what = string.Join(' ', new[] { res.Count == 1 ? res[0] : "", kinds.Count == 1 ? kinds[0] : "" }.Where(x => x.Length > 0));
        var title = SceneName.Title(items[0].Title.Name.Length > 0 && SceneName.LooksLikeRelease(items[0].Title.Name) ? items[0].Title.Name : items[0].Name);
        var from = string.Join(", ", c.Names.Take(c.Names.Count - 1)) + (c.Names.Count > 2 ? ", and " : " and ") + c.Names[^1];
        var aspects = new List<string>();
        var worst = c.Vmaf.Count > 0 ? c.Vmaf.Values.Min() : double.NaN;
        if (!double.IsNaN(worst) && worst < 90)
            aspects.Add("picture quality");
        if (c.Others.Any(it => Ctx.Regraded(it) || DifferentMaster(c, it)))
            aspects.Add("mastering");
        var sizes = items.Select(i => i.Title.Info?.SizeBytes ?? 0).Where(x => x > 0).ToList();
        if (sizes.Count == items.Count && sizes.Max() > sizes.Min() * 1.3)
            aspects.Add("compression");
        var verb = aspects.Count switch
        {
            0 => "shows them to be very much alike",
            _ => $"reveals {(aspects.Count >= 2 || worst < 60 ? "substantial" : "some")} differences in {Join(aspects)}",
        };
        return $"Comparing the {count} {(what.Length > 0 ? what + " " : "")}releases of {title} from {from} {verb}.";
    }

    private static IEnumerable<string> Scores(Ctx c)
    {
        if (c.Vmaf.Count == 0)
            yield break;
        var ranked = c.Others.Where(c.Vmaf.ContainsKey).OrderByDescending(it => c.Vmaf[it]).ToList();
        var best = ranked[0];
        string Score(CompareItem it) => $"a {c.ScoreName[it]} score of {Num(c.ScoreName[it], c.Vmaf[it])}";
        if (ranked.Count == 1)
        {
            yield return $"Using {c.Name(c.Ref)} as the reference, {c.Name(best)} reaches {Score(best)}, so it looks {PlainReport.Closeness((c.ScoreName[best], c.Vmaf[best]))}.";
        }
        else
        {
            var rest = ranked.Skip(1).ToList();
            var gap = c.Vmaf[best] - c.Vmaf[rest[^1]];
            var tail = rest.Count == 1
                ? $"while {c.Name(rest[0])} scores {(gap >= 15 ? "considerably lower at just " : "lower at ")}{Num(c.ScoreName[rest[0]], c.Vmaf[rest[0]])}"
                : "while " + Join(rest.Select(it => $"{c.Name(it)} scores {Num(c.ScoreName[it], c.Vmaf[it])}"));
            yield return $"Using {c.Name(c.Ref)} as the reference, {c.Name(best)} delivers the closest visual match with {Score(best)}, {tail}.";
        }
        var low = ranked.Any(it => c.ScoreName[it] == "VMAF" ? c.Vmaf[it] < 70 : c.ScoreName[it] == "SSIM" ? c.Vmaf[it] < 0.93 : c.Vmaf[it] < 32);
        if (low && c.Others.Any(it => Ctx.Regraded(it) || DifferentMaster(c, it) || Reframed(it)))
            yield return "However, these scores reflect differences in source transfers and mastering rather than compression quality alone.";
    }

    /// <summary>Low closeness without a measured re-grade: most likely another transfer of the film.</summary>
    private static bool DifferentMaster(Ctx c, CompareItem it) =>
        !Ctx.Regraded(it) && c.Vmaf.TryGetValue(it, out var v) && c.ScoreName[it] == "VMAF" && v < 60
        && !Upscaled(it);

    private static bool Upscaled(CompareItem it) =>
        it.Video?.NativeRes?.NativeHeight is { } h && it.Video.Height > 0 && h < it.Video.Height * 0.9;

    private static bool Reframed(CompareItem it) => it.Grade?.Findings.Any(f => f.Area == "Framing" && f.Level >= 1) == true;

    private static IEnumerable<string> PictureOf(Ctx c, CompareItem it)
    {
        var name = c.Name(it);
        var remastered = it.Name.Contains("REMASTER", StringComparison.OrdinalIgnoreCase);
        if (Ctx.Regraded(it))
        {
            var parts = GradeParts(it.Grade!);
            var strong = parts.Count >= 3 || (c.Vmaf.TryGetValue(it, out var v) && v < 50);
            yield return $"{c.Possessive(it)} {(remastered ? "remastered " : "")}release features {(strong ? "significantly altered" : "a different")} colour grading" +
                         (parts.Count > 0 ? ", with " + Join(parts) : "") + ".";
        }
        else if (DifferentMaster(c, it))
        {
            var best = c.Others.Where(c.Vmaf.ContainsKey).MaxBy(x => c.Vmaf[x]);
            yield return best == it && c.Vmaf.Count > 1
                ? $"{name} appears to originate from a different master but maintains a closer resemblance to {c.Possessive(c.Ref)} presentation."
                : $"{name} appears to originate from a different master or transfer.";
        }
        if (Reframed(it) && it.Grade!.Findings.FirstOrDefault(f => f.Area == "Framing" && f.Level >= 1) is { } fr && !fr.Text.StartsWith("The pictures could not", StringComparison.Ordinal))
            yield return $"Its framing differs as well ({Lower(fr.Text.TrimEnd('.'))}).";
        if (Upscaled(it) && it.Video!.NativeRes!.NativeHeight is { } nh)
            yield return $"Its picture looks upscaled from about {nh.ToString(Inv)} lines, so it carries less real detail than its resolution suggests.";
        if (Grain(c, it) is { Length: > 0 } g)
            yield return g;
        foreach (var f in CrossFindings.Video(c.Ref, it).Where(f => f.Area is "Halos" or "Banding"))
            yield return f.Area == "Halos" ? "Edges show halos from added sharpening." : "Smooth gradients show more banding than the reference.";
        if (Size(c, it) is { Length: > 0 } s)
            yield return s;
    }

    /// <summary>"31% stronger contrast", "darker midtones", "brighter highlights", "a noticeable green tint".</summary>
    private static List<string> GradeParts(GradeReport g)
    {
        var parts = new List<string>();
        var contrast = g.Median(x => x.ContrastRatio);
        if (!double.IsNaN(contrast) && Math.Abs(contrast - 1) >= 0.08)
            parts.Add($"{Math.Abs(contrast - 1) * 100:0}% {(contrast > 1 ? "stronger" : "weaker")} contrast");
        var (r, o) = g.ToneCurve();
        if (r.Length == GradeReport.Percentiles.Length)
        {
            foreach (var (k, what) in new[] { (1, "shadows"), (3, "midtones"), (5, "highlights") })
            {
                var d = o[k] - r[k];
                if (Math.Abs(d) >= 2.5)
                    parts.Add($"{(d > 0 ? "brighter" : "darker")} {what}");
            }
        }
        var sat = g.Median(x => x.SaturationRatio);
        if (!double.IsNaN(sat) && Math.Abs(sat - 1) >= 0.1)
            parts.Add(sat > 1 ? $"{(sat - 1) * 100:0}% stronger colours" : $"{(1 - sat) * 100:0}% paler colours");
        var da = g.Median(x => x.DeltaA);
        var db = g.Median(x => x.DeltaB);
        var tint = new List<string>();
        if (!double.IsNaN(da) && Math.Abs(da) >= 2)
            tint.Add(da < 0 ? "green" : "magenta");
        if (!double.IsNaN(db) && Math.Abs(db) >= 2)
            tint.Add(db > 0 ? "warm" : "cool");
        if (tint.Count > 0)
        {
            var strength = Math.Max(double.IsNaN(da) ? 0 : Math.Abs(da), double.IsNaN(db) ? 0 : Math.Abs(db)) >= 4 ? "noticeable" : "slight";
            parts.Add($"a {strength} {string.Join(", ", tint)} tint");
        }
        return parts;
    }

    private static string Grain(Ctx c, CompareItem it)
    {
        if (c.Ref.Video is not { } r || it.Video is not { } v || double.IsNaN(r.AvgNoise) || double.IsNaN(v.AvgNoise) || r.AvgNoise <= 0.2)
            return "";
        var ratio = v.AvgNoise / r.AvgNoise;
        var also = Ctx.Regraded(it) ? " also" : "";
        return ratio switch
        {
            >= 2 => $"Its film grain is{also} more pronounced, measuring more than {(ratio >= 3 ? Math.Floor(ratio).ToString(Inv) + " times" : "twice")} the noise of the reference.",
            >= 1.6 => $"Its film grain is{also} heavier, with about {(ratio - 1) * 100:0}% more noise than the reference.",
            <= 0.5 => $"Much of its grain has been removed ({ratio * 100:0}% of the reference's noise), a sign of noise reduction that can take fine detail with it.",
            <= 0.7 => $"It shows less grain than the reference ({ratio * 100:0}% of its noise), smoothed by filtering or the encode.",
            _ => "",
        };
    }

    private static string Size(Ctx c, CompareItem it)
    {
        var (rs, os) = (c.Ref.Title.Info?.SizeBytes ?? 0, it.Title.Info?.SizeBytes ?? 0);
        if (rs <= 0 || os <= 0)
            return "";
        if (os > rs * 1.5)
            return $"At {Format.Size(os)}, {c.Name(it)} is substantially larger than {c.Possessive(c.Ref)} {Format.Size(rs)}, potentially allowing for better preservation of fine detail" +
                   (Ctx.Regraded(it) ? " despite its different appearance." : ".");
        if (os < rs / 1.5)
            return $"At {Format.Size(os)}, {c.Name(it)} is much smaller than {c.Possessive(c.Ref)} {Format.Size(rs)}, which leaves less room for grain and fine detail.";
        return "";
    }

    // ---------- paragraph 2: sound, frames, subtitles, verdict ----------

    private static string Rest(Ctx c)
    {
        var s = new List<string>();
        s.AddRange(Sound(c));
        s.AddRange(Frames(c));
        s.AddRange(Subs(c));
        s.AddRange(Verdict(c));
        if (s.Count == 0)
            return "";
        // the paragraph leads in when it opens with the sound
        if (s[0].StartsWith(c.Name(c.Ref) + " includes", StringComparison.Ordinal) && (c.Subtitles.Count > 0 || c.Items.Any(i => i.Audio.Count > 0 || i.Title.Info?.Audio.Any() == true)))
            s.Insert(0, c.Subtitles.Count > 0 ? "Audio and subtitle differences further distinguish these releases." : "The sound differs as well.");
        return string.Join(' ', s);
    }

    private static List<StreamInfo> Tracks(CompareItem it) => it.Title.Info?.Audio.ToList() ?? [];

    private static StreamInfo? Best(CompareItem it) =>
        Tracks(it).OrderByDescending(PlainReport.Lossless).ThenByDescending(a => a.Channels).ThenByDescending(a => a.BitRate).FirstOrDefault();

    private static string Channels(int ch) => ch switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        > 2 => ch.ToString(Inv) + "-channel",
        _ => "",
    };

    private static string Describe(StreamInfo a) =>
        string.Join(' ', new[] { PlainReport.Lossless(a) ? "lossless" : "lossy", PlainReport.CodecName(a), Channels(a.Channels) }.Where(x => x.Length > 0));

    private static string AudioOf(CompareItem it)
    {
        var t = Tracks(it);
        if (t.Count == 0 || Best(it) is not { } b)
            return "no audio";
        if (t.Count == 1)
            return $"a single {Describe(b)} track";
        var n = t.Count < Numbers.Length ? Numbers[t.Count] : t.Count.ToString(Inv);
        return t.All(a => Describe(a) == Describe(b)) ? $"{n} audio tracks with {Describe(b)}" : $"{n} audio tracks, the best {Describe(b)}";
    }

    private static IEnumerable<string> Sound(Ctx c)
    {
        if (c.Items.All(i => Tracks(i).Count == 0))
            yield break;
        var lossless = c.Items.Where(i => Best(i) is { } b && PlainReport.Lossless(b)).ToList();
        var standout = lossless.Count == 1 && c.Items.Count > 2 ? lossless[0] : null;
        var plain = c.Items.Where(i => i != standout).ToList();
        var line = $"{c.Name(plain[0])} includes {AudioOf(plain[0])}";
        if (plain.Count > 1)
            line += ", while " + Join(plain.Skip(1).Select(i => $"{c.Name(i)} offers {AudioOf(i)}"));
        yield return line + ".";
        if (standout != null)
        {
            var b = Best(standout)!;
            var otherCh = c.Items.Where(i => i != standout).Select(i => Best(i)?.Channels ?? 0).Max();
            yield return $"{c.Name(standout)} stands out as the only release featuring lossless {PlainReport.CodecName(b)} audio" +
                         (b.Channels > 0 && b.Channels < otherCh ? $", although its soundtrack is {Channels(b.Channels)} rather than {Channels(otherCh)}." : ".");
        }
        else if (lossless.Count == 1)
        {
            yield return $"Only {c.Name(lossless[0])} carries lossless sound.";
        }
        foreach (var it in c.Others)
        {
            var f = CrossFindings.Audio(c.Ref, it);
            if (f.Any(x => x.Area == "Dynamics"))
                yield return $"{c.Possessive(it)} soundtrack is more compressed, with a smaller difference between loud and soft passages.";
            else if (f.Any(x => x.Area == "Master"))
                yield return $"{c.Possessive(it)} soundtrack appears to come from a different audio master (its tonal balance differs).";
        }
    }

    private static IEnumerable<string> Frames(Ctx c)
    {
        var checkedAny = c.Others.Any(i => i.Temporal is { SecondsChecked: > 0 });
        var hit = c.Others.Where(i => i.Temporal?.Events.Count > 0).Select(i =>
        {
            var drops = i.Temporal!.Events.Where(e => e.Kind.StartsWith("dropped", StringComparison.Ordinal)).Sum(e => e.Frames);
            return (Item: i, Drops: drops, Dups: i.Temporal.Events.Sum(e => e.Frames) - drops);
        }).ToList();
        if (hit.Count == 0)
        {
            if (checkedAny)
                yield return "No dropped or duplicated frames turned up in the parts checked.";
            yield break;
        }
        var minor = hit.All(h => h.Drops + h.Dups <= 3);
        var who = hit.Count == 2 ? $"Both {c.Name(hit[0].Item)} and {c.Name(hit[1].Item)}" : Join(hit.Select(h => c.Name(h.Item)));
        var verb = hit.Count == 1 ? "shows" : "show";
        var kind = minor ? "minor playback irregularities" : "playback irregularities";
        if (hit.All(h => h.Dups > 0))
        {
            var extra = hit.Where(h => h.Drops > 0).Select(h => $"{c.Name(h.Item)} also registering {Count(h.Drops, "dropped frame")}").ToList();
            yield return $"{who} {verb} {kind}, including {(minor ? "isolated " : "")}duplicated frames" + (extra.Count > 0 ? ", with " + Join(extra) + " during testing." : ".");
        }
        else if (hit.All(h => h.Drops > 0 && h.Dups == 0))
        {
            yield return $"{who} {verb} {kind}: {(minor ? "isolated " : "")}dropped frames.";
        }
        else
        {
            yield return $"{who} {verb} {kind}: " + Join(hit.Select(h => $"{c.Name(h.Item)} {Parts(h.Dups, h.Drops)}")) + ".";
        }
    }

    private static string Parts(int dups, int drops) =>
        Join(new[] { dups > 0 ? Count(dups, "duplicated frame") : "", drops > 0 ? Count(drops, "dropped frame") : "" }.Where(x => x.Length > 0));

    private static IEnumerable<string> Subs(Ctx c)
    {
        var reps = c.Subtitles.Where(r => r.ReferenceEvents > 0).ToList();
        if (reps.Count == 0)
            yield break;
        foreach (var r in reps)
        {
            var share = (double)r.Matched / r.ReferenceEvents;
            var who = reps.Count == 1 ? "Subtitle comparisons reveal" : $"For {c.Name(r.Item)}, subtitle comparisons reveal";
            var timing = !double.IsNaN(r.MedianDelta) && Math.Abs(r.MedianDelta) >= 0.1
                ? $", and the lines that match appear about {Math.Abs(r.MedianDelta).ToString("0.#", Inv)} s {(r.MedianDelta > 0 ? "late" : "early")}"
                : "";
            if (share < 0.9)
                yield return $"{who} only {share * 100:0}% matching lines against the reference{(r.ReferenceEvents < 300 ? " in the limited sample" : "")}, suggesting differences in subtitle versions rather than necessarily missing dialogue{timing}.";
            else
                yield return $"{(reps.Count == 1 ? "The subtitles" : c.Possessive(r.Item) + " subtitles")} match the reference closely ({share * 100:0}% of the lines){timing}.";
        }
    }

    private static IEnumerable<string> Verdict(Ctx c)
    {
        var others = c.Others.ToList();
        var regraded = others.Where(Ctx.Regraded).ToList();
        CompareItem? best = c.Vmaf.Count > 0 ? others.Where(c.Vmaf.ContainsKey).MaxBy(x => c.Vmaf[x]) : null;
        if (best != null && c.ScoreName[best] == "VMAF" && c.Vmaf[best] >= 90)
        {
            yield return $"Overall, {c.Name(best)} is practically a match for {c.Name(c.Ref)} in picture, so the choice between them comes down to size and sound.";
        }
        else if (best != null && !regraded.Contains(best))
        {
            var noticeable = c.ScoreName[best] == "VMAF" && c.Vmaf[best] < 70;
            yield return $"Overall, {c.Name(c.Ref)} and {c.Name(best)} provide the closest visual presentations" +
                         (noticeable ? $", with {c.Name(best)} showing noticeable differences despite its relatively higher similarity score." : ".");
        }
        foreach (var it in regraded)
        {
            var strengths = Strengths(c, it);
            yield return $"{c.Name(it)} offers {(strengths.Count > 0 ? Join(strengths) : "a different take on the film")}, but its {(it.Name.Contains("REMASTER", StringComparison.OrdinalIgnoreCase) ? "substantially revised restoration" : "different grade")} makes direct visual comparisons less meaningful.";
        }
        if (regraded.Count > 0 && !HasProblems(c.Ref))
        {
            var appeal = regraded.Select(it => (it, Appeal(c, it))).Where(x => x.Item2.Length > 0).ToList();
            yield return $"Ultimately, {c.Name(c.Ref)} is the safest choice for a consistent presentation" +
                         (appeal.Count > 0 ? ", while " + Join(appeal.Select(a => $"{c.Name(a.it)} may appeal to viewers who prefer {a.Item2}")) + "." : ".");
        }
        else if (others.FirstOrDefault(HasProblems) is { } bad && !HasProblems(c.Ref))
        {
            yield return $"Ultimately, {c.Name(c.Ref)} is the safer choice; {c.Name(bad)} has problems worth checking first.";
        }
    }

    private static bool HasProblems(CompareItem it) =>
        (it.Video?.Findings.Any(f => f.Level >= 2) ?? false) || it.Audio.Any(a => a.Findings.Any(f => f.Level >= 2));

    private static List<string> Strengths(Ctx c, CompareItem it)
    {
        var s = new List<string>();
        var lossless = c.Items.Where(i => Best(i) is { } b && PlainReport.Lossless(b)).ToList();
        if (lossless.Count == 1 && lossless[0] == it)
            s.Add("the strongest audio format");
        if ((it.Title.Info?.Video?.Height ?? 0) > c.Items.Where(i => i != it).Max(i => i.Title.Info?.Video?.Height ?? 0))
            s.Add("the highest resolution");
        if (it.Title.Info?.Video?.IsHdr == true && c.Items.Where(i => i != it).All(i => i.Title.Info?.Video?.IsHdr != true))
            s.Add("the only HDR picture");
        var size = it.Title.Info?.SizeBytes ?? 0;
        if (size > 0 && c.Items.Where(i => i != it).All(i => (i.Title.Info?.SizeBytes ?? 0) * 1.3 < size))
            s.Add("a larger video encode");
        return s;
    }

    private static string Appeal(Ctx c, CompareItem it)
    {
        var parts = new List<string> { $"its {(it.Name.Contains("REMASTER", StringComparison.OrdinalIgnoreCase) ? "remastered" : "new")} appearance" };
        var lossless = c.Items.Where(i => Best(i) is { } b && PlainReport.Lossless(b)).ToList();
        if (lossless.Count == 1 && lossless[0] == it && Best(it) is { } a)
            parts.Add($"lossless {(a.Channels == 1 ? "original mono" : Channels(a.Channels))} soundtrack".Replace("  ", " "));
        return parts.Count == 1 ? parts[0] : $"{parts[0]} and {parts[1]}";
    }

    // ---------- words ----------

    private static string Res(CompareItem it) => it.Title.Info?.Video is { Height: > 0 } v
        ? (v.Height >= 2000 || v.Width >= 3800 ? "2160p" : v.Height >= 1000 || v.Width >= 1900 ? "1080p" : v.Height >= 700 ? "720p" : v.Height.ToString(Inv) + "p")
        : "";

    private static string Kind(CompareItem it)
    {
        if (it.Source.Kind == SourceKind.BluRay)
            return "Blu-ray";
        if (it.Source.Kind == SourceKind.Dvd)
            return "DVD";
        var n = it.Name + " " + it.Source.DisplayName;
        return BluRayTag().IsMatch(n) ? "Blu-ray" : WebTag().IsMatch(n) ? "WEB" : DvdTag().IsMatch(n) ? "DVD" : "";
    }

    private static string Num(string metric, double v) => v.ToString(metric == "SSIM" ? "0.000" : "0", Inv);

    private static string Count(int n, string noun) => (n < Numbers.Length ? Numbers[n] : n.ToString(Inv)) + " " + noun + (n == 1 ? "" : "s");

    private static string Join(IEnumerable<string> parts)
    {
        var l = parts.ToList();
        return l.Count switch
        {
            0 => "",
            1 => l[0],
            2 => $"{l[0]} and {l[1]}",
            _ => string.Join(", ", l.Take(l.Count - 1)) + ", and " + l[^1],
        };
    }

    private static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        var line = 0;
        foreach (var w in text.Split(' '))
        {
            if (line > 0 && line + 1 + w.Length > width)
            {
                sb.AppendLine();
                line = 0;
            }
            else if (line > 0)
            {
                sb.Append(' ');
                line++;
            }
            sb.Append(w);
            line += w.Length;
        }
        return sb.ToString();
    }
}
