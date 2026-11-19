using System.Text.RegularExpressions;

namespace Ces.Spot;

/// <summary>Turns the software strings files carry (vendor strings, ©too, ISFT) into readable encoder names.</summary>
public static partial class EncoderNames
{
    /// <summary>libVorbis release by the date in its vendor string.</summary>
    private static readonly (string Date, string Version)[] Vorbis =
    [
        ("20000508", "1.0 beta 1"), ("20001031", "1.0 beta 3"), ("20010225", "1.0 beta 4"), ("20010813", "1.0 rc2"),
        ("20011231", "1.0 rc3"), ("20020717", "1.0"), ("20030909", "1.0.1"), ("20040629", "1.1.0"),
        ("20050304", "1.1.1 / 1.1.2"), ("20070622", "1.2.0"), ("20080501", "1.2.1"), ("20090624", "1.2.2"),
        ("20090709", "1.2.3"), ("20100325", "1.3.1"), ("20101101", "1.3.2"), ("20120203", "1.3.3"),
        ("20140122", "1.3.4"), ("20150105", "1.3.5"), ("20180316", "1.3.6"), ("20200704", "1.3.7"),
    ];

    public static string Normalize(string software)
    {
        var s = software.Trim();
        if (LavRegex().Match(s) is { Success: true } lav)
            return $"FFmpeg ({lav.Value})";
        if (AotuvRegex().Match(s) is { Success: true } ao)
            return "aoTuV " + ao.Groups[1].Value;
        if (s.StartsWith("Xiph.Org libVorbis", StringComparison.Ordinal) && VorbisDateRegex().Match(s) is { Success: true } d)
        {
            var date = d.Groups[1].Value;
            var v = Vorbis.FirstOrDefault(x => x.Date == date).Version;
            return v != null ? $"libVorbis {v}" : $"libVorbis ({date})";
        }
        if (s.StartsWith("libopus", StringComparison.Ordinal))
            return s.Split(' ', 3) is [var a, var b, ..] ? $"{a} {b}" : s;
        return s;
    }

    [GeneratedRegex(@"Lav[fc]\d+\.\d+\.\d+")]
    private static partial Regex LavRegex();

    [GeneratedRegex(@"aoTuV\s+(\S+)")]
    private static partial Regex AotuvRegex();

    [GeneratedRegex(@"\b((?:19|20)\d{6})\b")]
    private static partial Regex VorbisDateRegex();
}
