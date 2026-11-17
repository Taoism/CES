namespace EncoderSpot.Core.Mp3;

public enum MpegVersion
{
    Mpeg1,
    Mpeg2,
    Mpeg25,
}

public enum ChannelMode
{
    Stereo = 0,
    JointStereo = 1,
    DualChannel = 2,
    Mono = 3,
}

/// <summary>A decoded 4-byte MPEG audio frame header.</summary>
public readonly record struct MpegHeader(
    MpegVersion Version,
    int Layer,
    bool HasCrc,
    int BitrateKbps,
    int SampleRate,
    bool Padding,
    bool Private,
    ChannelMode Mode,
    int ModeExtension,
    bool Copyright,
    bool Original,
    int Emphasis)
{
    private static readonly int[,] Bitrates =
    {
        // V1 L1, V1 L2, V1 L3, V2 L1, V2 L2/L3
        { 0, 0, 0, 0, 0 },
        { 32, 32, 32, 32, 8 },
        { 64, 48, 40, 48, 16 },
        { 96, 56, 48, 56, 24 },
        { 128, 64, 56, 64, 32 },
        { 160, 80, 64, 80, 40 },
        { 192, 96, 80, 96, 48 },
        { 224, 112, 96, 112, 56 },
        { 256, 128, 112, 128, 64 },
        { 288, 160, 128, 144, 80 },
        { 320, 192, 160, 160, 96 },
        { 352, 224, 192, 176, 112 },
        { 384, 256, 224, 192, 128 },
        { 416, 320, 256, 224, 144 },
        { 448, 384, 320, 256, 160 },
    };

    private static readonly int[] Rates1 = [44100, 48000, 32000];

    public int Channels => Mode == ChannelMode.Mono ? 1 : 2;

    public int SamplesPerFrame => Layer switch
    {
        1 => 384,
        2 => 1152,
        _ => Version == MpegVersion.Mpeg1 ? 1152 : 576,
    };

    public int FrameLength
    {
        get
        {
            if (BitrateKbps == 0)
            {
                return 0;
            }
            if (Layer == 1)
            {
                return ((12 * BitrateKbps * 1000 / SampleRate) + (Padding ? 1 : 0)) * 4;
            }
            int coef = Layer == 3 && Version != MpegVersion.Mpeg1 ? 72 : 144;
            return (coef * BitrateKbps * 1000 / SampleRate) + (Padding ? 1 : 0);
        }
    }

    /// <summary>Layer III side information size in bytes.</summary>
    public int SideInfoSize => Version == MpegVersion.Mpeg1
        ? (Mode == ChannelMode.Mono ? 17 : 32)
        : (Mode == ChannelMode.Mono ? 9 : 17);

    public string VersionName => Version switch
    {
        MpegVersion.Mpeg1 => "MPEG-1",
        MpegVersion.Mpeg2 => "MPEG-2",
        _ => "MPEG-2.5",
    };

    public string LayerName => Layer switch
    {
        1 => "Layer I",
        2 => "Layer II",
        _ => "Layer III",
    };

    /// <summary>Same stream parameters (used to tell real frames from false syncs).</summary>
    public bool IsCompatibleWith(in MpegHeader other) =>
        Version == other.Version && Layer == other.Layer && SampleRate == other.SampleRate;

    public static bool TryParse(ReadOnlySpan<byte> b, out MpegHeader header)
    {
        header = default;
        if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0)
        {
            return false;
        }
        int ver = (b[1] >> 3) & 3;
        int layerBits = (b[1] >> 1) & 3;
        int brIndex = (b[2] >> 4) & 15;
        int srIndex = (b[2] >> 2) & 3;
        int emphasis = b[3] & 3;
        if (ver == 1 || layerBits == 0 || brIndex == 15 || srIndex == 3 || emphasis == 2)
        {
            return false;
        }
        var version = ver switch
        {
            3 => MpegVersion.Mpeg1,
            2 => MpegVersion.Mpeg2,
            _ => MpegVersion.Mpeg25,
        };
        int layer = 4 - layerBits;
        int column = version == MpegVersion.Mpeg1 ? layer - 1 : (layer == 1 ? 3 : 4);
        int sr = Rates1[srIndex] >> (version == MpegVersion.Mpeg1 ? 0 : version == MpegVersion.Mpeg2 ? 1 : 2);
        var mode = (ChannelMode)((b[3] >> 6) & 3);
        int bitrate = Bitrates[brIndex, column];
        // Layer II forbids some bitrate/mode combinations (MPEG-1 only).
        if (layer == 2 && version == MpegVersion.Mpeg1 && brIndex != 0)
        {
            bool mono = mode == ChannelMode.Mono;
            if ((mono && bitrate > 192) || (!mono && bitrate is 32 or 48 or 56 or 80))
            {
                return false;
            }
        }
        header = new MpegHeader(
            version,
            layer,
            (b[1] & 1) == 0,
            bitrate,
            sr,
            ((b[2] >> 1) & 1) != 0,
            (b[2] & 1) != 0,
            mode,
            (b[3] >> 4) & 3,
            ((b[3] >> 3) & 1) != 0,
            ((b[3] >> 2) & 1) != 0,
            emphasis);
        return true;
    }
}
