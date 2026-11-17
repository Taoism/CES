using System.Buffers.Binary;
using System.Text;

namespace Ces.Spot.Mp3;

/// <summary>Xing / Info header found in the first frame.</summary>
public sealed class XingHeader
{
    /// <summary>"Xing" (VBR) or "Info" (CBR).</summary>
    public required string Id { get; init; }
    public int? Frames { get; init; }
    public int? Bytes { get; init; }
    public bool HasToc { get; init; }
    public int? Quality { get; init; }
    public LameTag? Lame { get; init; }

    internal static XingHeader? TryParse(ReadOnlySpan<byte> frame, int offset)
    {
        if (offset + 8 > frame.Length)
        {
            return null;
        }
        string id = Encoding.ASCII.GetString(frame.Slice(offset, 4));
        if (id is not ("Xing" or "Info"))
        {
            return null;
        }
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame[(offset + 4)..]);
        int p = offset + 8;
        int? frames = null, bytes = null, quality = null;
        if ((flags & 1) != 0 && p + 4 <= frame.Length)
        {
            frames = BinaryPrimitives.ReadInt32BigEndian(frame[p..]);
            p += 4;
        }
        if ((flags & 2) != 0 && p + 4 <= frame.Length)
        {
            bytes = BinaryPrimitives.ReadInt32BigEndian(frame[p..]);
            p += 4;
        }
        bool toc = (flags & 4) != 0;
        if (toc)
        {
            p += 100;
        }
        if ((flags & 8) != 0 && p + 4 <= frame.Length)
        {
            quality = BinaryPrimitives.ReadInt32BigEndian(frame[p..]);
            p += 4;
        }
        return new XingHeader
        {
            Id = id,
            Frames = frames,
            Bytes = bytes,
            HasToc = toc,
            Quality = quality,
            Lame = LameTag.TryParse(frame, p),
        };
    }
}

/// <summary>Fraunhofer VBRI header (FhG VBR encoders).</summary>
public sealed class VbriHeader
{
    public int Version { get; init; }
    public int Delay { get; init; }
    public int Quality { get; init; }
    public int Bytes { get; init; }
    public int Frames { get; init; }

    internal static VbriHeader? TryParse(ReadOnlySpan<byte> frame)
    {
        const int off = 36;
        if (frame.Length < off + 18 || Encoding.ASCII.GetString(frame.Slice(off, 4)) != "VBRI")
        {
            return null;
        }
        return new VbriHeader
        {
            Version = BinaryPrimitives.ReadUInt16BigEndian(frame[(off + 4)..]),
            Delay = BinaryPrimitives.ReadUInt16BigEndian(frame[(off + 6)..]),
            Quality = BinaryPrimitives.ReadUInt16BigEndian(frame[(off + 8)..]),
            Bytes = BinaryPrimitives.ReadInt32BigEndian(frame[(off + 10)..]),
            Frames = BinaryPrimitives.ReadInt32BigEndian(frame[(off + 14)..]),
        };
    }
}

/// <summary>LAME / Info tag extension (written by LAME 3.90+, also by FFmpeg and GOGO).</summary>
public sealed class LameTag
{
    /// <summary>The 9-character encoder string, e.g. "LAME3.100", "Lavc61.19".</summary>
    public required string Encoder { get; init; }
    public int Revision { get; init; }
    public int VbrMethod { get; init; }
    public int LowpassHz { get; init; }
    public double? Peak { get; init; }
    public double? TrackGainDb { get; init; }
    public double? AlbumGainDb { get; init; }
    public int Flags { get; init; }
    public int AthType { get; init; }
    public int BitrateField { get; init; }
    public int EncoderDelay { get; init; }
    public int EncoderPadding { get; init; }
    public int NoiseShaping { get; init; }
    public int StereoModeCode { get; init; }
    public bool Unwise { get; init; }
    public int SourceRateCode { get; init; }
    public int Mp3Gain { get; init; }
    public int Surround { get; init; }
    public int Preset { get; init; }
    public uint MusicLength { get; init; }
    public ushort MusicCrc { get; init; }
    public ushort TagCrc { get; init; }

    /// <summary>True when the tag CRC matches the frame bytes before it (190 bytes for MPEG-1 stereo).</summary>
    public bool TagCrcOk { get; init; }

    /// <summary>Set by the analyzer after checking the audio data CRC (null = not checked).</summary>
    public bool? MusicCrcOk { get; internal set; }

    /// <summary>True when the encoder field has the layout LAME 3.90+ writes (not just a version string).</summary>
    public bool HasExtendedInfo { get; init; }

    public string VbrMethodName => VbrMethod switch
    {
        1 => "CBR",
        2 => "ABR",
        3 => "VBR (old/rh)",
        4 => "VBR (new/mtrh)",
        5 => "VBR (mt)",
        6 => "VBR (method 6)",
        8 => "CBR (2-pass)",
        9 => "ABR (2-pass)",
        _ => "unknown",
    };

    public string StereoModeName => StereoModeCode switch
    {
        0 => "mono",
        1 => "stereo",
        2 => "dual channel",
        3 => "joint stereo",
        4 => "forced joint stereo",
        5 => "auto",
        6 => "intensity stereo",
        _ => "undefined",
    };

