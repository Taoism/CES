namespace Ces.Vfs;

/// <summary>Read-only seekable stream over pieces of several files (a stored file spread over RAR volumes).</summary>
internal sealed class SegmentedStream : Stream
{
    private readonly List<Piece> _pieces;
    private readonly long[] _starts;
    private readonly long _length;
    private FileStream? _open;
    private int _openIndex = -1;
    private long _pos;

    public SegmentedStream(List<Piece> pieces, long length)
    {
        _pieces = pieces;
        _length = Math.Min(length, pieces.Sum(p => p.Length));
        _starts = new long[pieces.Count];
        long s = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            _starts[i] = s;
            s += pieces[i].Length;
        }
    }

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
            var piece = _pieces[i];
            long within = _pos - _starts[i];
            int want = (int)Math.Min(buffer.Length, piece.Length - within);
            var fs = File(i);
            fs.Position = piece.Offset + within;
            int n = fs.Read(buffer[..want]);
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

    private FileStream File(int index)
    {
        if (_openIndex != index || _open is null)
        {
            _open?.Dispose();
            _open = new FileStream(_pieces[index].Volume, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
            _openIndex = index;
        }
        return _open;
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
internal sealed class ExtentStream : Stream
{
    private readonly Stream _base;
    private readonly List<Extent> _extents;
    private readonly long[] _starts;
    private readonly long _length;
    private long _pos;

    public ExtentStream(Stream baseStream, List<Extent> extents, long length)
    {
        _base = baseStream;
        _extents = extents;
        _length = length;
        _starts = new long[extents.Count];
        long s = 0;
        for (int i = 0; i < extents.Count; i++)
        {
            _starts[i] = s;
            s += extents[i].Length;
        }
        _length = Math.Min(length, s);
    }

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
            var ext = _extents[i];
            long within = _pos - _starts[i];
            int want = (int)Math.Min(buffer.Length, Math.Min(ext.Length - within, _length - _pos));
            int n;
            if (ext.Offset < 0)
            {
                buffer[..want].Clear();
                n = want;
            }
            else
            {
                _base.Position = ext.Offset + within;
                n = _base.Read(buffer[..want]);
            }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _base.Dispose();
        }
        base.Dispose(disposing);
    }
}
