using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Ces.Vfs;

/// <summary>A file inside a RAR set: where its bytes lie in which volumes.</summary>
public sealed class RarEntry
{
    public required string Name { get; init; }
    public long Size { get; internal set; }
    public bool IsDirectory { get; init; }

    /// <summary>Stored (RAR -m0): the bytes can be read in place. Compressed entries cannot be read without unpacking.</summary>
    public bool IsStored { get; internal set; }
    public bool IsEncrypted { get; internal set; }
    public bool IsSolid { get; internal set; }
    internal List<Piece> Pieces { get; } = [];

    /// <summary>All parts were found (the set is complete for this file).</summary>
    public bool IsComplete => Pieces.Sum(p => p.Length) >= Size;

    public bool CanRead => IsStored && !IsEncrypted && IsComplete;
}

internal readonly record struct Piece(string Volume, long Offset, long Length);

/// <summary>
/// Reads the headers of a (multi-volume) RAR 4 or RAR 5 archive and opens stored files as seekable streams that
/// span the volumes. Most video releases are stored (-m0), so their video and ISO files are readable without unpacking.
/// </summary>
public sealed partial class RarArchive
{
    private static readonly byte[] Rar4Sig = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5Sig = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private readonly Dictionary<string, RarEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private RarArchive(string firstVolume) => FirstVolume = firstVolume;

    public string FirstVolume { get; }
    public int Version { get; private set; }
    public List<string> Volumes { get; } = [];
    public bool HeadersEncrypted { get; private set; }
    public IReadOnlyCollection<RarEntry> Entries => _entries.Values;

    public RarEntry? Find(string name) => _entries.GetValueOrDefault(Normalize(name));

