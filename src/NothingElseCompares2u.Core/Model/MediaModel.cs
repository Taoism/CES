namespace NothingElseCompares2u.Model;

public enum SourceKind
{
    File,
    BluRay,
    Dvd,
}

public enum StreamKind
{
    Video,
    Audio,
    Subtitle,
    Other,
}

/// <summary>One opened thing to compare: a media file, a Blu-ray folder/ISO or a DVD folder/ISO.</summary>
public sealed class MediaSource
{
    public required string Path { get; init; }
    public required SourceKind Kind { get; init; }
    public string DisplayName { get; set; } = "";
    public List<MediaTitle> Titles { get; } = [];

    /// <summary>The root folder after mounting an ISO (for discs), else null.</summary>
    public string? MountedRoot { get; set; }

    /// <summary>The ISO that was mounted by us and must be unmounted when the source is closed.</summary>
    public string? MountedIso { get; set; }

    public MediaTitle? MainTitle => Titles.FirstOrDefault(t => t.IsMain) ?? Titles.FirstOrDefault();

    public override string ToString() => DisplayName;
}

/// <summary>A playable title: a whole file, a Blu-ray playlist or a DVD title.</summary>
public sealed class MediaTitle
{
    public required string Name { get; init; }
    public string Description { get; set; } = "";
    public double Duration { get; set; }
    public bool IsMain { get; set; }
    public List<TitleSegment> Segments { get; } = [];
    public List<Chapter> Chapters { get; } = [];

    /// <summary>Language per stream id from the disc structure (BD PID, DVD stream id), lower-case hex like "0x1100".</summary>
    public Dictionary<string, string> StreamLanguages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total bytes of the title's data (for bitrate), 0 when unknown.</summary>
    public long SizeBytes { get; set; }

    public int Angles { get; set; } = 1;

    /// <summary>A still image: one picture, the same at every time.</summary>
    public bool IsStill { get; set; }

    /// <summary>
    /// Inverse telecine: the video is 23.976 film with 3:2 pulldown in a 29.97 stream; every video filter chain starts
    /// with fieldmatch + decimate so frames pair one to one with a 23.976 source.
    /// </summary>
    public bool Ivtc { get; set; }

    /// <summary>Filled by the prober.</summary>
    public MediaInfoSummary? Info { get; set; }

    public override string ToString() => Description.Length > 0 ? $"{Name} — {Description}" : Name;
}

/// <summary>
/// A piece of a title that ffmpeg can open on its own and seek in. A file is one segment; a Blu-ray playlist has one
/// per play item; a DVD title one per run of contiguous cells.
/// </summary>
public sealed class TitleSegment
{
    /// <summary>ffmpeg input URL (path, or subfile/concat protocol URL for DVD cells).</summary>
    public required string Url { get; init; }

    /// <summary>Forced input format (e.g. "mpeg" for VOB pieces), null to probe.</summary>
    public string? Format { get; init; }

    /// <summary>Where the segment starts in title time (seconds).</summary>
    public double TitleStart { get; set; }

    public double Duration { get; set; }

    /// <summary>Seconds from the start of the input to the first presented frame of this segment (for -ss).</summary>
    public double SeekBase { get; init; }

    /// <summary>Absolute in/out timestamps (seconds) for the concat demuxer, null when the whole input is used.</summary>
    public double? InPoint { get; init; }
    public double? OutPoint { get; init; }

    /// <summary>The underlying file(s), for display.</summary>
    public string FileLabel { get; init; } = "";

    public long SizeBytes { get; init; }
}

public sealed record Chapter(double Start, string Name);

public sealed class StreamInfo
{
    public int Index { get; set; }

    /// <summary>Index within its kind (v:0, a:1 …).</summary>
    public int KindIndex { get; set; }