    public string SourceRateName => SourceRateCode switch
    {
        0 => "32 kHz or lower",
        1 => "44.1 kHz",
        2 => "48 kHz",
        _ => "higher than 48 kHz",
    };

    /// <summary>Preset name such as "V2", "--preset extreme" or "ABR 192".</summary>
    public string? PresetName => Preset switch
    {
        0 => null,
        >= 8 and <= 320 => VbrMethod is 2 or 9 ? $"ABR {Preset}" : null,
        >= 410 and <= 500 when Preset % 10 == 0 => $"V{(500 - Preset) / 10}",
        1000 => "--r3mix",
        1001 => "--preset standard",
        1002 => "--preset extreme",
        1003 => "--preset insane",
        1004 => "--preset fast standard",
        1005 => "--preset fast extreme",
        1006 => "--preset medium",
        1007 => "--preset fast medium",
        _ => $"preset {Preset}",
    };

    internal static LameTag? TryParse(ReadOnlySpan<byte> frame, int p)
    {
        if (p + 9 > frame.Length)
        {
            return null;
        }
        var enc = frame.Slice(p, 9);
        if (!LooksLikeEncoder(enc))
        {
            return null;
        }
        string encoder = Encoding.ASCII.GetString(enc).TrimEnd('\0', ' ', '.');
        bool extended = p + 36 <= frame.Length;
        if (!extended)
        {
            return new LameTag { Encoder = encoder };
        }
        int b9 = frame[p + 9];
        uint peakRaw = BinaryPrimitives.ReadUInt32BigEndian(frame[(p + 11)..]);
        ushort rg1 = BinaryPrimitives.ReadUInt16BigEndian(frame[(p + 15)..]);
        ushort rg2 = BinaryPrimitives.ReadUInt16BigEndian(frame[(p + 17)..]);
        int b19 = frame[p + 19];
        int delayPad = (frame[p + 21] << 16) | (frame[p + 22] << 8) | frame[p + 23];
        int misc = frame[p + 24];
        int presetBits = BinaryPrimitives.ReadUInt16BigEndian(frame[(p + 26)..]);
        ushort tagCrc = BinaryPrimitives.ReadUInt16BigEndian(frame[(p + 34)..]);
        return new LameTag
        {
            Encoder = encoder,
            Revision = b9 >> 4,
            VbrMethod = b9 & 15,
            LowpassHz = frame[p + 10] * 100,
            Peak = peakRaw == 0 ? null : peakRaw / (double)(1 << 23),
            TrackGainDb = ReplayGain(rg1, 1),
            AlbumGainDb = ReplayGain(rg2, 2),
            Flags = b19 >> 4,
            AthType = b19 & 15,
            BitrateField = frame[p + 20],
            EncoderDelay = delayPad >> 12,
            EncoderPadding = delayPad & 0xFFF,
            NoiseShaping = misc & 3,
            StereoModeCode = (misc >> 2) & 7,
            Unwise = ((misc >> 5) & 1) != 0,
            SourceRateCode = (misc >> 6) & 3,
            Mp3Gain = (sbyte)frame[p + 25],
            Surround = (presetBits >> 11) & 7,
            Preset = presetBits & 0x7FF,
            MusicLength = BinaryPrimitives.ReadUInt32BigEndian(frame[(p + 28)..]),
            MusicCrc = BinaryPrimitives.ReadUInt16BigEndian(frame[(p + 32)..]),
            TagCrc = tagCrc,
            TagCrcOk = Crc16Arc.Compute(frame[..(p + 34)]) == tagCrc,
            HasExtendedInfo = true,
        };
    }

    private static double? ReplayGain(ushort v, int expectedName)
    {
        int name = v >> 13;
        if (name != expectedName)
        {
            return null;
        }
        int value = v & 0x1FF;
        bool negative = (v & 0x200) != 0;
        return (negative ? -value : value) / 10.0;
    }

    private static bool LooksLikeEncoder(ReadOnlySpan<byte> enc)
    {
        // First four characters must be printable letters/digits ("LAME", "L3.9", "GOGO", "Lavc", "Lavf").
        for (int i = 0; i < 4; i++)
        {
            byte c = enc[i];
            if (c is < 0x20 or > 0x7E)
            {
                return false;
            }
        }
        string head = Encoding.ASCII.GetString(enc[..4]);
        return head is "LAME" or "GOGO" or "Lavc" or "Lavf" || head.StartsWith("L3.", StringComparison.Ordinal);
    }
}

/// <summary>CRC-16/ARC (reflected 0x8005, init 0) as used by the LAME tag.</summary>
internal static class Crc16Arc
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1;
            }
            t[i] = (ushort)c;
        }
        return t;
    }

    public static ushort Compute(ReadOnlySpan<byte> data, ushort crc = 0)
    {
        foreach (byte b in data)
        {
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);
        }
        return crc;
    }
}

/// <summary>CRC-16 (0x8005, init 0xFFFF, not reflected) protecting MPEG frame headers and side info.</summary>
internal static class Crc16Mpeg
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        int crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
            }
            crc &= 0xFFFF;
        }
        return (ushort)crc;
    }
}
