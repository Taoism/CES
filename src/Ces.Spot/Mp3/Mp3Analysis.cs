namespace Ces.Spot.Mp3;

/// <summary>Everything learned from an MPEG audio stream.</summary>
public sealed class Mp3Analysis
{
    public MpegHeader FirstHeader { get; internal set; }

    /// <summary>File offset of the first audio frame (the Xing/Info/VBRI frame when there is one).</summary>
    public long FirstFramePosition { get; internal set; }

    /// <summary>Audio frames (a Xing/Info/VBRI tag frame is not counted).</summary>
    public int Frames { get; internal set; }
    public long AudioBytes { get; internal set; }
    public double DurationSeconds { get; internal set; }
    public double AverageBitrateKbps { get; internal set; }

    /// <summary>Bitrate (kbps) → frame count.</summary>
    public SortedDictionary<int, int> BitrateHistogram { get; } = [];

    /// <summary>Per-frame bitrate (kbps), for graphs.</summary>
    public List<short> FrameBitrates { get; } = [];

    /// <summary>Per-frame main_data_begin (bytes taken from the bit reservoir), Layer III only.</summary>
    public List<short> FrameReservoir { get; } = [];

    public Dictionary<ChannelMode, int> ModeCounts { get; } = [];

    /// <summary>Joint stereo frames using mid/side and intensity stereo.</summary>
    public int MidSideFrames { get; internal set; }
    public int IntensityFrames { get; internal set; }

    public int PaddedFrames { get; internal set; }
    public int CrcFrames { get; internal set; }
    public int CrcErrors { get; internal set; }
    public int PrivateBitFrames { get; internal set; }
    public int CopyrightFrames { get; internal set; }
    public int OriginalFrames { get; internal set; }
    public int Emphasis { get; internal set; }

    public int SyncErrors { get; internal set; }
    public long SkippedBytes { get; internal set; }
    public bool BadLastFrame { get; internal set; }

    /// <summary>Bytes between the end of the audio frames and the end of the audio range (junk before tags).</summary>
    public long TrailingBytes { get; internal set; }
    public bool FreeFormat { get; internal set; }

    public XingHeader? Xing { get; internal set; }
    public VbriHeader? Vbri { get; internal set; }

    public Layer3Stats Layer3 { get; } = new();

    public bool IsVbr => BitrateHistogram.Count > 1;

    /// <summary>Text found in the ancillary data or padding of the first frames (encoder signatures).</summary>
    public string AncillarySignature { get; internal set; } = "";
}

/// <summary>Layer III side info statistics, counted per granule and channel ("granules").</summary>
public sealed class Layer3Stats
{
    public long Granules { get; internal set; }
    public long LongBlocks { get; internal set; }
    public long StartBlocks { get; internal set; }
    public long ShortBlocks { get; internal set; }
    public long StopBlocks { get; internal set; }
    public long MixedBlocks { get; internal set; }
    public long ScalefacScale { get; internal set; }
    public long Preflag { get; internal set; }

    /// <summary>scfsi bands set (MPEG-1 only), and frames that used scfsi at all.</summary>
    public long ScfsiBands { get; internal set; }
    public int ScfsiFrames { get; internal set; }

    public int MaxReservoir { get; internal set; }
    public double AverageReservoir { get; internal set; }

    /// <summary>Frames whose main data starts before the stream (reservoir underflow) or overlaps the previous frame.</summary>
    public int ReservoirErrors { get; internal set; }
    public long AncillaryBytes { get; internal set; }

    /// <summary>Ancillary bytes that are 0x00, and 0x55 ('U', the fill pattern LAME uses).</summary>
    public long AncillaryZeroBytes { get; internal set; }
    public long AncillaryUBytes { get; internal set; }

    /// <summary>Granules with big_values above 288 (bigger than allowed).</summary>
    public long BigValuesViolations { get; internal set; }

    /// <summary>big_values (0..288) → granule count, for the big-values histogram.</summary>
    public int[] BigValuesHistogram { get; } = new int[19];

    public double Percent(long count) => Granules == 0 ? 0 : 100.0 * count / Granules;
}
