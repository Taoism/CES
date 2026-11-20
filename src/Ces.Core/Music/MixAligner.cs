using System.Globalization;
using System.Runtime.InteropServices;
using Ces.Analysis;
using Ces.Dsp;
using Ces.Model;
using Ces.Tools;

namespace Ces.Music;

/// <summary>
/// One song of the reference against the same song in another mix: where it is (source time = reference time ×
/// <see cref="Speed"/> + <see cref="Offset"/>), or null when no stretch of it lines up (another recording or edit).
/// </summary>
public sealed record SongMatch(int Number, double Start, double Duration, double? Offset, double Speed, int Windows, int Matched)
{
    /// <summary>
    /// Stretches of the song with their own line (another edit inside the song: a longer intro, a bar cut, a later
    /// fade); the first is the song's own <see cref="Offset"/> and <see cref="Speed"/>. Empty when unmatched.
    /// </summary>
    public List<SongPiece> Pieces { get; init; } = [];
}

/// <summary>From reference time <see cref="Start"/> on: source time = reference time × <see cref="Speed"/> + <see cref="Offset"/>.</summary>
public sealed record SongPiece(double Start, double Offset, double Speed, int Matched);

/// <summary>
/// Lines up two mixes of one album (stereo and mono, a remix, an Atmos fold-down) song by song. Different mixes share
/// no waveform — other balance, panning, effects, often another tape speed — but they share where the notes start:
/// the onset envelope (positive spectral flux, 100 per second) of each 8-second stretch of a song is found in the
/// same song of the other mix, and a straight line through the stretches that agree gives the song's offset and its
/// speed (a mono mix made on another machine runs a few tenths of a percent off).
/// </summary>
public static class MixAligner
{
    public const int Fps = 100;
    private const int Rate = 16000;
    private const int Hop = Rate / Fps;
    private const int Frame = 512;
    private const int LowBin = 4;    // 125 Hz
    private const int HighBin = 200; // 6.25 kHz
    private const int Win = 8 * Fps;
    private const int Step = 5 * Fps;
    private const double Margin = 20;

    /// <summary>A window on the line within this (seconds) agrees with it.</summary>
    private const double Tolerance = 0.03;

    /// <summary>Both titles are albums (lossless, songs as chapters) with the same number of songs.</summary>
    public static bool Applies(MediaTitle reference, MediaTitle other) =>
        Songs(reference) is { Count: >= 3 } a && Songs(other) is { } b && a.Count == b.Count;

    private static List<MusicTrack>? Songs(MediaTitle t)
    {
        if (t.Info is not { } info)
            return null;
        var path = t.Segments.FirstOrDefault()?.Url ?? t.Name;
        return MusicAnalyzer.IsMusic(path, t, info) ? MusicAnalyzer.Tracks(t, info) : null;
    }

    public static async Task<List<SongMatch>?> AlignAsync(FfTools tools, MediaTitle reference, MediaTitle other, string tempDir, IProgress<string>? progress, CancellationToken ct)
    {
        if (Songs(reference) is not { } ra || Songs(other) is not { } oa || ra.Count != oa.Count)
            return null;
        progress?.Report("Decoding both mixes…");
        var fluxes = await Task.WhenAll(FluxAsync(tools, reference, tempDir, ct), FluxAsync(tools, other, tempDir, ct)).ConfigureAwait(false);
        progress?.Report("Matching song by song…");
        return ra.Zip(oa).Select(p => Align(fluxes[0], fluxes[1], p.First, p.Second, ct)).ToList();
    }

