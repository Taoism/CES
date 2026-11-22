using System.Globalization;
using System.Text;
using Ces.Spot;

namespace Ces.SpotUi;

/// <summary>Plain-text and CSV renderings of results (clipboard and export).</summary>
public static class SpotReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Text(AnalysisResult r, bool details = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Path);
        if (r.Error is not null)
        {
            sb.AppendLine("  error: " + r.Error);
            return sb.ToString();
        }
        sb.AppendLine(string.Create(Inv, $"  {r.Format}  {r.Encoder}  [{r.Confidence}]  {r.BitrateKbps:0} kbps  {r.SampleRate} Hz {r.ModeName}  {Fmt.Duration(r.DurationSeconds)}  {r.Quality}"));
        if (r.EncoderSettings.Length > 0)
        {
            sb.AppendLine("  settings: " + r.EncoderSettings);
        }
        foreach (var p in r.Problems)
        {
            sb.AppendLine("  ! " + p);
        }
        if (details)
        {
            string? group = null;
            foreach (var d in r.Details)
            {
                if (d.Group != group)
                {
                    group = d.Group;
                    sb.AppendLine("  [" + group + "]");
                }
                sb.AppendLine(string.Create(Inv, $"    {d.Name,-26} {d.Value}"));
            }
        }
        return sb.ToString();
    }

    public static string Table(IEnumerable<FileRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name\tEncoder\tSettings\tkbps\tLength\tQuality\tProblems");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join('\t', r.Name, r.Encoder, r.Settings, r.BitrateText, r.LengthText, r.QualityText, r.Problems));
        }
        return sb.ToString();
    }

    public static string Csv(IEnumerable<FileRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,Format,Encoder,Confidence,Settings,Bitrate kbps,VBR,Length,Seconds,Sample rate,Mode,Frames,Size,Quality,Source,Problems,Artist,Title,Album");
        foreach (var r in rows)
        {
            var x = r.Result!;
            sb.AppendJoin(',',
                Q(r.Path), Q(x.Format), Q(r.Encoder), Q(r.Confidence), Q(r.Settings),
                x.BitrateKbps.ToString("0.0", Inv), Q(r.VbrText), Q(r.LengthText), x.DurationSeconds.ToString("0.000", Inv),
                x.SampleRate.ToString(Inv), Q(r.ModeText), x.FrameCount.ToString(Inv), r.Size.ToString(Inv),
                Q(r.QualityText), Q(r.SourceCheck), Q(r.Problems), Q(r.Artist), Q(r.Title), Q(r.Album));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The checked files in everyday words (summary.txt of an export): how many are real lossless copies, which
    /// may not be what they claim, which are damaged, and one plain sentence per file.
    /// </summary>
    public static string Plain(IReadOnlyList<FileRow> rows)
    {
        var sb = new StringBuilder();
        var title = "In plain words: " + (rows.Count == 1 ? "1 file checked" : rows.Count.ToString(Inv) + " files checked");
        sb.AppendLine(title).AppendLine(new string('=', title.Length));
        sb.AppendLine("CES · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)).AppendLine();
        var results = rows.Select(r => r.Result!).ToList();
        var lossless = results.Count(r => r.Error is null && r.Quality == QualityRating.Lossless && Genuine(r));
        var suspect = results.Count(r => r.Error is null && !Genuine(r));
        var damaged = results.Count(r => r.Error is not null || HasDamage(r));
        sb.AppendLine("The short answer").AppendLine("----------------");
        if (lossless > 0)
            sb.AppendLine($"- {Count(lossless, "file")} {(lossless == 1 ? "is" : "are")} lossless: a perfect copy of the original sound.");
        foreach (var (q, words) in Ratings)
        {
            var n = results.Count(r => r.Error is null && r.Quality == q && !IsLosslessFormat(r));
            if (n > 0)
                sb.AppendLine($"- {Count(n, "file")}: {words}.");
        }
        if (suspect > 0)
            sb.AppendLine($"- {Count(suspect, "file")} may not be what {(suspect == 1 ? "it claims" : "they claim")} (see below).");
        if (damaged > 0)
            sb.AppendLine($"- {Count(damaged, "file")} {(damaged == 1 ? "has" : "have")} errors or damage.");
        if (lossless + suspect + damaged == 0 && results.All(r => r.Quality == QualityRating.Unknown))
            sb.AppendLine("- No quality rating could be given for these files.");
        sb.AppendLine();
        sb.AppendLine("File by file").AppendLine("------------");
        foreach (var r in rows)
            sb.AppendLine("- " + r.Name + ": " + Sentence(r.Result!));
        sb.AppendLine();
        var check = rows.Where(r => r.Result!.Problems.Count > 0).ToList();
        if (check.Count > 0)
        {
            sb.AppendLine("Worth checking").AppendLine("--------------");
            foreach (var r in check)
            {
                foreach (var p in r.Result!.Problems)
                    sb.AppendLine("- " + r.Name + ": " + p);
            }
            sb.AppendLine();
        }
        sb.AppendLine("Words used in this report").AppendLine("-------------------------");
        sb.AppendLine("- lossless: stored exactly as the original, nothing thrown away (FLAC, WAV).");
        sb.AppendLine("- lossy: made smaller by leaving out sound most people do not hear (MP3, AAC, Ogg).");
        sb.AppendLine("- kbps: how much data is used per second of sound; more usually means better quality.");
        sb.AppendLine("- upsampled / padded bits: converted to a higher number than the original had; it looks better on paper but sounds the same as the lower original.");
        sb.AppendLine();
        sb.AppendLine("The full technical details are in report.txt and spot-report.csv next to this file.");
        return sb.ToString();
    }

    private static readonly (QualityRating Rating, string Words)[] Ratings =
    [
        (QualityRating.Good, "good quality: most people cannot tell them from the original"),
        (QualityRating.Fair, "fair quality: the difference can be heard on good speakers or headphones"),
        (QualityRating.Poor, "low quality: the loss is easy to hear"),
    ];

    private static bool IsLosslessFormat(AnalysisResult r) => r.Format is "FLAC" or "WAV";

    private static bool Genuine(AnalysisResult r) => r.SourceCheck is "" or "ok";

    private static bool HasDamage(AnalysisResult r) =>
        r.Problems.Exists(p => p.Contains("CRC", StringComparison.OrdinalIgnoreCase) || p.Contains("sync", StringComparison.OrdinalIgnoreCase)
                               || p.Contains("MD5", StringComparison.OrdinalIgnoreCase) || p.Contains("truncated", StringComparison.OrdinalIgnoreCase));

    private static string Sentence(AnalysisResult r)
    {
        if (r.Error is not null)
            return "could not be read (" + r.Error + ").";
        var what = IsLosslessFormat(r) ? r.Format : r.Encoder is { Length: > 0 } e && e != "Unknown" ? $"{r.Format} made with {e}" : r.Format;
        var kbps = r.BitrateKbps > 0 && !IsLosslessFormat(r) ? string.Create(Inv, $", {r.BitrateKbps:0} kbps") : "";
        var s = r.Quality switch
        {
            QualityRating.Lossless => $"lossless ({what}), a perfect copy of the original",
            QualityRating.Good => $"good quality ({what}{kbps}); most people cannot tell it from the original",
            QualityRating.Fair when IsLosslessFormat(r) => $"{what}, but with problems (see below)",
            QualityRating.Fair => $"fair quality ({what}{kbps}); the difference can be heard on good equipment",
            QualityRating.Poor when IsLosslessFormat(r) => $"{what}, but damaged (see below)",
            QualityRating.Poor => $"low quality ({what}{kbps}); the loss is easy to hear",
            _ => $"{what}{kbps}",
        };
        if (r.SourceCheck.Contains("lossy source", StringComparison.Ordinal))
            s += ". But the sound looks like it came from a lossy file such as an MP3, so it is no better than that file";
        if (r.SourceCheck.Contains("upsampled", StringComparison.Ordinal))
            s += ". It was converted up from a lower sample rate, so it has no more detail than that original";
        if (r.SourceCheck.Contains("-bit in", StringComparison.Ordinal))
            s += string.Create(Inv, $". It says {r.BitsPerSample}-bit, but really uses fewer bits (padded)");
        if (HasDamage(r))
            s += ". It has errors in the data (damage)";
        return s + ".";
    }

    private static string Count(int n, string noun) => n.ToString(Inv) + " " + noun + (n == 1 ? "" : "s");

    private static string Q(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
}
