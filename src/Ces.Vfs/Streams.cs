namespace Ces.Vfs;

/// <summary>Read-only seekable stream made of consecutive pieces; subclasses read one piece at a time.</summary>
internal abstract class PieceStream : Stream
{
    private readonly long[] _starts;
    private readonly long[] _lengths;
    private readonly long _length;
    private long _pos;

    protected PieceStream(IReadOnlyList<long> pieceLengths, long length)
    {
        _lengths = [.. pieceLengths];
        _starts = new long[_lengths.Length];
        long s = 0;
        for (int i = 0; i < _lengths.Length; i++)
        {
            _starts[i] = s;
            s += _lengths[i];
        }
        _length = Math.Min(length, s);
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes of piece <paramref name="index"/> from <paramref name="within"/>.</summary>
    protected abstract int ReadPiece(int index, long within, Span<byte> buffer);

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _pos < _length)
        {
            int i = Array.BinarySearch(_starts, _pos);
            if (i < 0)
            {
                i = ~i - 1;
            }
            long within = _pos - _starts[i];
            int want = (int)Math.Min(buffer.Length, Math.Min(_lengths[i] - within, _length - _pos));
            int n = ReadPiece(i, within, buffer[..want]);
            if (n <= 0)
            {
                break;
            }
            buffer = buffer[n..];
            _pos += n;
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => _length + offset,
        };
        return _pos;
    }

    public override void Flush()
    {
        // read-only
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Read-only seekable stream over pieces of several files (a stored file spread over RAR volumes).</summary>
internal sealed class SegmentedStream(List<Piece> pieces, long length)
    : PieceStream([.. pieces.Select(p => p.Length)], length)
{
    private FileStream? _open;
    private int _openIndex = -1;

    protected override int ReadPiece(int index, long within, Span<byte> buffer)
    {
        var fs = File(index);
        fs.Position = pieces[index].Offset + within;
        return fs.Read(buffer);
    }

    private FileStream File(int index)
    {
        if (_openIndex != index || _open is null)
        {
            _open?.Dispose();
            _open = new FileStream(pieces[index].Volume, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
            _openIndex = index;
        }
        return _open;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _open?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>One extent of a file inside an image: <see cref="Offset"/> &lt; 0 means an unrecorded (zero) extent.</summary>
internal readonly record struct Extent(long Offset, long Length);

/// <summary>Read-only seekable stream over extents of a base stream (a file inside an ISO image).</summary>
internal sealed class ExtentStream(Stream baseStream, List<Extent> extents, long length)
    : PieceStream([.. extents.Select(e => e.Length)], length)
{
    protected override int ReadPiece(int index, long within, Span<byte> buffer)
    {
        var ext = extents[index];
        if (ext.Offset < 0)
        {
            buffer.Clear();
            return buffer.Length;
        }
        baseStream.Position = ext.Offset + within;
        return baseStream.Read(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            baseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
