namespace Ces.Spot.Flac;

public enum FlacChannelAssignment
{
    Independent,
    LeftSide,
    RightSide,
    MidSide,
}

public sealed record FlacMetadataBlock(int Type, string Name, int Length);

/// <summary>Everything learned from a FLAC stream.</summary>
public sealed class FlacAnalysis
{
    public long StreamOffset { get; internal set; }

    // STREAMINFO
    public int MinBlockSize { get; internal set; }
    public int MaxBlockSize { get; internal set; }
    public int MinFrameSize { get; internal set; }
    public int MaxFrameSize { get; internal set; }
    public int SampleRate { get; internal set; }
    public int Channels { get; internal set; }
    public int BitsPerSample { get; internal set; }
    public long TotalSamples { get; internal set; }
    public byte[] StoredMd5 { get; internal set; } = new byte[16];
    public bool Md5Unset => StoredMd5.All(b => b == 0);

    // Metadata
    public List<FlacMetadataBlock> Blocks { get; } = [];
    public string? Vendor { get; internal set; }
    public List<KeyValuePair<string, string>> Comments { get; } = [];
    public long PaddingBytes { get; internal set; }
    public int Pictures { get; internal set; }
    public int SeekPoints { get; internal set; }
    public bool HasCueSheet { get; internal set; }
    public List<string> Applications { get; } = [];

    // Frames
    public int Frames { get; internal set; }
    public bool VariableBlockSize { get; internal set; }
    public SortedDictionary<int, int> BlockSizeHistogram { get; } = [];
    public Dictionary<FlacChannelAssignment, int> ChannelAssignments { get; } = [];
    public long DecodedSamples { get; internal set; }
    public long AudioBytes { get; internal set; }

    /// <summary>Per-frame bitrate in kbps, for graphs.</summary>
    public List<short> FrameBitrates { get; } = [];

    // Subframes
    public long Subframes { get; internal set; }
    public long ConstantSubframes { get; internal set; }
    public long VerbatimSubframes { get; internal set; }

    /// <summary>Fixed predictor order (0..4) → subframe count.</summary>
    public long[] FixedOrders { get; } = new long[5];

    /// <summary>LPC order (1..32) → subframe count (index = order).</summary>
    public long[] LpcOrders { get; } = new long[33];

    /// <summary>Quantized coefficient precision (1..15) → LPC subframe count.</summary>
    public long[] LpcPrecisions { get; } = new long[16];
    public long WastedBitsSubframes { get; internal set; }
    public int MaxRicePartitionOrder { get; internal set; }
    public long Rice2Partitions { get; internal set; }
    public long EscapedPartitions { get; internal set; }

    // Integrity
    public int SyncErrors { get; internal set; }
    public long SkippedBytes { get; internal set; }
    public int HeaderCrcErrors { get; internal set; }
    public int FrameCrcErrors { get; internal set; }
    public int DecodeErrors { get; internal set; }
    public long TrailingBytes { get; internal set; }

    /// <summary>Set when the samples were decoded and hashed.</summary>
    public byte[]? ComputedMd5 { get; internal set; }

    public bool? Md5Ok => ComputedMd5 is null || Md5Unset || DecodeErrors > 0 || SyncErrors > 0 ? null : ComputedMd5.AsSpan().SequenceEqual(StoredMd5);

    public double DurationSeconds => SampleRate == 0 ? 0 : (TotalSamples > 0 ? TotalSamples : DecodedSamples) / (double)SampleRate;

    public double AverageBitrateKbps => DurationSeconds <= 0 ? 0 : AudioBytes * 8.0 / DurationSeconds / 1000.0;

    /// <summary>Compressed size as a fraction of the raw PCM size.</summary>
    public double CompressionRatio
    {
        get
        {
            double raw = DurationSeconds * SampleRate * Channels * BitsPerSample / 8.0;
            return raw <= 0 ? 0 : AudioBytes / raw;
        }
    }

    public int MaxLpcOrderUsed
    {
        get
        {
            for (int i = LpcOrders.Length - 1; i > 0; i--)
            {
                if (LpcOrders[i] > 0)
                {
                    return i;
                }
            }
            return 0;
        }
    }

    public string? Comment(string key) =>
        Comments.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}
