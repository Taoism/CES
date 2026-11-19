using System.Globalization;
using System.Text;

namespace Ces.Spot.Ogg;

/// <summary>An Ogg file (Vorbis, Opus or FLAC): codec headers, comments, and the integrity of every page.</summary>
public sealed class OggAnalysis
{
    public string Codec { get; internal set; } = "";
    public string Vendor { get; internal set; } = "";
    public List<KeyValuePair<string, string>> Comments { get; } = [];
    public int Channels { get; internal set; }
    public int SampleRate { get; internal set; }

    /// <summary>Opus: the rate of the original input (informational; Opus always decodes at 48 kHz).</summary>
    public int InputSampleRate { get; internal set; }
    public int PreSkip { get; internal set; }
    public int NominalBitrate { get; internal set; }
    public int MaxBitrate { get; internal set; }
    public int MinBitrate { get; internal set; }
    public int Pages { get; internal set; }
    public int CrcErrors { get; internal set; }
    public int SequenceGaps { get; internal set; }
    public long SkippedBytes { get; internal set; }
    public long TrailingBytes { get; internal set; }
    public bool EndOfStream { get; internal set; }
    public int LogicalStreams { get; internal set; }
    public long LastGranule { get; internal set; }

    /// <summary>Bitrate per second of audio (kbps), from the granule positions of the pages.</summary>
    public List<short> SecondBitrates { get; } = [];
}

