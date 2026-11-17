using System.Globalization;
using System.Text.RegularExpressions;

namespace EncoderSpot.Core.Flac;

/// <summary>Turns a <see cref="FlacAnalysis"/> into the encoder guess, rating and detail rows.</summary>
internal static partial class FlacReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Fill(AnalysisResult r)
    {
        var a = r.Flac;
        if (a is null)
        {
            r.Error = "Not a FLAC stream";
            return;
        }
        r.DurationSeconds = a.DurationSeconds;
        r.BitrateKbps = a.AverageBitrateKbps;
        r.IsVbr = true;
        r.SampleRate = a.SampleRate;
        r.Channels = a.Channels;
        r.BitsPerSample = a.BitsPerSample;
        r.FrameCount = a.Frames;
        r.ModeName = a.Channels switch
        {
            1 => "mono",
            2 => "stereo",
            _ => string.Create(Inv, $"{a.Channels} channels"),
        };
        Guess(r, a);
        Problems(r, a);
        r.Quality = r.Problems.Count == 0 ? QualityRating.Lossless : QualityRating.Poor;
        Details(r, a);
    }

    private static void Guess(AnalysisResult r, FlacAnalysis a)
    {
        string? v = a.Vendor;
        bool rewritten = v is not null && v.StartsWith("Mutagen", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(v) || rewritten)
        {
            string why = rewritten ? $"vendor string replaced by the tagger ({v})" : "no VORBIS_COMMENT vendor string";
            GuessFromStructure(r, a, why);
        }
        else
        {
            var m = LibFlacVendor().Match(v);
            if (m.Success)
            {
                r.Encoder = "libFLAC " + m.Groups[1].Value;
                r.GuessReason = $"vendor string \"{v}\"";
            }
            else if (v.StartsWith("Lavf", StringComparison.Ordinal) || v.StartsWith("Lavc", StringComparison.Ordinal))
            {
                r.Encoder = $"FFmpeg ({v})";
                r.GuessReason = "vendor string written by FFmpeg's FLAC encoder";
            }
            else
            {
                r.Encoder = v.Trim();
                r.GuessReason = "vendor string";
            }
            r.Confidence = GuessConfidence.High;
        }
        if (a.Frames > 0)
        {
            r.EncoderSettings = Settings(r, a);
        }
    }

    /// <summary>When the vendor string is missing or was overwritten, the frame layout still says a lot.</summary>
    private static void GuessFromStructure(AnalysisResult r, FlacAnalysis a, string why)
    {
        if (a.Frames == 0)
        {
            r.Encoder = "Unknown";
            r.Confidence = GuessConfidence.None;
            r.GuessReason = why;
            return;
        }
        int block = a.BlockSizeHistogram.MaxBy(kv => kv.Value).Key;
        long lpc = a.LpcOrders.Sum();
        bool precision15 = lpc > 0 && a.LpcPrecisions[15] == lpc;
        if (block == 4608 || (a.VariableBlockSize && precision15))
        {
            r.Encoder = "FFmpeg (flac)";
            r.Confidence = GuessConfidence.Medium;
            r.GuessReason = why + "; block size 4608 and 15-bit LPC coefficients are FFmpeg defaults";
        }
        else if (block is 4096 or 1152 && !a.VariableBlockSize)
        {
            r.Encoder = "libFLAC";
            r.Confidence = GuessConfidence.Low;
            r.GuessReason = why + string.Create(Inv, $"; block size {block} matches libFLAC presets");
        }
        else
        {
            r.Encoder = "Unknown";
            r.Confidence = GuessConfidence.None;
            r.GuessReason = why;
        }
    }

    /// <summary>Describes the encoding parameters and, for libFLAC, the likely compression level.</summary>
    private static string Settings(AnalysisResult r, FlacAnalysis a)
    {
        int block = a.BlockSizeHistogram.MaxBy(kv => kv.Value).Key;
        int maxLpc = a.MaxLpcOrderUsed;
        bool anyLpc = maxLpc > 0;
        bool stereoDecorrelation = a.ChannelAssignments.Any(kv => kv.Key != FlacChannelAssignment.Independent && kv.Value > 0);
        var parts = new List<string>();
        if (r.Encoder.StartsWith("libFLAC", StringComparison.Ordinal))
        {
            string? level = LibFlacLevel(block, maxLpc, anyLpc, stereoDecorrelation, a.Channels);
            if (level is not null)
            {
                parts.Add(level);
            }
        }
        parts.Add(string.Create(Inv, $"block size {block}{(a.VariableBlockSize || a.BlockSizeHistogram.Count > 2 ? " (variable)" : "")}"));
        parts.Add(anyLpc ? string.Create(Inv, $"LPC order ≤ {maxLpc}") : "fixed predictors only");
        if (a.Channels == 2)
        {
            parts.Add(stereoDecorrelation ? "stereo decorrelation" : "independent channels");
        }
        return string.Join(", ", parts);
    }

    private static string? LibFlacLevel(int block, int maxLpc, bool anyLpc, bool decorrelation, int channels)
    {
        // libFLAC presets: -0..-2 block 1152, fixed predictors; -3 LPC ≤ 6; -4..-6 LPC ≤ 8; -7/-8 LPC ≤ 12 (block 4096).
        // The highest order seen is a lower bound for -l, so this is an estimate.
        bool stereo = channels == 2;
        if (block == 1152 && !anyLpc)
        {
            return !stereo ? "≈ -0…-2" : decorrelation ? "≈ -1/-2" : "≈ -0";
        }
        if (block != 4096)
        {
            return "custom settings";
        }
        if (!anyLpc)
        {
            return "custom settings (no LPC)";
        }
        return maxLpc switch
        {
            <= 6 when stereo && !decorrelation => "≈ -3",
            <= 6 => "≈ -3…-6",
            <= 8 => "≈ -4…-6",
            <= 12 => "≈ -7/-8",
            _ => string.Create(Inv, $"custom (-l {maxLpc})"),
        };
    }

    private static void Problems(AnalysisResult r, FlacAnalysis a)
    {
        if (a.SyncErrors > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.SyncErrors} sync error(s), {a.SkippedBytes} bytes skipped"));
        }
        if (a.HeaderCrcErrors > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.HeaderCrcErrors} frame header CRC-8 error(s)"));
        }
        if (a.FrameCrcErrors > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.FrameCrcErrors} frame CRC-16 error(s)"));
        }
        if (a.DecodeErrors > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.DecodeErrors} frame(s) could not be decoded"));
        }
        if (a.Md5Ok == false)
        {
            r.Problems.Add("MD5 of the decoded audio does not match STREAMINFO");
        }
        if (a.Frames > 0 && a.TotalSamples > 0 && a.DecodedSamples != a.TotalSamples)
        {
            r.Problems.Add(string.Create(Inv, $"STREAMINFO says {a.TotalSamples} samples, decoded {a.DecodedSamples}"));
        }
        if (a.TrailingBytes > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.TrailingBytes} bytes of junk after the last frame"));
        }
        if (r.Tags is { } t && (t.HasId3v1 || t.ApeVersion is not null || t.Id3v2Version is not null))
        {
            r.Problems.Add($"non-standard tags in a FLAC file: {t.Describe()}");
        }
    }

    private static void Details(AnalysisResult r, FlacAnalysis a)
    {
        var d = r.Details;
        void Add(string g, string n, string v) => d.Add(new InfoItem(g, n, v));
        const string S = "Stream", E = "Encoder", F = "Frames", P = "Prediction", M = "Metadata", T = "Tags";

        Add(S, "Type", "FLAC");
        Add(S, "Sample rate", string.Create(Inv, $"{a.SampleRate} Hz"));
        Add(S, "Channels", a.Channels.ToString(Inv));
        Add(S, "Bits per sample", a.BitsPerSample.ToString(Inv));
        Add(S, "Length", Fmt.Duration(a.DurationSeconds));
        Add(S, "Total samples", a.TotalSamples > 0 ? a.TotalSamples.ToString("N0", Inv) : "unknown");
        if (a.Frames > 0)
        {
            Add(S, "Average bitrate", string.Create(Inv, $"{a.AverageBitrateKbps:0.0} kbps"));
            Add(S, "Compression", string.Create(Inv, $"{a.CompressionRatio * 100:0.0}% of PCM size"));
        }
        Add(S, "MD5 (STREAMINFO)", a.Md5Unset ? "not set" : Fmt.Hex(a.StoredMd5));
        if (a.ComputedMd5 is { } md5)
        {
            Add(S, "MD5 (decoded)", Fmt.Hex(md5));
        }
        Add(S, "MD5 check", a.Md5Ok switch
        {
            true => "OK",
            false => "MISMATCH",
            _ => a.Md5Unset ? "not possible (no MD5 stored)" : a.ComputedMd5 is null ? "not checked" : "not possible (stream errors)",
        });

        Add(E, "Encoder (guess)", r.Encoder);
        Add(E, "Vendor string", a.Vendor ?? "none");
        Add(E, "Reason", r.GuessReason);
        if (r.EncoderSettings.Length > 0)
        {
            Add(E, "Settings", r.EncoderSettings);
        }

        if (a.Frames > 0)
        {
            Add(F, "Frames", a.Frames.ToString(Inv));
            Add(F, "Blocking", a.VariableBlockSize ? "variable" : "fixed");
            Add(F, "Block sizes", string.Join(", ", a.BlockSizeHistogram.Select(kv => string.Create(Inv, $"{kv.Key}:{kv.Value}"))));
            Add(F, "STREAMINFO block size", string.Create(Inv, $"{a.MinBlockSize}–{a.MaxBlockSize}"));
            Add(F, "STREAMINFO frame size", string.Create(Inv, $"{a.MinFrameSize}–{a.MaxFrameSize} bytes"));
            foreach (var kv in a.ChannelAssignments.OrderBy(k => k.Key))
            {
                Add(F, "Channels: " + kv.Key switch
                {
                    FlacChannelAssignment.LeftSide => "left/side",
                    FlacChannelAssignment.RightSide => "side/right",
                    FlacChannelAssignment.MidSide => "mid/side",
                    _ => "independent",
                }, Pct(kv.Value, a.Frames));
            }
            Add(F, "Sync errors", a.SyncErrors.ToString(Inv));
            Add(F, "Header CRC errors", a.HeaderCrcErrors.ToString(Inv));
            Add(F, "Frame CRC errors", a.FrameCrcErrors.ToString(Inv));

            Add(P, "Subframes", a.Subframes.ToString(Inv));
            Add(P, "Constant", Pct(a.ConstantSubframes, a.Subframes));
            Add(P, "Verbatim", Pct(a.VerbatimSubframes, a.Subframes));
            for (int i = 0; i < a.FixedOrders.Length; i++)
            {
                if (a.FixedOrders[i] > 0)
                {
                    Add(P, string.Create(Inv, $"Fixed order {i}"), Pct(a.FixedOrders[i], a.Subframes));
                }
            }
            long lpc = a.LpcOrders.Sum();
            Add(P, "LPC", Pct(lpc, a.Subframes));
            if (lpc > 0)
            {
                Add(P, "LPC orders", string.Join(", ", a.LpcOrders.Select((n, i) => (n, i)).Where(x => x.n > 0).Select(x => string.Create(Inv, $"{x.i}:{x.n}"))));
                Add(P, "Coefficient precision", string.Join(", ", a.LpcPrecisions.Select((n, i) => (n, i)).Where(x => x.n > 0).Select(x => string.Create(Inv, $"{x.i}:{x.n}"))));
            }
            Add(P, "Wasted bits subframes", a.WastedBitsSubframes.ToString(Inv));
            Add(P, "Max Rice partition order", a.MaxRicePartitionOrder.ToString(Inv));
            Add(P, "Rice2 (5-bit) residuals", a.Rice2Partitions.ToString(Inv));
            Add(P, "Escaped partitions", a.EscapedPartitions.ToString(Inv));
        }

        Add(M, "Blocks", string.Join(", ", a.Blocks.Select(b => b.Name)));
        Add(M, "Padding", string.Create(Inv, $"{a.PaddingBytes:N0} bytes"));
        Add(M, "Pictures", a.Pictures.ToString(Inv));
        Add(M, "Seek points", a.SeekPoints.ToString(Inv));
        if (a.HasCueSheet)
        {
            Add(M, "Cue sheet", "yes");
        }
        if (a.Applications.Count > 0)
        {
            Add(M, "Application blocks", string.Join(", ", a.Applications));
        }
        foreach (var c in a.Comments)
        {
            Add(T, c.Key, c.Value);
        }
        if (r.Tags is { } t && t.Fields.Count > 0 && (t.HasId3v1 || t.Id3v2Version is not null || t.ApeVersion is not null))
        {
            foreach (var kv in t.Fields)
            {
                Add(T, kv.Key + " (" + t.Describe() + ")", kv.Value);
            }
        }
    }

    private static string Pct(long n, long total) =>
        total == 0 ? "0" : string.Create(Inv, $"{n} ({100.0 * n / total:0.0}%)");

    [GeneratedRegex(@"libFLAC\s+([0-9][0-9.]*[a-z0-9_-]*)(?:\s+(\d{8}))?")]
    private static partial Regex LibFlacVendor();
}
