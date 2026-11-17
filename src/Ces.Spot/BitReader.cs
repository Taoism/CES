namespace EncoderSpot.Core;

/// <summary>MSB-first bit reader over a byte span.</summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _bitPos;

    public BitReader(ReadOnlySpan<byte> data, long bitPos = 0)
    {
        _data = data;
        _bitPos = bitPos;
    }

    public readonly long BitPosition => _bitPos;
    public readonly long BitsLeft => (_data.Length * 8L) - _bitPos;
    public readonly bool IsByteAligned => (_bitPos & 7) == 0;
    public readonly int BytePosition => (int)(_bitPos >> 3);

    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }
        if (count > 32 || count > BitsLeft)
        {
            throw new EndOfStreamException();
        }
        uint value = 0;
        while (count > 0)
        {
            int byteIndex = (int)(_bitPos >> 3);
            int bitOffset = (int)(_bitPos & 7);
            int take = Math.Min(8 - bitOffset, count);
            int b = _data[byteIndex];
            int bits = (b >> (8 - bitOffset - take)) & ((1 << take) - 1);
            value = (value << take) | (uint)bits;
            _bitPos += take;
            count -= take;
        }
        return value;
    }

    public bool ReadBit() => ReadBits(1) != 0;

    public int ReadSigned(int count)
    {
        if (count == 0)
        {
            return 0;
        }
        uint v = ReadBits(count);
        int shift = 32 - count;
        return (int)(v << shift) >> shift;
    }

    /// <summary>Counts zero bits up to the next 1 bit and consumes the 1 (FLAC Rice prefix).</summary>
    public uint ReadUnary()
    {
        uint zeros = 0;
        while (true)
        {
            if (_bitPos >= _data.Length * 8L)
            {
                throw new EndOfStreamException();
            }
            int byteIndex = (int)(_bitPos >> 3);
            int bitOffset = (int)(_bitPos & 7);
            int b = (_data[byteIndex] << bitOffset) & 0xFF;
            if (b == 0)
            {
                zeros += (uint)(8 - bitOffset);
                _bitPos += 8 - bitOffset;
                continue;
            }
            int lead = System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24;
            zeros += (uint)lead;
            _bitPos += lead + 1;
            return zeros;
        }
    }

    public void Skip(long bits)
    {
        if (bits > BitsLeft)
        {
            throw new EndOfStreamException();
        }
        _bitPos += bits;
    }

    public void AlignToByte() => _bitPos = (_bitPos + 7) & ~7L;
}
