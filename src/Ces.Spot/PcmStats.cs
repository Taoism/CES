using Ces.Dsp;

namespace Ces.Spot;

/// <summary>
/// What the decoded samples of a lossless file say about its source: the bits really used (16-bit audio padded to
/// 24 bits), the peak, and an averaged spectrum from windows spread over the whole file, whose top edge shows a
/// lossy source (brick-wall lowpass of an MP3/AAC encoder) or upsampling (nothing above the old Nyquist).
/// Fed with interleaved little-endian PCM as it is decoded.
/// </summary>
public sealed class PcmStats(int sampleRate, int channels, int bitsPerSample, long totalFrames, bool floatSamples = false)
{
    public const int WindowSize = 8192;
    private const int Windows = 32;

    private readonly int _bytes = (bitsPerSample + 7) / 8;
    private readonly float[] _window = new float[WindowSize];
    private readonly long[] _starts = WindowStarts(totalFrames);
    private double[]? _power;
    private int _spectra;
    private int _next;
    private int _fill;
    private long _frame;
    private long _orBits;
    private long _peak;
    private double _peakFloat;

    public int SampleRate => sampleRate;
    public int BitsPerSample => bitsPerSample;

    private static long[] WindowStarts(long total)
    {
        if (total <= WindowSize)
            return [0];
        // skip the first and last 5 % (silence, fades)
        var from = total / 20;
        var span = Math.Max(0, total - 2 * from - WindowSize);
        return Enumerable.Range(0, Windows).Select(i => from + span * i / Math.Max(1, Windows - 1)).Distinct().ToArray();
    }

    public void Add(ReadOnlySpan<byte> pcm)
    {
        if (floatSamples)
        {
            AddFloat(pcm);
            return;
        }
        // 8-bit PCM (WAV) is unsigned
        var offset = bitsPerSample <= 8 ? 128L : 0;
        var frameBytes = _bytes * channels;
        var scale = 1.0 / (1L << (bitsPerSample - 1));
        for (var p = 0; p + frameBytes <= pcm.Length; p += frameBytes)
        {
            double mono = 0;
            for (var c = 0; c < channels; c++)
            {
                long v = 0;
                for (var k = 0; k < _bytes; k++)
                    v |= (long)pcm[p + c * _bytes + k] << (8 * k);
                if (offset != 0)
                {
                    v -= offset;
                }
                else
                {
                    // sign-extend from the sample width
                    var shift = 64 - 8 * _bytes;
                    v = (v << shift) >> shift;
                }
                _orBits |= v;
                var a = Math.Abs(v);
                if (a > _peak)
                    _peak = a;
                mono += v * scale;
            }
            Collect((float)(mono / channels));
            _frame++;
        }
    }

    /// <summary>32/64-bit float samples: spectrum and peak only (there are no "bits used" in floating point).</summary>
    private void AddFloat(ReadOnlySpan<byte> pcm)
    {
        var frameBytes = _bytes * channels;
        for (var p = 0; p + frameBytes <= pcm.Length; p += frameBytes)
        {
            double mono = 0;
            for (var c = 0; c < channels; c++)
            {
                var at = pcm.Slice(p + c * _bytes, _bytes);
                double v = _bytes == 8 ? BitConverter.ToDouble(at) : BitConverter.ToSingle(at);
                if (double.IsNaN(v))
                    continue;
                _peakFloat = Math.Max(_peakFloat, Math.Abs(v));
                mono += v;
            }
            Collect((float)(mono / channels));
            _frame++;
        }
    }

    private void Collect(float sample)
    {
        if (_next >= _starts.Length || _frame < _starts[_next])
            return;
        _window[_fill++] = sample;
        if (_fill < WindowSize)
            return;
        var db = Fft.PowerSpectrumDb(_window, WindowSize, WindowSize);
        _power ??= new double[db.Length];
        for (var k = 0; k < db.Length; k++)
            _power[k] += Math.Pow(10, db[k] / 10);
        _spectra++;
        _fill = 0;
        _next++;
        // the next window may already have started (overlapping starts on short files)
        while (_next < _starts.Length && _starts[_next] <= _frame)
            _next++;
    }

    /// <summary>Bits in use: the stream's bits minus the low bits that are zero in every sample (0 for silence).</summary>
    public int UsedBits
    {
        get
        {
            if (_orBits == 0 || floatSamples)
                return 0;
            var zeros = 0;
            while (zeros < bitsPerSample && ((_orBits >> zeros) & 1) == 0)
                zeros++;
            return bitsPerSample - zeros;
        }
    }

    public double PeakDb => floatSamples ? (_peakFloat > 0 ? 20 * Math.Log10(_peakFloat) : double.NegativeInfinity) : _peak == 0 ? double.NegativeInfinity : 20 * Math.Log10(_peak / (double)(1L << (bitsPerSample - 1)));

    /// <summary>Average spectrum in dB (WindowSize / 2 + 1 bins up to Nyquist), or empty when the file was too short.</summary>
    public double[] Spectrum => _power == null ? [] : _power.Select(p => 10 * Math.Log10(Math.Max(p / _spectra, 1e-30))).ToArray();
}