/// <summary>Walks the Ogg pages, checks CRCs and sequence numbers, and reads the first stream's codec headers.</summary>
public static class OggAnalyzer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly uint[] CrcTable = BuildCrc();

    public static bool IsOgg(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("OggS"u8);

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var k = 0; k < 8; k++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        for (var i = 0; i < page.Length; i++)
        {
            // the CRC field itself (bytes 22–25) counts as zero
            var b = i is >= 22 and < 26 ? (byte)0 : page[i];
            crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
        }
        return crc;
    }

    public static void Analyze(Stream s, long start, long end, AnalysisResult r, CancellationToken ct)
    {
        var a = new OggAnalysis();
        r.Ogg = a;
        r.Format = "Ogg";
        var data = new byte[end - start];
        s.Position = start;
        s.ReadExactly(data);

        int? serial = null;
        var seq = new Dictionary<int, int>();
        var packets = new List<byte[]>();
        var partial = new MemoryStream();
        var bytesPerSecond = new Dictionary<int, long>();
        long prevGranule = 0;
        var p = 0;
        while (p + 27 <= data.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (!data.AsSpan(p, 4).SequenceEqual("OggS"u8))
            {
                // lost sync: look for the next page
                var next = data.AsSpan(p + 1).IndexOf("OggS"u8);
                if (next < 0)
                {
                    a.TrailingBytes = data.Length - p;
                    break;
                }
                a.SkippedBytes += next + 1;
                p += next + 1;
                continue;
            }
            int nseg = data[p + 26];
            if (p + 27 + nseg > data.Length)
            {
                a.TrailingBytes = data.Length - p;
                break;
            }
            var bodyLen = 0;
            for (var i = 0; i < nseg; i++)
                bodyLen += data[p + 27 + i];
            var pageLen = 27 + nseg + bodyLen;
            if (p + pageLen > data.Length)
            {
                a.TrailingBytes = data.Length - p;
                break;
            }
            var page = data.AsSpan(p, pageLen);
            var flags = page[5];
            var granule = BitConverter.ToInt64(page[6..]);
            var pageSerial = BitConverter.ToInt32(page[14..]);
            var pageSeq = BitConverter.ToInt32(page[18..]);
            var crc = BitConverter.ToUInt32(page[22..]);
            a.Pages++;
            if (Crc(page) != crc)
                a.CrcErrors++;
            if ((flags & 0x02) != 0)
                a.LogicalStreams++;
            if (seq.TryGetValue(pageSerial, out var last) && pageSeq != last + 1)
                a.SequenceGaps++;
            seq[pageSerial] = pageSeq;
            serial ??= pageSerial;
            if (pageSerial == serial)
            {
                if ((flags & 0x04) != 0)
                    a.EndOfStream = true;
                // header packets (the first three are enough for Vorbis, two for Opus)
                if (packets.Count < 3)
                {
                    var off = p + 27 + nseg;
                    for (var i = 0; i < nseg && packets.Count < 3; i++)
                    {
                        int lace = data[p + 27 + i];
                        partial.Write(data, off, lace);
                        off += lace;
                        if (lace < 255)
                        {
                            packets.Add(partial.ToArray());
                            partial.SetLength(0);
                        }
                    }
                }
                if (granule > 0)
                {
                    a.LastGranule = granule;
                    if (a.SampleRate > 0 && granule > prevGranule)
                    {
                        var second = (int)(granule / (double)GranuleRate(a));
                        bytesPerSecond[second] = bytesPerSecond.GetValueOrDefault(second) + pageLen;
                    }
                    prevGranule = Math.Max(prevGranule, granule);
                }
                if (packets.Count >= 1 && a.Codec.Length == 0)
                    ReadIdentification(packets[0], a);
            }
            p += pageLen;
        }
        if (packets.Count >= 2)
            ReadComments(packets[1], a);
        if (a.Codec.Length == 0)
        {
            r.Error = "Ogg file with an unknown codec";
            return;
        }
        var top = bytesPerSecond.Count > 0 ? bytesPerSecond.Keys.Max() : -1;
        for (var sec = 0; sec <= top; sec++)
            a.SecondBitrates.Add((short)Math.Min(short.MaxValue, bytesPerSecond.GetValueOrDefault(sec) * 8 / 1000));
        Fill(r, a, data.Length);
    }

    private static int GranuleRate(OggAnalysis a) => a.Codec == "Opus" ? 48000 : a.SampleRate;

    private static void ReadIdentification(byte[] pk, OggAnalysis a)
    {
        if (pk.Length >= 30 && pk[0] == 1 && pk.AsSpan(1, 6).SequenceEqual("vorbis"u8))
        {
            a.Codec = "Vorbis";
            a.Channels = pk[11];
            a.SampleRate = (int)BitConverter.ToUInt32(pk, 12);
            a.MaxBitrate = BitConverter.ToInt32(pk, 16);
            a.NominalBitrate = BitConverter.ToInt32(pk, 20);
            a.MinBitrate = BitConverter.ToInt32(pk, 24);
        }
        else if (pk.Length >= 19 && pk.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            a.Codec = "Opus";
            a.Channels = pk[9];
            a.PreSkip = BitConverter.ToUInt16(pk, 10);
            a.InputSampleRate = (int)BitConverter.ToUInt32(pk, 12);
            a.SampleRate = 48000;
        }
        else if (pk.Length >= 51 && pk[0] == 0x7F && pk.AsSpan(1, 4).SequenceEqual("FLAC"u8))
        {
            a.Codec = "FLAC";
            // STREAMINFO follows the "fLaC" marker and a metadata block header
            var si = pk.AsSpan(13 + 4);
            a.SampleRate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
            a.Channels = ((si[12] >> 1) & 7) + 1;
        }
    }

    private static void ReadComments(byte[] pk, OggAnalysis a)
    {
        var p = a.Codec switch
        {
            "Vorbis" when pk.Length > 7 && pk[0] == 3 => 7,
            "Opus" when pk.Length > 8 && pk.AsSpan(0, 8).SequenceEqual("OpusTags"u8) => 8,
            "FLAC" when pk.Length > 4 && (pk[0] & 0x7F) == 4 => 4,
            _ => -1,
        };
        if (p < 0 || p + 4 > pk.Length)
            return;
        var vlen = (int)BitConverter.ToUInt32(pk, p);
        if (vlen < 0 || p + 4 + vlen > pk.Length)
            return;
        a.Vendor = Encoding.UTF8.GetString(pk, p + 4, vlen);
        p += 4 + vlen;
        if (p + 4 > pk.Length)
            return;
        var count = (int)BitConverter.ToUInt32(pk, p);
        p += 4;
        for (var i = 0; i < count && p + 4 <= pk.Length; i++)
        {
            var len = (int)BitConverter.ToUInt32(pk, p);
            if (len < 0 || p + 4 + len > pk.Length)
                break;
            var text = Encoding.UTF8.GetString(pk, p + 4, len);
            var eq = text.IndexOf('=');
            if (eq > 0)
                a.Comments.Add(new(text[..eq].ToUpperInvariant(), text[(eq + 1)..]));
            p += 4 + len;
        }
    }

    /// <summary>libVorbis quality from the nominal bitrate (stereo 44.1/48 kHz, the usual -q table).</summary>
    public static string VorbisQuality(int nominalKbps) => nominalKbps switch
    {
        <= 0 => "",
        < 56 => "q-1",
        < 72 => "q0",
        < 88 => "q1",
        < 104 => "q2",
        < 120 => "q3",
        < 144 => "q4",
        < 176 => "q5",
        < 208 => "q6",
        < 240 => "q7",
        < 288 => "q8",
        < 400 => "q9",
        _ => "q10",
    };

    private static void Fill(AnalysisResult r, OggAnalysis a, long bytes)
    {
        r.Format = a.Codec == "FLAC" ? "Ogg FLAC" : a.Codec;
        r.SampleRate = a.SampleRate;
        r.Channels = a.Channels;
        r.ModeName = a.Channels switch { 1 => "mono", 2 => "stereo", _ => string.Create(Inv, $"{a.Channels} channels") };
        var samples = a.Codec == "Opus" ? Math.Max(0, a.LastGranule - a.PreSkip) : a.LastGranule;
        r.DurationSeconds = GranuleRate(a) > 0 ? samples / (double)GranuleRate(a) : 0;
        r.BitrateKbps = r.DurationSeconds > 0 ? bytes * 8 / r.DurationSeconds / 1000 : 0;
        r.IsVbr = a.Codec != "FLAC";
        r.FrameCount = a.Pages;
        foreach (var (k, v) in a.Comments)
        {
            var field = k switch { "ARTIST" => "Artist", "TITLE" => "Title", "ALBUM" => "Album", "DATE" => "Year", "GENRE" => "Genre", "ENCODER" => "Encoder", _ => null };
            if (field != null)
                (r.Tags ??= new Tags.TagInfo()).Fields[field] = v;
        }
        var encoderTag = a.Comments.FirstOrDefault(c => c.Key == "ENCODER").Value;
        if (a.Vendor.Length > 0)
            (r.Encoder, r.Confidence, r.GuessReason) = (EncoderNames.Normalize(a.Vendor), GuessConfidence.High, "vendor string in the comment header");
        else
            (r.Encoder, r.Confidence, r.GuessReason) = ("Unknown", GuessConfidence.None, "no vendor string");
        if (!string.IsNullOrEmpty(encoderTag) && !encoderTag.Equals(a.Vendor, StringComparison.Ordinal))
            r.GuessReason += $"; ENCODER tag: {encoderTag}";
        var nominal = a.NominalBitrate / 1000;
        r.EncoderSettings = a.Codec switch
        {
            "Vorbis" when nominal > 0 => string.Create(Inv, $"nominal {nominal} kbps{(a.Channels == 2 ? $" (≈ {VorbisQuality(nominal)})" : "")}"),
            "Opus" => string.Create(Inv, $"{r.BitrateKbps:0} kbps, input {a.InputSampleRate} Hz, pre-skip {a.PreSkip}"),
            _ => "",
        };

        if (a.CrcErrors > 0)
            r.Problems.Add(string.Create(Inv, $"{a.CrcErrors} page CRC error(s)"));
        if (a.SequenceGaps > 0)
            r.Problems.Add(string.Create(Inv, $"{a.SequenceGaps} gap(s) in the page sequence (missing pages)"));
        if (a.SkippedBytes > 0)
            r.Problems.Add(string.Create(Inv, $"{a.SkippedBytes} bytes of junk between pages"));
        if (a.TrailingBytes > 0)
            r.Problems.Add(string.Create(Inv, $"{a.TrailingBytes} bytes after the last complete page (truncated?)"));
        if (!a.EndOfStream)
            r.Problems.Add("no end-of-stream page: truncated or not finished");

        // Vorbis rates on its nominal bitrate (the -q target); VBR on simple material lands far below it
        var kbps = a.Codec == "Vorbis" && a.NominalBitrate > 0 ? Math.Max(r.BitrateKbps, a.NominalBitrate / 1000.0) : r.BitrateKbps;
        var perChannel = a.Channels > 0 ? kbps / a.Channels : kbps;
        r.Quality = r.Problems.Count > 0 ? QualityRating.Poor
            : a.Codec == "FLAC" ? QualityRating.Lossless
            : a.Codec == "Opus" ? (perChannel >= 32 ? QualityRating.Good : perChannel >= 24 ? QualityRating.Fair : QualityRating.Poor)
            : perChannel >= 64 ? QualityRating.Good : perChannel >= 48 ? QualityRating.Fair : QualityRating.Poor;

        r.Details.Add(new InfoItem("Stream", "Codec", r.Format));
        r.Details.Add(new InfoItem("Stream", "Sample rate", string.Create(Inv, $"{a.SampleRate} Hz{(a.Codec == "Opus" ? $" (input {a.InputSampleRate} Hz)" : "")}")));
        r.Details.Add(new InfoItem("Stream", "Channels", a.Channels.ToString(Inv)));
        r.Details.Add(new InfoItem("Stream", "Length", Fmt.Duration(r.DurationSeconds)));
        r.Details.Add(new InfoItem("Stream", "Average bitrate", string.Create(Inv, $"{r.BitrateKbps:0.0} kbps")));
        if (a.Codec == "Vorbis")
            r.Details.Add(new InfoItem("Stream", "Bitrate fields", string.Create(Inv, $"nominal {a.NominalBitrate / 1000} · max {Math.Max(0, a.MaxBitrate) / 1000} · min {Math.Max(0, a.MinBitrate) / 1000} kbps")));
        r.Details.Add(new InfoItem("Stream", "Pages", string.Create(Inv, $"{a.Pages} ({a.LogicalStreams} logical stream{(a.LogicalStreams == 1 ? "" : "s")})")));
        r.Details.Add(new InfoItem("Stream", "Page CRCs", a.CrcErrors == 0 ? "all OK" : string.Create(Inv, $"{a.CrcErrors} bad")));
        r.Details.Add(new InfoItem("Encoder", "Encoder (guess)", r.Encoder));
        r.Details.Add(new InfoItem("Encoder", "Vendor string", a.Vendor));
        foreach (var (k, v) in a.Comments)
            r.Details.Add(new InfoItem("Comments", k, v));
    }
}
