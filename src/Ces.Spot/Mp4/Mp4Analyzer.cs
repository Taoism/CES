using System.Globalization;
using System.Text;

namespace Ces.Spot.Mp4;

/// <summary>An MP4/M4A audio file: brand, codec (AAC profile, ALAC, ...), timing and the iTunes-style tags.</summary>
public sealed class Mp4Analysis
{
    public string Brand { get; internal set; } = "";
    public List<string> CompatibleBrands { get; } = [];
    public string Codec { get; internal set; } = "";
    public string Profile { get; internal set; } = "";
    public int Channels { get; internal set; }
    public int SampleRate { get; internal set; }
    public int BitsPerSample { get; internal set; }
    public long AverageBitrate { get; internal set; }
    public long MaxBitrate { get; internal set; }
    public double DurationSeconds { get; internal set; }
    public long MdatBytes { get; internal set; }
    public int AudioTracks { get; internal set; }
    public int VideoTracks { get; internal set; }

    /// <summary>ilst items by their four-character code (©too, ©nam, ©ART, ...) and freeform names (iTunSMPB, ...).</summary>
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    /// <summary>Top-level boxes in file order.</summary>
    public List<KeyValuePair<string, long>> Boxes { get; } = [];

    public bool MoovMissing { get; internal set; }
    public bool Truncated { get; internal set; }
}

