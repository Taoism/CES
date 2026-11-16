namespace NothingElseCompares2u.Disc;

public sealed record MplsStream(string Kind, int Pid, int CodingType, string Language);

public sealed class MplsPlayItem
{
    public required string Clip { get; init; }
    public required string Codec { get; init; }
    public uint InTime { get; init; }
    public uint OutTime { get; init; }
    public int StcId { get; init; }
    public int Angles { get; init; } = 1;
    public double Duration => (OutTime - (double)InTime) / 45000.0;
}

public sealed record MplsMark(int Type, int PlayItem, uint Time);

/// <summary>A Blu-ray movie playlist (BDMV/PLAYLIST/xxxxx.mpls).</summary>
public sealed class Mpls
{
    public string Version { get; private set; } = "";
    public List<MplsPlayItem> Items { get; } = [];
    public List<MplsMark> Marks { get; } = [];
    public List<MplsStream> Streams { get; } = [];

    public double Duration => Items.Sum(i => i.Duration);

    public static Mpls Parse(byte[] d)
    {
        var r = new BeReader(d);
        if (r.Ascii(4) != "MPLS")
            throw new InvalidDataException("Not an MPLS playlist.");
        var m = new Mpls { Version = r.Ascii(4) };
        var playlistStart = (int)r.U32();
        var markStart = (int)r.U32();

        // PlayList()
        r.Pos = playlistStart;
        r.U32(); // length
        r.U16(); // reserved
        var itemCount = r.U16();
        r.U16(); // sub paths
        for (var i = 0; i < itemCount; i++)
        {
            var len = r.U16();
            var itemStart = r.Pos;
            var clip = r.Ascii(5);
            var codec = r.Ascii(4);
            var flags = r.U16();
            var multiAngle = (flags & 0x10) != 0;
            var stc = r.U8();
            var inTime = r.U32();
            var outTime = r.U32();
            r.Skip(8); // UO mask
            r.Skip(1); // random access flag
            r.Skip(1); // still mode
            r.Skip(2); // still time
            var angles = 1;
            if (multiAngle)
            {
                angles = r.U8();
                r.Skip(1);
                for (var a = 1; a < angles; a++)
                    r.Skip(10);
            }
            // STN table: only read it for the first item (all items normally share the layout)
            if (i == 0)
                ReadStn(r, m.Streams);
            m.Items.Add(new MplsPlayItem { Clip = clip, Codec = codec, InTime = inTime, OutTime = outTime, StcId = stc, Angles = Math.Max(1, angles) });
            r.Pos = itemStart + len;
        }

        // PlayListMark()
        if (markStart > 0 && markStart + 6 <= d.Length)
        {
            r.Pos = markStart;
            r.U32();
            var marks = r.U16();
            for (var i = 0; i < marks && r.Pos + 14 <= d.Length; i++)
            {
                r.Skip(1);
                var type = r.U8();
                var item = r.U16();
                var ts = r.U32();
                r.Skip(2 + 4);
                m.Marks.Add(new MplsMark(type, item, ts));
            }
        }
        return m;
    }

    private static void ReadStn(BeReader r, List<MplsStream> streams)
    {
        var len = r.U16();
        var end = r.Pos + len;
        if (len < 14)
        {
            r.Pos = end;
            return;
        }
        r.Skip(2);
        var nVideo = r.U8();
        var nAudio = r.U8();
        var nPg = r.U8();
        var nIg = r.U8();
        r.Skip(3 + 5); // secondary audio/video, PiP PG, reserved
        void Read(string kind, int n)
        {
            for (var i = 0; i < n && r.Pos < end; i++)
            {
                // stream_entry
                var eLen = r.U8();
                var eStart = r.Pos;
                var type = r.U8();
                var pid = type switch
                {
                    1 => BeReader.U16At(r.Data, eStart + 1),
                    2 or 4 => BeReader.U16At(r.Data, eStart + 3),
                    3 => BeReader.U16At(r.Data, eStart + 2),
                    _ => 0,
                };
                r.Pos = eStart + eLen;
                // stream_attributes
                var aLen = r.U8();
                var aStart = r.Pos;
                var coding = aLen > 0 ? r.Data[aStart] : 0;
                var lang = "";
                if (kind == "audio" && aLen >= 5)
                    lang = Lang(r.Data, aStart + 2);
                else if (kind is "pg" or "ig" && coding is 0x90 or 0x91 && aLen >= 4)
                    lang = Lang(r.Data, aStart + 1);
                else if (kind == "pg" && coding == 0x92 && aLen >= 5)
                    lang = Lang(r.Data, aStart + 2);
                r.Pos = aStart + aLen;
                streams.Add(new MplsStream(kind, pid, coding, lang));
            }
        }
        Read("video", nVideo);
        Read("audio", nAudio);
        Read("pg", nPg);
        Read("ig", nIg);
        r.Pos = end;
    }

    private static string Lang(byte[] d, int at)
    {
        if (at + 3 > d.Length)
            return "";
        var s = System.Text.Encoding.ASCII.GetString(d, at, 3);
        return s.All(char.IsAsciiLetter) ? s.ToLowerInvariant() : "";
    }
}

/// <summary>The bits of a clip information file (BDMV/CLIPINF/xxxxx.clpi) needed to seek inside the m2ts.</summary>
public sealed class Clpi
{
    /// <summary>Presentation start time per STC sequence (45 kHz).</summary>
    public List<uint> StcStarts { get; } = [];

    public static Clpi Parse(byte[] d)
    {
        var r = new BeReader(d);
        if (r.Ascii(4) != "HDMV")
            throw new InvalidDataException("Not a CLPI file.");
        r.Skip(4);
        var seqStart = (int)r.U32();
        var c = new Clpi();
        r.Pos = seqStart;
        r.U32();
        r.Skip(1);
        var atcCount = r.U8();
        for (var a = 0; a < atcCount; a++)
        {
            r.U32(); // SPN_ATC_start
            var stcCount = r.U8();
            r.Skip(1); // offset_STC_id
            for (var s = 0; s < stcCount; s++)
            {
                r.U16(); // PCR PID
                r.U32(); // SPN_STC_start
                c.StcStarts.Add(r.U32());
                r.U32(); // end
            }
        }
        return c;
    }
}