    public static bool IsRarFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> sig = stackalloc byte[8];
            int n = fs.ReadAtLeast(sig, 8, false);
            return (n >= 7 && sig[..7].SequenceEqual(Rar4Sig)) || (n >= 8 && sig.SequenceEqual(Rar5Sig));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for the file that starts a RAR set: "x.rar" (not "x.part2.rar"), or a lone "x.r00"-style set without
    /// a .rar. Used to show one entry per set.
    /// </summary>
    public static bool IsFirstVolumeName(string path)
    {
        string name = Path.GetFileName(path);
        var part = PartName().Match(name);
        if (part.Success)
        {
            return int.Parse(part.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        return name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
    }

    public static RarArchive Open(string firstVolume)
    {
        var a = new RarArchive(Path.GetFullPath(firstVolume));
        foreach (var vol in VolumeNames(a.FirstVolume))
        {
            if (!File.Exists(vol))
            {
                break;
            }
            a.Volumes.Add(vol);
            if (!a.ReadVolume(vol))
            {
                break; // not a RAR volume, or encrypted headers
            }
        }
        if (a.Volumes.Count == 0 || a.Version == 0)
        {
            throw new InvalidDataException("Not a RAR archive: " + Path.GetFileName(firstVolume));
        }
        return a;
    }

    /// <summary>Opens a stored entry as a seekable stream over its volume pieces.</summary>
    public Stream OpenRead(RarEntry e)
    {
        if (e.IsEncrypted)
        {
            throw new InvalidDataException($"{e.Name} is encrypted in the RAR set.");
        }
        if (!e.IsStored)
        {
            throw new InvalidDataException($"{e.Name} is compressed in the RAR set; only stored (-m0) files can be read in place. Extract it first.");
        }
        if (!e.IsComplete)
        {
            throw new InvalidDataException($"{e.Name}: RAR volumes are missing.");
        }
        return new SegmentedStream(e.Pieces, e.Size);
    }

    /// <summary>Volume names in order: x.rar, x.r00, x.r01 … (old style) or x.part1.rar, x.part2.rar … (new style).</summary>
    internal static IEnumerable<string> VolumeNames(string first)
    {
        string dir = Path.GetDirectoryName(first) ?? "";
        string name = Path.GetFileName(first);
        var part = PartName().Match(name);
        if (part.Success)
        {
            int width = part.Groups[2].Value.Length;
            int start = int.Parse(part.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            for (int i = start; i < 100000; i++)
            {
                yield return Path.Combine(dir, part.Groups[1].Value + i.ToString(new string('0', width), System.Globalization.CultureInfo.InvariantCulture) + part.Groups[3].Value);
            }
            yield break;
        }
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        bool upper = ext.Length > 1 && char.IsUpper(ext[1]);
        if (!ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            // started from x.r00: there may be no .rar
            yield return first;
            yield break;
        }
        yield return first;
        // .r00 … .r99, .s00 … .s99, …
        for (char c = upper ? 'R' : 'r'; c <= (upper ? 'Z' : 'z'); c++)
        {
            for (int i = 0; i < 100; i++)
            {
                yield return Path.Combine(dir, $"{stem}.{c}{i:00}");
            }
        }
    }

    /// <summary>Reads one volume's headers; false when it is not a readable RAR volume.</summary>
    private bool ReadVolume(string vol)
    {
        using var fs = new FileStream(vol, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        var sig = new byte[8];
        int n = fs.ReadAtLeast(sig, 8, false);
        if (n >= 8 && sig.AsSpan().SequenceEqual(Rar5Sig))
        {
            Version = 5;
            ReadRar5(fs, vol);
            return !HeadersEncrypted;
        }
        if (n >= 7 && sig.AsSpan(0, 7).SequenceEqual(Rar4Sig))
        {
            Version = 4;
            fs.Position = 7;
            ReadRar4(fs, vol);
            return !HeadersEncrypted;
        }
        return false;
    }

    private bool ReadRar4(FileStream fs, string vol)
    {
        bool continues = false;
        var h = new byte[7];
        while (fs.Position + 7 <= fs.Length)
        {
            long start = fs.Position;
            fs.ReadExactly(h);
            int type = h[2];
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(3));
            int size = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(5));
            if (size < 7)
            {
                break;
            }
            var body = new byte[size - 7];
            fs.ReadExactly(body);
            long add = 0;
            if (type == 0x73)
            {
                if ((flags & 0x0080) != 0)
                {
                    HeadersEncrypted = true;
                    return false;
                }
            }
            else if (type == 0x74 && body.Length >= 25)
            {
                long pack = BinaryPrimitives.ReadUInt32LittleEndian(body);
                long unp = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
                int method = body[18];
                int nameSize = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(19));
                int p = 25;
                if ((flags & 0x100) != 0 && body.Length >= 33)
                {
                    pack |= (long)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(25)) << 32;
                    unp |= (long)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(29)) << 32;
                    p = 33;
                }
                string name = DecodeRar4Name(body.AsSpan(p, Math.Min(nameSize, body.Length - p)), (flags & 0x200) != 0);
                bool dir = (flags & 0xE0) == 0xE0;
                AddPiece(name, dir, unp, method == 0x30, (flags & 0x04) != 0, (flags & 0x10) != 0, vol, start + size, pack, (flags & 0x01) != 0);
                if ((flags & 0x02) != 0)
                {
                    continues = true;
                }
                add = pack;
            }
            else if ((flags & 0x8000) != 0 && body.Length >= 4)
            {
                add = BinaryPrimitives.ReadUInt32LittleEndian(body);
            }
            if (type == 0x7B)
            {
                break; // end of archive
            }
            fs.Position = start + size + add;
        }
        return continues;
    }

    private static string DecodeRar4Name(ReadOnlySpan<byte> raw, bool unicode)
    {
        if (unicode)
        {
            int zero = raw.IndexOf((byte)0);
            if (zero >= 0)
            {
                raw = raw[..zero]; // the part before the zero is the (OEM/UTF-8-ish) name
            }
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw);
        }
    }

    private bool ReadRar5(FileStream fs, string vol)
    {
        bool continues = false;
        fs.Position = 8;
        var buf = new byte[1 << 16];
        while (fs.Position + 7 <= fs.Length)
        {
            long start = fs.Position;
            // CRC32 (4) + header size (vint), read enough to decode the size
            int got = fs.ReadAtLeast(buf.AsSpan(0, 16), 16, false);
            if (got < 6)
            {
                break;
            }
            int p = 4;
            long headSize = VInt(buf, ref p);
            int sizeEnd = p;
            if (headSize <= 0 || headSize > 2 * 1024 * 1024)
            {
                break;
            }
            var head = headSize + sizeEnd <= buf.Length ? buf : new byte[headSize + sizeEnd];
            fs.Position = start;
            fs.ReadExactly(head.AsSpan(0, (int)(sizeEnd + headSize)));
            p = sizeEnd;
            long type = VInt(head, ref p);
            long hflags = VInt(head, ref p);
            long extraSize = (hflags & 1) != 0 ? VInt(head, ref p) : 0;
            long dataSize = (hflags & 2) != 0 ? VInt(head, ref p) : 0;
            long dataStart = start + sizeEnd + headSize;
            int headEnd = (int)(sizeEnd + headSize);
            switch (type)
            {
                case 4: // archive encryption header
                    HeadersEncrypted = true;
                    return false;
                case 2: // file
                {
                    long fflags = VInt(head, ref p);
                    long unp = VInt(head, ref p);
                    VInt(head, ref p); // attributes
                    if ((fflags & 2) != 0)
                    {
                        p += 4;
                    }
                    if ((fflags & 4) != 0)
                    {
                        p += 4;
                    }
                    long comp = VInt(head, ref p);
                    VInt(head, ref p); // host OS
                    int nameLen = (int)VInt(head, ref p);
                    string name = Encoding.UTF8.GetString(head, p, Math.Min(nameLen, headEnd - p));
                    p += nameLen;
                    bool encrypted = extraSize > 0 && HasRar5EncryptionRecord(head, p, (int)extraSize);
                    int method = (int)((comp >> 7) & 7);
                    AddPiece(name, (fflags & 1) != 0, unp, method == 0, encrypted, (comp & 0x40) != 0, vol, dataStart, dataSize, (hflags & 0x08) != 0);
                    if ((hflags & 0x10) != 0)
                    {
                        continues = true;
                    }
                    break;
                }
                case 5: // end of archive
                    fs.Position = fs.Length;
                    continue;
            }
            fs.Position = dataStart + dataSize;
        }
        return continues;
    }

    private static bool HasRar5EncryptionRecord(byte[] head, int p, int size)
    {
        int end = Math.Min(head.Length, p + size);
        while (p < end)
        {
            long recSize = VInt(head, ref p);
            int recStart = p;
            long recType = VInt(head, ref p);
            if (recType == 1)
            {
                return true;
            }
            p = recStart + (int)recSize;
        }
        return false;
    }

    private static long VInt(byte[] b, ref int p)
    {
        long v = 0;
        int shift = 0;
        while (p < b.Length)
        {
            byte x = b[p++];
            v |= (long)(x & 0x7F) << shift;
            if ((x & 0x80) == 0)
            {
                break;
            }
            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("bad RAR5 vint");
            }
        }
        return v;
    }

    private void AddPiece(string name, bool dir, long size, bool stored, bool encrypted, bool solid, string vol, long offset, long length, bool continued)
    {
        string key = Normalize(name);
        if (!_entries.TryGetValue(key, out var e) || !continued)
        {
            if (e is not null && !continued)
            {
                return; // duplicate header (e.g. a repeated file): keep the first
            }
            e = new RarEntry { Name = key, IsDirectory = dir, Size = size };
            _entries[key] = e;
        }
        e.IsStored = stored;
        e.IsEncrypted |= encrypted;
        e.IsSolid |= solid;
        if (!dir && length > 0)
        {
            e.Pieces.Add(new Piece(vol, offset, length));
        }
    }

    internal static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

    [GeneratedRegex(@"^(.*\.part)(\d+)(\.rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex PartName();
}
