using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ces.Spot.Tags;

/// <summary>Finds ID3v2 (start), and ID3v1, APE and Lyrics3 tags (end) of a file.</summary>
public static class TagScanner
{
    private static readonly string[] Id3v1Genres =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal",
        "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae", "Rock", "Techno", "Industrial",
        "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk",
        "Fusion", "Trance", "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise",
        "AlternRock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic",
        "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta",
        "Top 40", "Christian Rap", "Pop/Funk", "Jungle", "Native American", "Cabaret", "New Wave", "Psychadelic", "Rave", "Showtunes",
        "Trailer", "Lo-Fi", "Tribal", "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
    ];

    public static TagInfo Scan(Stream s)
    {
        var info = new TagInfo { AudioStart = 0, AudioEnd = s.Length };
        ReadLeadingId3v2(s, info);
        ReadTrailingTags(s, info);
        return info;
    }

    private static void ReadLeadingId3v2(Stream s, TagInfo info)
    {
        // Some files carry several ID3v2 tags back to back: skip them all, parse the first.
        long pos = 0;
        var h = new byte[10];
        bool first = true;
        while (pos + 10 <= s.Length)
        {
            s.Position = pos;
            if (s.ReadAtLeast(h, 10, false) < 10 || h[0] != 'I' || h[1] != 'D' || h[2] != '3' || h[3] == 0xFF || h[4] == 0xFF)
            {
                break;
            }
            long size = Syncsafe(h.AsSpan(6, 4));
            if (size < 0)
            {
                break;
            }
            bool footer = (h[5] & 0x10) != 0;
            long total = 10 + size + (footer ? 10 : 0);
            if (first)
            {
                info.Id3v2Version = h[3];
                int toRead = (int)Math.Min(size, 16 * 1024 * 1024);
                var body = new byte[toRead];
                s.ReadAtLeast(body, toRead, false);
                try
                {
                    Id3v2Reader.Parse(h[3], h[5], body, info);
                }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
                {
                    // malformed frames: keep what was read so far
                }
                first = false;
            }
            info.Id3v2Size += total;
            pos += total;
        }
        info.AudioStart = Math.Min(pos, s.Length);
    }

    private static void ReadTrailingTags(Stream s, TagInfo info)
    {
        long end = s.Length;
        // Tags can appear in any order (APE before ID3v1 is typical, Lyrics3 before ID3v1).
        for (int guard = 0; guard < 4; guard++)
        {
            long before = end;
            end = TryId3v1(s, info, end);
            end = TryLyrics3(s, info, end);
            end = TryApe(s, info, end);
            if (end == before)
            {
                break;
            }
        }
        info.AudioEnd = Math.Max(info.AudioStart, end);
    }

    private static long TryId3v1(Stream s, TagInfo info, long end)
    {
        if (info.HasId3v1 || end - 128 < info.AudioStart)
        {
            return end;
        }
        var t = new byte[128];
        s.Position = end - 128;
        s.ReadExactly(t);
        if (t[0] != 'T' || t[1] != 'A' || t[2] != 'G')
        {
            return end;
        }
        info.HasId3v1 = true;
        info.SetIfEmpty("Title", TagInfo.Latin1(t.AsSpan(3, 30)));
        info.SetIfEmpty("Artist", TagInfo.Latin1(t.AsSpan(33, 30)));
        info.SetIfEmpty("Album", TagInfo.Latin1(t.AsSpan(63, 30)));
        info.SetIfEmpty("Year", TagInfo.Latin1(t.AsSpan(93, 4)));
        bool v11 = t[125] == 0 && t[126] != 0;
        info.SetIfEmpty("Comment", TagInfo.Latin1(t.AsSpan(97, v11 ? 28 : 30)));
        if (v11)
        {
            info.SetIfEmpty("Track", t[126].ToString(CultureInfo.InvariantCulture));
        }
        if (t[127] < Id3v1Genres.Length)
        {
            info.SetIfEmpty("Genre", Id3v1Genres[t[127]]);
        }
        end -= 128;
        if (end - 227 >= info.AudioStart)
        {
            var e = new byte[4];
            s.Position = end - 227;
            s.ReadExactly(e);
            if (e[0] == 'T' && e[1] == 'A' && e[2] == 'G' && e[3] == '+')
            {
                info.HasId3v1Enhanced = true;
                end -= 227;
            }
        }
        return end;
    }

    private static long TryLyrics3(Stream s, TagInfo info, long end)
    {
        if (info.Lyrics3Size > 0 || end - 15 < info.AudioStart)
        {
            return end;
        }
        var t = new byte[15];
        s.Position = end - 15;
        s.ReadExactly(t);
        string tail = Encoding.ASCII.GetString(t);
        if (!tail.EndsWith("LYRICS200", StringComparison.Ordinal)
            || !long.TryParse(tail.AsSpan(0, 6), NumberStyles.None, CultureInfo.InvariantCulture, out long size))
        {
            return end;
        }
        long total = size + 15;
        if (end - total < info.AudioStart)
        {
            return end;
        }
        info.Lyrics3Size = total;
        return end - total;
    }

    private static long TryApe(Stream s, TagInfo info, long end)
    {
        if (info.ApeVersion is not null || end - 32 < info.AudioStart)
        {
            return end;
        }
        var f = new byte[32];
        s.Position = end - 32;
        s.ReadExactly(f);
        if (Encoding.ASCII.GetString(f, 0, 8) != "APETAGEX")
        {
            return end;
        }
        int version = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(8));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(12)); // items + footer
        int count = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(16));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(20));
        bool hasHeader = (flags & 0x80000000) != 0;
        long total = size + (hasHeader ? 32 : 0);
        if (size < 32 || size > 64 * 1024 * 1024 || end - total < info.AudioStart)
        {
            return end;
        }
        info.ApeVersion = version;
        info.ApeSize = total;
        var items = new byte[size - 32];
        s.Position = end - size;
        s.ReadExactly(items);
        ParseApeItems(items, count, info);
        return end - total;
    }

    private static void ParseApeItems(byte[] items, int count, TagInfo info)
    {
        int p = 0;
        for (int i = 0; i < count && p + 8 < items.Length; i++)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(items.AsSpan(p));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(items.AsSpan(p + 4));
            p += 8;
            int keyEnd = Array.IndexOf(items, (byte)0, p);
            if (keyEnd < 0 || len < 0 || keyEnd + 1 + len > items.Length)
            {
                return;
            }
            string key = Encoding.ASCII.GetString(items, p, keyEnd - p);
            p = keyEnd + 1;
            if (((flags >> 1) & 3) == 0)
            {
                string? name = key.ToUpperInvariant() switch
                {
                    "TITLE" => "Title",
                    "ARTIST" => "Artist",
                    "ALBUM" => "Album",
                    "YEAR" => "Year",
                    "GENRE" => "Genre",
                    "COMMENT" => "Comment",
                    "TRACK" => "Track",
                    "TOOL NAME" or "TOOL VERSION" or "ENCODER" => "Encoder",
                    _ => null,
                };
                if (name is not null)
                {
                    info.SetIfEmpty(name, Encoding.UTF8.GetString(items, p, len).Replace('\0', '/'));
                }
            }
            p += len;
        }
    }

    internal static long Syncsafe(ReadOnlySpan<byte> b)
    {
        if ((b[0] | b[1] | b[2] | b[3]) >= 0x80)
        {
            return -1;
        }
        return (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3];
    }
}
