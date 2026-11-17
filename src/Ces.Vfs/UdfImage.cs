using System.Buffers.Binary;
using System.Text;

namespace Ces.Vfs;

/// <summary>
/// Read-only UDF reader for disc images (DVD-Video UDF 1.02, Blu-ray UDF 2.50 with a metadata partition).
/// Works on any seekable stream, so it reads an ISO that is itself inside a RAR set. No mounting needed.
/// </summary>
public sealed class UdfImage
{
    private const int Sector = 2048;
    private readonly Func<Stream> _open;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PartMap> _maps = [];
    private readonly Dictionary<int, long> _partitionStarts = [];
    private List<Extent> _metaExtents = [];
    private int _blockSize = Sector;

    private sealed class Node
    {
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public List<Extent> Extents { get; init; } = [];
        public byte[]? Inline { get; init; }
        public List<string> Children { get; } = [];
    }

    private sealed record PartMap(int Type, int PartitionNumber, uint MetadataFile);

    private UdfImage(Func<Stream> open) => _open = open;

    /// <summary>Parses the image; <paramref name="open"/> must return a new independent stream each call.</summary>
    public static UdfImage Open(Func<Stream> open)
    {
        var img = new UdfImage(open);
        using var s = open();
        img.Parse(s);
        return img;
    }

