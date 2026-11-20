using System.Globalization;
using System.Text.RegularExpressions;
using Ces.Disc;
using Ces.Dsp;
using Ces.Model;
using Ces.Probe;
using Ces.Spot;
using Ces.Tools;

namespace Ces.Music;

/// <summary>One song of a music release: where it is in the title and what its audio says about its source.</summary>
public sealed record MusicTrack(int Number, string Name, double Start, double Duration)
{
    public int UsedBits { get; set; }
    public double PeakDb { get; set; } = double.NegativeInfinity;
    public double CutoffHz { get; set; } = double.NaN;
    public string Verdict { get; set; } = "";
    public List<string> Problems { get; } = [];

    /// <summary>Share of frames whose left and right samples are equal (1 = mono carried as stereo), NaN when not stereo.</summary>
    public double SameChannels { get; set; } = double.NaN;

    /// <summary>Level of L−R against L+R in dB (−∞ for identical channels, NaN when not stereo): below −30 dB it is mono content.</summary>
    public double SideDb { get; set; } = double.NaN;
}

/// <summary>
/// Music releases on Blu-ray (Pure Audio, music Blu-rays and their rips): a title whose chapters are songs, with
/// lossless audio (LPCM, TrueHD, DTS-HD MA, FLAC). The whole lossless track is decoded once and each song checked like
/// a FLAC or WAV file in Spot: the bits really used (16-bit padded to 24), upsampling (nothing above the old
/// Nyquist), a lossy source (brick-wall lowpass) and mono carried on two identical channels. Dolby Atmos is reported
/// from the TrueHD stream; its objects are not checked (only the decoded bed).
/// </summary>
public static partial class MusicAnalyzer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>L−R this far below L+R is a mono recording on two channels (a real stereo mix is −10…−20 dB at most).</summary>
    public const double MonoSideDb = -30;

    private static readonly string[] Lossless = ["truehd", "mlp", "pcm_bluray", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_s16be", "pcm_s24be", "flac", "alac", "wavpack"];

    /// <summary>Whether a stream is lossless audio (DTS only with its HD MA extension).</summary>
    public static bool IsLossless(StreamInfo a) =>
        a.Kind == StreamKind.Audio && (Lossless.Contains(a.Codec, StringComparer.OrdinalIgnoreCase)
            || (a.Codec.Equals("dts", StringComparison.OrdinalIgnoreCase) && a.Profile.Contains("MA", StringComparison.OrdinalIgnoreCase)));

    public static bool IsAtmos(StreamInfo a) =>
        a.Profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase) || a.CommercialName.Contains("Atmos", StringComparison.OrdinalIgnoreCase);

    /// <summary>Release names that say music: "MBluRay", "MBLURAY", "PURE", "Pure Audio", "BDA".</summary>
    [GeneratedRegex(@"(^|[.\-_ ])(m-?blu-?ray|pure([.\-_ ]audio)?|bda)([.\-_ ]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MusicName();

    public static bool NameSaysMusic(string path) => MusicName().IsMatch(path.Replace('\\', '/').Replace("::", "/"));

    /// <summary>
    /// A music title: lossless audio and at least three chapters, and either the release name says so or the title is
    /// album-shaped (at most 100 minutes, songs of at most 12 minutes on average, no subtitles). A film has subtitles
    /// and runs longer; a concert film usually also has a lossy or no subtitle-free shape, so it stays a video row.
    /// </summary>
    public static bool IsMusic(string path, MediaTitle title, MediaInfoSummary info)
    {
        if (!info.Audio.Any(IsLossless))
            return false;
        var chapters = Chapters(title, info);
        if (chapters.Count < 3)
            return false;
        if (NameSaysMusic(path))
            return true;
        var duration = info.Duration > 0 ? info.Duration : title.Duration;
        return duration <= 100 * 60 && duration / chapters.Count <= 12 * 60 && !info.Subtitles.Any();
    }

    private static List<Chapter> Chapters(MediaTitle title, MediaInfoSummary info) =>
        title.Chapters.Count > 0 ? title.Chapters : info.Chapters;

    /// <summary>The lossless stream to check: TrueHD / DTS-HD MA / LPCM / FLAC, the most channels first.</summary>
    public static StreamInfo? PickStream(MediaInfoSummary info) =>
        info.Audio.Where(IsLossless).OrderByDescending(a => a.Channels).ThenByDescending(a => a.SampleRate).FirstOrDefault();

    /// <summary>Songs from the chapters (named when the chapter has a real name, not "Chapter 03").</summary>
    public static List<MusicTrack> Tracks(MediaTitle title, MediaInfoSummary info)
    {
        var duration = info.Duration > 0 ? info.Duration : title.Duration;
        var ch = Chapters(title, info).Where(c => c.Start < duration - 1).OrderBy(c => c.Start).ToList();
        if (ch.Count == 0)
            return [new MusicTrack(1, "", 0, duration)];
        // the part before the first chapter belongs to the first song
        var tracks = new List<MusicTrack>();
        for (var i = 0; i < ch.Count; i++)
        {
            var start = i == 0 ? 0 : ch[i].Start;
            var end = i + 1 < ch.Count ? ch[i + 1].Start : duration;
            var name = GenericChapter().IsMatch(ch[i].Name) ? "" : ch[i].Name.Trim();
            tracks.Add(new MusicTrack(i + 1, name, start, end - start));
        }
        return tracks;
    }

    [GeneratedRegex(@"^\s*(chapter|kapitel|chapitre|capitolo|cap[ií]tulo)?\s*\d+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericChapter();

    /// <summary>
    /// The music titles under <paramref name="path"/> (a rip, or a disc folder / ISO / RAR set: every playlist of
    /// five minutes or more whose audio is lossless and album-shaped, one per mix), each as a path that opens it again
    /// (a disc playlist as its .mpls).
    /// </summary>
    public static async Task<List<(string Path, MediaTitle Title, MediaInfoSummary Info)>> FindAsync(FfTools tools, string path, CancellationToken ct)
    {
        var found = new List<(string, MediaTitle, MediaInfoSummary)>();
        var src = await SourceOpener.OpenAsync(path, false, ct).ConfigureAwait(false);
        try
        {
            var prober = new MediaProber(tools);
            var titles = src.Kind == SourceKind.BluRay
                ? src.Titles.Where(t => t.Duration >= 300).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : src.MainTitle is { } m ? [m] : [];
            foreach (var t in titles)
            {
                ct.ThrowIfCancellationRequested();
                var info = await prober.ProbeAsync(src, t, ct).ConfigureAwait(false);
                if (!IsMusic(path, t, info))
                    continue;
                var open = src.Kind == SourceKind.BluRay && t.StructureFile != null ? t.StructureFile : path;
                found.Add((open, t, info));
            }
        }
        finally
        {
            await SourceOpener.CloseAsync(src).ConfigureAwait(false);
        }
        return found;
    }

    /// <summary>Opens <paramref name="path"/> (a rip or a playlist path from <see cref="FindAsync"/>) and analyses its music title.</summary>
    public static async Task<AnalysisResult> AnalyzePathAsync(FfTools tools, string path, string tempDir, CancellationToken ct)
    {
        var r = new AnalysisResult { Path = path };
        MediaSource? src = null;
        try
        {
            src = await SourceOpener.OpenAsync(path, false, ct).ConfigureAwait(false);
            var title = src.MainTitle ?? throw new InvalidDataException("no playable title");
            var info = await new MediaProber(tools).ProbeAsync(src, title, ct).ConfigureAwait(false);
            await AnalyzeAsync(tools, src, title, info, r, tempDir, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or FormatException)
        {
            r.Error = ex.Message;
        }
        finally
        {
            if (src != null)
                await SourceOpener.CloseAsync(src).ConfigureAwait(false);
        }
        return r;
    }

    /// <summary>Decodes the picked stream once and fills <paramref name="r"/> with the album result and a row per song.</summary>
    public static async Task AnalyzeAsync(FfTools tools, MediaSource src, MediaTitle title, MediaInfoSummary info, AnalysisResult r, string tempDir, CancellationToken ct)
    {
        var a = PickStream(info) ?? throw new InvalidDataException("no lossless audio track (" + string.Join(", ", info.Audio.Select(x => $"{x.Codec}/{x.Profile}")) + $") in {title.Name}, first clip {title.Segments.FirstOrDefault()?.Url} exists={Ces.Vfs.Vfs.FileExists(title.Segments.FirstOrDefault()?.Url ?? "")}");
        var tracks = Tracks(title, info);
        var rate = a.SampleRate > 0 ? a.SampleRate : 48000;
        var channels = Math.Max(1, a.Channels);
        var bits = a.BitDepth is > 0 and <= 32 ? a.BitDepth : 24;
        var duration = info.Duration > 0 ? info.Duration : title.Duration;

        var sink = new TrackSink(tracks, rate, channels);
        var input = InputBuilder.Input(title, 0, null, tempDir);
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        args.AddRange(InputBuilder.WithoutHwaccel(input.Args));
        args.AddRange(["-map", "0:a:" + a.KindIndex.ToString(Inv), "-vn", "-sn", "-dn", "-af", input.ATrimFilter + "anull",
            "-ac", channels.ToString(Inv), "-ar", rate.ToString(Inv), "-f", "s32le", "-acodec", "pcm_s32le", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct, null, null, sink).ConfigureAwait(false);
        if (!res.Ok && sink.Frames == 0)
            throw new InvalidDataException("the audio could not be decoded: " + res.StdErr.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim());

        foreach (var (t, stats, same) in tracks.Zip(sink.Stats, sink.Same))
        {
            var one = new AnalysisResult { Path = r.Path };
            var spectrum = stats.Spectrum;
            var (cut, steep) = spectrum.Length > 0 ? Spectrum.FindCutoff(spectrum, rate) : (double.NaN, double.NaN);
            // the decoder hands out 32-bit samples: their used bits are the stream's
            var used = stats.UsedBits;
            LosslessSource.Check(one, rate, bits, Math.Min(used, bits), spectrum, cut, steep);
            t.UsedBits = Math.Min(used, bits);
            t.PeakDb = stats.PeakDb;
            t.CutoffHz = cut;
            t.Problems.AddRange(one.Problems);
            t.SameChannels = channels == 2 && same.Frames > 0 ? same.Equal / (double)same.Frames : double.NaN;
            t.SideDb = channels == 2 && same.Mid > 0 ? (same.Side > 0 ? 10 * Math.Log10(same.Side / same.Mid) : double.NegativeInfinity) : double.NaN;
            if (t.SameChannels > 0.999)
                t.Problems.Add("mono on two identical channels");
            var mono = t.SameChannels > 0.999 ? "dual mono" : t.SideDb < MonoSideDb ? "mono content" : "";
            t.Verdict = string.Join(", ", new[] { one.SourceCheck, mono }.Where(x => x.Length > 0 && !(x == "ok" && mono.Length > 0)));
        }
        Fill(r, src, title, info, a, tracks, rate, channels, bits, duration);
    }

    private static void Fill(AnalysisResult r, MediaSource src, MediaTitle title, MediaInfoSummary info, StreamInfo a, List<MusicTrack> tracks, int rate, int channels, int bits, double duration)
    {
        var atmos = IsAtmos(a);
        r.Format = (src.Kind == SourceKind.BluRay ? "Music Blu-ray" : Path.GetExtension(src.Path).TrimStart('.').ToUpperInvariant() + " (music)");
        r.SampleRate = rate;
        r.Channels = channels;
        r.BitsPerSample = bits;
        r.DurationSeconds = duration;
        r.BitrateKbps = a.BitRate > 0 ? a.BitRate / 1000.0 : 0;
        if (r.FileSize == 0)
            r.FileSize = title.SizeBytes > 0 ? title.SizeBytes : info.SizeBytes;
        var codec = CodecName(a);
        r.ModeName = $"{Layout(a, channels)}{(atmos ? " Atmos" : "")}";
        r.Encoder = codec + (atmos ? " + Dolby Atmos" : "");
        r.Confidence = GuessConfidence.High;
        r.GuessReason = "lossless audio stream of a music release (songs = chapters)";
        r.EncoderSettings = string.Create(Inv, $"{tracks.Count} tracks · {rate / 1000.0:0.#} kHz / {bits}-bit · {Layout(a, channels)}");
        r.Quality = QualityRating.Unknown;

        // album verdict: the same finding on every song is said once; songs that differ are named
        var verdicts = tracks.GroupBy(t => t.Verdict.Length == 0 ? "ok" : t.Verdict).OrderByDescending(g => g.Count()).ToList();
        r.SourceCheck = verdicts.Count == 1
            ? (verdicts[0].Key == "ok" ? "ok" : verdicts[0].Key) + string.Create(Inv, $" ({tracks.Count} tracks)")
            : string.Join("; ", verdicts.Select(g => g.Key + " (" + Numbers(g) + ")"));
        foreach (var g in tracks.Where(t => t.Problems.Count > 0).SelectMany(t => t.Problems.Select(p => (t.Number, p))).GroupBy(x => x.p))
        {
            var n = g.Select(x => x.Number).ToList();
            r.Problems.Add(n.Count == tracks.Count ? $"{g.Key} (all tracks)" : $"{g.Key} (track{(n.Count == 1 ? "" : "s")} {Numbers(n)})");
        }

        r.Details.Add(new InfoItem("Music", "Audio", $"{codec}{(atmos ? " with Dolby Atmos" : "")}, {Layout(a, channels)}, {rate / 1000.0:0.#} kHz, {bits}-bit"));
        if (atmos)
            r.Details.Add(new InfoItem("Music", "Atmos", "object audio in the TrueHD stream; checked here: the decoded 7.1 bed, not the objects"));
        foreach (var other in info.Audio.Where(x => x != a))
            r.Details.Add(new InfoItem("Music", "Other audio", other.Label));
        if (info.Video is { } v)
            r.Details.Add(new InfoItem("Music", "Video", string.Create(Inv, $"{v.Codec.ToUpperInvariant()} {v.Width}×{v.Height}{(v.BitRate > 0 ? $", {v.BitRate / 1e6:0.#} Mb/s" : "")}")));
        r.Details.Add(new InfoItem("Music", "Title", title.Name));
        foreach (var t in tracks)
        {
            var label = string.Create(Inv, $"{t.Number:00}  {Format.Time(t.Duration, false)}") + (t.Name.Length > 0 ? "  " + t.Name : "");
            var parts = new List<string> { t.Verdict.Length == 0 ? "ok" : t.Verdict };
            if (t.UsedBits > 0)
                parts.Add(string.Create(Inv, $"{t.UsedBits}-bit used"));
            if (!double.IsNegativeInfinity(t.PeakDb))
                parts.Add(string.Create(Inv, $"peak {t.PeakDb:0.0} dB"));
            if (!double.IsNaN(t.CutoffHz))
                parts.Add(t.CutoffHz >= rate / 2.0 * 0.97 ? "content to Nyquist" : string.Create(Inv, $"content to {t.CutoffHz / 1000:0.0} kHz"));
            if (!double.IsNaN(t.SideDb))
                parts.Add(double.IsNegativeInfinity(t.SideDb) ? "L = R" : string.Create(Inv, $"side {t.SideDb:0} dB"));
            r.Details.Add(new InfoItem("Tracks", label, string.Join(" · ", parts)));
        }
    }

    private static string Numbers(IEnumerable<MusicTrack> g) => Numbers(g.Select(t => t.Number).ToList());

    /// <summary>"1–4, 7, 9–10".</summary>
    internal static string Numbers(List<int> n)
    {
        var parts = new List<string>();
        for (var i = 0; i < n.Count; i++)
        {
            var j = i;
            while (j + 1 < n.Count && n[j + 1] == n[j] + 1)
                j++;
            parts.Add(j > i ? string.Create(Inv, $"{n[i]}–{n[j]}") : n[i].ToString(Inv));
            i = j;
        }
        return string.Join(", ", parts);
    }

    private static string CodecName(StreamInfo a) => a.Codec.ToLowerInvariant() switch
    {
        "truehd" or "mlp" => "Dolby TrueHD",
        "dts" => "DTS-HD Master Audio",
        "flac" => "FLAC",
        "alac" => "ALAC",
        "wavpack" => "WavPack",
        _ => "LPCM",
    };

    private static string Layout(StreamInfo a, int channels) => channels switch
    {
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        _ => a.ChannelLayout.Length > 0 ? a.ChannelLayout : channels.ToString(Inv) + " ch",
    };

    /// <summary>Takes ffmpeg's 32-bit interleaved PCM as it arrives and hands each song's frames to its own statistics.</summary>
    private sealed class TrackSink : Stream
    {
        private readonly long[] _ends;
        private readonly int _frameBytes;
        private readonly int _channels;
        private readonly byte[] _carry;
        private int _carried;
        private int _track;

        public List<PcmStats> Stats { get; }
        public List<(long Frames, long Equal, double Side, double Mid)> Same { get; }
        public long Frames { get; private set; }

        public TrackSink(List<MusicTrack> tracks, int rate, int channels)
        {
            _channels = channels;
            _frameBytes = 4 * channels;
            _carry = new byte[_frameBytes];
            _ends = tracks.Select(t => (long)Math.Round((t.Start + t.Duration) * rate)).ToArray();
            _ends[^1] = long.MaxValue;
            Stats = tracks.Select(t => new PcmStats(rate, channels, 32, (long)(t.Duration * rate))).ToList();
            Same = tracks.Select(_ => (0L, 0L, 0.0, 0.0)).ToList();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            // finish a frame split across writes
            if (_carried > 0)
            {
                var take = Math.Min(_frameBytes - _carried, buffer.Length);
                buffer[..take].CopyTo(_carry.AsSpan(_carried));
                _carried += take;
                buffer = buffer[take..];
                if (_carried < _frameBytes)
                    return;
                Frame(_carry);
                _carried = 0;
            }
            var whole = buffer.Length / _frameBytes * _frameBytes;
            var p = 0;
            while (p < whole)
            {
                // as many frames as fit in the current song
                var left = _ends[_track] - Frames;
                var n = (int)Math.Min((whole - p) / _frameBytes, Math.Max(1, left));
                var span = buffer.Slice(p, n * _frameBytes);
                Stats[_track].Add(span);
                if (_channels == 2)
                    CountSame(span);
                Frames += n;
                p += n * _frameBytes;
                while (_track < _ends.Length - 1 && Frames >= _ends[_track])
                    _track++;
            }
            buffer[whole..].CopyTo(_carry);
            _carried = buffer.Length - whole;
        }

        private void Frame(ReadOnlySpan<byte> frame)
        {
            Stats[_track].Add(frame);
            if (_channels == 2)
                CountSame(frame);
            Frames++;
            while (_track < _ends.Length - 1 && Frames >= _ends[_track])
                _track++;
        }

        private void CountSame(ReadOnlySpan<byte> span)
        {
            var (frames, equal, side, mid) = Same[_track];
            for (var i = 0; i + 8 <= span.Length; i += 8)
            {
                frames++;
                var li = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i, 4));
                var ri = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i + 4, 4));
                if (li == ri)
                    equal++;
                double l = li, r = ri;
                side += (l - r) * (l - r);
                mid += (l + r) * (l + r);
            }
            Same[_track] = (frames, equal, side, mid);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