    public string Id { get; set; } = "";
    public StreamKind Kind { get; set; }
    public string Codec { get; set; } = "";
    public string CodecLong { get; set; } = "";
    public string Profile { get; set; } = "";
    public string CommercialName { get; set; } = "";
    public string Language { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Default { get; set; }
    public bool Forced { get; set; }
    public long BitRate { get; set; }

    // video
    public int Width { get; set; }
    public int Height { get; set; }
    public string Sar { get; set; } = "";
    public string Dar { get; set; } = "";
    public double FrameRate { get; set; }
    public string FrameRateText { get; set; } = "";
    public string PixFmt { get; set; } = "";
    public int BitDepth { get; set; }
    public string ChromaSubsampling { get; set; } = "";
    public string ColorRange { get; set; } = "";
    public string ColorPrimaries { get; set; } = "";
    public string ColorTransfer { get; set; } = "";
    public string ColorSpace { get; set; } = "";
    public string FieldOrder { get; set; } = "";
    public string HdrFormat { get; set; } = "";
    public string MasteringDisplay { get; set; } = "";
    public string ContentLight { get; set; } = "";
    public string ScanType { get; set; } = "";

    // audio
    public int Channels { get; set; }
    public string ChannelLayout { get; set; } = "";
    public int SampleRate { get; set; }
    public string BitRateMode { get; set; } = "";

    public string Label
    {
        get
        {
            var parts = new List<string>();
            switch (Kind)
            {
                case StreamKind.Video:
                    parts.Add(CodecDisplay);
                    if (Width > 0)
                        parts.Add($"{Width}×{Height}");
                    if (FrameRateText.Length > 0)
                        parts.Add(FrameRateText + " fps");
                    if (BitDepth > 0)
                        parts.Add($"{BitDepth}-bit");
                    if (HdrShort.Length > 0)
                        parts.Add(HdrShort);
                    break;
                case StreamKind.Audio:
                    parts.Add(CodecDisplay);
                    if (Channels > 0)
                        parts.Add(ChannelText);
                    if (SampleRate > 0)
                        parts.Add($"{SampleRate / 1000.0:0.#} kHz");
                    if (BitDepth > 0)
                        parts.Add($"{BitDepth}-bit");
                    break;
                default:
                    parts.Add(CodecDisplay);
                    break;
            }
            if (BitRate > 0)
                parts.Add(Format.BitRate(BitRate));
            if (Language.Length > 0)
                parts.Insert(0, $"[{Language}]");
            if (Title.Length > 0)
                parts.Add($"“{Title}”");
            return string.Join("  ", parts);
        }
    }

    public string CodecDisplay => CommercialName.Length > 0 ? CommercialName : Profile.Length > 0 ? $"{Codec.ToUpperInvariant()} {Profile}" : Codec.ToUpperInvariant();

    public string ChannelText => Channels switch
    {
        1 => "1.0",
        2 => "2.0",
        3 => ChannelLayout.Contains("2.1", StringComparison.Ordinal) ? "2.1" : "3.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => Channels + " ch",
    };

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67" || HdrFormat.Length > 0;

    public string HdrShort
    {
        get
        {
            var tags = new List<string>();
            if (HdrFormat.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
                tags.Add("DV");
            if (HdrFormat.Contains("2094", StringComparison.Ordinal) || HdrFormat.Contains("HDR10+", StringComparison.Ordinal))
                tags.Add("HDR10+");
            if (ColorTransfer == "smpte2084" && !tags.Contains("HDR10+"))
                tags.Add("HDR10");
            if (ColorTransfer == "arib-std-b67")
                tags.Add("HLG");
            return string.Join(" ", tags);
        }
    }
}

public sealed class MediaInfoSummary
{
    public string Container { get; set; } = "";
    public double Duration { get; set; }
    public long SizeBytes { get; set; }
    public long OverallBitRate { get; set; }
    public List<StreamInfo> Streams { get; } = [];
    public List<Chapter> Chapters { get; } = [];
    public string Encoder { get; set; } = "";
    public string EncodingSettings { get; set; } = "";

    public StreamInfo? Video => Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
    public IEnumerable<StreamInfo> Audio => Streams.Where(s => s.Kind == StreamKind.Audio);
    public IEnumerable<StreamInfo> Subtitles => Streams.Where(s => s.Kind == StreamKind.Subtitle);
}