/// <summary>Walks the box tree of an MP4/M4A and reads the first audio track's sample description and the tags.</summary>
public static class Mp4Analyzer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "udta", "edts", "ilst"];

    public static bool IsMp4(ReadOnlySpan<byte> head) => head.Length >= 8 && head.Slice(4, 4).SequenceEqual("ftyp"u8);

    public static void Analyze(Stream s, long start, long end, AnalysisResult r, CancellationToken ct)
    {
        var a = new Mp4Analysis();
        r.Mp4 = a;
        r.Format = "MP4";
        var p = start;
        var moov = false;
        while (p + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            var (type, size, header) = ReadBoxHeader(s, p, end);
            if (size < header)
                break;
            a.Boxes.Add(new(type, size));
            if (p + size > end)
            {
                a.Truncated = true;
                size = end - p;
            }
            switch (type)
            {
                case "ftyp":
                    ReadFtyp(Read(s, p + header, (int)Math.Min(size - header, 256)), a);
                    break;
                case "moov":
                    moov = true;
                    if (size <= 64 << 20)
                        Walk(Read(s, p + header, (int)(size - header)), a, "moov");
                    break;
                case "mdat":
                    a.MdatBytes += size - header;
                    break;
            }
            p += size;
        }
        a.MoovMissing = !moov;
        Fill(r, a);
    }

    private static (string Type, long Size, int Header) ReadBoxHeader(Stream s, long p, long end)
    {
        var h = Read(s, p, (int)Math.Min(16, end - p));
        if (h.Length < 8)
            return ("", 0, 8);
        long size = BinaryPrimitivesBE.U32(h, 0);
        var type = Encoding.Latin1.GetString(h, 4, 4);
        if (size == 1 && h.Length >= 16)
            return (type, (long)BinaryPrimitivesBE.U64(h, 8), 16);
        if (size == 0)
            size = end - p; // to the end of the file
        return (type, size, 8);
    }

    private static byte[] Read(Stream s, long at, int count)
    {
        var b = new byte[Math.Max(0, count)];
        s.Position = at;
        var got = s.ReadAtLeast(b, b.Length, false);
        return got == b.Length ? b : b[..got];
    }

    private static void ReadFtyp(byte[] b, Mp4Analysis a)
    {
        if (b.Length < 8)
            return;
        a.Brand = Encoding.Latin1.GetString(b, 0, 4).Trim();
        for (var i = 8; i + 4 <= b.Length; i += 4)
            a.CompatibleBrands.Add(Encoding.Latin1.GetString(b, i, 4).Trim());
    }

    /// <summary>Children of a container box held in memory.</summary>
    private static void Walk(byte[] b, Mp4Analysis a, string parent)
    {
        var p = 0;
        string? handler = null;
        while (p + 8 <= b.Length)
        {
            long size = BinaryPrimitivesBE.U32(b, p);
            var type = Encoding.Latin1.GetString(b, p + 4, 4);
            var header = 8;
            if (size == 1 && p + 16 <= b.Length)
            {
                size = (long)BinaryPrimitivesBE.U64(b, p + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = b.Length - p;
            }
            if (size < header || p + size > b.Length)
                break;
            var body = b.AsSpan(p + header, (int)size - header);
            switch (type)
            {
                case "trak":
                    // the handler decides whether this is the audio track
                    var hdlr = FindHandler(body);
                    if (hdlr == "soun")
                        a.AudioTracks++;
                    else if (hdlr == "vide")
                        a.VideoTracks++;
                    if (hdlr == "soun" && a.AudioTracks == 1)
                        Walk(body.ToArray(), a, type);
                    break;
                case "meta":
                    // full box: version/flags first
                    if (body.Length > 4)
                        Walk(body[4..].ToArray(), a, type);
                    break;
                case "mvhd" when a.DurationSeconds == 0:
                    a.DurationSeconds = Duration(body);
                    break;
                case "mdhd":
                    var d = Duration(body);
                    if (d > 0)
                        a.DurationSeconds = d;
                    break;
                case "hdlr":
                    handler = body.Length >= 12 ? Encoding.Latin1.GetString(body.Slice(8, 4)) : null;
                    break;
                case "stsd":
                    ReadStsd(body, a);
                    break;
                case "----":
                    ReadFreeform(body, a);
                    break;
                default:
                    if (parent == "ilst")
                        ReadTag(type, body, a);
                    else if (Containers.Contains(type))
                        Walk(body.ToArray(), a, type);
                    break;
            }
            p += (int)size;
        }
        _ = handler;
    }

    private static string? FindHandler(ReadOnlySpan<byte> trak)
    {
        var i = trak.IndexOf("hdlr"u8);
        return i >= 0 && i + 16 <= trak.Length ? Encoding.Latin1.GetString(trak.Slice(i + 12, 4)) : null;
    }

    private static double Duration(ReadOnlySpan<byte> b)
    {
        if (b.Length < 20)
            return 0;
        var version = b[0];
        long scale, duration;
        if (version == 1 && b.Length >= 32)
        {
            scale = BinaryPrimitivesBE.U32(b, 20);
            duration = (long)BinaryPrimitivesBE.U64(b, 24);
        }
        else
        {
            scale = BinaryPrimitivesBE.U32(b, 12);
            duration = BinaryPrimitivesBE.U32(b, 16);
        }
        return scale > 0 ? duration / (double)scale : 0;
    }

    private static void ReadStsd(ReadOnlySpan<byte> b, Mp4Analysis a)
    {
        // full box + entry count, then the first sample entry
        if (b.Length < 8 + 36)
            return;
        var e = b[8..];
        var size = (int)BinaryPrimitivesBE.U32(e, 0);
        var fmt = Encoding.Latin1.GetString(e.Slice(4, 4));
        if (size > e.Length || size < 36)
            return;
        // AudioSampleEntry: channels at 24, sample size at 26, rate (16.16) at 32
        a.Channels = BinaryPrimitivesBE.U16(e, 24);
        a.BitsPerSample = BinaryPrimitivesBE.U16(e, 26);
        a.SampleRate = (int)(BinaryPrimitivesBE.U32(e, 32) >> 16);
        var ext = e[36..size];
        a.Codec = fmt switch
        {
            "mp4a" => "AAC",
            "alac" => "ALAC",
            "Opus" => "Opus",
            "fLaC" => "FLAC",
            "ac-3" => "AC-3",
            "ec-3" => "E-AC-3",
            ".mp3" => "MP3",
            _ => fmt.Trim(),
        };
        var esds = ext.IndexOf("esds"u8);
        if (fmt == "mp4a" && esds >= 4)
            ReadEsds(ext[(esds + 8)..], a);
        var alac = ext.IndexOf("alac"u8);
        if (fmt == "alac" && alac >= 0 && alac + 4 + 4 + 24 <= ext.Length)
        {
            // ALACSpecificConfig after version/flags: frameLength(4) compatibleVersion(1) bitDepth(1) ...
            var c = ext.Slice(alac + 8, 24);
            a.BitsPerSample = c[5];
            a.Channels = c[9];
            a.AverageBitrate = BinaryPrimitivesBE.U32(c, 16);
            a.SampleRate = (int)BinaryPrimitivesBE.U32(c, 20);
        }
    }

    /// <summary>MPEG-4 elementary stream descriptor: object type, bitrates and the AudioSpecificConfig (AAC profile).</summary>
    private static void ReadEsds(ReadOnlySpan<byte> b, Mp4Analysis a)
    {
        var p = 0;
        while (p < b.Length)
        {
            var tag = b[p++];
            var len = 0;
            for (var k = 0; k < 4 && p < b.Length; k++)
            {
                var v = b[p++];
                len = (len << 7) | (v & 0x7F);
                if ((v & 0x80) == 0)
                    break;
            }
            switch (tag)
            {
                case 0x03: // ES descriptor: id(2) flags(1), then children
                    var flags = p + 2 < b.Length ? b[p + 2] : 0;
                    p += 3 + ((flags & 0x80) != 0 ? 2 : 0) + ((flags & 0x20) != 0 ? 2 : 0);
                    if ((flags & 0x40) != 0 && p < b.Length)
                        p += 1 + b[p];
                    continue;
                case 0x04 when p + 13 <= b.Length: // decoder config
                    var oti = b[p];
                    if (oti == 0x6B || oti == 0x69)
                        a.Codec = "MP3";
                    a.MaxBitrate = BinaryPrimitivesBE.U32(b, p + 5);
                    a.AverageBitrate = BinaryPrimitivesBE.U32(b, p + 9);
                    p += 13;
                    continue;
                case 0x05 when p + 2 <= b.Length && a.Codec == "AAC": // AudioSpecificConfig
                    var aot = b[p] >> 3;
                    // backward-compatible SBR signalling: sync 0x2B7, extension type 5, then the "SBR present" flag
                    // (encoders such as FFmpeg write the extension with the flag off for plain AAC-LC)
                    var ext = len >= 5 ? b.Slice(p, len).IndexOf(new byte[] { 0x56, 0xE5 }) : -1;
                    var sbr = ext >= 0 && ext + 2 < len && (b[p + ext + 2] & 0x80) != 0;
                    a.Profile = aot switch
                    {
                        1 => "AAC Main",
                        2 => sbr ? "HE-AAC (SBR)" : "AAC-LC",
                        3 => "AAC SSR",
                        4 => "AAC LTP",
                        5 => "HE-AAC (SBR)",
                        29 => "HE-AAC v2 (SBR + PS)",
                        23 => "AAC-LD",
                        39 => "AAC-ELD",
                        42 => "xHE-AAC (USAC)",
                        _ => string.Create(Inv, $"AAC object type {aot}"),
                    };
                    break;
            }
            p += Math.Max(0, len);
        }
    }

    private static void ReadTag(string type, ReadOnlySpan<byte> b, Mp4Analysis a)
    {
        // item box → 'data' box: size(4) 'data' type(4) locale(4) value
        if (b.Length < 16 || !b.Slice(4, 4).SequenceEqual("data"u8))
            return;
        var kind = BinaryPrimitivesBE.U32(b, 8) & 0xFFFFFF;
        var len = (int)Math.Min(BinaryPrimitivesBE.U32(b, 0), (uint)b.Length);
        if (kind != 1 || len < 16)
            return; // text items only
        var key = type.Replace('©', '©');
        a.Tags[key] = Encoding.UTF8.GetString(b[16..len]);
    }

    private static void ReadFreeform(ReadOnlySpan<byte> b, Mp4Analysis a)
    {
        // '----' → mean, name, data
        string? name = null;
        var p = 0;
        while (p + 12 <= b.Length)
        {
            var size = (int)BinaryPrimitivesBE.U32(b, p);
            if (size < 12 || p + size > b.Length)
                break;
            var t = Encoding.Latin1.GetString(b.Slice(p + 4, 4));
            if (t == "name")
                name = Encoding.UTF8.GetString(b.Slice(p + 12, size - 12));
            else if (t == "data" && name != null && size >= 16)
                a.Tags[name] = Encoding.UTF8.GetString(b.Slice(p + 16, size - 16)).Trim();
            p += size;
        }
    }

    private static void Fill(AnalysisResult r, Mp4Analysis a)
    {
        r.Format = a.Codec.Length > 0 ? (a.Codec == "AAC" ? "AAC (M4A)" : a.Codec + " (M4A)") : "MP4";
        r.SampleRate = a.SampleRate;
        r.Channels = a.Channels;
        r.BitsPerSample = a.Codec == "ALAC" ? a.BitsPerSample : 0;
        r.ModeName = a.Channels switch { 1 => "mono", 2 => "stereo", _ => string.Create(Inv, $"{a.Channels} channels") };
        r.DurationSeconds = a.DurationSeconds;
        r.BitrateKbps = a.DurationSeconds > 0 && a.MdatBytes > 0 ? a.MdatBytes * 8 / a.DurationSeconds / 1000 : a.AverageBitrate / 1000.0;
        r.IsVbr = a.MaxBitrate > 0 && a.AverageBitrate > 0 && a.MaxBitrate > a.AverageBitrate * 1.1;
        foreach (var (k, field) in new[] { ("©nam", "Title"), ("©ART", "Artist"), ("©alb", "Album"), ("©day", "Year"), ("©gen", "Genre") })
        {
            if (a.Tags.TryGetValue(k, out var v))
                (r.Tags ??= new Tags.TagInfo()).Fields[field] = v;
        }

        var tool = a.Tags.GetValueOrDefault("©too") ?? "";
        var itunes = a.Tags.ContainsKey("iTunSMPB") || a.Tags.ContainsKey("iTunNORM");
        (r.Encoder, r.Confidence, r.GuessReason) = tool.Length > 0
            ? (EncoderNames.Normalize(tool), GuessConfidence.High, "encoder tag (©too)")
            : itunes ? ("iTunes / Apple CoreAudio", GuessConfidence.Medium, "iTunSMPB / iTunNORM gapless and volume tags (written by iTunes and CoreAudio encoders)")
            : ("Unknown", GuessConfidence.None, "no encoder tag");
        r.EncoderSettings = string.Join(", ", new[]
        {
            a.Profile,
            a.Codec == "ALAC" && a.BitsPerSample > 0 ? string.Create(Inv, $"{a.BitsPerSample}-bit") : "",
            a.AverageBitrate > 0 && a.Codec != "ALAC" ? string.Create(Inv, $"avg {a.AverageBitrate / 1000} kbps") : "",
            a.MaxBitrate > 0 && a.Codec != "ALAC" ? string.Create(Inv, $"max {a.MaxBitrate / 1000} kbps") : "",
        }.Where(x => x.Length > 0));

        if (a.MoovMissing)
            r.Problems.Add("no moov box (index): the file cannot be played");
        if (a.Truncated)
            r.Problems.Add("a box runs past the end of the file: truncated");
        if (a.Codec.Length == 0 && !a.MoovMissing)
            r.Problems.Add("no audio track found");
        if (a.VideoTracks > 0)
            r.Problems.Add(string.Create(Inv, $"also has {a.VideoTracks} video track(s): a video file, not M4A audio"));

        // the declared average when there is one (VBR on simple material lands far below the target)
        var kbps = Math.Max(r.BitrateKbps, a.AverageBitrate / 1000.0);
        var perChannel = a.Channels > 0 ? kbps / a.Channels : kbps;
        var he = a.Profile.StartsWith("HE", StringComparison.Ordinal);
        r.Quality = r.Problems.Count > 0 ? QualityRating.Poor
            : a.Codec is "ALAC" or "FLAC" ? QualityRating.Lossless
            : he ? (perChannel >= 24 ? QualityRating.Fair : QualityRating.Poor)
            : perChannel >= 60 ? QualityRating.Good : perChannel >= 48 ? QualityRating.Fair : QualityRating.Poor;

        r.Details.Add(new InfoItem("Stream", "Container", string.Create(Inv, $"MP4, brand {a.Brand}{(a.CompatibleBrands.Count > 0 ? " (" + string.Join(", ", a.CompatibleBrands.Distinct()) + ")" : "")}")));
        r.Details.Add(new InfoItem("Stream", "Codec", a.Codec + (a.Profile.Length > 0 ? " · " + a.Profile : "")));
        r.Details.Add(new InfoItem("Stream", "Sample rate", string.Create(Inv, $"{a.SampleRate} Hz")));
        r.Details.Add(new InfoItem("Stream", "Channels", a.Channels.ToString(Inv)));
        if (a.Codec == "ALAC")
            r.Details.Add(new InfoItem("Stream", "Bits per sample", a.BitsPerSample.ToString(Inv)));
        r.Details.Add(new InfoItem("Stream", "Length", Fmt.Duration(a.DurationSeconds)));
        r.Details.Add(new InfoItem("Stream", "Average bitrate", string.Create(Inv, $"{r.BitrateKbps:0.0} kbps (audio data {a.MdatBytes:N0} bytes)")));
        if (a.AverageBitrate > 0 || a.MaxBitrate > 0)
            r.Details.Add(new InfoItem("Stream", "Declared bitrate", string.Create(Inv, $"avg {a.AverageBitrate / 1000} · max {a.MaxBitrate / 1000} kbps")));
        r.Details.Add(new InfoItem("Stream", "Tracks", string.Create(Inv, $"{a.AudioTracks} audio, {a.VideoTracks} video")));
        r.Details.Add(new InfoItem("Stream", "Boxes", string.Join(", ", a.Boxes.Select(b => string.Create(Inv, $"{b.Key} ({b.Value:N0})")))));
        r.Details.Add(new InfoItem("Encoder", "Encoder (guess)", r.Encoder));
        r.Details.Add(new InfoItem("Encoder", "Reason", r.GuessReason));
        foreach (var (k, v) in a.Tags)
            r.Details.Add(new InfoItem("Tags", k, v.Length > 120 ? v[..120] + "…" : v));
    }
}

/// <summary>Big-endian reads (MP4 is big-endian).</summary>
internal static class BinaryPrimitivesBE
{
    public static ushort U16(ReadOnlySpan<byte> b, int p) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b[p..]);
    public static uint U32(ReadOnlySpan<byte> b, int p) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[p..]);
    public static ulong U64(ReadOnlySpan<byte> b, int p) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b[p..]);
}