    public static UdfImage Open(string isoPath) =>
        Open(() => new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess));

    public bool FileExists(string path) => _nodes.TryGetValue(Norm(path), out var n) && !n.IsDirectory;

    public bool DirectoryExists(string path) => _nodes.TryGetValue(Norm(path), out var n) && n.IsDirectory;

    public long Length(string path) =>
        _nodes.TryGetValue(Norm(path), out var n) && !n.IsDirectory ? n.Size : throw new FileNotFoundException(path);

    /// <summary>Entries of a directory (inner paths with '/').</summary>
    public IEnumerable<(string Path, bool IsDirectory)> List(string dir)
    {
        string d = Norm(dir);
        if (!_nodes.TryGetValue(d, out var node) || !node.IsDirectory)
        {
            yield break;
        }
        foreach (var c in node.Children)
        {
            string p = d.Length == 0 ? c : d + "/" + c;
            yield return (p, _nodes[p].IsDirectory);
        }
    }

    public Stream OpenRead(string path)
    {
        if (!_nodes.TryGetValue(Norm(path), out var n) || n.IsDirectory)
        {
            throw new FileNotFoundException("Not in the image: " + path);
        }
        if (n.Inline is { } data)
        {
            return new MemoryStream(data, false);
        }
        return new ExtentStream(_open(), n.Extents, n.Size);
    }

    internal static string Norm(string p) => p.Replace('\\', '/').Trim('/');

    // ---------------------------------------------------------------- parsing

    private static byte[] ReadAt(Stream s, long offset, int length)
    {
        var b = new byte[length];
        s.Position = offset;
        s.ReadExactly(b);
        return b;
    }

    private static int Tag(byte[] b) => BinaryPrimitives.ReadUInt16LittleEndian(b);

    private void Parse(Stream s)
    {
        byte[]? avdp = null;
        foreach (long sector in new[] { 256L, (s.Length / Sector) - 1, (s.Length / Sector) - 257 })
        {
            if (sector <= 0 || (sector + 1) * Sector > s.Length)
            {
                continue;
            }
            var b = ReadAt(s, sector * Sector, Sector);
            if (Tag(b) == 2)
            {
                avdp = b;
                break;
            }
        }
        if (avdp is null)
        {
            throw new InvalidDataException("Not a UDF disc image (no anchor descriptor).");
        }
        uint vdsLen = BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(16));
        uint vdsLoc = BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(20));
        byte[]? lvd = null;
        for (uint i = 0; i < Math.Min(64u, vdsLen / Sector); i++)
        {
            var d = ReadAt(s, (long)(vdsLoc + i) * Sector, Sector);
            int tag = Tag(d);
            if (tag == 5)
            {
                int num = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(22));
                _partitionStarts[num] = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(188));
            }
            else if (tag == 6)
            {
                lvd = d;
            }
            else if (tag == 8)
            {
                break;
            }
        }
        if (lvd is null || _partitionStarts.Count == 0)
        {
            throw new InvalidDataException("UDF image without a logical volume or partition.");
        }
        _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(212));
        if (_blockSize != Sector)
        {
            throw new InvalidDataException($"UDF block size {_blockSize} is not supported.");
        }
        int maps = (int)BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(268));
        int p = 440;
        for (int i = 0; i < maps && p + 2 <= lvd.Length; i++)
        {
            int type = lvd[p];
            int len = lvd[p + 1];
            if (type == 1)
            {
                _maps.Add(new PartMap(1, BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(p + 4)), 0));
            }
            else if (type == 2)
            {
                string id = Encoding.ASCII.GetString(lvd, p + 5, 23).TrimEnd('\0', ' ');
                int num = BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(p + 38));
                if (id.Contains("Metadata", StringComparison.Ordinal))
                {
                    _maps.Add(new PartMap(3, num, BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(p + 40))));
                }
                else if (id.Contains("Virtual", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("UDF virtual partitions (packet-written CD/DVD-R) are not supported.");
                }
                else
                {
                    _maps.Add(new PartMap(1, num, 0)); // sparable: read like a plain partition
                }
            }
            p += Math.Max(len, 6);
        }
        // metadata partition: its content is the file at MetadataFile in the physical partition
        int metaIndex = _maps.FindIndex(m => m.Type == 3);
        if (metaIndex >= 0)
        {
            var meta = _maps[metaIndex];
            int physIndex = PhysicalMapFor(meta.PartitionNumber);
            var fe = ReadEntry(s, physIndex, meta.MetadataFile);
            _metaExtents = fe.Extents;
        }

        int fsdLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(248 + 4));
        int fsdPart = BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(248 + 8));
        var fsd = ReadAt(s, Address(s, fsdPart, (uint)fsdLbn), Sector);
        if (Tag(fsd) != 256)
        {
            throw new InvalidDataException("UDF file set descriptor not found.");
        }
        uint rootLbn = BinaryPrimitives.ReadUInt32LittleEndian(fsd.AsSpan(400 + 4));
        int rootPart = BinaryPrimitives.ReadUInt16LittleEndian(fsd.AsSpan(400 + 8));
        ReadTree(s, "", rootPart, rootLbn, 0);
    }

    private int PhysicalMapFor(int partitionNumber)
    {
        int i = _maps.FindIndex(m => m.Type == 1 && m.PartitionNumber == partitionNumber);
        return i >= 0 ? i : _maps.FindIndex(m => m.Type == 1);
    }

    /// <summary>Absolute byte offset of logical block <paramref name="lbn"/> in partition map <paramref name="partRef"/>.</summary>
    private long Address(Stream s, int partRef, uint lbn)
    {
        if (partRef < 0 || partRef >= _maps.Count)
        {
            throw new InvalidDataException("bad UDF partition reference");
        }
        var map = _maps[partRef];
        if (map.Type == 3)
        {
            long logical = (long)lbn * Sector;
            foreach (var e in _metaExtents)
            {
                if (logical < e.Length)
                {
                    return e.Offset + logical;
                }
                logical -= e.Length;
            }
            throw new InvalidDataException("UDF metadata block out of range");
        }
        _ = s;
        return (_partitionStarts.GetValueOrDefault(map.PartitionNumber) + lbn) * Sector;
    }

    private sealed record Entry(bool IsDirectory, long Size, List<Extent> Extents, byte[]? Inline);

    private Entry ReadEntry(Stream s, int partRef, uint lbn)
    {
        var b = ReadAt(s, Address(s, partRef, lbn), Sector);
        int tag = Tag(b);
        int lea, lad, baseOff;
        if (tag == 261)
        {
            lea = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(168));
            lad = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(172));
            baseOff = 176;
        }
        else if (tag == 266)
        {
            lea = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(208));
            lad = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(212));
            baseOff = 216;
        }
        else
        {
            throw new InvalidDataException($"UDF file entry expected, found tag {tag}");
        }
        int fileType = b[16 + 11];
        int adType = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16 + 18)) & 7;
        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(56));
        int adStart = baseOff + lea;
        if (adStart + lad > b.Length)
        {
            throw new InvalidDataException("UDF allocation descriptors out of range");
        }
        bool dir = fileType == 4;
        if (adType == 3)
        {
            return new Entry(dir, size, [], b.AsSpan(adStart, (int)Math.Min(lad, size)).ToArray());
        }
        // short_ad refer to the partition the ICB is in (ECMA-167 4/14.14.1); directories of Blu-ray discs live in
        // the metadata partition, file data is addressed with long_ad or from entries in the physical partition
        int shortPart = partRef;
        var extents = new List<Extent>();
        ReadAds(s, b, adStart, lad, adType, shortPart, extents, 0);
        return new Entry(dir, size, extents, null);
    }

    private void ReadAds(Stream s, byte[] b, int start, int length, int adType, int shortPart, List<Extent> extents, int depth)
    {
        int size = adType == 0 ? 8 : adType == 1 ? 16 : 20;
        for (int p = start; p + size <= start + length; p += size)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
            long len = raw & 0x3FFFFFFF;
            int kind = (int)(raw >> 30);
            if (len == 0)
            {
                break;
            }
            uint lbn;
            int part;
            if (adType == 0)
            {
                lbn = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
                part = shortPart;
            }
            else if (adType == 1)
            {
                lbn = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
                part = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 8));
            }
            else
            {
                lbn = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 12));
                part = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 16));
            }
            if (kind == 3)
            {
                // the descriptor list continues in another block (Allocation Extent Descriptor)
                if (depth > 64)
                {
                    throw new InvalidDataException("UDF allocation extent chain too long");
                }
                var next = ReadAt(s, Address(s, part, lbn), Sector);
                int nlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(next.AsSpan(20));
                ReadAds(s, next, 24, Math.Min(nlen, Sector - 24), adType, shortPart, extents, depth + 1);
                return;
            }
            extents.Add(kind == 0 ? new Extent(Address(s, part, lbn), len) : new Extent(-1, len));
        }
    }

    private byte[] ReadData(Stream s, Entry e)
    {
        if (e.Inline is { } inline)
        {
            return inline;
        }
        if (e.Size > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("UDF directory too large");
        }
        using var es = new ExtentStream(new NonClosing(s), e.Extents, e.Size);
        var data = new byte[e.Size];
        es.ReadExactly(data);
        return data;
    }

    private void ReadTree(Stream s, string path, int partRef, uint lbn, int depth)
    {
        if (depth > 32)
        {
            return;
        }
        var e = ReadEntry(s, partRef, lbn);
        var node = new Node { IsDirectory = e.IsDirectory, Size = e.Size, Extents = e.Extents, Inline = e.Inline };
        _nodes[path] = node;
        if (!e.IsDirectory)
        {
            return;
        }
        var data = ReadData(s, e);
        int p = 0;
        while (p + 38 <= data.Length)
        {
            if (Tag((ReadOnlySpan<byte>)data.AsSpan(p)) != 257)
            {
                break;
            }
            int chars = data[p + 18];
            int lfi = data[p + 19];
            uint icbLbn = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 24));
            int icbPart = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 28));
            int liu = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 36));
            int nameOff = p + 38 + liu;
            int total = (38 + liu + lfi + 3) & ~3;
            if ((chars & 0x0C) == 0 && lfi > 0 && nameOff + lfi <= data.Length)
            {
                string name = DString(data.AsSpan(nameOff, lfi));
                if (name.Length > 0 && name != "." && name != "..")
                {
                    string child = path.Length == 0 ? name : path + "/" + name;
                    if (!_nodes.ContainsKey(child))
                    {
                        node.Children.Add(name);
                        try
                        {
                            ReadTree(s, child, icbPart, icbLbn, depth + 1);
                        }
                        catch (InvalidDataException)
                        {
                            node.Children.Remove(name);
                        }
                    }
                }
            }
            p += total;
        }
    }

    private static string DString(ReadOnlySpan<byte> d)
    {
        return d[0] switch
        {
            16 => Encoding.BigEndianUnicode.GetString(d[1..]),
            _ => Encoding.Latin1.GetString(d[1..]),
        };
    }

    private static int Tag(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16LittleEndian(b);

    /// <summary>Lets an <see cref="ExtentStream"/> borrow the parse stream without closing it.</summary>
    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
            // read-only
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
