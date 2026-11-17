using System.Globalization;

namespace Ces.Model;

/// <summary>Display formatting shared by GUI, CLI and report.</summary>
public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Time(double seconds, bool millis = true)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "—";
        var neg = seconds < 0;
        var ts = TimeSpan.FromSeconds(Math.Abs(seconds));
        var s = millis
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}"
            : $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
        return neg ? "-" + s : s;
    }

    /// <summary>Parses "1:02:03.5", "62:03", "3723.5" or "3723.5s" into seconds.</summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim().TrimEnd('s');
        var neg = text.StartsWith('-');
        if (neg)
            text = text[1..];
        var parts = text.Split(':');
        if (parts.Length > 3)
            return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, Inv, out var v) || v < 0)
                return false;
            total = total * 60 + v;
        }
        seconds = neg ? -total : total;
        return true;
    }

    public static string Size(long bytes)
    {
        if (bytes <= 0)
            return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.00", Inv) + " " + units[u];
    }

    public static string BitRate(long bps)
    {
        if (bps <= 0)
            return "—";
        if (bps >= 1_000_000)
            return (bps / 1_000_000.0).ToString("0.00", Inv) + " Mb/s";
        return (bps / 1000.0).ToString("0", Inv) + " kb/s";
    }

    public static string Num(double v, string fmt = "0.00") =>
        double.IsNaN(v) || double.IsInfinity(v) ? "—" : v.ToString(fmt, Inv);

    public static string Db(double v) =>
        double.IsNegativeInfinity(v) ? "-∞ dB" : double.IsNaN(v) ? "—" : v.ToString("0.0", Inv) + " dB";
}
