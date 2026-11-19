using System.Globalization;
using System.Text.Json;
using Ces.Model;
using Ces.Tools;

namespace Ces.Probe;

/// <summary>Stream and container details from ffprobe, enriched with MediaInfo (HDR format, commercial codec names, bit depth).</summary>
public sealed partial class MediaProber(FfTools tools)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<MediaInfoSummary> ProbeAsync(MediaSource source, MediaTitle title, CancellationToken ct = default)
    {
        // probe the biggest segment: the first one can be a short intro clip
        var seg = title.Segments.OrderByDescending(s => s.Duration > 0 ? s.Duration : s.SizeBytes).First();
        var args = new List<string> { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters" };
        if (seg.Format == "mpeg" || source.Kind != SourceKind.File)
            args.AddRange(["-analyzeduration", "100M", "-probesize", "100M"]);
        if (seg.Format != null)
            args.AddRange(["-f", seg.Format]);
        args.Add(seg.Url);
        var res = await ProcessRunner.RunAsync(tools.FFprobe, args, ct).ConfigureAwait(false);
        if (!res.Ok && res.StdOut.Length == 0)
            throw new IOException("ffprobe failed: " + res.StdErr.Trim());

        var info = ParseFfprobe(res.StdOutText);
        if (source.Kind != SourceKind.File)
        {
            info.Container = source.Kind == SourceKind.BluRay ? "Blu-ray (BDAV MPEG-TS)" : "DVD-Video (MPEG-PS)";
            info.Duration = title.Duration;
            info.SizeBytes = title.SizeBytes;
            info.OverallBitRate = title.Duration > 0 ? (long)(title.SizeBytes * 8 / title.Duration) : 0;
            info.Chapters.Clear();
            info.Chapters.AddRange(title.Chapters);
            foreach (var s in info.Streams)
            {
                if (title.StreamLanguages.TryGetValue(s.Id, out var lang))
                    s.Language = lang;
            }
            // DVD/BD: ffprobe misses some streams that start late; nothing more to do without a full scan
        }
        else
        {
            if (info.Chapters.Count == 0)
                info.Chapters.AddRange(title.Chapters);
            title.Duration = info.Duration;
            seg.Duration = info.Duration;
            if (title.Chapters.Count == 0)
                title.Chapters.AddRange(info.Chapters);
        }

        await FirstFrameSideDataAsync(seg, info, ct).ConfigureAwait(false);
        if (tools.MediaInfo != null)
        {
            var miTarget = source.Kind switch
            {
                // MediaInfo reads a playlist with its clips from disk; inside an image it gets the main clip over HTTP
                SourceKind.BluRay when source.MountedRoot is { } vr && Ces.Vfs.Vfs.IsVirtual(vr) => seg.Url,
                SourceKind.BluRay => Path.Combine(source.MountedRoot is { } mr && Disc.BluRayReader.IsBluRayRoot(mr) ? mr : source.Path, "BDMV", "PLAYLIST", title.Name),
                SourceKind.Dvd => FirstFile(seg.Url),
                _ => source.Path,
            };
            if (Ces.Vfs.Vfs.FileExists(FirstFile(miTarget)))
                await EnrichWithMediaInfoAsync(miTarget, info, source.Kind, ct).ConfigureAwait(false);
        }
        title.Info = info;
        return info;
    }

    private static string FirstFile(string url)
    {
        var u = url.StartsWith("concat:", StringComparison.Ordinal) ? url[7..].Split('|')[0] : url;
        var i = u.IndexOf(",,:", StringComparison.Ordinal);
        return i >= 0 ? u[(i + 3)..] : u;
    }

    internal static MediaInfoSummary ParseFfprobe(string json)
    {
        var info = new MediaInfoSummary();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("format", out var f))
        {
            info.Container = Str(f, "format_long_name");
            info.Duration = Dbl(f, "duration");
            info.SizeBytes = (long)Dbl(f, "size");
            info.OverallBitRate = (long)Dbl(f, "bit_rate");
            if (f.TryGetProperty("tags", out var ft))
            {
                info.Encoder = Tag(ft, "encoder");
            }
        }
        var counters = new Dictionary<StreamKind, int>();
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var kind = Str(s, "codec_type") switch
                {
                    "video" => StreamKind.Video,
                    "audio" => StreamKind.Audio,
                    "subtitle" => StreamKind.Subtitle,
                    _ => StreamKind.Other,
                };
                // cover art / attached pictures are not the video
                if (kind == StreamKind.Video && s.TryGetProperty("disposition", out var disp) && Int(disp, "attached_pic") == 1)
                    kind = StreamKind.Other;
                counters.TryGetValue(kind, out var ki);
                counters[kind] = ki + 1;
                var st = new StreamInfo
                {
                    Index = Int(s, "index"),
                    KindIndex = ki,
                    Id = Str(s, "id").ToLowerInvariant(),
                    Kind = kind,
                    Codec = Str(s, "codec_name"),
                    CodecLong = Str(s, "codec_long_name"),
                    Profile = Str(s, "profile") is var p && p != "unknown" ? p : "",
                    BitRate = (long)Dbl(s, "bit_rate"),
                    Width = Int(s, "width"),
                    Height = Int(s, "height"),
                    Sar = Str(s, "sample_aspect_ratio"),
                    Dar = Str(s, "display_aspect_ratio"),
                    PixFmt = Str(s, "pix_fmt"),
                    ColorRange = Str(s, "color_range"),
                    ColorPrimaries = Str(s, "color_primaries"),
                    ColorTransfer = Str(s, "color_transfer"),
                    ColorSpace = Str(s, "color_space"),
                    FieldOrder = Str(s, "field_order"),
                    Channels = Int(s, "channels"),
                    ChannelLayout = Str(s, "channel_layout"),
                    SampleRate = Int(s, "sample_rate"),
                };
                if (kind == StreamKind.Video)
                {
                    var fr = Str(s, "avg_frame_rate");
                    if (fr is "0/0" or "")
                        fr = Str(s, "r_frame_rate");
                    st.FrameRate = Rational(fr);
                    st.FrameRateText = FrameRateText(st.FrameRate);
                    (st.BitDepth, st.ChromaSubsampling) = PixFmtInfo(st.PixFmt, Int(s, "bits_per_raw_sample"));
                    if (s.TryGetProperty("side_data_list", out var sd))
                    {
                        foreach (var e in sd.EnumerateArray())
                        {
                            var t = Str(e, "side_data_type");
                            if (t.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                                st.DvProfile = Int(e, "dv_profile");
                            if (t.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                                st.HdrFormat = $"Dolby Vision profile {Int(e, "dv_profile")}.{Int(e, "dv_bl_signal_compatibility_id")}" +
                                               (Int(e, "el_present_flag") == 1 ? " BL+EL+RPU" : " BL+RPU");
                        }
                    }
                }
                else if (kind == StreamKind.Audio)
                {
                    st.BitDepth = Int(s, "bits_per_raw_sample");
                    if (st.BitDepth == 0)
                        st.BitDepth = Int(s, "bits_per_sample");
                    // DTS core is lossy, DTS-HD MA is not
                    if (IsLossy(st.Codec) && !(st.Codec == "dts" && st.Profile.Contains("MA", StringComparison.Ordinal)))
                        st.BitDepth = 0;
                    if (st.Profile.Length > 0)
                        st.CommercialName = st.Codec switch
                        {
                            "dts" => st.Profile, // "DTS-HD MA", "DTS"…
                            "truehd" => "TrueHD",
                            _ => "",
                        };
                }
                if (s.TryGetProperty("tags", out var tags))
                {
                    st.Language = Tag(tags, "language") is var l && l != "und" ? l : "";
                    st.Title = Tag(tags, "title");
                    if (st.BitRate == 0 && long.TryParse(Tag(tags, "BPS"), NumberStyles.Integer, Inv, out var bps))
                        st.BitRate = bps;
                }
                if (s.TryGetProperty("disposition", out var d))
                {
                    st.Default = Int(d, "default") == 1;
                    st.Forced = Int(d, "forced") == 1;
                }
                info.Streams.Add(st);
            }
        }
        if (root.TryGetProperty("chapters", out var chapters))
        {
            foreach (var c in chapters.EnumerateArray())
            {
                var name = c.TryGetProperty("tags", out var ct) ? Tag(ct, "title") : "";
                info.Chapters.Add(new Chapter(Dbl(c, "start_time"), name.Length > 0 ? name : $"Chapter {info.Chapters.Count + 1:00}"));
            }
        }
        return info;
    }

    /// <summary>Mastering display / content light level / HDR10+ / DV RPU live on frames, not on the stream.</summary>
    private async Task FirstFrameSideDataAsync(TitleSegment seg, MediaInfoSummary info, CancellationToken ct)
    {
        var v = info.Video;
        if (v == null || !(v.ColorTransfer is "smpte2084" or "arib-std-b67" || v.HdrFormat.Length > 0))
            return;
        var args = new List<string> { "-v", "error", "-select_streams", "v:0", "-print_format", "json", "-read_intervals", "%+#1", "-show_frames", "-show_entries", "frame=side_data_list" };
        if (seg.Format != null)
            args.AddRange(["-f", seg.Format]);
        args.Add(seg.Url);
        var res = await ProcessRunner.RunAsync(tools.FFprobe, args, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(res.StdOutText);
            if (!doc.RootElement.TryGetProperty("frames", out var frames))
                return;
            var extra = new List<string>();
            foreach (var fr in frames.EnumerateArray())
            {
                if (!fr.TryGetProperty("side_data_list", out var sdl))
                    continue;
                foreach (var e in sdl.EnumerateArray())
                {
                    var t = Str(e, "side_data_type");
                    if (t.StartsWith("Mastering display", StringComparison.Ordinal))
                    {
                        var maxL = Rational(Str(e, "max_luminance"));
                        var minL = Rational(Str(e, "min_luminance"));
                        var prim = MasteringPrimaries(Rational(Str(e, "red_x")), Rational(Str(e, "green_x")));
                        v.MasteringDisplay = $"{prim}, {minL:0.####}–{maxL:0} nits".Trim(',', ' ');
                    }
                    else if (t.StartsWith("Content light", StringComparison.Ordinal))
                        v.ContentLight = $"MaxCLL {Int(e, "max_content")}, MaxFALL {Int(e, "max_average")}";
                    else if (t.Contains("2094-40", StringComparison.Ordinal))
                        extra.Add("HDR10+");
                    else if (t.Contains("Dolby Vision RPU", StringComparison.OrdinalIgnoreCase) && !v.HdrFormat.Contains("Dolby", StringComparison.Ordinal))
                        extra.Add("Dolby Vision (RPU)");
                }
            }
            foreach (var x in extra.Distinct())
                v.HdrFormat = v.HdrFormat.Length > 0 ? v.HdrFormat + " / " + x : x;
        }
        catch (JsonException)
        {
            // no side data
        }
    }

    private static string MasteringPrimaries(double redX, double greenX)
    {
        if (redX <= 0)
            return "";
        if (Math.Abs(redX - 0.708) < 0.01 && Math.Abs(greenX - 0.170) < 0.01)
            return "BT.2020";
        if (Math.Abs(redX - 0.680) < 0.01 && Math.Abs(greenX - 0.265) < 0.01)
            return "Display P3";
        if (Math.Abs(redX - 0.640) < 0.01)
            return "BT.709";
        return $"R x {redX:0.###}";
    }

    private async Task EnrichWithMediaInfoAsync(string target, MediaInfoSummary info, SourceKind kind, CancellationToken ct)
    {
        ProcessResult res;
        try
        {
            res = await ProcessRunner.RunAsync(tools.MediaInfo!, ["--Output=JSON", target], ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (!res.Ok)
            return;
        try
        {
            ApplyMediaInfo(res.StdOutText, info, kind);
        }
        catch (JsonException)
        {
            // MediaInfo output not usable
        }
    }

    internal static void ApplyMediaInfo(string json, MediaInfoSummary info, SourceKind kind)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("media", out var media) || !media.TryGetProperty("track", out var tracks))
            return;
        var byKind = new Dictionary<StreamKind, List<JsonElement>>();
        foreach (var t in tracks.EnumerateArray())
        {
            var type = Str(t, "@type");
            if (type == "General")
            {
                if (info.EncodingSettings.Length == 0)
                    info.Encoder = info.Encoder.Length > 0 ? info.Encoder : Str(t, "Encoded_Application");
                continue;
            }
            var k = type switch
            {
                "Video" => StreamKind.Video,
                "Audio" => StreamKind.Audio,
                "Text" => StreamKind.Subtitle,
                _ => StreamKind.Other,
            };
            if (!byKind.TryGetValue(k, out var l))
                byKind[k] = l = [];
            l.Add(t);
        }
        foreach (var (k, list) in byKind)
        {
            var streams = info.Streams.Where(s => s.Kind == k).ToList();
            for (var i = 0; i < Math.Min(list.Count, streams.Count); i++)
            {
                var t = list[i];
                var s = streams[i];
                // match by stream id when MediaInfo has one (disc playlists list streams in PID order)
                var id = Str(t, "ID");
                if (id.Length > 0 && kind != SourceKind.File)
                {
                    // "4113 (0x1011)" on Blu-ray, "189 (0xBD)-138 (0x8A)" for DVD private stream 1 substreams
                    var last = id.Split('-')[^1].Trim().Split(' ')[0];
                    var hex = int.TryParse(last, NumberStyles.Integer, Inv, out var n) ? "0x" + n.ToString("x", Inv) : "";
                    var byId = streams.FirstOrDefault(x => x.Id == hex);
                    if (byId == null)
                        continue;
                    s = byId;
                }
                var bd = Str(t, "BitDepth");
                if (int.TryParse(bd, NumberStyles.Integer, Inv, out var bdv) && bdv > 0 && !(k == StreamKind.Audio && IsLossy(s.Codec)))
                    s.BitDepth = bdv;
                var commercial = Str(t, "Format_Commercial_IfAny");
                if (k == StreamKind.Audio && commercial.Length > 0)
                    s.CommercialName = commercial.Replace("Dolby Digital Plus", "DD+").Replace("Dolby Digital", "DD");
                if (k == StreamKind.Audio && s.CommercialName.Length == 0)
                {
                    var fmt = Str(t, "Format");
                    var add = Str(t, "Format_AdditionalFeatures");
                    if (fmt.Length > 0)
                        s.CommercialName = add.Length > 0 ? $"{fmt} {add}" : "";
                }
                if (k == StreamKind.Video)
                {
                    var hdr = Str(t, "HDR_Format");
                    if (hdr.Length > 0)
                    {
                        var prof = Str(t, "HDR_Format_Profile");
                        var compat = Str(t, "HDR_Format_Compatibility");
                        s.HdrFormat = HdrText(hdr, prof, compat, s);
                    }
                    var scan = Str(t, "ScanType");
                    if (scan.Length > 0)
                        s.ScanType = scan + (Str(t, "ScanOrder") is { Length: > 0 } so ? $" ({so})" : "");
                    var enc = Str(t, "Encoded_Library_Name");
                    if (enc.Length > 0)
                        info.Encoder = enc + (Str(t, "Encoded_Library_Version") is { Length: > 0 } ev ? " " + ev : "");
                    var settings = Str(t, "Encoded_Library_Settings");
                    if (settings.Length > 0)
                        info.EncodingSettings = settings;
                }
                if (s.BitRate == 0 && long.TryParse(Str(t, "BitRate"), NumberStyles.Integer, Inv, out var br))
                    s.BitRate = br;
                if (k == StreamKind.Audio)
                    s.BitRateMode = Str(t, "BitRate_Mode");
                if (s.Language.Length == 0)
                {
                    var lang = Str(t, "Language");
                    if (lang.Length is 2 or 3)
                        s.Language = lang;
                }
            }
        }
    }

    // ---------- helpers ----------

    /// <summary>Lossy audio has no real bit depth (decoders report their output format).</summary>
    public static bool IsLossy(string codec) =>
        codec is "ac3" or "eac3" or "aac" or "mp3" or "mp2" or "opus" or "vorbis" or "wmav2" or "dts" or "dca";

    internal static (int Depth, string Chroma) PixFmtInfo(string pixFmt, int rawBits)
    {
        var depth = rawBits;
        if (depth == 0)
        {
            depth = pixFmt switch
            {
                _ when pixFmt.Contains("p16", StringComparison.Ordinal) || pixFmt.Contains("16le", StringComparison.Ordinal) => 16,
                _ when pixFmt.Contains("p14", StringComparison.Ordinal) => 14,
                _ when pixFmt.Contains("p12", StringComparison.Ordinal) => 12,
                _ when pixFmt.Contains("p10", StringComparison.Ordinal) || pixFmt.Contains("p010", StringComparison.Ordinal) => 10,
                _ when pixFmt.Length > 0 => 8,
                _ => 0,
            };
        }
        var chroma = pixFmt switch
        {
            _ when pixFmt.StartsWith("yuv420", StringComparison.Ordinal) || pixFmt.StartsWith("yuvj420", StringComparison.Ordinal) || pixFmt.StartsWith("nv12", StringComparison.Ordinal) || pixFmt.StartsWith("p010", StringComparison.Ordinal) => "4:2:0",
            _ when pixFmt.StartsWith("yuv422", StringComparison.Ordinal) || pixFmt.StartsWith("yuvj422", StringComparison.Ordinal) => "4:2:2",
            _ when pixFmt.StartsWith("yuv444", StringComparison.Ordinal) || pixFmt.StartsWith("yuvj444", StringComparison.Ordinal) => "4:4:4",
            _ when pixFmt.StartsWith("gray", StringComparison.Ordinal) => "4:0:0",
            _ when pixFmt.StartsWith("rgb", StringComparison.Ordinal) || pixFmt.StartsWith("gbr", StringComparison.Ordinal) || pixFmt.StartsWith("bgr", StringComparison.Ordinal) => "RGB",
            _ => "",
        };
        return (depth, chroma);
    }

    public static string FrameRateText(double fps)
    {
        if (fps <= 0)
            return "";
        foreach (var (v, t) in new[] { (24000 / 1001.0, "23.976"), (30000 / 1001.0, "29.970"), (60000 / 1001.0, "59.940") })
        {
            if (Math.Abs(fps - v) < 0.002)
                return t;
        }
        return fps.ToString(Math.Abs(fps - Math.Round(fps)) < 0.001 ? "0" : "0.###", Inv);
    }

    internal static double Rational(string s)
    {
        if (string.IsNullOrEmpty(s))
            return 0;
        var i = s.IndexOf('/');
        if (i < 0)
            return double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0;
        if (double.TryParse(s[..i], NumberStyles.Float, Inv, out var a) && double.TryParse(s[(i + 1)..], NumberStyles.Float, Inv, out var b) && b != 0)
            return a / b;
        return 0;
    }

    /// <summary>
    /// MediaInfo's HDR fields as one line without empty or repeated parts ("Dolby Vision / SMPTE ST 2086", "dvhe.08 / ",
    /// "HDR10 / HDR10" → "Dolby Vision / SMPTE ST 2086 / profile 8 (dvhe.08) / HDR10"); the Dolby Vision profile is
    /// taken from the codec string when ffprobe did not report it.
    /// </summary>
    internal static string HdrText(string format, string profile, string compat, StreamInfo s)
    {
        var parts = new List<string>();
        foreach (var part in new[] { format, profile, compat }.SelectMany(x => x.Split('/')).Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var text = part;
            if (DvCodecRegex().Match(part) is { Success: true } m)
            {
                var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (s.DvProfile == 0)
                    s.DvProfile = n;
                text = $"profile {n} ({part})";
            }
            if (!parts.Contains(text, StringComparer.OrdinalIgnoreCase))
                parts.Add(text);
        }
        return string.Join(" / ", parts);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^dv(?:he|h1|av|a1)\.(\d{2})")]
    private static partial System.Text.RegularExpressions.Regex DvCodecRegex();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var n) : int.TryParse(v.GetString(), NumberStyles.Integer, Inv, out n)) ? n : 0;

    private static double Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, Inv, out var v) ? v : 0;

    private static string Tag(JsonElement tags, string name)
    {
        foreach (var p in tags.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString() ?? "";
        }
        return "";
    }
}
