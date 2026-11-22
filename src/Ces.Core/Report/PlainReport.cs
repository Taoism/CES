using System.Globalization;
using System.Net;
using System.Text;
using Ces.Analysis;
using Ces.Compare;
using Ces.Model;
using Ces.Probe;

namespace Ces.Report;

/// <summary>One part of the plain-language summary: a heading and its sentences.</summary>
public sealed record PlainSection(string Heading, List<string> Lines);

/// <summary>
/// The comparison in everyday words, for someone who does not know what VMAF or a bitrate is: what was compared, the
/// short answer, what differs, what to look out for, and a list of the few technical words that appear.
/// </summary>
public static class PlainReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly (string Term, string Meaning)[] Words =
    [
        ("reference", "the source everything else is measured against (number 1)."),
        ("VMAF", "a score from 0 to 100 for how close a picture looks to the reference, made to match what people see. 95 and up looks the same to almost everyone."),
        ("PSNR", "a simpler closeness score in decibels; higher is closer. Above 45 is very close, below 35 shows visible differences."),
        ("XPSNR", "a version of PSNR weighted for what the eye notices; read it like PSNR."),
        ("SSIM", "a closeness score from 0 to 1 for structure and detail; 0.99 and up is very close."),
        ("HDR", "high dynamic range: brighter highlights and deeper colour on a TV that supports it. On other screens it can look dull unless converted."),
        ("Dolby Vision", "a kind of HDR that adjusts the picture scene by scene; needs a player and TV that support it."),
        ("HDR10", "the basic, most widely supported kind of HDR."),
        ("lossless", "sound stored exactly as on the studio master, nothing thrown away (TrueHD, DTS-HD MA, FLAC, PCM)."),
        ("lossy", "sound squeezed smaller by leaving out detail most people do not hear (Dolby Digital, DTS, AAC)."),
        ("bitrate", "how much data is used per second; more usually means fewer compression flaws."),
        ("upscale", "a picture made at a lower resolution and enlarged afterwards; it has the pixels of the bigger size but not the detail."),
        ("banding", "visible steps instead of smooth gradients, for example in skies or shadows."),
        ("grain", "the fine texture of film. Removing it (noise reduction) can also remove real detail and make faces look waxy."),
        ("halo", "bright outlines around edges, left by artificial sharpening."),
        ("chroma", "the colour part of the picture (as opposed to brightness)."),
        ("crop", "part of the picture cut off at the edges, or black bars added."),
        ("frame", "one single picture of the video; films have about 24 per second."),
        ("offset", "how far one source's timing is shifted from the reference, so the same moment can be compared."),
        ("LUFS", "how loud the sound is overall; the closer to zero, the louder."),
    ];

    public static List<PlainSection> Build(IReadOnlyList<CompareItem> items,
        IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics,
        IReadOnlyList<SubtitleTimingReport>? subtitles = null)
    {
        var list = new List<PlainSection>();
        if (items.Count == 0)
            return list;
        list.Add(new PlainSection("What was compared", Compared(items)));
        list.Add(new PlainSection("The short answer", ShortAnswer(items, metrics)));
        if (Differences(items) is { Count: > 0 } diff)
            list.Add(new PlainSection("What is different", diff));
        if (metrics.Count > 0)
            list.Add(new PlainSection("How close the picture is to the reference", Closeness(metrics)));
        if (subtitles is { Count: > 0 })
            list.Add(new PlainSection("Subtitles", Subtitles(items, subtitles)));
        var (check, notes) = Issues(items, subtitles);
        list.Add(new PlainSection("Worth checking", check.Count > 0 ? check : ["Nothing that looks like a problem was found."]));
        if (notes.Count > 0)
            list.Add(new PlainSection("Good to know", notes));
        var text = string.Join('\n', list.SelectMany(s => s.Lines));
        var glossary = Words.Where(w => Mentions(text, w.Term)).Select(w => $"{w.Term}: {w.Meaning}").ToList();
        if (glossary.Count > 0)
            list.Add(new PlainSection("Words used in this report", glossary));
        return list;
    }

    public static string ToText(string title, IReadOnlyList<PlainSection> sections)
    {
        var sb = new StringBuilder();
        var head = "In plain words: " + title;
        sb.AppendLine(head).AppendLine(new string('=', Math.Min(head.Length, 80)));
        sb.AppendLine("CES · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)).AppendLine();
        foreach (var s in sections)
        {
            sb.AppendLine(s.Heading).AppendLine(new string('-', s.Heading.Length));
            foreach (var l in s.Lines)
                sb.AppendLine(Wrap("- " + l, "  "));
            sb.AppendLine();
        }
        sb.AppendLine("The full technical details are in report.txt and index.html next to this file.");
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<PlainSection> sections)
    {
        var sb = new StringBuilder("<section class=\"plain\"><h2>In plain words</h2><div class=\"cards\">");
        foreach (var s in sections)
        {
            sb.Append("<div class=\"card\"><h3>").Append(WebUtility.HtmlEncode(s.Heading)).Append("</h3><ul>");
            foreach (var l in s.Lines)
                sb.Append("<li>").Append(WebUtility.HtmlEncode(l)).Append("</li>");
            sb.Append("</ul></div>");
        }
        return sb.Append("</div></section>").ToString();
    }

    // ---------- parts ----------

    private static string N(int i) => (i + 1).ToString(Inv);

    private static List<string> Compared(IReadOnlyList<CompareItem> items)
    {
        var lines = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var kind = it.Source.Kind switch
            {
                SourceKind.BluRay => "a Blu-ray disc",
                SourceKind.Dvd => "a DVD",
                _ => "a video file",
            };
            var parts = new List<string> { kind };
            var dur = it.Title.Info?.Duration ?? it.Title.Duration;
            if (dur > 60)
                parts.Add(Length(dur) + " long");
            if (it.Title.Info?.Video is { } v)
                parts.Add(Picture(v));
            if (BestAudio(it) is { } a)
                parts.Add(Sound(a, it.Title.Info!.Audio.Count()));
            lines.Add($"{N(i)}. {it.Name}: {string.Join(", ", parts)}.");
        }
        if (items.Count > 1)
            lines.Add($"Number 1 ({items[0].Name}) is the reference: the others are measured against it.");
        return lines;
    }

    private static List<string> ShortAnswer(IReadOnlyList<CompareItem> items,
        IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics)
    {
        var lines = new List<string>();
        if (items.Count == 1)
        {
            var it = items[0];
            var problems = Findings(items, 0, null).Count(f => f.Level == 2);
            lines.Add(problems == 0
                ? "Only one source was checked. Nothing that looks like a problem was found."
                : $"Only one source was checked. {Count(problems, "thing")} may be a problem (see \"Worth checking\").");
            if (it.Video?.NativeRes?.NativeHeight is { } h && it.Video.Height > 0 && h < it.Video.Height * 0.9)
                lines.Add($"Its picture looks enlarged from about {h} lines (an upscale), so it has less detail than its size suggests.");
            return lines;
        }
        if (metrics.Count > 0)
        {
            var scored = metrics.Select(m => (m.Distorted, Score: Score(m.Result))).Where(s => !double.IsNaN(s.Score.Value)).OrderByDescending(s => s.Score.Value).ToList();
            if (scored.Count > 0)
            {
                var best = scored[0];
                lines.Add(scored.Count == 1
                    ? $"Compared with the reference, number {Idx(items, best.Distorted)} ({best.Distorted.Name}) looks {Closeness(best.Score)}."
                    : $"Closest to the reference: number {Idx(items, best.Distorted)} ({best.Distorted.Name}); it looks {Closeness(best.Score)}.");
            }
        }
        // the plain facts that matter most to a viewer
        var vids = items.Select(i => i.Title.Info?.Video).ToList();
        if (vids.All(v => v != null))
        {
            var maxPx = vids.Max(v => v!.Width * v.Height);
            var sharpest = items.Where((_, k) => vids[k]!.Width * vids[k]!.Height == maxPx).ToList();
            if (sharpest.Count < items.Count)
                lines.Add($"Highest resolution: {Names(items, sharpest)}, {Resolution(vids[Idx(items, sharpest[0]) - 1]!)}.");
            var hdr = items.Where((_, k) => vids[k]!.IsHdr).ToList();
            if (hdr.Count > 0 && hdr.Count < items.Count)
                lines.Add($"HDR (for HDR TVs): only {Names(items, hdr)}.");
        }
        var lossless = items.Where(i => BestAudio(i) is { } a && Lossless(a)).ToList();
        if (lossless.Count > 0 && lossless.Count < items.Count)
            lines.Add($"Studio-quality (lossless) sound: only {Names(items, lossless)}.");
        for (var i = 0; i < items.Count; i++)
        {
            var problems = Findings(items, i, null).Count(f => f.Level == 2);
            if (problems > 0)
                lines.Add($"Number {N(i)} has {Count(problems, "possible problem")} (see \"Worth checking\").");
        }
        if (lines.Count == 0)
            lines.Add("The sources are very much alike; see below for the details.");
        return lines;
    }

    private static List<string> Differences(IReadOnlyList<CompareItem> items)
    {
        var lines = new List<string>();
        if (items.Count < 2)
            return lines;
        var r = items[0];
        for (var i = 1; i < items.Count; i++)
        {
            var it = items[i];
            if (Math.Abs(it.Speed - 1) > 0.001)
                lines.Add($"Number {N(i)} plays {Math.Abs(it.Speed - 1) * 100:0.#}% {(it.Speed < 1 ? "faster" : "slower")} than the reference" +
                          (Math.Abs(1 / it.Speed - 25 / 23.976) < 0.002 ? " (the usual European TV speed-up; voices sound slightly higher)." : "."));
            if (it.Sections.Count > 0 && !it.SongMapped)
            {
                var cut = it.Sections.Count(s => s.Offset == null);
                lines.Add($"Number {N(i)} is a different cut of the film: {Count(it.Sections.Count, "part")} line up differently" +
                          (cut > 0 ? $", and {Count(cut, "part")} of the reference {(cut == 1 ? "is" : "are")} missing from it." : "."));
            }
            else if (Math.Abs(it.Offset) >= 0.04)
            {
                lines.Add($"Number {N(i)} starts {Seconds(Math.Abs(it.Offset))} {(it.Offset > 0 ? "later" : "earlier")} than the reference; CES lined them up so the same moments are compared.");
            }
            var (rs, os) = (r.Title.Info?.SizeBytes ?? 0, it.Title.Info?.SizeBytes ?? 0);
            if (rs > 0 && os > 0 && (os > rs * 1.5 || rs > os * 1.5))
                lines.Add($"Number {N(i)} is {(os > rs ? "much bigger" : "much smaller")} than the reference ({Format.Size(os)} against {Format.Size(rs)}); more data usually means fewer compression flaws, but not always.");
            var (rd, od) = (r.Title.Info?.Duration ?? 0, it.Title.Info?.Duration ?? 0);
            if (rd > 60 && od > 60 && Math.Abs(rd - od) > 30 && it.Sections.Count == 0 && Math.Abs(it.Speed - 1) < 0.001)
                lines.Add($"Number {N(i)} is {Length(Math.Abs(rd - od))} {(od > rd ? "longer" : "shorter")} than the reference.");
            var (ra, oa) = (r.Title.Info?.Audio.Count() ?? 0, it.Title.Info?.Audio.Count() ?? 0);
            if (ra != oa)
                lines.Add($"Number {N(i)} has {Count(oa, "sound track")}, the reference {ra}.");
            var (rsub, osub) = (r.Title.Info?.Subtitles.Count() ?? 0, it.Title.Info?.Subtitles.Count() ?? 0);
            if (rsub != osub)
                lines.Add($"Number {N(i)} has {Count(osub, "subtitle track")}, the reference {rsub}.");
        }
        return lines;
    }

    private static List<string> Closeness(IReadOnlyList<(CompareItem Reference, CompareItem Distorted, MetricResult Result)> metrics)
    {
        var lines = new List<string>();
        foreach (var (rf, ds, res) in metrics)
        {
            var s = Score(res);
            if (double.IsNaN(s.Value))
                continue;
            var line = $"{ds.Name} looks {Closeness(s)} next to {rf.Name} ({s.Name} {s.Value.ToString(s.Name == "SSIM" ? "0.000" : "0", Inv)}).";
            if (res.Series.TryGetValue(Metric.Vmaf, out var vmaf) && vmaf.Low1 < vmaf.Mean - 15)
                line += " Some scenes are clearly worse than the rest, often dark or fast-moving ones.";
            lines.Add(line);
        }
        lines.Add($"Measured over {Count(metrics.Max(m => m.Result.Frames), "frame")}; 100 (VMAF) would be identical.");
        return lines;
    }

    private static List<string> Subtitles(IReadOnlyList<CompareItem> items, IReadOnlyList<SubtitleTimingReport> reports)
    {
        var lines = new List<string>();
        foreach (var r in reports)
        {
            var i = Idx(items, r.Item);
            var share = r.ReferenceEvents > 0 ? (double)r.Matched / r.ReferenceEvents : 0;
            var timing = double.IsNaN(r.MedianDelta) ? "" : Math.Abs(r.MedianDelta) < 0.05
                ? "they appear at the right moments"
                : $"they appear about {Seconds(Math.Abs(r.MedianDelta))} {(r.MedianDelta > 0 ? "late" : "early")}";
            if (!double.IsNaN(r.DriftPerHour) && Math.Abs(r.DriftPerHour) > 0.2)
                timing += (timing.Length > 0 ? " and " : "") + "they slowly drift out of step during the film";
            lines.Add($"Number {i} ({r.TrackLabel}): {share:P0} of the lines match the reference" + (timing.Length > 0 ? $"; {timing}." : "."));
        }
        return lines;
    }

    private static (List<string> Check, List<string> Notes) Issues(IReadOnlyList<CompareItem> items, IReadOnlyList<SubtitleTimingReport>? subtitles)
    {
        var check = new List<string>();
        var notes = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var f in Findings(items, i, subtitles).Where(f => f.Level > 0).DistinctBy(f => f.Area + f.Text))
                (f.Level == 2 ? check : notes).Add($"Number {N(i)} · {f.Area}" + (f.Text.Length > 0 ? $" Measured: {f.Text.TrimEnd('.')}." : ""));
        }
        return (check, notes);
    }

    /// <summary>What each kind of finding means, in everyday words: a short name and one sentence.</summary>
    private static readonly Dictionary<string, (string Name, string Meaning)> Plain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Framing"] = ("framing", "the picture is framed differently (zoomed, cropped or shifted) from the reference."),
        ["Black bars"] = ("black bars", "there are black bars at the edges, so part of the screen is not picture."),
        ["Channels"] = ("speakers", "the sound channels look unusual, for example a silent channel or two channels that are copies."),
        ["Bit depth"] = ("bit depth", "the file claims more detail (bits) than it really uses."),
        ["Spectrum"] = ("high notes", "the highest frequencies of the sound are cut off, a sign of lossy compression somewhere along the way."),
        ["Frames"] = ("smooth motion", "some frames are repeated, dropped or out of order, which can show as stutter."),
        ["Stabilisation"] = ("steadiness", "the picture moves differently from the reference: it was steadied, or it shakes."),
        ["Restoration"] = ("dirt and scratches", "the picture was cleaned up differently: dirt and scratches were removed, or more of them are visible."),
        ["Grain"] = ("film grain", "the fine film texture differs; strong noise reduction removes grain and can take real detail with it."),
        ["Detail"] = ("sharpness", "the amount of fine detail differs."),
        ["Chroma"] = ("colour detail", "colour detail is lower than brightness detail, a common side effect of converting the video."),
        ["Upscale"] = ("real resolution", "the picture looks enlarged from a lower resolution, so it has less real detail than its size suggests."),
        ["Over time"] = ("colour over time", "the colours or brightness change during the film compared with the reference."),
        ["Loudness"] = ("loudness", "the sound is louder or quieter than expected."),
        ["Halos"] = ("halos", "bright outlines around edges, left by artificial sharpening."),
        ["HDR"] = ("HDR", "about the HDR information that tells the TV how bright to show the picture."),
        ["HDR10"] = ("HDR", "about the HDR information that tells the TV how bright to show the picture."),
        ["Dynamic HDR"] = ("HDR", "about the scene-by-scene HDR information for HDR TVs."),
        ["Dolby Vision"] = ("Dolby Vision", "about the Dolby Vision information for TVs that support it."),
        ["Banding"] = ("banding", "smooth areas such as skies show visible steps instead of a smooth gradient."),
        ["Tone"] = ("brightness", "brightness differs: the picture is darker, brighter or has more contrast than the reference."),
        ["Shift"] = ("subtitle timing", "the subtitles appear earlier or later than in the reference."),
        ["Drift"] = ("subtitle timing", "the subtitles slowly drift out of step during the film."),
        ["Lines"] = ("subtitle lines", "some subtitle lines are missing or extra compared with the reference."),
        ["Subtitles"] = ("subtitles", "the subtitles differ from the reference."),
        ["Scan"] = ("interlacing", "the picture is made of half-pictures (interlaced), which can show comb-like lines when things move."),
        ["Saturation"] = ("colour strength", "colours are stronger or paler than in the reference."),
        ["Peaks"] = ("loud peaks", "the loudest moments are cut off or squashed, which can sound harsh."),
        ["Master"] = ("source master", "it looks like it was made from a different master (another transfer of the film)."),
        ["Levels"] = ("black and white levels", "blacks may look grey and washed out, or shadows and highlights lose detail."),
        ["Grade"] = ("colour grading", "the colour grading differs from the reference."),
        ["Dynamics"] = ("loud and soft", "the difference between loud and soft parts is smaller (compressed sound)."),
        ["DC offset"] = ("sound wave offset", "a small technical flaw in the sound wave; usually not audible."),
        ["Contrast"] = ("contrast", "the contrast differs from the reference."),
        ["Colour"] = ("colour tint", "the overall colour differs: warmer, cooler or with a tint."),
    };

    private static string Uncap(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    /// <summary>A source's findings with plain group names (picture, sound, colour, compared with the reference, subtitles).</summary>
    private static List<Finding> Findings(IReadOnlyList<CompareItem> items, int i, IReadOnlyList<SubtitleTimingReport>? subtitles)
    {
        var it = items[i];
        var all = new List<Finding>();
        // "group, what: meaning" for the kinds of finding with a plain explanation; the others keep their own words
        void Add(IEnumerable<Finding> fs, string group) =>
            all.AddRange(fs.Select(f => Plain.TryGetValue(f.Area, out var p)
                ? new Finding { Area = $"{group}, {p.Name}: {p.Meaning}", Text = f.Text, Level = f.Level }
                : new Finding { Area = $"{group}, {f.Area.ToLowerInvariant()}: {Uncap(f.Text)}", Text = "", Level = f.Level }));
        Add(it.Video?.Findings ?? [], "picture");
        foreach (var a in it.Audio)
            Add(a.Findings, it.Audio.Count > 1 ? $"sound track {a.TrackIndex + 1}" : "sound");
        if (i > 0)
        {
            Add(it.Grade?.Findings ?? [], "colour and framing against the reference");
            Add(it.Temporal?.Findings ?? [], "picture over time");
            Add(CrossFindings.Video(items[0], it).Concat(CrossFindings.Audio(items[0], it)), "against the reference");
            foreach (var s in subtitles?.Where(s => s.Item == it) ?? [])
                Add(s.Findings, "subtitles");
        }
        return all;
    }

    // ---------- words ----------

    private static (string Name, double Value) Score(MetricResult r)
    {
        foreach (var m in new[] { Metric.Vmaf, Metric.Ssim, Metric.Xpsnr, Metric.Psnr })
        {
            if (r.Series.TryGetValue(m, out var s) && !double.IsNaN(s.Mean))
                return (s.Name, s.Mean);
        }
        return ("", double.NaN);
    }

    /// <summary>A closeness score in words (VMAF, SSIM or PSNR scale).</summary>
    public static string Closeness((string Name, double Value) s)
    {
        // map every metric onto a 0–4 step: identical-looking … clearly worse
        var step = s.Name switch
        {
            "VMAF" => s.Value >= 95 ? 0 : s.Value >= 90 ? 1 : s.Value >= 80 ? 2 : s.Value >= 70 ? 3 : 4,
            "SSIM" => s.Value >= 0.99 ? 0 : s.Value >= 0.98 ? 1 : s.Value >= 0.96 ? 2 : s.Value >= 0.93 ? 3 : 4,
            _ => s.Value >= 45 ? 0 : s.Value >= 40 ? 1 : s.Value >= 36 ? 2 : s.Value >= 32 ? 3 : 4,
        };
        return step switch
        {
            0 => "practically identical",
            1 => "very close (differences are hard to spot)",
            2 => "close, with small differences you may notice on a big screen",
            3 => "noticeably softer or blockier",
            _ => "clearly worse",
        };
    }

    private static string Picture(StreamInfo v)
    {
        var s = "picture " + Resolution(v);
        if (v.IsHdr)
            s += v.HdrFormat.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase) ? " with HDR (Dolby Vision)" : " with HDR";
        return s;
    }

    private static string Resolution(StreamInfo v) =>
        v.Width >= 3400 || v.Height >= 1900 ? "4K (Ultra HD)"
        : v.Width >= 1800 || v.Height >= 1000 ? "Full HD (1080p)"
        : v.Width >= 1200 || v.Height >= 700 ? "HD (720p)"
        : v.Width > 0 ? $"standard definition ({v.Height} lines, DVD quality)" : "unknown size";

    private static string Sound(StreamInfo a, int tracks)
    {
        var ch = a.Channels switch
        {
            1 => "mono",
            2 => "stereo",
            >= 3 => $"{a.ChannelText} surround",
            _ => "",
        };
        var q = Lossless(a) ? "lossless" : "lossy";
        return $"sound {ch}, {q} ({CodecName(a)})".Replace("sound , ", "sound ") + (tracks > 1 ? $", the best of {Count(tracks, "sound track")}" : "");
    }

    /// <summary>The name a sound format is sold under.</summary>
    private static string CodecName(StreamInfo a) => a.Codec switch
    {
        "ac3" => "Dolby Digital",
        "eac3" => a.CodecDisplay.Contains("Atmos", StringComparison.OrdinalIgnoreCase) ? "Dolby Digital Plus with Atmos" : "Dolby Digital Plus",
        "truehd" => a.CodecDisplay.Contains("Atmos", StringComparison.OrdinalIgnoreCase) ? "Dolby TrueHD with Atmos" : "Dolby TrueHD",
        "dts" or "dca" => a.Profile.Contains("MA", StringComparison.Ordinal) ? "DTS-HD Master Audio" : a.Profile.Contains('X') ? "DTS:X" : "DTS",
        "aac" => "AAC",
        "flac" => "FLAC",
        "mp3" => "MP3",
        "mp2" => "MPEG audio",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        _ when a.Codec.StartsWith("pcm", StringComparison.Ordinal) => "PCM, uncompressed",
        _ => a.CodecDisplay,
    };

    private static bool Lossless(StreamInfo a) =>
        !MediaProber.IsLossy(a.Codec) || (a.Codec is "dts" or "dca" && a.Profile.Contains("MA", StringComparison.Ordinal));

    private static StreamInfo? BestAudio(CompareItem it) =>
        it.Title.Info?.Audio.OrderByDescending(Lossless).ThenByDescending(a => a.Channels).ThenByDescending(a => a.BitRate).FirstOrDefault();

    private static int Idx(IReadOnlyList<CompareItem> items, CompareItem it) => items.ToList().IndexOf(it) + 1;

    private static string Names(IReadOnlyList<CompareItem> items, IEnumerable<CompareItem> which) =>
        string.Join(" and ", which.Select(w => "number " + Idx(items, w)));

    private static string Count(int n, string noun) => n.ToString(Inv) + " " + noun + (n == 1 ? "" : "s");

    private static string Length(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{t.Minutes} min" + (t.Minutes < 10 && t.Seconds > 0 ? $" {t.Seconds} s" : "")
            : $"{t.Seconds} s";
    }

    private static string Seconds(double s) =>
        s < 0.1 ? $"{s * 1000:0} milliseconds" : s < 60 ? (s.ToString("0.#", Inv) + (s.ToString("0.#", Inv) == "1" ? " second" : " seconds")) : Length(s);

    private static bool Mentions(string text, string term)
    {
        // the start of a word ("frames" and "cropped" count, "XPSNR" is not "PSNR")
        for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
        {
            if (at == 0 || !char.IsLetter(text[at - 1]))
                return true;
        }
        return false;
    }

    private static string Wrap(string line, string indent, int width = 100)
    {
        if (line.Length <= width)
            return line;
        var sb = new StringBuilder();
        var cur = new StringBuilder();
        foreach (var w in line.Split(' '))
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > width)
            {
                sb.AppendLine(cur.ToString());
                cur.Clear().Append(indent);
            }
            else if (cur.Length > 0 && cur.ToString() != indent)
            {
                cur.Append(' ');
            }
            cur.Append(w);
        }
        return sb.Append(cur).ToString();
    }
}
