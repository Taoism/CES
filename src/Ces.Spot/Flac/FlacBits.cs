using System.Buffers.Binary;
using System.Numerics;

namespace EncoderSpot.Core.Flac;

/// <summary>Fast MSB-first bit reader for FLAC frames (reads 64-bit words).</summary>
internal sealed class FlacBits
{
    private byte[] _data = [];
    private long _endBits;
    private long _bitPos;

    public void Reset(byte[] data, int byteOffset, int byteEnd)
    {
        _data = data;
        _bitPos = byteOffset * 8L;
        _endBits = byteEnd * 8L;
    }

    public int BytePosition => (int)(_bitPos >> 3);

    private ulong Load(int byteIndex)
    {
        if (byteIndex + 8 <= _data.Length)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(byteIndex));
        }
        ulong w = 0;
        for (int i = 0; i < 8; i++)
        {
            w <<= 8;
            if (byteIndex + i < _data.Length)
            {
                w |= _data[byteIndex + i];
            }
        }
        return w;
    }

    public uint ReadBits(int n)
    {
        if (n == 0)
        {
            return 0;
        }
        if (_bitPos + n > _endBits)
        {
            throw new EndOfStreamException();
        }
        int byteIndex = (int)(_bitPos >> 3);
        int off = (int)(_bitPos & 7);
        ulong w = Load(byteIndex) << off;
        _bitPos += n;
        return (uint)(w >> (64 - n));
    }

    public int ReadSigned(int n)
    {
        if (n == 0)
        {
            return 0;
        }
        uint v = ReadBits(n);
        int shift = 32 - n;
        return (int)(v << shift) >> shift;
    }

    /// <summary>Reads a signed value wider than 32 bits is never needed: FLAC samples fit in 33 bits only for side channels of 32-bit audio.</summary>
    public long ReadSignedLong(int n)
    {
        if (n <= 32)
        {
            return ReadSigned(n);
        }
        long hi = ReadBits(n - 32);
        long lo = ReadBits(32);
        long v = (hi << 32) | lo;
        int shift = 64 - n;
        return (v << shift) >> shift;
    }

    public uint ReadUnary()
    {
        uint count = 0;
        while (true)
        {
            if (_bitPos >= _endBits)
            {
                throw new EndOfStreamException();
            }
            int byteIndex = (int)(_bitPos >> 3);
            int off = (int)(_bitPos & 7);
            ulong w = Load(byteIndex) << off;
            if (w == 0)
            {
                int skipped = 64 - off;
                count += (uint)skipped;
                _bitPos += skipped;
                continue;
            }
            int lz = BitOperations.LeadingZeroCount(w);
            count += (uint)lz;
            _bitPos += lz + 1;
            if (_bitPos > _endBits)
            {
                throw new EndOfStreamException();
            }
            return count;
        }
    }

    public void AlignToByte() => _bitPos = (_bitPos + 7) & ~7L;
}
