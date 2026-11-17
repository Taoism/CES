using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EncoderSpot.Core.Flac;

/// <summary>Reads FLAC metadata and decodes every frame (CRC and MD5 checks, subframe statistics).</summary>
public static class FlacAnalyzer
{
    private const int WindowSize = 16 * 1024 * 1024;
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    private static readonly int[] SampleRates = [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];
    private static readonly int[] SampleSizes = [0, 8, 12, 0, 16, 20, 24, 32];

    /// <summary>Returns null when there is no "fLaC" marker at <paramref name="start"/>.</summary>
    public static FlacAnalysis? Analyze(Stream s, long start, long end, bool decode = true, CancellationToken ct = default, Stream? pcmOutput = null)
    {
        s.Position = start;
        var marker = new byte[4];
        if (s.ReadAtLeast(marker, 4, false) < 4 || marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C')
        {
            return null;
        }
        var a = new FlacAnalysis { StreamOffset = start };
        long framesStart = ReadMetadata(s, a, end);
        if (framesStart < 0)
        {
            return a;
        }
        if (decode)
        {
            new FrameWalker(s, a, framesStart, end, pcmOutput).Run(ct);
        }
        return a;
    }

    private static long ReadMetadata(Stream s, FlacAnalysis a, long end)
    {
        var h = new byte[4];
        while (s.Position + 4 <= end)
        {
            s.ReadExactly(h);
            bool last = (h[0] & 0x80) != 0;
            int type = h[0] & 0x7F;
            int len = (h[1] << 16) | (h[2] << 8) | h[3];
            if (s.Position + len > end)
            {
                return -1;
            }
            string name = type switch
            {
                0 => "STREAMINFO",
                1 => "PADDING",
                2 => "APPLICATION",
                3 => "SEEKTABLE",
                4 => "VORBIS_COMMENT",
                5 => "CUESHEET",
                6 => "PICTURE",
                127 => "INVALID",
                _ => $"RESERVED({type})",
            };
            a.Blocks.Add(new FlacMetadataBlock(type, name, len));
            if (type is 0 or 2 or 4)
            {
                var body = new byte[len];
                s.ReadExactly(body);
                switch (type)
                {
                    case 0:
                        ReadStreamInfo(body, a);
                        break;
                    case 2 when len >= 4:
                        a.Applications.Add(Encoding.ASCII.GetString(body, 0, 4));
                        break;
                    case 4:
                        ReadVorbisComment(body, a);
                        break;
                }
            }
            else
            {
                switch (type)
                {
                    case 1:
                        a.PaddingBytes += len;
                        break;
                    case 3:
                        a.SeekPoints += len / 18;
                        break;
                    case 5:
                        a.HasCueSheet = true;
                        break;
                    case 6:
                        a.Pictures++;
                        break;
                }
                s.Position += len;
            }
            if (last)
            {
                return s.Position;
            }
        }
        return -1;
    }

    private static void ReadStreamInfo(byte[] b, FlacAnalysis a)
    {
        if (b.Length < 34)
        {
            return;
        }
        a.MinBlockSize = BinaryPrimitives.ReadUInt16BigEndian(b);
        a.MaxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(2));
        a.MinFrameSize = (b[4] << 16) | (b[5] << 8) | b[6];
        a.MaxFrameSize = (b[7] << 16) | (b[8] << 8) | b[9];
        ulong x = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(10));
        a.SampleRate = (int)(x >> 44);
        a.Channels = (int)((x >> 41) & 7) + 1;
        a.BitsPerSample = (int)((x >> 36) & 31) + 1;
        a.TotalSamples = (long)(x & 0xFFFFFFFFFUL);
        a.StoredMd5 = b.AsSpan(18, 16).ToArray();
    }

    private static void ReadVorbisComment(byte[] b, FlacAnalysis a)
    {
        int p = 0;
        if (b.Length < 4)
        {
            return;
        }
        int vlen = BinaryPrimitives.ReadInt32LittleEndian(b);
        p += 4;
        if (vlen < 0 || p + vlen > b.Length)
        {
            return;
        }
        a.Vendor = Encoding.UTF8.GetString(b, p, vlen);
        p += vlen;
        if (p + 4 > b.Length)
        {
            return;
        }
        int count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        p += 4;
        for (int i = 0; i < count && p + 4 <= b.Length; i++)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            p += 4;
            if (len < 0 || p + len > b.Length)
            {
                return;
            }
            string c = Encoding.UTF8.GetString(b, p, len);
            p += len;
            int eq = c.IndexOf('=');
            if (eq > 0)
            {
                a.Comments.Add(new(c[..eq], c[(eq + 1)..]));
            }
        }
    }

    internal static byte Crc8(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
        }
        return (byte)crc;
    }

    private static readonly ushort[] Crc16Table = BuildCrc16();

    private static ushort[] BuildCrc16()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
            }
            t[i] = (ushort)c;
        }
        return t;
    }

    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc = ((crc << 8) ^ Crc16Table[((crc >> 8) ^ b) & 0xFF]) & 0xFFFF;
        }
        return (ushort)crc;
    }

    internal readonly record struct FrameHeader(
        int HeaderLength,
        bool VariableBlockSize,
        int BlockSize,
        int SampleRate,
        int ChannelCode,
        int BitsPerSample);

    /// <summary>Parses a frame header at buf[pos]; checks its CRC-8. Returns false if it is not a valid header.</summary>
    internal static bool TryParseHeader(ReadOnlySpan<byte> buf, FlacAnalysis a, out FrameHeader fh, out bool crcBad)
    {
        fh = default;
        crcBad = false;
        if (buf.Length < 6 || buf[0] != 0xFF || (buf[1] & 0xFE) != 0xF8)
        {
            return false;
        }
        int bsCode = buf[2] >> 4;
        int srCode = buf[2] & 15;
        int chCode = buf[3] >> 4;
        int ssCode = (buf[3] >> 1) & 7;
        if (bsCode == 0 || srCode == 15 || chCode > 10 || ssCode == 3 || (buf[3] & 1) != 0)
        {
            return false;
        }
        int p = 4;
        // UTF-8-like coded frame/sample number
        int first = buf[p];
        int extra;
        if ((first & 0x80) == 0)
        {
            extra = 0;
        }
        else if ((first & 0xE0) == 0xC0)
        {
            extra = 1;
        }
        else if ((first & 0xF0) == 0xE0)
        {
            extra = 2;
        }
        else if ((first & 0xF8) == 0xF0)
        {
            extra = 3;
        }
        else if ((first & 0xFC) == 0xF8)
        {
            extra = 4;
        }
        else if ((first & 0xFE) == 0xFC)
        {
            extra = 5;
        }
        else if (first == 0xFE)
        {
            extra = 6;
        }
        else
        {
            return false;
        }
        p++;
        for (int i = 0; i < extra; i++, p++)
        {
            if (p >= buf.Length || (buf[p] & 0xC0) != 0x80)
            {
                return false;
            }
        }
        int blockSize;
        switch (bsCode)
        {
            case 1:
                blockSize = 192;
                break;
            case >= 2 and <= 5:
                blockSize = 576 << (bsCode - 2);
                break;
            case 6:
                if (p + 1 > buf.Length)
                {
                    return false;
                }
                blockSize = buf[p] + 1;
                p += 1;
                break;
            case 7:
                if (p + 2 > buf.Length)
                {
                    return false;
                }
                blockSize = ((buf[p] << 8) | buf[p + 1]) + 1;
                p += 2;
                break;
            default:
                blockSize = 256 << (bsCode - 8);
                break;
        }
        int sampleRate;
        switch (srCode)
        {
            case 0:
                sampleRate = a.SampleRate;
                break;
            case 12:
                if (p + 1 > buf.Length)
                {
                    return false;
                }
                sampleRate = buf[p] * 1000;
                p += 1;
                break;
            case 13:
            case 14:
                if (p + 2 > buf.Length)
                {
                    return false;
                }
                sampleRate = ((buf[p] << 8) | buf[p + 1]) * (srCode == 14 ? 10 : 1);
                p += 2;
                break;
            default:
                sampleRate = SampleRates[srCode];
                break;
        }
        if (p >= buf.Length)
        {
            return false;
        }
        crcBad = Crc8(buf[..p]) != buf[p];
        int bps = ssCode == 0 ? a.BitsPerSample : SampleSizes[ssCode];
        fh = new FrameHeader(p + 1, (buf[1] & 1) != 0, blockSize, sampleRate, chCode, bps);
        return true;
    }

    /// <summary>Decodes frames sequentially, sliding a window over the file.</summary>
    private sealed class FrameWalker(Stream s, FlacAnalysis a, long start, long end, Stream? pcmOutput)
    {
        private readonly byte[] _buf = new byte[WindowSize];
        private readonly FlacBits _bits = new();
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private long _bufStart;
        private int _bufLen;
        private int[][] _ch = [];
        private long[] _tmp = [];
        private byte[] _pcm = [];

        public void Run(CancellationToken ct)
        {
            long pos = start;
            bool hashOk = a.Channels > 0 && a.BitsPerSample > 0;
            while (pos < end)
            {
                if ((a.Frames & 255) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
                Fill(pos);
                int off = (int)(pos - _bufStart);
                int avail = _bufLen - off;
                if (avail < 6)
                {
                    a.TrailingBytes = end - pos;
                    break;
                }
                int frameLen = TryDecodeFrame(off, avail, hashOk);
                if (frameLen > 0)
                {
                    pos += frameLen;
                    continue;
                }
                long next = FindSync(pos + 1);
                if (next < 0)
                {
                    a.TrailingBytes = end - pos;
                    break;
                }
                a.SyncErrors++;
                a.SkippedBytes += next - pos;
                pos = next;
            }
            if (hashOk && a.DecodeErrors == 0)
            {
                a.ComputedMd5 = _md5.GetHashAndReset();
            }
        }

        private void Fill(long pos)
        {
            if (pos >= _bufStart && pos + MaxFrameBytes <= _bufStart + _bufLen)
            {
                return;
            }
            if (pos >= _bufStart && _bufStart + _bufLen >= end)
            {
                return; // already holds the tail
            }
            s.Position = pos;
            int want = (int)Math.Min(_buf.Length, end - pos);
            _bufLen = s.ReadAtLeast(_buf.AsSpan(0, want), want, false);
            _bufStart = pos;
        }

        private long FindSync(long from)
        {
            long pos = from;
            while (pos + 6 <= end)
            {
                Fill(pos);
                int off = (int)(pos - _bufStart);
                int limit = _bufLen - 6;
                for (int i = off; i <= limit; i++)
                {
                    if (_buf[i] == 0xFF && (_buf[i + 1] & 0xFE) == 0xF8
                        && TryParseHeader(_buf.AsSpan(i, Math.Min(16, _bufLen - i)), a, out _, out bool bad) && !bad)
                    {
                        return _bufStart + i;
                    }
                }
                long scanned = _bufStart + limit + 1;
                if (scanned <= pos || _bufStart + _bufLen >= end)
                {
                    return -1;
                }
                pos = scanned;
            }
            return -1;
        }

        /// <summary>Returns the frame length, or 0 when no valid frame starts at <paramref name="off"/>.</summary>
        private int TryDecodeFrame(int off, int avail, bool hash)
        {
            var span = _buf.AsSpan(off, avail);
            if (!TryParseHeader(span[..Math.Min(16, span.Length)], a, out var fh, out bool crcBad))
            {
                return 0;
            }
            if (crcBad)
            {
                a.HeaderCrcErrors++;
                return 0;
            }
            int nch = fh.ChannelCode <= 7 ? fh.ChannelCode + 1 : 2;
            int bs = fh.BlockSize;
            if (fh.BitsPerSample == 0 || bs == 0)
            {
                return 0;
            }
            EnsureBuffers(nch, bs);
            _bits.Reset(_buf, off + fh.HeaderLength, off + avail);
            try
            {
                for (int c = 0; c < nch; c++)
                {
                    int bps = fh.BitsPerSample;
                    if ((fh.ChannelCode == 8 && c == 1) || (fh.ChannelCode == 9 && c == 0) || (fh.ChannelCode == 10 && c == 1))
                    {
                        bps++;
                    }
                    DecodeSubframe(_ch[c], bs, bps);
                }
                _bits.AlignToByte();
                int crcPos = _bits.BytePosition;
                if (crcPos + 2 > off + avail)
                {
                    return 0;
                }
                ushort stored = (ushort)((_buf[crcPos] << 8) | _buf[crcPos + 1]);
                int frameLen = crcPos + 2 - off;
                bool crcOk = Crc16(_buf.AsSpan(off, frameLen - 2)) == stored;
                if (!crcOk)
                {
                    // A wrong CRC can also mean a false sync inside audio data; only accept it when the next header follows.
                    if (!NextIsHeader(off + frameLen))
                    {
                        return 0;
                    }
                    a.FrameCrcErrors++;
                }
                Decorrelate(fh.ChannelCode, bs);
                if (hash || pcmOutput is not null)
                {
                    Hash(nch, bs, fh.BitsPerSample, hash);
                }
                Record(fh, nch, bs, frameLen);
                return frameLen;
            }
            catch (Exception ex) when (ex is EndOfStreamException or IndexOutOfRangeException or InvalidDataException)
            {
                a.DecodeErrors++;
                return 0;
            }
        }

        private bool NextIsHeader(int bufPos)
        {
            if (_bufStart + bufPos >= end)
            {
                return true;
            }
            if (bufPos + 6 > _bufLen)
            {
                return false;
            }
            return TryParseHeader(_buf.AsSpan(bufPos, Math.Min(16, _bufLen - bufPos)), a, out _, out bool bad) && !bad;
        }

        private void Record(in FrameHeader fh, int nch, int bs, int frameLen)
        {
            a.Frames++;
            if (fh.VariableBlockSize)
            {
                a.VariableBlockSize = true;
            }
            a.BlockSizeHistogram[bs] = a.BlockSizeHistogram.GetValueOrDefault(bs) + 1;
            var ca = fh.ChannelCode switch
            {
                8 => FlacChannelAssignment.LeftSide,
                9 => FlacChannelAssignment.RightSide,
                10 => FlacChannelAssignment.MidSide,
                _ => FlacChannelAssignment.Independent,
            };
            a.ChannelAssignments[ca] = a.ChannelAssignments.GetValueOrDefault(ca) + 1;
            a.DecodedSamples += bs;
            a.AudioBytes += frameLen;
            int rate = fh.SampleRate > 0 ? fh.SampleRate : a.SampleRate;
            if (rate > 0)
            {
                a.FrameBitrates.Add((short)Math.Min(short.MaxValue, frameLen * 8.0 * rate / bs / 1000.0));
            }
            a.Subframes += nch;
        }

        private void EnsureBuffers(int nch, int bs)
        {
            if (_ch.Length < nch || _ch[0].Length < bs)
            {
                int size = Math.Max(bs, _ch.Length > 0 ? _ch[0].Length : 0);
                _ch = new int[Math.Max(nch, 8)][];
                for (int i = 0; i < _ch.Length; i++)
                {
                    _ch[i] = new int[size];
                }
                _tmp = new long[size];
            }
        }

        private void DecodeSubframe(int[] dst, int bs, int bps)
        {
            if (_bits.ReadBits(1) != 0)
            {
                throw new InvalidDataException("subframe padding bit");
            }
            int type = (int)_bits.ReadBits(6);
            int wasted = 0;
            if (_bits.ReadBits(1) != 0)
            {
                wasted = (int)_bits.ReadUnary() + 1;
                a.WastedBitsSubframes++;
            }
            int effBps = bps - wasted;
            if (effBps <= 0 || effBps > 33)
            {
                throw new InvalidDataException("bad sample size");
            }
            var samples = _tmp;
            if (type == 0)
            {
                a.ConstantSubframes++;
                long v = _bits.ReadSignedLong(effBps);
                Array.Fill(samples, v, 0, bs);
            }
            else if (type == 1)
            {
                a.VerbatimSubframes++;
                for (int i = 0; i < bs; i++)
                {
                    samples[i] = _bits.ReadSignedLong(effBps);
                }
            }
            else if (type is >= 8 and <= 12)
            {
                int order = type - 8;
                if (order > bs)
                {
                    throw new InvalidDataException("order > block size");
                }
                a.FixedOrders[order]++;
                for (int i = 0; i < order; i++)
                {
                    samples[i] = _bits.ReadSignedLong(effBps);
                }
                ReadResidual(samples, bs, order);
                RestoreFixed(samples, bs, order);
            }
            else if (type >= 32)
            {
                int order = type - 31;
                if (order > bs)
                {
                    throw new InvalidDataException("order > block size");
                }
                a.LpcOrders[order]++;
                for (int i = 0; i < order; i++)
                {
                    samples[i] = _bits.ReadSignedLong(effBps);
                }
                int precision = (int)_bits.ReadBits(4) + 1;
                if (precision == 16)
                {
                    throw new InvalidDataException("bad LPC precision");
                }
                a.LpcPrecisions[precision]++;
                int shift = _bits.ReadSigned(5);
                if (shift < 0)
                {
                    throw new InvalidDataException("negative LPC shift");
                }
                Span<int> coefs = stackalloc int[32];
                for (int i = 0; i < order; i++)
                {
                    coefs[i] = _bits.ReadSigned(precision);
                }
                ReadResidual(samples, bs, order);
                RestoreLpc(samples, bs, coefs[..order], shift);
            }
            else
            {
                throw new InvalidDataException("reserved subframe type");
            }
            for (int i = 0; i < bs; i++)
            {
                dst[i] = (int)(samples[i] << wasted);
            }
        }

        private void ReadResidual(long[] samples, int bs, int order)
        {
            int method = (int)_bits.ReadBits(2);
            if (method > 1)
            {
                throw new InvalidDataException("reserved residual method");
            }
            int paramBits = method == 0 ? 4 : 5;
            int escape = method == 0 ? 15 : 31;
            if (method == 1)
            {
                a.Rice2Partitions++;
            }
            int partOrder = (int)_bits.ReadBits(4);
            a.MaxRicePartitionOrder = Math.Max(a.MaxRicePartitionOrder, partOrder);
            int parts = 1 << partOrder;
            int perPart = bs >> partOrder;
            if (perPart < order || (perPart << partOrder) != bs)
            {
                throw new InvalidDataException("bad partition order");
            }
            int idx = order;
            for (int p = 0; p < parts; p++)
            {
                int n = p == 0 ? perPart - order : perPart;
                int k = (int)_bits.ReadBits(paramBits);
                if (k == escape)
                {
                    a.EscapedPartitions++;
                    int raw = (int)_bits.ReadBits(5);
                    for (int i = 0; i < n; i++)
                    {
                        samples[idx++] = _bits.ReadSigned(raw);
                    }
                    continue;
                }
                for (int i = 0; i < n; i++)
                {
                    ulong q = _bits.ReadUnary();
                    ulong u = (q << k) | _bits.ReadBits(k);
                    samples[idx++] = (long)(u >> 1) ^ -(long)(u & 1);
                }
            }
        }

        private static void RestoreFixed(long[] s, int bs, int order)
        {
            switch (order)
            {
                case 1:
                    for (int i = 1; i < bs; i++)
                    {
                        s[i] += s[i - 1];
                    }
                    break;
                case 2:
                    for (int i = 2; i < bs; i++)
                    {
                        s[i] += (2 * s[i - 1]) - s[i - 2];
                    }
                    break;
                case 3:
                    for (int i = 3; i < bs; i++)
                    {
                        s[i] += (3 * s[i - 1]) - (3 * s[i - 2]) + s[i - 3];
                    }
                    break;
                case 4:
                    for (int i = 4; i < bs; i++)
                    {
                        s[i] += (4 * s[i - 1]) - (6 * s[i - 2]) + (4 * s[i - 3]) - s[i - 4];
                    }
                    break;
            }
        }

        private static void RestoreLpc(long[] s, int bs, ReadOnlySpan<int> coefs, int shift)
        {
            int order = coefs.Length;
            for (int i = order; i < bs; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++)
                {
                    sum += coefs[j] * s[i - 1 - j];
                }
                s[i] += sum >> shift;
            }
        }

        private void Decorrelate(int code, int bs)
        {
            int[] l = _ch[0], r = _ch[1];
            switch (code)
            {
                case 8: // left/side
                    for (int i = 0; i < bs; i++)
                    {
                        r[i] = l[i] - r[i];
                    }
                    break;
                case 9: // side/right
                    for (int i = 0; i < bs; i++)
                    {
                        l[i] += r[i];
                    }
                    break;
                case 10: // mid/side
                    for (int i = 0; i < bs; i++)
                    {
                        long side = r[i];
                        long mid = ((long)l[i] << 1) | (side & 1);
                        l[i] = (int)((mid + side) >> 1);
                        r[i] = (int)((mid - side) >> 1);
                    }
                    break;
            }
        }

        private void Hash(int nch, int bs, int bps, bool hash)
        {
            int width = (bps + 7) / 8;
            int need = nch * bs * width;
            if (_pcm.Length < need)
            {
                _pcm = new byte[need];
            }
            int p = 0;
            for (int i = 0; i < bs; i++)
            {
                for (int c = 0; c < nch; c++)
                {
                    int v = _ch[c][i];
                    for (int k = 0; k < width; k++)
                    {
                        _pcm[p++] = (byte)(v >> (8 * k));
                    }
                }
            }
            if (hash)
            {
                _md5.AppendData(_pcm, 0, need);
            }
            pcmOutput?.Write(_pcm, 0, need);
        }
    }
}