    /// <summary>One song: its 8-second stretches found in the other mix's song (± <see cref="Margin"/> s), then the line.</summary>
    internal static SongMatch Align(float[] refFlux, float[] otherFlux, MusicTrack r, MusicTrack o, CancellationToken ct)
    {
        int r0 = (int)(r.Start * Fps), r1 = Math.Min(refFlux.Length, (int)((r.Start + r.Duration) * Fps));
        int o0 = Math.Max(0, (int)((o.Start - Margin) * Fps)), o1 = Math.Min(otherFlux.Length, (int)((o.Start + o.Duration + Margin) * Fps));
        if (r1 - r0 < Win || o1 - o0 < Win)
            return new SongMatch(r.Number, r.Start, r.Duration, null, 1, 0, 0);
        var windows = MotionSignature.AlignWindows(refFlux[r0..r1], otherFlux[o0..o1], Win, Step, ct);
        // reference time of the window's middle → other time of the same moment
        var pts = windows.Where(w => w.Ncc >= 0.2)
            .Select(w => (T: (r0 + w.RefFrame + Win / 2.0) / Fps, S: (o0 + w.RefFrame + w.Shift + Win / 2.0) / Fps, W: w.Ncc)).ToList();
        var pieces = Pieces(pts, r.Start);
        var matched = pieces.Sum(x => x.Matched);
        if (pieces.Count == 0 || matched < Math.Max(3, windows.Count * 0.3))
            return new SongMatch(r.Number, r.Start, r.Duration, null, 1, windows.Count, matched);
        return new SongMatch(r.Number, r.Start, r.Duration, pieces[0].Offset, pieces[0].Speed, windows.Count, matched) { Pieces = pieces };
    }

    /// <summary>
    /// Lines through the windows, the strongest first: each claims the stretch of the song its windows span, and the
    /// next is looked for in what is left. In time order, a stretch starts halfway between its windows and the
    /// previous stretch's (the first at the song's start).
    /// </summary>
    internal static List<SongPiece> Pieces(IReadOnlyList<(double T, double S, double W)> pts, double songStart)
    {
        var left = pts.ToList();
        var found = new List<(double From, double To, double Offset, double Speed, int Count)>();
        while (left.Count >= 3 && found.Count < 6)
        {
            if (FitLine(left, Tolerance) is not { Inliers: >= 3 } l)
                break;
            var on = left.Where(p => Math.Abs(p.T * l.Speed + l.Offset - p.S) <= Tolerance).ToList();
            double from = on.Min(p => p.T), to = on.Max(p => p.T);
            // a line that crosses a stretch already claimed is a chance alignment
            if (found.Any(f => from < f.To && to > f.From))
                break;
            found.Add((from, to, l.Offset, l.Speed, l.Inliers));
            left.RemoveAll(p => p.T >= from && p.T <= to);
        }
        found.Sort((a, b) => a.From.CompareTo(b.From));
        var pieces = new List<SongPiece>();
        for (var i = 0; i < found.Count; i++)
        {
            var start = i == 0 ? songStart : (found[i - 1].To + found[i].From) / 2;
            pieces.Add(new SongPiece(start, found[i].Offset, found[i].Speed, found[i].Count));
        }
        return pieces;
    }

    /// <summary>
    /// The line s = t × speed + offset through the most points (within <paramref name="tol"/>), from every pair of
    /// points, then least squares over the ones on it. Speeds beyond ±3 % are not the same song.
    /// </summary>
    internal static (double Offset, double Speed, int Inliers)? FitLine(IReadOnlyList<(double T, double S, double W)> pts, double tol)
    {
        if (pts.Count < 2)
            return null;
        var best = (Count: 0, Weight: 0.0, Offset: 0.0, Speed: 1.0);
        for (var i = 0; i < pts.Count; i++)
        {
            for (var j = i + 1; j < pts.Count; j++)
            {
                var dt = pts[j].T - pts[i].T;
                if (dt < 1)
                    continue;
                var speed = (pts[j].S - pts[i].S) / dt;
                if (Math.Abs(speed - 1) > 0.03)
                    continue;
                var off = pts[i].S - pts[i].T * speed;
                var on = pts.Where(p => Math.Abs(p.T * speed + off - p.S) <= tol).ToList();
                var weight = on.Sum(p => p.W);
                if (on.Count > best.Count || (on.Count == best.Count && weight > best.Weight))
                    best = (on.Count, weight, off, speed);
            }
        }
        if (best.Count < 2)
            return null;
        var inl = pts.Where(p => Math.Abs(p.T * best.Speed + best.Offset - p.S) <= tol).ToList();
        double mt = inl.Average(p => p.T), ms = inl.Average(p => p.S);
        double sxx = inl.Sum(p => (p.T - mt) * (p.T - mt)), sxy = inl.Sum(p => (p.T - mt) * (p.S - ms));
        var sp = sxx > 1e-9 ? sxy / sxx : best.Speed;
        return (ms - mt * sp, sp, inl.Count);
    }

