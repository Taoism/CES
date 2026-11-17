using Ces.Spot.Flac;
using Ces.Spot.Mp3;
using Ces.Spot.Tags;

namespace Ces.Spot;

public enum GuessConfidence
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>Traffic-light rating shown in the file list, like EncSpot's Quality column.</summary>
public enum QualityRating
{
    Unknown,
    Poor,
    Fair,
    Good,
    Lossless,
}

/// <summary>A row of the details view.</summary>
public sealed record InfoItem(string Group, string Name, string Value);

public sealed class AnalysisResult
{
    public required string Path { get; init; }
    public long FileSize { get; init; }
    public DateTime LastWriteUtc { get; init; }

    /// <summary>"MP3", "MP2", "MP1", "FLAC", or "Unknown".</summary>
    public string Format { get; internal set; } = "Unknown";

    public TagInfo? Tags { get; internal set; }
    public Mp3Analysis? Mp3 { get; internal set; }
    public FlacAnalysis? Flac { get; internal set; }

    public string Encoder { get; internal set; } = "Unknown";

    /// <summary>Encoder settings that could be recovered (preset, VBR method, lowpass, compression level...).</summary>
    public string EncoderSettings { get; internal set; } = "";
    public GuessConfidence Confidence { get; internal set; }

    /// <summary>Why the encoder was guessed this way.</summary>
    public string GuessReason { get; internal set; } = "";
    public QualityRating Quality { get; internal set; }

    public double DurationSeconds { get; internal set; }
    public double BitrateKbps { get; internal set; }
    public bool IsVbr { get; internal set; }
    public int SampleRate { get; internal set; }
    public int Channels { get; internal set; }
    public int BitsPerSample { get; internal set; }
    public string ModeName { get; internal set; } = "";
    public long FrameCount { get; internal set; }

    /// <summary>Problems found in the stream (sync errors, CRC failures, truncated last frame, MD5 mismatch...).</summary>
    public List<string> Problems { get; } = [];
    public string? Error { get; internal set; }

    public List<InfoItem> Details { get; } = [];

    public string Artist => Tag("Artist");
    public string Title => Tag("Title");
    public string Album => Tag("Album");

    private string Tag(string key)
    {
        if (Tags?.Fields.TryGetValue(key, out var v) == true)
        {
            return v;
        }
        return Flac?.Comment(key) ?? "";
    }
}
