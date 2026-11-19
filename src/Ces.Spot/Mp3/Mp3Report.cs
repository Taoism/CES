using System.Globalization;
using System.Text.RegularExpressions;

namespace Ces.Spot.Mp3;

/// <summary>Turns an <see cref="Mp3Analysis"/> into the encoder guess, rating and detail rows.</summary>
internal static partial class Mp3Report
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Fill(AnalysisResult r)
    {
        var a = r.Mp3!;
        var h = a.FirstHeader;
        r.DurationSeconds = a.DurationSeconds;
        r.BitrateKbps = a.AverageBitrateKbps;
        r.IsVbr = a.IsVbr;
        r.SampleRate = h.SampleRate;
        r.Channels = h.Channels;
        r.FrameCount = a.Frames;
        r.ModeName = ModeName(a);

        Guess(r, a);
        Problems(r, a);
        r.Quality = Rate(r, a);
        Details(r, a);
    }

    private static string ModeName(Mp3Analysis a)
    {
        if (a.ModeCounts.Count == 0)
        {
            return "";
        }
        var main = a.ModeCounts.MaxBy(kv => kv.Value).Key;
        string name = Name(main);
        if (a.ModeCounts.Count > 1)
        {
            name += " (mixed)";
        }
        return name;
    }

    private static string Name(ChannelMode m) => m switch
    {
        ChannelMode.Stereo => "stereo",
        ChannelMode.JointStereo => "joint stereo",
        ChannelMode.DualChannel => "dual channel",
        _ => "mono",
    };

    private static void Guess(AnalysisResult r, Mp3Analysis a)
    {
        var lame = a.Xing?.Lame;
        if (lame is not null)
        {
            GuessFromLameTag(r, a, lame);
            return;
        }
        if (a.Vbri is not null)
        {
            Set(r, "FhG (VBRI)", GuessConfidence.High, "VBRI header (Fraunhofer VBR)");
            r.EncoderSettings = string.Create(Inv, $"VBRI v{a.Vbri.Version}, quality {a.Vbri.Quality}, delay {a.Vbri.Delay}");
            return;
        }
        string sig = a.AncillarySignature;
        var lameText = LameText().Match(sig);
        if (lameText.Success)
        {
            Set(r, "LAME " + lameText.Groups[1].Value.Trim(), GuessConfidence.High, $"\"{lameText.Value.Trim()}\" in ancillary data");
            AddXingNote(r, a);
            return;
        }
        if (sig.Contains("GOGO", StringComparison.Ordinal))
        {
            Set(r, "GOGO", GuessConfidence.High, "\"GOGO\" in ancillary data");
            return;
        }
        // GOGO-no-coda writes "GOGO" into the Xing header's quality field
        if (a.Xing?.Quality == 0x474F474F)
        {
            Set(r, "GOGO", GuessConfidence.High, "\"GOGO\" in the Xing header's quality field");
            r.EncoderSettings = "VBR";
            return;
        }
        if (a.Xing is not null)
        {
            if (a.Xing.Id == "Xing")
            {
                Set(r, "Xing (new)", GuessConfidence.Medium, "Xing VBR header without LAME tag");
            }
            else
            {
                Set(r, "Unknown", GuessConfidence.Low, "Info header without LAME tag");
            }
            AddXingNote(r, a);
            return;
        }
        var l3 = a.Layer3;
        if (a.FirstHeader.Layer == 3 && l3.AncillaryBytes > 64 && l3.AncillaryUBytes * 2 > l3.AncillaryBytes)
        {
            Set(r, "LAME (untagged)", GuessConfidence.Medium, "ancillary data filled with LAME's 0x55 pattern");
            return;
        }
        // GOGO-no-coda (built on LAME 3.88) leaves leftover bytes, neither LAME's 0x55 nor zero; old LAME can look alike
        if (a.FirstHeader.Layer == 3 && l3.AncillaryBytes > 64 && l3.AncillaryZeroBytes * 20 < l3.AncillaryBytes && l3.AncillaryUBytes * 20 < l3.AncillaryBytes)
        {
            Set(r, "GOGO or old LAME (untagged)", GuessConfidence.Low, "no tag; ancillary data neither zero nor LAME's 0x55 filler (GOGO-no-coda / LAME before 3.90)");
            return;
        }
        if (Fingerprint(r, a))
        {
            return;
        }
        if (r.Tags?.Fields.TryGetValue("Encoder", out var tagged) == true)
        {
            Set(r, tagged, GuessConfidence.Low, "encoder named in the tags (TSSE/APE), not in the stream");
            return;
        }
        if (a.FirstHeader.Layer == 3 && a.Frames > 200)
        {
            long switched = l3.ShortBlocks + l3.StartBlocks + l3.StopBlocks;
            if (switched == 0)
            {
                bool joint = a.ModeCounts.ContainsKey(ChannelMode.JointStereo);
                Set(r, joint ? "Xing (old)" : "Blade", GuessConfidence.Low,
                    "no short blocks at all" + (joint ? ", joint stereo" : ", simple stereo only"));
                return;
            }
        }
        Set(r, "Unknown", GuessConfidence.None, "no encoder signature found");
    }

    /// <summary>
    /// Untagged files told apart by how the encoder uses Layer III (measured on real FastEnc, Blade, LAME, GOGO and
    /// Helix encodes): Fraunhofer never uses scfsi but leans on preflag and fills ancillary data with zeros; Blade
    /// hardly touches the bit reservoir and only writes simple stereo. LAME, GOGO and Helix use scfsi, not preflag.
    /// </summary>
    private static bool Fingerprint(AnalysisResult r, Mp3Analysis a)
    {
        var l3 = a.Layer3;
        if (a.FirstHeader.Layer != 3 || a.Frames < 100 || l3.Granules == 0)
            return false;
        var scfsi = (double)l3.ScfsiFrames / a.Frames;
        var preflag = (double)l3.Preflag / l3.Granules;
        var joint = a.ModeCounts.ContainsKey(ChannelMode.JointStereo);
        if (scfsi < 0.01 && l3.AverageReservoir < 32 && !joint)
        {
            Set(r, "Blade", GuessConfidence.Medium, string.Create(Inv, $"no scfsi, bit reservoir hardly used (avg {l3.AverageReservoir:0} bytes), simple stereo only"));
            return true;
        }
        if (scfsi < 0.01 && preflag > 0.2 && l3.AverageReservoir >= 32 && l3.AncillaryZeroBytes * 2 >= l3.AncillaryBytes)
        {
            Set(r, "FhG (untagged)", GuessConfidence.Medium, string.Create(Inv, $"no scfsi, preflag in {preflag:P0} of granules, zero-filled ancillary data (Fraunhofer encoders)"));
            return true;
        }
        return false;
    }

    private static void AddXingNote(AnalysisResult r, Mp3Analysis a)
    {
        if (a.Xing?.Quality is int q)
        {
            r.EncoderSettings = string.Create(Inv, $"Xing quality {q}");
        }
    }

    private static void GuessFromLameTag(AnalysisResult r, Mp3Analysis a, LameTag t)
    {
        string enc = t.Encoder;
        // Helix hmp3 writes a LAME-style tag with its own version ("H5.24" = hmp3 5.2.4); the LAME-only fields are unset
        if (HelixRegex().Match(enc.StartsWith("LAME", StringComparison.Ordinal) ? enc[4..] : enc) is { Success: true } hx)
        {
            var ver = string.Join(".", hx.Groups[1].Value.Replace(".", "", StringComparison.Ordinal).Select(ch => ch.ToString()));
            Set(r, "Helix hmp3 " + ver, GuessConfidence.High, $"LAME-style tag \"{enc}\" written by the Helix encoder");
            r.EncoderSettings = string.Create(Inv, $"{(a.IsVbr ? "VBR" : "CBR")}{(t.LowpassHz > 0 ? $", lowpass {t.LowpassHz / 1000.0:0.0} kHz" : "")}");
            return;
        }
        if (enc.StartsWith("Lavc", StringComparison.Ordinal) || enc.StartsWith("Lavf", StringComparison.Ordinal))
        {
            Set(r, $"FFmpeg ({enc})", GuessConfidence.High, "LAME tag written by FFmpeg (usually libmp3lame)");
            // FFmpeg only fills in delay/padding (and CRCs); the other fields are zero.
            if (t.HasExtendedInfo)
            {
                r.EncoderSettings = string.Create(Inv, $"{(a.IsVbr ? "VBR" : "CBR")}, delay {t.EncoderDelay}, padding {t.EncoderPadding}");
            }
            return;
        }
        else if (enc.StartsWith("GOGO", StringComparison.Ordinal))
        {
            Set(r, enc, GuessConfidence.High, "encoder string in the Info tag");
        }
        else
        {
            string ver = enc.StartsWith("LAME", StringComparison.Ordinal) ? enc[4..] : enc.StartsWith("L3.", StringComparison.Ordinal) ? enc[1..] : enc;
            // some builds drop the dots to fit the 9 characters ("LAME3995o" = 3.99.5, build "o")
            if (UndottedRegex().Match(ver.Trim()) is { Success: true } u)
                ver = $"{u.Groups[1].Value}.{u.Groups[2].Value}.{u.Groups[3].Value}{(u.Groups[4].Value.Length > 0 ? " (" + u.Groups[4].Value + ")" : "")}";
            Set(r, "LAME " + ver.Trim(), GuessConfidence.High, $"LAME tag \"{enc}\"");
        }
        if (!t.HasExtendedInfo)
        {
            return;
        }
        var parts = new List<string>();
        bool vbr = t.VbrMethod is 3 or 4 or 5 or 6;
        string? preset = t.PresetName;
        if (preset is not null)
        {
            parts.Add(preset);
        }
        else if (vbr && a.Xing?.Quality is int q && q <= 100)
        {
            // LAME stores 100 - 10*V - q in the Xing quality field.
            int v = (100 - q) / 10;
            int qq = (100 - q) % 10;
            parts.Add(string.Create(Inv, $"-V {v} -q {qq}"));
        }
        parts.Add(t.VbrMethodName);
        if (t.VbrMethod is 1 or 8 && t.BitrateField > 0)
        {
            // the tag's bitrate byte tops out at 255: above that the stream's own bitrate is the answer
            int cbr = t.BitrateField == 255 && a.BitrateHistogram.Count > 0 ? a.BitrateHistogram.Keys.Max() : t.BitrateField;
            parts.Add(string.Create(Inv, $"{cbr} kbps"));
        }
        else if (t.VbrMethod is 2 or 9 && t.BitrateField > 0)
        {
            parts.Add(t.BitrateField == 255 ? "target 255+ kbps" : string.Create(Inv, $"target {t.BitrateField} kbps"));
        }
        if (t.LowpassHz > 0)
        {
            parts.Add(string.Create(Inv, $"lowpass {t.LowpassHz / 1000.0:0.0} kHz"));
        }
        parts.Add(t.StereoModeName);
        r.EncoderSettings = string.Join(", ", parts);
    }

    private static void Set(AnalysisResult r, string encoder, GuessConfidence c, string reason)
    {
        r.Encoder = encoder;
        r.Confidence = c;
        r.GuessReason = reason;
    }

    private static void Problems(AnalysisResult r, Mp3Analysis a)
    {
        if (a.SyncErrors > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.SyncErrors} sync error(s), {a.SkippedBytes} bytes skipped"));
        }
        if (a.BadLastFrame)
        {
            r.Problems.Add("last frame is truncated");
        }
        if (a.CrcErrors > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.CrcErrors} frame CRC error(s)"));
        }
        if (a.Layer3.ReservoirErrors > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.Layer3.ReservoirErrors} bit reservoir error(s)"));
        }
        if (a.Layer3.BigValuesViolations > 0)
        {
            r.Problems.Add(string.Create(Inv, $"{a.Layer3.BigValuesViolations} granule(s) with big_values > 288"));
        }
        // a truncated last frame is already reported: its bytes are not separate junk
        if (a.TrailingBytes > 0 && !a.BadLastFrame)
        {
            r.Problems.Add(string.Create(Inv, $"{a.TrailingBytes} bytes of junk after the last frame"));
        }
        var lame = a.Xing?.Lame;
        if (lame is { HasExtendedInfo: true })
        {
            if (!lame.TagCrcOk)
            {
                r.Problems.Add("LAME tag CRC mismatch");
            }
            if (lame.MusicCrcOk == false)
            {
                r.Problems.Add("LAME music CRC mismatch (audio changed after encoding)");
            }
        }
        // Xing's own encoder counts the header frame too: one extra frame is how it writes the field, not damage
        var xingCountsItself = a.Xing is { Lame: null } && a.Xing.Frames == a.Frames + 1;
        if (a.Xing?.Frames is int xf && xf != a.Frames && !xingCountsItself)
        {
            r.Problems.Add(string.Create(Inv, $"Xing header says {xf} frames, found {a.Frames}"));
        }
    }

    private static QualityRating Rate(AnalysisResult r, Mp3Analysis a)
    {
        if (a.SyncErrors > 0 || a.BadLastFrame || a.CrcErrors > 0 || a.Layer3.ReservoirErrors > 0)
        {
            return QualityRating.Poor;
        }
        string e = r.Encoder;
        if (e.StartsWith("Blade", StringComparison.Ordinal) || e.StartsWith("Xing (old)", StringComparison.Ordinal))
        {
            return QualityRating.Poor;
        }
        if (a.Xing?.Lame is { HasExtendedInfo: true } lame && lame.VbrMethod is 3 or 4 or 5 or 6)
        {
            // VBR: the bitrate follows the music, so rate the quality setting instead.
            int v = VbrLevel(lame, a.Xing.Quality);
            return v switch
            {
                <= 2 => QualityRating.Good,
                <= 5 => QualityRating.Fair,
                _ => QualityRating.Poor,
            };
        }
        double br = a.AverageBitrateKbps * (a.FirstHeader.Channels == 1 ? 2 : 1);
        if (br < 127.5)
        {
            return QualityRating.Poor;
        }
        bool modern = e.StartsWith("LAME", StringComparison.Ordinal) || e.StartsWith("FFmpeg", StringComparison.Ordinal) || e.StartsWith("FhG", StringComparison.Ordinal);
        if (modern && br >= 175)
        {
            return QualityRating.Good;
        }
        return QualityRating.Fair;
    }

    /// <summary>LAME -V level (0 best) from the preset field or the Xing quality field.</summary>
    private static int VbrLevel(LameTag t, int? xingQuality) => t.Preset switch
    {
        >= 410 and <= 500 => (500 - t.Preset) / 10,
        1001 or 1004 => 2,
        1002 or 1003 or 1005 => 0,
        1006 or 1007 => 4,
        _ when xingQuality is int q and <= 100 => (100 - q) / 10,
        _ => 4,
    };

    private static void Details(AnalysisResult r, Mp3Analysis a)
    {
        var h = a.FirstHeader;
        var d = r.Details;
        void Add(string g, string n, string v) => d.Add(new InfoItem(g, n, v));
        const string S = "Stream", E = "Encoder", F = "Frames", L = "Layer III", T = "Tags";

        Add(S, "Type", $"{h.VersionName} {h.LayerName}");
        Add(S, "Sample rate", string.Create(Inv, $"{h.SampleRate} Hz"));
        Add(S, "Mode", r.ModeName);
        Add(S, "Length", Fmt.Duration(a.DurationSeconds));
        Add(S, "Average bitrate", string.Create(Inv, $"{a.AverageBitrateKbps:0.0} kbps{(a.IsVbr ? " (VBR)" : "")}"));
        Add(S, "Bitrates used", string.Join(", ", a.BitrateHistogram.Select(kv => string.Create(Inv, $"{kv.Key}:{kv.Value}"))));
        Add(S, "Emphasis", a.Emphasis switch { 0 => "none", 1 => "50/15 µs", 3 => "CCITT J.17", _ => "reserved" });
        Add(S, "First frame at", string.Create(Inv, $"{a.FirstFramePosition} bytes"));
        Add(S, "Audio bytes", string.Create(Inv, $"{a.AudioBytes:N0}"));

        Add(E, "Encoder (guess)", r.Encoder);
        Add(E, "Confidence", r.Confidence.ToString());
        Add(E, "Reason", r.GuessReason);
        if (r.EncoderSettings.Length > 0)
        {
            Add(E, "Settings", r.EncoderSettings);
        }
        if (a.Xing is { } x)
        {
            Add(E, "VBR header", x.Id + (x.HasToc ? " (with TOC)" : ""));
            if (x.Frames is int xf)
            {
                Add(E, "Header frames", xf.ToString(Inv));
            }
            if (x.Quality is int xq)
            {
                Add(E, "Header quality", xq.ToString(Inv));
            }
        }
        if (a.Vbri is { } v)
        {
            Add(E, "VBR header", string.Create(Inv, $"VBRI v{v.Version}, {v.Frames} frames, quality {v.Quality}"));
        }
        if (a.Xing?.Lame is { HasExtendedInfo: true } t)
        {
            Add(E, "LAME tag revision", t.Revision.ToString(Inv));
            Add(E, "VBR method", t.VbrMethodName);
            Add(E, "Lowpass", t.LowpassHz > 0 ? string.Create(Inv, $"{t.LowpassHz} Hz") : "none");
            Add(E, "ATH type", t.AthType.ToString(Inv));
            Add(E, "Encoder delay / padding", string.Create(Inv, $"{t.EncoderDelay} / {t.EncoderPadding} samples"));
            Add(E, "Stereo mode", t.StereoModeName);
            Add(E, "Noise shaping", t.NoiseShaping.ToString(Inv));
            Add(E, "Source sample rate", t.SourceRateName);
            if (t.PresetName is { } p)
            {
                Add(E, "Preset", p);
            }
            if (t.TrackGainDb is double tg)
            {
                Add(E, "Track gain", string.Create(Inv, $"{tg:+0.0;-0.0} dB"));
            }
            if (t.Peak is double pk)
            {
                Add(E, "Peak", string.Create(Inv, $"{pk:0.000000}"));
            }
            Add(E, "Tag CRC", t.TagCrcOk ? "OK" : "mismatch");
            Add(E, "Music CRC", t.MusicCrcOk switch { true => "OK", false => "mismatch", _ => "not checked" });
            Add(E, "Unwise settings", t.Unwise ? "yes" : "no");
        }
        if (a.AncillarySignature.Length > 0)
        {
            Add(E, "Ancillary text", a.AncillarySignature);
        }

        Add(F, "Frames", a.Frames.ToString(Inv));
        Add(F, "Padded frames", Pct(a.PaddedFrames, a.Frames));
        Add(F, "CRC protected", Pct(a.CrcFrames, a.Frames));
        Add(F, "Private bit", Pct(a.PrivateBitFrames, a.Frames));
        Add(F, "Copyright", Pct(a.CopyrightFrames, a.Frames));
        Add(F, "Original", Pct(a.OriginalFrames, a.Frames));
        foreach (var kv in a.ModeCounts.OrderBy(k => k.Key))
        {
            Add(F, "Mode: " + Name(kv.Key), Pct(kv.Value, a.Frames));
        }
        if (a.ModeCounts.ContainsKey(ChannelMode.JointStereo))
        {
            Add(F, "Mid/side stereo", Pct(a.MidSideFrames, a.Frames));
            Add(F, "Intensity stereo", Pct(a.IntensityFrames, a.Frames));
        }
        Add(F, "Sync errors", a.SyncErrors.ToString(Inv));
        Add(F, "CRC errors", a.CrcErrors.ToString(Inv));
        Add(F, "Bad last frame", a.BadLastFrame ? "yes" : "no");
        if (a.FreeFormat)
        {
            Add(F, "Free format", "yes");
        }

        if (h.Layer == 3)
        {
            var l3 = a.Layer3;
            Add(L, "Max reservoir", string.Create(Inv, $"{l3.MaxReservoir} bytes"));
            Add(L, "Average reservoir", string.Create(Inv, $"{l3.AverageReservoir:0.0} bytes"));
            Add(L, "Long blocks", PctG(l3, l3.LongBlocks));
            Add(L, "Short blocks", PctG(l3, l3.ShortBlocks));
            Add(L, "Start / stop blocks", string.Create(Inv, $"{l3.StartBlocks} / {l3.StopBlocks}"));
            Add(L, "Mixed blocks", PctG(l3, l3.MixedBlocks));
            Add(L, "scalefac_scale", PctG(l3, l3.ScalefacScale));
            Add(L, "Preflag", PctG(l3, l3.Preflag));
            if (h.Version == MpegVersion.Mpeg1)
            {
                Add(L, "scfsi used", Pct(l3.ScfsiFrames, a.Frames));
            }
            Add(L, "Ancillary data", string.Create(Inv, $"{l3.AncillaryBytes:N0} bytes ({l3.AncillaryZeroBytes:N0} zero, {l3.AncillaryUBytes:N0} 0x55)"));
            Add(L, "Reservoir errors", l3.ReservoirErrors.ToString(Inv));
        }

        if (r.Tags is { } tags)
        {
            Add(T, "Tags", tags.Describe());
            if (tags.Id3v2Size > 0)
            {
                Add(T, "ID3v2 size", string.Create(Inv, $"{tags.Id3v2Size} bytes"));
            }
            foreach (var kv in tags.Fields)
            {
                Add(T, kv.Key, kv.Value);
            }
        }
    }

    private static string Pct(long n, long total) =>
        total == 0 ? "0" : string.Create(Inv, $"{n} ({100.0 * n / total:0.0}%)");

    private static string PctG(Layer3Stats l3, long n) => string.Create(Inv, $"{n} ({l3.Percent(n):0.0}%)");

    [GeneratedRegex(@"^(\d)(\d\d)(\d)([a-z]?)$")]
    private static partial Regex UndottedRegex();

    [GeneratedRegex(@"^H(\d\.\d+)$")]
    private static partial Regex HelixRegex();

    [GeneratedRegex(@"LAME\s?(\d\.\d+[a-z0-9 ().]*|[A-Za-z][A-Za-z0-9.]{1,15})")]
    private static partial Regex LameText();
}
