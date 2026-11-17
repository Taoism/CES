using System.Buffers.Binary;
using System.Text;

namespace EncoderSpot.Core.Tags;

internal static class Id3v2Reader
{
    public static void Parse(int major, byte flags, byte[] body, TagInfo info)
    {
        byte[] data = body;
        if (major <= 3 && (flags & 0x80) != 0)
        {
            data = RemoveUnsync(body);
        }
        int pos = 0;
        if ((flags & 0x40) != 0 && major >= 3 && data.Length >= 4)
        {
            int ext = major == 4 ? (int)TagScanner.Syncsafe(data.AsSpan(0, 4)) : BinaryPrimitives.ReadInt32BigEndian(data) + 4;
            pos = Math.Clamp(ext, 0, data.Length);
        }
        int idLen = major == 2 ? 3 : 4;
        int headLen = major == 2 ? 6 : 10;
        while (pos + headLen <= data.Length && data[pos] != 0)
        {
            string id = Encoding.ASCII.GetString(data, pos, idLen);
            int size;
            byte fmt = 0;
            if (major == 2)
            {
                size = (data[pos + 3] << 16) | (data[pos + 4] << 8) | data[pos + 5];
            }
            else if (major == 4)
            {
                long ss = TagScanner.Syncsafe(data.AsSpan(pos + 4, 4));
                size = ss < 0 ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos + 4)) : (int)ss;
                fmt = data[pos + 9];
            }
            else
            {
                size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos + 4));
                fmt = data[pos + 9];
            }
            pos += headLen;
            if (size <= 0 || pos + size > data.Length)
            {
                break;
            }
            byte[] frame = data.AsSpan(pos, size).ToArray();
            pos += size;
            bool compressedOrEncrypted = major == 4 ? (fmt & 0x0C) != 0 : major == 3 && (fmt & 0xC0) != 0;
            if (compressedOrEncrypted)
            {
                continue;
            }
            if (major == 4 && (fmt & 0x02) != 0)
            {
                frame = RemoveUnsync(frame);
            }
            if (major == 4 && (fmt & 0x01) != 0 && frame.Length >= 4)
            {
                frame = frame[4..]; // data length indicator
            }
            HandleFrame(id, frame, info);
        }
    }

    private static void HandleFrame(string id, ReadOnlySpan<byte> frame, TagInfo info)
    {
        string? key = id switch
        {
            "TIT2" or "TT2" => "Title",
            "TPE1" or "TP1" => "Artist",
            "TALB" or "TAL" => "Album",
            "TYER" or "TYE" or "TDRC" => "Year",
            "TCON" or "TCO" => "Genre",
            "TRCK" or "TRK" => "Track",
            "TSSE" or "TSS" => "Encoder",
            "TENC" or "TEN" => "EncodedBy",
            "COMM" or "COM" => "Comment",
            _ => null,
        };
        if (key is null || frame.Length < 2)
        {
            return;
        }
        if (key == "Comment")
        {
            // encoding(1) language(3) short description (terminated) text
            if (frame.Length < 5)
            {
                return;
            }
            int enc = frame[0];
            var rest = frame[4..];
            info.SetIfEmpty(key, Decode(rest[TerminatorEnd(rest, enc)..], enc));
            return;
        }
        info.SetIfEmpty(key, Decode(frame[1..], frame[0]).Replace('\0', '/'));
    }

    private static int TerminatorEnd(ReadOnlySpan<byte> b, int enc)
    {
        if (enc is 1 or 2)
        {
            for (int i = 0; i + 1 < b.Length; i += 2)
            {
                if (b[i] == 0 && b[i + 1] == 0)
                {
                    return i + 2;
                }
            }
            return b.Length;
        }
        int z = b.IndexOf((byte)0);
        return z < 0 ? b.Length : z + 1;
    }

    private static string Decode(ReadOnlySpan<byte> b, int enc)
    {
        string s;
        switch (enc)
        {
            case 1:
                if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
                {
                    s = Encoding.BigEndianUnicode.GetString(b[2..]);
                }
                else if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
                {
                    s = Encoding.Unicode.GetString(b[2..]);
                }
                else
                {
                    s = Encoding.Unicode.GetString(b);
                }
                break;
            case 2:
                s = Encoding.BigEndianUnicode.GetString(b);
                break;
            case 3:
                s = Encoding.UTF8.GetString(b);
                break;
            default:
                s = Encoding.Latin1.GetString(b);
                break;
        }
        return s.TrimEnd('\0').Trim();
    }

    private static byte[] RemoveUnsync(byte[] b)
    {
        var o = new List<byte>(b.Length);
        for (int i = 0; i < b.Length; i++)
        {
            o.Add(b[i]);
            if (b[i] == 0xFF && i + 1 < b.Length && b[i + 1] == 0)
            {
                i++;
            }
        }
        return [.. o];
    }
}
