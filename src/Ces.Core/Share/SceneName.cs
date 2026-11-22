using System.Globalization;
using System.Text.RegularExpressions;
using Ces.Compare;
using Ces.Model;

namespace Ces.Share;

/// <summary>
/// Keeps release names out of what goes public (slow.pics): "Movie.1970.COMPLETE.BLURAY-GRP" becomes "Movie (1970)",
/// and each source is named after what it is ("Blu-ray 1080p AVC") instead of the release it came from.
/// </summary>
public static partial class SceneName
{
    // words that only ever appear in release names (source, format, codec, edition tags)
    private const string Tags = "COMPLETE|BLURAY|BLU-RAY|BDRIP|BRRIP|BDREMUX|REMUX|UHD|UNTOUCHED|FULL|BD25|BD50|BD66|BD100|AVC|HEVC|VC-?1|MPEG-?2|"
        + "X26[45]|H\\.?26[45]|XVID|DIVX|WEB-?DL|WEBRIP|WEB|HDTV|DVDRIP|DVDR|DVD5|DVD9|DVD|NTSC|PAL|HDR10\\+?|HDR|DV|DOVI|SDR|"
        + "DTS(-?HD)?|DTS-?X|MA|TRUEHD|ATMOS|DDP?5\\.1|DD\\+?|AC3|EAC3|AAC|FLAC|LPCM|\\d{3,4}[PI]|4K|REPACK|PROPER|RERIP|INTERNAL|"
        + "LIMITED|EXTENDED|REMASTERED|UNRATED|DC|IMAX|MULTI|MULTISUBS|SUBBED|DUBBED|NORDIC|GERMAN|FRENCH|ISO|MKV|MP4|M2TS";

    [GeneratedRegex(@"^(?:" + Tags + @")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagWord();

    [GeneratedRegex(@"^(?:19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Year();

    // "Some.Words.Joined.By.Dots" or "words_with_underscores", optionally ending in -GROUP
    [GeneratedRegex(@"^[\w'&!+-]+(?:[._][\w'&!+-]+){2,}(?:-[A-Za-z0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Dotted();

    [GeneratedRegex(@"-[A-Za-z0-9]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupSuffix();

    private static readonly string[] Extensions = [".mkv", ".mp4", ".m2ts", ".ts", ".iso", ".rar", ".avi", ".mpls", ".vob", ".ifo"];

    /// <summary>True when <paramref name="s"/> reads like a release name rather than something a person typed.</summary>
    public static bool LooksLikeRelease(string s)
    {
        s = StripExtension(s.Trim());
        if (s.Length == 0)
            return false;
        if (Dotted().IsMatch(s))
            return true;
        var words = Words(s);
        return words.Count(w => TagWord().IsMatch(w)) >= 2 || (words.Count >= 2 && GroupSuffix().IsMatch(s) && words.Any(w => TagWord().IsMatch(w) || Year().IsMatch(w)));
    }

    /// <summary>"Movie.Title.1970.COMPLETE.BLURAY-GRP" → "Movie Title (1970)"; plain text stays as it is.</summary>
    public static string Title(string s)
    {
        if (!LooksLikeRelease(s))
            return s.Trim();
        var words = Words(StripExtension(s.Trim()));
        var keep = new List<string>();
        string? year = null;
        // the title is everything before the year ("Movie.Title.1970..."), or before the first tag when there is none
        var y = words.FindIndex(1, w => Year().IsMatch(w));
        if (y > 0)
        {
            year = words[y];
            keep.AddRange(words.Take(y).Where(w => !TagWord().IsMatch(w)));
        }
        else
        {
            keep.AddRange(words.TakeWhile(w => !TagWord().IsMatch(w)));
        }
        var title = string.Join(' ', keep.Select(Capital)).Trim();
        if (title.Length == 0)
            return "Comparison";
        return year != null ? $"{title} ({year})" : title;
    }

    /// <summary>What a source is, without its release name: "Blu-ray 1080p AVC HDR" (or the label the user gave it, if that is not a release name).</summary>
    public static string Describe(CompareItem it)
    {
        if (it.Label.Length > 0 && !LooksLikeRelease(it.Label))
            return it.Label;
        var parts = new List<string>
        {
            it.Source.Kind switch
            {
                SourceKind.BluRay => "Blu-ray",
                SourceKind.Dvd => "DVD",
                _ => "File",
            },
        };
        if (it.Title.Info?.Video is { } v)
        {
            if (v.Height > 0)
                parts.Add((v.Height >= 2000 || v.Width >= 3800 ? 2160 : v.Height >= 1000 || v.Width >= 1900 ? 1080 : v.Height >= 700 ? 720 : v.Height).ToString(CultureInfo.InvariantCulture) + "p");
            var codec = v.Codec switch
            {
                "h264" => "AVC",
                "hevc" => "HEVC",
                "av1" => "AV1",
                "vc1" => "VC-1",
                "mpeg2video" => "MPEG-2",
                "" => "",
                _ => v.Codec.ToUpperInvariant(),
            };
            if (codec.Length > 0)
                parts.Add(codec);
            if (v.HdrShort.Length > 0)
                parts.Add(v.HdrShort);
        }
        return string.Join(' ', parts);
    }

    /// <summary>Neutral names for every source: "1 · Blu-ray 1080p AVC", numbered so equal descriptions stay apart.</summary>
    public static List<string> Labels(IReadOnlyList<CompareItem> items) =>
        items.Select((it, i) => $"{i + 1} · {Describe(it)}").ToList();

    /// <summary>Replaces each source's name in <paramref name="text"/> (a caption like "worst for X") with its neutral label, then cleans what is left.</summary>
    public static string Scrub(string text, IReadOnlyList<CompareItem> items)
    {
        var labels = Labels(items);
        // longest names first, so one name inside another is not half replaced
        foreach (var (it, i) in items.Select((it, i) => (it, i)).OrderByDescending(x => x.it.Name.Length))
        {
            foreach (var n in new[] { it.Name, it.Source.DisplayName, Path.GetFileName(it.Source.Path) }.Where(n => n.Length > 0).Distinct())
                text = text.Replace(n, "source " + labels[i], StringComparison.OrdinalIgnoreCase);
        }
        // anything else that still reads like a release name
        return string.Join(' ', text.Split(' ').Select(w => LooksLikeRelease(w) ? Title(w) : w));
    }

    private static string StripExtension(string s)
    {
        foreach (var e in Extensions)
        {
            if (s.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                return s[..^e.Length];
        }
        return s;
    }

    private static List<string> Words(string s) =>
        s.Split(['.', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
         .SelectMany(w => w.Contains('-') && GroupSuffix().IsMatch(w) && !TagWord().IsMatch(w) ? w.Split('-', StringSplitOptions.RemoveEmptyEntries) : [w])
         .ToList();

    private static string Capital(string w) =>
        w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsLower(c)) ? char.ToUpperInvariant(w[0]) + w[1..] : w;
}
