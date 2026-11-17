using System.Text;

namespace Ces.Spot.Mp3;

/// <summary>Walks every frame of an MPEG audio stream and gathers statistics.</summary>
public static class Mp3Analyzer
{
    private const int AncillarySampleFrames = 64;
    private const int AncillarySampleMax = 4096;

    /// <summary>Analyzes the audio in <paramref name="buf"/>; <paramref name="fileOffset"/> is the file position of buf[0].</summary>
    public static Mp3Analysis? Analyze(ReadOnlySpan<byte> buf, long fileOffset, CancellationToken ct = default)
    {
        int first = FindFrame(buf, 0, null);
        if (first < 0)
        {
            return null;
        }
        MpegHeader.TryParse(buf[first..], out var reference);
        var a = new Mp3Analysis { FirstHeader = reference, FirstFramePosition = fileOffset + first, Emphasis = reference.Emphasis };
        var md = new MainDataTracker();
        long reservoirSum = 0;
        int pos = first;
        bool firstFrame = true;
        int tagFramePos = -1, tagFrameLen = 0;

        while (pos + 4 <= buf.Length)
        {
            if ((a.Frames & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (!MpegHeader.TryParse(buf[pos..], out var h) || !h.IsCompatibleWith(reference))
            {
                int next = FindFrame(buf, pos + 1, reference);
                if (next < 0)
                {
                    a.TrailingBytes = buf.Length - pos;
                    break;
                }
                a.SyncErrors++;
                a.SkippedBytes += next - pos;
                pos = next;
                md.Reset();
                continue;
            }
            int len = h.BitrateKbps == 0 ? FreeFormatLength(buf, pos, h) : h.FrameLength;
            if (h.BitrateKbps == 0)
            {
                a.FreeFormat = true;
            }
            if (len <= 4 || pos + len > buf.Length)
            {
                a.BadLastFrame = true;
                a.TrailingBytes = buf.Length - pos;
                break;
            }
            var frame = buf.Slice(pos, len);
            if (firstFrame)
            {
                firstFrame = false;
                if (TryReadTagFrame(frame, h, a))
                {
                    tagFramePos = pos;
                    tagFrameLen = len;
                    pos += len;
                    continue;
                }
            }

            a.Frames++;
            a.AudioBytes += len;
            a.BitrateHistogram[h.BitrateKbps] = a.BitrateHistogram.GetValueOrDefault(h.BitrateKbps) + 1;
            a.FrameBitrates.Add((short)h.BitrateKbps);
            a.ModeCounts[h.Mode] = a.ModeCounts.GetValueOrDefault(h.Mode) + 1;
            if (h.Mode == ChannelMode.JointStereo)
            {
                if (h.Layer == 3 && (h.ModeExtension & 2) != 0)
                {
                    a.MidSideFrames++;
                }
                if (h.Layer == 3 ? (h.ModeExtension & 1) != 0 : h.ModeExtension != 0)
                {
                    a.IntensityFrames++;
                }
            }
            if (h.Padding)
            {
                a.PaddedFrames++;
            }
            if (h.Private)
            {
                a.PrivateBitFrames++;
            }
            if (h.Copyright)
            {
                a.CopyrightFrames++;
            }
            if (h.Original)
            {
                a.OriginalFrames++;
            }
            if (h.HasCrc)
            {
                a.CrcFrames++;
            }
            if (h.Layer == 3)
            {
                int mdb = ReadLayer3(frame, h, a, md, pos);
                a.FrameReservoir.Add((short)mdb);
                reservoirSum += mdb;
                a.Layer3.MaxReservoir = Math.Max(a.Layer3.MaxReservoir, mdb);
            }
            pos += len;
        }

        md.Finish(a);
        a.AncillarySignature = md.Signature();
        if (a.Frames > 0)
        {
            int spf = reference.SamplesPerFrame;
            a.DurationSeconds = (double)a.Frames * spf / reference.SampleRate;
            a.AverageBitrateKbps = a.AudioBytes * 8.0 / a.DurationSeconds / 1000.0;
            if (a.FrameReservoir.Count > 0)
            {
                a.Layer3.AverageReservoir = (double)reservoirSum / a.FrameReservoir.Count;
            }
        }
        VerifyMusicCrc(buf, a, tagFramePos, tagFrameLen);
        return a;
    }

    private static bool TryReadTagFrame(ReadOnlySpan<byte> frame, in MpegHeader h, Mp3Analysis a)
    {
        int xingOffset = 4 + (h.HasCrc ? 2 : 0) + (h.Layer == 3 ? h.SideInfoSize : 0);
        // LAME places the tag right after the side info even when the frame has a CRC.
        var xing = XingHeader.TryParse(frame, xingOffset) ?? (h.HasCrc ? XingHeader.TryParse(frame, xingOffset - 2) : null);
        if (xing is not null)
        {
            a.Xing = xing;
            return true;
        }
        var vbri = VbriHeader.TryParse(frame);
        if (vbri is not null)
        {
            a.Vbri = vbri;
            return true;
        }
        return false;
    }

    private static void VerifyMusicCrc(ReadOnlySpan<byte> buf, Mp3Analysis a, int tagPos, int tagLen)
    {
        var lame = a.Xing?.Lame;
        if (lame is null || !lame.HasExtendedInfo || lame.MusicLength == 0 || tagPos < 0)
        {
            return;
        }
        long end = tagPos + (long)lame.MusicLength;
        int from = tagPos + tagLen;
        if (end > buf.Length || end <= from)
        {
            lame.MusicCrcOk = false;
            return;
        }
        lame.MusicCrcOk = Crc16Arc.Compute(buf[from..(int)end]) == lame.MusicCrc;
    }

    /// <summary>Reads Layer III side info; returns main_data_begin.</summary>
    private static int ReadLayer3(ReadOnlySpan<byte> frame, in MpegHeader h, Mp3Analysis a, MainDataTracker md, int framePos)
    {
        int crcLen = h.HasCrc ? 2 : 0;
        int sideSize = h.SideInfoSize;
        if (4 + crcLen + sideSize > frame.Length)
        {
            return 0;
        }
        var side = frame.Slice(4 + crcLen, sideSize);
        if (h.HasCrc)
        {
            Span<byte> crcData = stackalloc byte[2 + 32];
            crcData[0] = frame[2];
            crcData[1] = frame[3];
            side.CopyTo(crcData[2..]);
            ushort stored = (ushort)((frame[4] << 8) | frame[5]);
            if (Crc16Mpeg.Compute(crcData[..(2 + sideSize)]) != stored)
            {
                a.CrcErrors++;
            }
        }

        var l3 = a.Layer3;
        var r = new BitReader(side);
        bool v1 = h.Version == MpegVersion.Mpeg1;
        int nch = h.Channels;
        int mainDataBegin = (int)r.ReadBits(v1 ? 9 : 8);
        r.Skip(v1 ? (nch == 1 ? 5 : 3) : (nch == 1 ? 1 : 2));
        if (v1)
        {
            int bands = 0;
            for (int ch = 0; ch < nch; ch++)
            {
                bands += System.Numerics.BitOperations.PopCount(r.ReadBits(4));
            }
            l3.ScfsiBands += bands;
            if (bands > 0)
            {
                l3.ScfsiFrames++;
            }
        }
        long part23Bits = 0;
        int granules = v1 ? 2 : 1;
        for (int gr = 0; gr < granules; gr++)
        {
            for (int ch = 0; ch < nch; ch++)
            {
                part23Bits += r.ReadBits(12);
                int bigValues = (int)r.ReadBits(9);
                r.Skip(8); // global_gain
                r.Skip(v1 ? 4 : 9); // scalefac_compress
                bool windowSwitching = r.ReadBit();
                if (windowSwitching)
                {
                    int blockType = (int)r.ReadBits(2);
                    bool mixed = r.ReadBit();
                    r.Skip(10 + 9); // table_select x2, subblock_gain x3
                    switch (blockType)
                    {
                        case 1:
                            l3.StartBlocks++;
                            break;
                        case 2:
                            l3.ShortBlocks++;
                            break;
                        case 3:
                            l3.StopBlocks++;
                            break;
                        default:
                            l3.LongBlocks++; // block_type 0 with window switching is invalid; count as long
                            break;
                    }
                    if (mixed)
                    {
                        l3.MixedBlocks++;
                    }
                }
                else
                {
                    r.Skip(15 + 4 + 3); // table_select x3, region0/1_count
                    l3.LongBlocks++;
                }
                if (v1 && r.ReadBit())
                {
                    l3.Preflag++;
                }
                if (r.ReadBit())
                {
                    l3.ScalefacScale++;
                }
                r.Skip(1); // count1table_select
                l3.Granules++;
                if (bigValues > 288)
                {
                    l3.BigValuesViolations++;
                }
                l3.BigValuesHistogram[Math.Min(bigValues, 288) / 16]++;
            }
        }
        int capacity = frame.Length - 4 - crcLen - sideSize;
        md.AddFrame(framePos + 4 + crcLen + sideSize, capacity, mainDataBegin, part23Bits, frame, 4 + crcLen + sideSize, l3);
        return mainDataBegin;
    }

    /// <summary>Finds the next frame whose following frames line up too (avoids false syncs).</summary>
    internal static int FindFrame(ReadOnlySpan<byte> buf, int from, MpegHeader? reference)
    {
        for (int i = from; i + 4 <= buf.Length; i++)
        {
            if (buf[i] != 0xFF || !MpegHeader.TryParse(buf[i..], out var h))
            {
                continue;
            }
            if (reference is MpegHeader refH && !h.IsCompatibleWith(refH))
            {
                continue;
            }
            if (h.BitrateKbps == 0)
            {
                if (FreeFormatLength(buf, i, h) > 0)
                {
                    return i;
                }
                continue;
            }
            if (ConfirmChain(buf, i, h, 3))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool ConfirmChain(ReadOnlySpan<byte> buf, int pos, MpegHeader h, int count)
    {
        for (int k = 0; k < count; k++)
        {
            int next = pos + h.FrameLength;
            if (next + 4 > buf.Length)
            {
                // Running into the end of the data is fine as long as the frame fits.
                return next <= buf.Length;
            }
            if (!MpegHeader.TryParse(buf[next..], out var nh) || !nh.IsCompatibleWith(h) || nh.BitrateKbps == 0)
            {
                return false;
            }
            pos = next;
            h = nh;
        }
        return true;
    }

    /// <summary>Free-format frames: the length is the distance to the next header with the same fixed bits.</summary>
    private static int FreeFormatLength(ReadOnlySpan<byte> buf, int pos, in MpegHeader h)
    {
        int min = h.Layer == 1 ? 32 : 48;
        for (int i = pos + min; i + 4 <= buf.Length && i - pos < 8192; i++)
        {
            if (buf[i] == 0xFF && buf[i + 1] == buf[pos + 1] && (buf[i + 2] & 0xF0) == 0 && (buf[i + 2] & 0x0C) == (buf[pos + 2] & 0x0C)
                && MpegHeader.TryParse(buf[i..], out var nh) && nh.IsCompatibleWith(h))
            {
                return i - pos;
            }
        }
        return buf.Length - pos >= min ? buf.Length - pos : 0;
    }

    /// <summary>
    /// Follows the Layer III bit reservoir: main data of each frame starts main_data_begin bytes before the
    /// frame's own data slot. Bytes between one frame's main data and the next are ancillary data.
    /// </summary>
    private sealed class MainDataTracker
    {
        private readonly List<(long Cum, int BufPos, int Length, byte[] Data)> _segments = [];
        private readonly List<byte> _ancillary = [];
        private long _cum;
        private long _prevEnd = -1;
        private int _frames;

        public void Reset()
        {
            _cum = 0;
            _prevEnd = -1;
            _segments.Clear();
        }

        public void AddFrame(int bufPos, int capacity, int mainDataBegin, long part23Bits, ReadOnlySpan<byte> frame, int dataOffset, Layer3Stats l3)
        {
            if (capacity < 0)
            {
                capacity = 0;
            }
            long start = _cum - mainDataBegin;
            long end = start + ((part23Bits + 7) / 8);
            if (start < 0)
            {
                l3.ReservoirErrors++;
                start = 0;
            }
            if (_prevEnd >= 0)
            {
                if (start < _prevEnd)
                {
                    l3.ReservoirErrors++;
                }
                else
                {
                    Ancillary(_prevEnd, start, l3);
                }
            }
            if (end > _cum + capacity)
            {
                l3.ReservoirErrors++;
                end = _cum + capacity;
            }
            _segments.Add((_cum, bufPos, capacity, frame.Slice(dataOffset, capacity).ToArray()));
            if (_segments.Count > 12)
            {
                _segments.RemoveAt(0);
            }
            _cum += capacity;
            _prevEnd = end;
            _frames++;
        }

        public void Finish(Mp3Analysis a)
        {
            if (_prevEnd >= 0 && _prevEnd < _cum)
            {
                Ancillary(_prevEnd, _cum, a.Layer3);
            }
        }

        private void Ancillary(long from, long to, Layer3Stats l3)
        {
            l3.AncillaryBytes += to - from;
            bool sample = _frames < AncillarySampleFrames && _ancillary.Count < AncillarySampleMax;
            foreach (var seg in _segments)
            {
                long s = Math.Max(from, seg.Cum);
                long e = Math.Min(to, seg.Cum + seg.Length);
                for (long p = s; p < e; p++)
                {
                    byte b = seg.Data[p - seg.Cum];
                    if (b == 0)
                    {
                        l3.AncillaryZeroBytes++;
                    }
                    else if (b == 0x55)
                    {
                        l3.AncillaryUBytes++;
                    }
                    if (sample && _ancillary.Count < AncillarySampleMax)
                    {
                        _ancillary.Add(b);
                    }
                }
            }
            if (sample)
            {
                _ancillary.Add(0); // separator between frames
            }
        }

        /// <summary>Printable text runs of 4+ characters found in the sampled ancillary data.</summary>
        public string Signature()
        {
            var runs = new List<string>();
            var sb = new StringBuilder();
            foreach (byte b in _ancillary)
            {
                if (b is >= 0x20 and < 0x7F && b != 0x55)
                {
                    sb.Append((char)b);
                    continue;
                }
                if (sb.Length >= 4 && !runs.Contains(sb.ToString()))
                {
                    runs.Add(sb.ToString());
                }
                sb.Clear();
            }
            if (sb.Length >= 4 && !runs.Contains(sb.ToString()))
            {
                runs.Add(sb.ToString());
            }
            return string.Join(" | ", runs.Take(8));
        }
    }
}
