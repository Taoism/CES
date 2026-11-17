using System.Text;

namespace EncoderSpot.Core.Tags;

/// <summary>Tags found around the audio stream and the byte range of the audio itself.</summary>
public sealed class TagInfo
{
    public int? Id3v2Version { get; internal set; }
    public long Id3v2Size { get; internal set; }
    public bool HasId3v1 { get; internal set; }
    public bool HasId3v1Enhanced { get; internal set; }
    public long ApeSize { get; internal set; }
    public int? ApeVersion { get; internal set; }
    public long Lyrics3Size { get; internal set; }

    /// <summary>First byte of the audio (after a leading ID3v2 tag).</summary>
    public long AudioStart { get; internal set; }

    /// <summary>One past the last audio byte (before trailing tags).</summary>
    public long AudioEnd { get; internal set; }

    /// <summary>Text fields keyed by a friendly name (Artist, Title, Album, Year, Genre, Comment, Encoder, EncodedBy...).</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Describe()
    {
        var parts = new List<string>();
        if (Id3v2Version is int v)
        {
            parts.Add($"ID3v2.{v}");
        }
        if (ApeVersion is int a)
        {
            parts.Add(a >= 2000 ? "APEv2" : "APEv1");
        }
        if (Lyrics3Size > 0)
        {
            parts.Add("Lyrics3");
        }
        if (HasId3v1)
        {
            parts.Add(HasId3v1Enhanced ? "ID3v1+" : "ID3v1");
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    internal void SetIfEmpty(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        value = value.Trim().TrimEnd('\0').Trim();
        if (value.Length > 0 && !Fields.ContainsKey(key))
        {
            Fields[key] = value;
        }
    }

    internal static string Latin1(ReadOnlySpan<byte> b)
    {
        int end = b.IndexOf((byte)0);
        if (end >= 0)
        {
            b = b[..end];
        }
        return Encoding.Latin1.GetString(b).Trim();
    }
}
