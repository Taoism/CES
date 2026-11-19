namespace Ces.Analysis;

/// <summary>What one Dolby Vision RPU says about its frame (PQ values are 12-bit, 0–4095).</summary>
public sealed class DoviRpuData
{
    public int SourceMinPq { get; init; }
    public int SourceMaxPq { get; init; }
    public int SourceDiagonal { get; init; }
    public bool SceneRefresh { get; init; }

    /// <summary>Level 1: the shot's minimum, maximum and average (maxRGB) PQ.</summary>
    public (int Min, int Max, int Avg)? Level1 { get; init; }

    /// <summary>Level 2: target displays (PQ) the colourist made trims for.</summary>
    public IReadOnlyList<int> TrimTargetsPq { get; init; } = [];

    /// <summary>Level 6: mastering display max / min (nits, min in 0.0001 nits) and MaxCLL / MaxFALL.</summary>
    public (int MaxDisplay, int MinDisplay, int MaxCll, int MaxFall)? Level6 { get; init; }
}

/// <summary>
/// Dolby Vision RPU reader (HEVC NAL unit type 62) up to the display-management extension blocks, after the
/// public bitstream layout that FFmpeg and dovi_tool also follow. FFmpeg parses the same data but does not print the
/// extension blocks, which carry the per-shot (level 1) values.
/// </summary>
public static class DoviRpu
{
    /// <summary>The RPU NAL units of an Annex B stream, one per access unit, in decode order.</summary>
    public static List<byte[]> RpuNals(ReadOnlySpan<byte> annexB)
    {
        var list = new List<byte[]>();
        var starts = new List<int>();
        for (var i = 0; i + 2 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                starts.Add(i + 3);
                i += 2;
            }
        }
        for (var k = 0; k < starts.Count; k++)
        {
            var s = starts[k];
            if (s + 2 >= annexB.Length || ((annexB[s] >> 1) & 0x3F) != 62)
                continue;
            var e = k + 1 < starts.Count ? starts[k + 1] - 3 : annexB.Length;
            while (e > s && annexB[e - 1] == 0)
                e--;
            list.Add(annexB[s..e].ToArray());
        }
        return list;
    }

    /// <summary>Parses one RPU NAL unit (with its 2-byte NAL header); null when it is not a readable RPU.</summary>
    public static DoviRpuData? Parse(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 8 || ((nal[0] >> 1) & 0x3F) != 62)
            return null;
        try
        {
            return Read(new BitReader(Unescape(nal[2..])));
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static DoviRpuData? Read(BitReader b)
    {
        if (b.U(8) != 25 || b.U(6) != 2)
            return null;
        var format = b.U(11);
        b.U(4); // vdr_rpu_profile
        b.U(4); // vdr_rpu_level
        if (b.U(1) == 0) // vdr_seq_info_present: every RPU in practice carries it
            return null;
        b.U(1); // chroma_resampling_explicit_filter_flag
        var coefType = b.U(2);
        var denom = coefType == 0 ? (int)b.Ue() : 32;
        if (denom > 32)
            return null;
        b.U(2); // vdr_rpu_normalized_idc
        b.U(1); // bl_video_full_range_flag
        int bl = 10, el = 10;
        var disableResidual = true;
        if ((format & 0x700) == 0)
        {
            bl = (int)b.Ue() + 8;
            el = (int)(b.Ue() & 0xFF) + 8;
            b.Ue(); // vdr_bit_depth_minus8
            b.U(1); // spatial_resampling_filter_flag
            b.U(3); // reserved
            b.U(1); // el_spatial_resampling_filter_flag
            disableResidual = b.U(1) == 1;
        }
        if (bl > 16 || el > 16)
            return null;
        var dmPresent = b.U(1) == 1;
        if (b.U(1) == 1) // use_prev_vdr_rpu: the mapping is not repeated (rare; not followed)
            return null;
        b.Ue(); // vdr_rpu_id
        b.Ue(); // mapping_color_space
        b.Ue(); // mapping_chroma_format_idc
        var pieces = new int[3];
        for (var c = 0; c < 3; c++)
        {
            var pivots = (int)b.Ue() + 2;
            if (pivots > 9)
                return null;
            for (var i = 0; i < pivots; i++)
                b.U(bl);
            pieces[c] = pivots - 1;
        }
        var nlqMethod = -1;
        if ((format & 0x700) == 0 && !disableResidual)
        {
            nlqMethod = (int)b.U(3);
            b.U(bl);
            b.U(bl);
        }
        b.Ue(); // num_x_partitions_minus1
        b.Ue(); // num_y_partitions_minus1
        for (var c = 0; c < 3; c++)
        {
            for (var p = 0; p < pieces[c]; p++)
                SkipMapping(b, coefType, denom);
        }
        if (nlqMethod >= 0)
        {
            for (var c = 0; c < 3; c++)
            {
                b.U(el); // nlq_offset
                Coef(b, coefType, denom, signed: false); // vdr_in_max
                if (nlqMethod == 0)
                {
                    Coef(b, coefType, denom, signed: false); // linear_deadzone_slope
                    Coef(b, coefType, denom, signed: false); // linear_deadzone_threshold
                }
            }
        }
        if (!dmPresent)
            return null;
        b.Ue(); // affected_dm_metadata_id
        b.Ue(); // current_dm_metadata_id
        var sceneRefresh = b.Ue() != 0;
        b.Skip(9 * 16 + 3 * 32 + 9 * 16); // ycc_to_rgb, offsets, rgb_to_lms
        b.Skip(16 + 16 + 16 + 32); // signal_eotf and its parameters
        b.Skip(5 + 2 + 2 + 2); // signal bit depth, colour space, chroma format, full range
        var srcMin = (int)b.U(12);
        var srcMax = (int)b.U(12);
        var diagonal = (int)b.U(10);
        var blocks = b.Ue();
        if (blocks > 64)
            return null;
        (int, int, int)? l1 = null;
        (int, int, int, int)? l6 = null;
        var trims = new List<int>();
        if (blocks > 0)
        {
            b.Align();
            for (var i = 0; i < blocks; i++)
            {
                var length = b.Ue();
                if (length > 1024)
                    return null;
                var level = b.U(8);
                var start = b.Position;
                switch (level)
                {
                    case 1:
                        l1 = ((int)b.U(12), (int)b.U(12), (int)b.U(12));
                        break;
                    case 2:
                        trims.Add((int)b.U(12));
                        break;
                    case 6:
                        l6 = ((int)b.U(16), (int)b.U(16), (int)b.U(16), (int)b.U(16));
                        break;
                }
                b.Seek(start + (int)length * 8);
            }
        }
        if (l1 is var (mn, mx, av) && !(mn <= av && av <= mx))
            return null;
        return new DoviRpuData
        {
            SourceMinPq = srcMin,
            SourceMaxPq = srcMax,
            SourceDiagonal = diagonal,
            SceneRefresh = sceneRefresh,
            Level1 = l1,
            TrimTargetsPq = trims,
            Level6 = l6,
        };
    }

    /// <summary>One piece of a component's mapping curve: a polynomial or an MMR (multivariate) predictor.</summary>
    private static void SkipMapping(BitReader b, uint coefType, int denom)
    {
        var idc = b.Ue();
        if (idc == 0)
        {
            var order = b.Ue() + 1;
            if (order > 2)
                throw new InvalidDataException("polynomial order");
            if (order == 1 && b.U(1) == 1)
                throw new InvalidDataException("linear interpolation"); // not used by real encoders
            for (var i = 0; i <= order; i++)
                Coef(b, coefType, denom, signed: true);
        }
        else if (idc == 1)
        {
            var order = b.U(2) + 1;
            if (order > 3)
                throw new InvalidDataException("MMR order");
            Coef(b, coefType, denom, signed: true);
            for (var i = 0; i < order * 7; i++)
                Coef(b, coefType, denom, signed: true);
        }
        else
        {
            throw new InvalidDataException("mapping idc");
        }
    }

    private static void Coef(BitReader b, uint coefType, int denom, bool signed)
    {
        if (coefType == 0)
        {
            if (signed)
                b.Se();
            else
                b.Ue();
        }
        b.Skip(denom);
    }

    /// <summary>Drops the emulation-prevention bytes (00 00 03 → 00 00).</summary>
    internal static byte[] Unescape(ReadOnlySpan<byte> b)
    {
        var o = new byte[b.Length];
        int n = 0, zeros = 0;
        foreach (var x in b)
        {
            if (zeros >= 2 && x == 3)
            {
                zeros = 0;
                continue;
            }
            o[n++] = x;
            zeros = x == 0 ? zeros + 1 : 0;
        }
        return o[..n];
    }

    private sealed class BitReader(byte[] data)
    {
        public int Position { get; private set; }

        public uint U(int n)
        {
            if (Position + n > data.Length * 8)
                throw new EndOfStreamException();
            uint v = 0;
            for (var i = 0; i < n; i++, Position++)
                v = (v << 1) | (uint)((data[Position >> 3] >> (7 - (Position & 7))) & 1);
            return v;
        }

        public uint Ue()
        {
            var zeros = 0;
            while (U(1) == 0)
            {
                if (++zeros > 31)
                    throw new InvalidDataException("exp-Golomb");
            }
            return (uint)((1L << zeros) - 1 + U(zeros));
        }

        public void Se() => Ue();

        public void Skip(int n)
        {
            if (Position + n > data.Length * 8)
                throw new EndOfStreamException();
            Position += n;
        }

        public void Align() => Skip((8 - (Position & 7)) & 7);

        public void Seek(int bit)
        {
            if (bit > data.Length * 8 || bit < 0)
                throw new EndOfStreamException();
            Position = bit;
        }
    }
}