    /// <summary>The title's first audio track as mono at 16 kHz, turned into its onset envelope while it decodes.</summary>
    private static async Task<float[]> FluxAsync(FfTools tools, MediaTitle title, string tempDir, CancellationToken ct)
    {
        var info = title.Info;
        var a = info == null ? null : MusicAnalyzer.PickStream(info);
        var input = InputBuilder.Input(title, 0, null, tempDir);
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        args.AddRange(InputBuilder.WithoutHwaccel(input.Args));
        args.AddRange(["-map", "0:a:" + (a?.KindIndex ?? 0).ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn", "-af", input.ATrimFilter + "anull",
            "-ac", "1", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-acodec", "pcm_f32le", "-"]);
        var sink = new FluxSink();
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct, null, null, sink).ConfigureAwait(false);
        if (!res.Ok && sink.Count == 0)
            throw new InvalidDataException("the audio could not be decoded: " + res.StdErr.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim());
        return sink.Result();
    }

    /// <summary>Onset envelope of mono samples (positive change of the log spectrum, 125 Hz – 6.25 kHz, per hop).</summary>
    internal static float[] Flux(ReadOnlySpan<float> samples)
    {
        var f = new FluxSink();
        f.Write(MemoryMarshal.AsBytes(samples));
        return f.Result();
    }

    private sealed class FluxSink : Stream
    {
        private static readonly double[] Hann = Enumerable.Range(0, Frame).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Frame)).ToArray();
        private readonly float[] _ring = new float[Frame];
        private readonly byte[] _carry = new byte[4];
        private readonly double[] _re = new double[Frame];
        private readonly double[] _im = new double[Frame];
        private readonly double[] _last = new double[HighBin - LowBin];
        private readonly List<float> _out = [];
        private int _carried;
        private long _n;
        private bool _first = true;

        public long Count => _n;

        public float[] Result() => [.. _out];

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (_carried > 0 && buffer.Length > 0)
            {
                _carry[_carried++] = buffer[0];
                buffer = buffer[1..];
                if (_carried == 4)
                {
                    Add(BitConverter.ToSingle(_carry));
                    _carried = 0;
                }
            }
            var whole = buffer.Length / 4 * 4;
            foreach (var v in MemoryMarshal.Cast<byte, float>(buffer[..whole]))
                Add(v);
            buffer[whole..].CopyTo(_carry);
            _carried = buffer.Length - whole;
        }

        private void Add(float v)
        {
            _ring[_n % Frame] = v * 32768;
            _n++;
            if (_n >= Frame && (_n - Frame) % Hop == 0)
                Spectrum();
        }

        private void Spectrum()
        {
            var start = _n - Frame;
            for (var i = 0; i < Frame; i++)
            {
                _re[i] = _ring[(start + i) % Frame] * Hann[i];
                _im[i] = 0;
            }
            Fft.Transform(_re, _im);
            double flux = 0;
            for (var k = LowBin; k < HighBin; k++)
            {
                var l = Math.Log(1 + Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]));
                if (!_first && l > _last[k - LowBin])
                    flux += l - _last[k - LowBin];
                _last[k - LowBin] = l;
            }
            _first = false;
            _out.Add((float)flux);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _n;
        public override long Position { get => _n; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
