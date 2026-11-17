using System.Globalization;

namespace Ces.Spot;

public static class Fmt
{
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return "00:00:00";
        }
        var t = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
    }

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
}
