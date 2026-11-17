using System.Globalization;
using Ces.Model;

using V = Ces.Vfs.Vfs;

namespace Ces.Disc;

/// <summary>Reads VIDEO_TS: titles from VIDEO_TS.IFO, cells/chapters/languages from the VTS IFOs.</summary>
public static class DvdReader
{
    private const int Sector = 2048;

    public static string? FindVideoTs(string dir)
    {
        if (Path.GetFileName(dir.TrimEnd('\\', '/')).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase) &&
            V.FileExists(Path.Combine(dir, "VIDEO_TS.IFO")))
            return dir;
        var sub = Path.Combine(dir, "VIDEO_TS");
        return V.FileExists(Path.Combine(sub, "VIDEO_TS.IFO")) ? sub : null;
    }

    internal sealed record Cell(double Duration, uint FirstSector, uint LastSector, int VobId);

    internal sealed class Pgc
    {
        public double Duration;
        public List<Cell> Cells { get; } = [];
        public List<int> ProgramEntryCells { get; } = []; // 1-based cell numbers
        public List<int> AudioStreams { get; } = []; // per logical audio stream: physical stream number or -1
        public List<int[]> SubStreams { get; } = []; // per logical subpicture stream: physical numbers (4:3, wide, letterbox, pan&scan), empty if unused
    }

    internal sealed class Vts
    {
        public List<Pgc> Pgcs { get; } = [];
        public List<(int Pgcn, int Pgn)>[] Ptts { get; set; } = [];
        public List<(string Codec, string Lang, int Channels)> Audio { get; } = [];
        public List<string> SubLangs { get; } = [];
        public string VideoDesc = "";
    }

    public static List<MediaTitle> ReadTitles(string videoTs, double minSeconds = 30)
    {
        var vmg = V.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO"));
        if (vmg.Length < 0x100 || System.Text.Encoding.ASCII.GetString(vmg, 0, 12) != "DVDVIDEO-VMG")
            throw new InvalidDataException("VIDEO_TS.IFO is not a DVD video manager.");
        var ttSrpt = (int)BeReader.U32At(vmg, 0xC4) * Sector;
        var r = new BeReader(vmg, ttSrpt);
        var count = r.U16();
        r.Skip(6);
        var vtsCache = new Dictionary<int, Vts?>();
        var titles = new List<MediaTitle>();
        for (var t = 1; t <= count; t++)
        {
            r.U8(); // type
            var angles = r.U8();
            r.U16(); // chapters (counted from the PTT table instead)
            r.U16(); // parental
            int vtsn = r.U8();
            int vtsTtn = r.U8();
            r.U32(); // start sector

            if (!vtsCache.TryGetValue(vtsn, out var vts))
            {
                try
                {
                    vts = ReadVts(Path.Combine(videoTs, $"VTS_{vtsn:00}_0.IFO"));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    vts = null;
                }
                vtsCache[vtsn] = vts;
            }
            if (vts == null || vtsTtn < 1 || vtsTtn > vts.Ptts.Length)
                continue;

            var title = BuildTitle(videoTs, t, vtsn, vts, vts.Ptts[vtsTtn - 1], angles);
            if (title != null && title.Duration >= minSeconds)
                titles.Add(title);
        }
        var main = titles.OrderByDescending(x => x.Duration).FirstOrDefault();
        if (main != null)
            main.IsMain = true;
        return titles;
    }

    private static MediaTitle? BuildTitle(string videoTs, int titleNo, int vtsn, Vts vts, List<(int Pgcn, int Pgn)> ptts, int angles)
    {
        if (ptts.Count == 0)
            return null;
        var vobs = Enumerable.Range(1, 9)
            .Select(i => Path.Combine(videoTs, $"VTS_{vtsn:00}_{i}.VOB"))
            .TakeWhile(V.FileExists)
            .Select(p => (Path: p, Size: V.Length(p)))
            .ToList();
        if (vobs.Count == 0)
            return null;

        var title = new MediaTitle { Name = $"Title {titleNo}", Angles = Math.Max(1, (int)angles) };
        double t = 0;
        var pgcStartTimes = new Dictionary<int, double>();
        foreach (var pgcn in ptts.Select(p => p.Pgcn).Distinct())
        {
            if (pgcn < 1 || pgcn > vts.Pgcs.Count)
                continue;
            var pgc = vts.Pgcs[pgcn - 1];
            pgcStartTimes[pgcn] = t;
            // group cells of the same VOB id with contiguous sectors into one seekable segment
            var cellStarts = new List<double>();
            var runStart = 0;
            for (var i = 0; i < pgc.Cells.Count; i++)
            {
                cellStarts.Add(t + pgc.Cells.Take(i).Sum(c => c.Duration));
                if (pgc.Cells[i].VobId < 0)
                {
                    // other-angle cell: not played
                    runStart = i + 1;
                    continue;
                }
                var last = i == pgc.Cells.Count - 1 ||
                           pgc.Cells[i + 1].VobId != pgc.Cells[i].VobId ||
                           pgc.Cells[i + 1].FirstSector != pgc.Cells[i].LastSector + 1;
                if (!last)
                    continue;
                var run = pgc.Cells.Skip(runStart).Take(i - runStart + 1).ToList();
                var dur = run.Sum(c => c.Duration);
                var seg = MakeSegment(vobs, run[0].FirstSector, run[^1].LastSector, cellStarts[runStart], dur);
                if (seg != null)
                    title.Segments.Add(seg);
                runStart = i + 1;
            }
            // chapters of this PGC
            foreach (var (_, pgn) in ptts.Where(p => p.Pgcn == pgcn))
            {
                if (pgn < 1 || pgn > pgc.ProgramEntryCells.Count)
                    continue;
                var cellNo = pgc.ProgramEntryCells[pgn - 1];
                if (cellNo < 1 || cellNo > cellStarts.Count)
                    continue;
                title.Chapters.Add(new Chapter(cellStarts[cellNo - 1], $"Chapter {title.Chapters.Count + 1:00}"));
            }
            t += pgc.Cells.Sum(c => c.Duration);

            // stream languages (DVD stream ids as ffmpeg reports them)
            for (var i = 0; i < pgc.AudioStreams.Count && i < vts.Audio.Count; i++)
            {
                var phys = pgc.AudioStreams[i];
                if (phys < 0)
                    continue;
                var (codec, lang, _) = vts.Audio[i];
                var id = codec switch
                {
                    "ac3" => 0x80 + phys,
                    "dts" => 0x88 + phys,
                    "pcm" => 0xA0 + phys,
                    "mp2" => 0x1C0 + phys,
                    _ => 0x80 + phys,
                };
                if (lang.Length > 0)
                    title.StreamLanguages["0x" + id.ToString("x", CultureInfo.InvariantCulture)] = lang;
            }
            for (var i = 0; i < pgc.SubStreams.Count && i < vts.SubLangs.Count; i++)
            {
                if (vts.SubLangs[i].Length == 0)
                    continue;
                foreach (var phys in pgc.SubStreams[i])
                    title.StreamLanguages.TryAdd("0x" + (0x20 + phys).ToString("x", CultureInfo.InvariantCulture), vts.SubLangs[i]);
            }
        }
        if (title.Segments.Count == 0)
            return null;
        title.Duration = t;
        title.SizeBytes = title.Segments.Sum(s => s.SizeBytes);
        title.Description = $"{Format.Time(t, false)}, VTS {vtsn}, {title.Chapters.Count} ch"
            + (vts.VideoDesc.Length > 0 ? ", " + vts.VideoDesc : "")
            + (title.Angles > 1 ? $", {title.Angles} angles" : "");
        return title;
    }

    /// <summary>A subfile URL covering sectors [first, last] of the VTS's title VOBs (which may span VOB files).</summary>
    private static TitleSegment? MakeSegment(List<(string Path, long Size)> vobs, uint firstSector, uint lastSector, double titleStart, double duration)
    {
        var start = (long)firstSector * Sector;
        var end = ((long)lastSector + 1) * Sector;
        var pieces = new List<string>();
        var labels = new List<string>();
        long offset = 0;
        foreach (var (path, size) in vobs)
        {
            var fileStart = offset;
            var fileEnd = offset + size;
            offset = fileEnd;
            if (end <= fileStart || start >= fileEnd)
                continue;
            var s = Math.Max(start, fileStart) - fileStart;
            var e = Math.Min(end, fileEnd) - fileStart;
            pieces.Add(s == 0 && e == size ? path : $"subfile,,start,{s},end,{e},,:{path}");
            labels.Add(Path.GetFileName(path));
        }
        if (pieces.Count == 0)
            return null;
        return new TitleSegment
        {
            Url = pieces.Count == 1 ? pieces[0] : "concat:" + string.Join("|", pieces),
            Format = "mpeg",
            TitleStart = titleStart,
            Duration = duration,
            FileLabel = string.Join(", ", labels.Distinct()) + $" @{firstSector}",
            SizeBytes = end - start,
        };
    }

    internal static Vts ReadVts(string ifoPath)
    {
        var d = V.ReadAllBytes(ifoPath);
        if (d.Length < 0x400 || System.Text.Encoding.ASCII.GetString(d, 0, 12) != "DVDVIDEO-VTS")
            throw new InvalidDataException(Path.GetFileName(ifoPath) + " is not a VTS IFO.");
        var vts = new Vts();

        // video attributes
        var v0 = d[0x200];
        var v1 = d[0x201];
        var pal = ((v0 >> 4) & 3) == 1;
        var aspect = ((v0 >> 2) & 3) == 3 ? "16:9" : "4:3";
        var res = ((v1 >> 3) & 7) switch
        {
            0 => "720",
            1 => "704",
            2 => "352",
            _ => "352",
        };
        vts.VideoDesc = $"{res}×{(pal ? 576 : 480)} {(pal ? "PAL" : "NTSC")} {aspect}";

        int nAudio = BeReader.U16At(d, 0x202);
        for (var i = 0; i < Math.Min(nAudio, 8); i++)
        {
            var a = 0x204 + i * 8;
            var mode = d[a] >> 5;
            var hasLang = ((d[a] >> 2) & 3) == 1;
            var codec = mode switch
            {
                0 => "ac3",
                2 or 3 => "mp2",
                4 => "pcm",
                6 => "dts",
                _ => "?",
            };
            vts.Audio.Add((codec, hasLang ? Lang(d, a + 2) : "", (d[a + 1] & 7) + 1));
        }
        int nSub = BeReader.U16At(d, 0x254);
        for (var i = 0; i < Math.Min(nSub, 32); i++)
        {
            var a = 0x256 + i * 6;
            vts.SubLangs.Add((d[a] & 3) == 1 ? Lang(d, a + 2) : "");
        }

        // PGCs
        var pgciAt = (int)BeReader.U32At(d, 0xCC) * Sector;
        int nPgc = BeReader.U16At(d, pgciAt);
        for (var i = 0; i < nPgc; i++)
        {
            var off = (int)BeReader.U32At(d, pgciAt + 8 + i * 8 + 4);
            vts.Pgcs.Add(ReadPgc(d, pgciAt + off));
        }

        // part-of-title search pointers: VTS title -> (PGC, program) per chapter
        var pttAt = (int)BeReader.U32At(d, 0xC8) * Sector;
        int nTitles = BeReader.U16At(d, pttAt);
        var pttEnd = (int)BeReader.U32At(d, pttAt + 4);
        var offsets = Enumerable.Range(0, nTitles).Select(i => (int)BeReader.U32At(d, pttAt + 8 + i * 4)).ToList();
        vts.Ptts = new List<(int, int)>[nTitles];
        for (var i = 0; i < nTitles; i++)
        {
            var from = offsets[i];
            var to = i + 1 < nTitles ? offsets[i + 1] : pttEnd + 1;
            var list = new List<(int, int)>();
            for (var p = from; p + 4 <= to; p += 4)
                list.Add((BeReader.U16At(d, pttAt + p), BeReader.U16At(d, pttAt + p + 2)));
            vts.Ptts[i] = list;
        }
        return vts;
    }

    private static Pgc ReadPgc(byte[] d, int at)
    {
        var pgc = new Pgc { Duration = BcdTime(d, at + 4) };
        int nPrograms = d[at + 2];
        int nCells = d[at + 3];
        for (var i = 0; i < 8; i++)
        {
            var c = BeReader.U16At(d, at + 0x0C + i * 2);
            pgc.AudioStreams.Add((c & 0x8000) != 0 ? (c >> 8) & 7 : -1);
        }
        for (var i = 0; i < 32; i++)
        {
            var c = BeReader.U32At(d, at + 0x1C + i * 4);
            // byte 0: available + 4:3 stream, then wide, letterbox, pan&scan stream numbers
            pgc.SubStreams.Add((c & 0x80000000) != 0
                ? new[] { (int)((c >> 24) & 0x1F), (int)((c >> 16) & 0x1F), (int)((c >> 8) & 0x1F), (int)(c & 0x1F) }.Distinct().ToArray()
                : []);
        }
        int progMap = BeReader.U16At(d, at + 0xE6);
        int cellPlay = BeReader.U16At(d, at + 0xE8);
        int cellPos = BeReader.U16At(d, at + 0xEA);
        for (var i = 0; i < nPrograms && progMap > 0; i++)
            pgc.ProgramEntryCells.Add(d[at + progMap + i]);

        var inAngleBlock = false;
        for (var i = 0; i < nCells && cellPlay > 0; i++)
        {
            var c = at + cellPlay + i * 24;
            if (c + 24 > d.Length)
                break;
            var blockMode = (d[c] >> 6) & 3;
            var blockType = (d[c] >> 4) & 3;
            var dur = BcdTime(d, c + 4);
            var first = BeReader.U32At(d, c + 8);
            var last = BeReader.U32At(d, c + 0x14);
            var vobId = cellPos > 0 ? BeReader.U16At(d, at + cellPos + i * 4) : 0;
            // multi-angle: keep only the first angle of each block
            if (blockType == 1)
            {
                if (blockMode == 1)
                    inAngleBlock = true;
                else if (inAngleBlock)
                {
                    if (blockMode == 3)
                        inAngleBlock = false;
                    // zero-length placeholder keeps the cell numbering the program map refers to
                    pgc.Cells.Add(new Cell(0, first, last, -1 - i));
                    continue;
                }
            }
            pgc.Cells.Add(new Cell(dur, first, last, vobId));
        }
        return pgc;
    }

    private static int Bcd(byte b) => (b >> 4) * 10 + (b & 0xF);

    internal static double BcdTime(byte[] d, int at)
    {
        if (at + 4 > d.Length)
            return 0;
        var fps = (d[at + 3] >> 6) switch
        {
            1 => 25.0,
            3 => 30000 / 1001.0,
            _ => 25.0,
        };
        return Bcd(d[at]) * 3600 + Bcd(d[at + 1]) * 60 + Bcd(d[at + 2]) + Bcd((byte)(d[at + 3] & 0x3F)) / fps;
    }

    private static string Lang(byte[] d, int at)
    {
        var s = System.Text.Encoding.ASCII.GetString(d, at, 2);
        return s.All(char.IsAsciiLetter) ? s.ToLowerInvariant() : "";
    }
}
