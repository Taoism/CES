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

    private static string Q(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
}
