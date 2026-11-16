using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NothingElseCompares2u.Dsp;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Probe;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

public sealed class ChannelStats
{
    public string Name { get; set; } = "";
    public double PeakDb { get; set; } = double.NaN;
    public double RmsDb { get; set; } = double.NaN;
    public double DcOffset { get; set; } = double.NaN;
    public double NoiseFloorDb { get; set; } = double.NaN;
    public double DynamicRangeDb { get; set; } = double.NaN;
    public string BitDepth { get; set; } = "";
}

public sealed class AudioReport
{
    public int TrackIndex { get; set; }
    public required StreamInfo Stream { get; init; }
    public double IntegratedLufs { get; set; } = double.NaN;
    public double LoudnessRange { get; set; } = double.NaN;
    public double TruePeakDb { get; set; } = double.NaN;
    public ChannelStats Overall { get; set; } = new();
    public List<ChannelStats> Channels { get; } = [];
    public double[] Spectrum { get; set; } = [];
    public int SpectrumRate { get; set; }
    public double CutoffHz { get; set; } = double.NaN;
    public double CutoffSteepnessDb { get; set; } = double.NaN;
    public string? SpectrogramPng { get; set; }
    public double AnalyzedSeconds { get; set; }
    public List<Finding> Findings { get; } = [];
}

public sealed class AudioAnalysisOptions
{
    /// <summary>Loudness + stats over the whole title (slow for long titles) instead of sampled windows.</summary>
    public bool FullLength { get; set; }

    public int Windows { get; set; } = 8;
    public double WindowSeconds { get; set; } = 20;
    public bool Spectrogram { get; set; } = true;
    public int SpectrogramWidth { get; set; } = 1200;
    public int SpectrogramHeight { get; set; } = 480;
}

public sealed partial class AudioAnalyzer(FfTools tools, string tempDir)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<AudioReport> AnalyzeAsync(MediaTitle title, int audioIndex, double offset, AudioAnalysisOptions opt, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var stream = title.Info?.Audio.ElementAtOrDefault(audioIndex) ?? throw new InvalidOperationException("No such audio track.");
        var rep = new AudioReport { TrackIndex = audioIndex, Stream = stream };
        var windows = Windows(title, offset, opt);
        rep.AnalyzedSeconds = windows.Sum(w => w.Length);

        // 1) loudness + astats: one pass per window, merged by averaging the per-window numbers
        var loud = new List<(double I, double Lra, double Tp)>();
        var stats = new List<(ChannelStats Overall, List<ChannelStats> Ch)>();
        var n = 0;
        foreach (var (start, len) in windows)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(opt.FullLength ? "Loudness + statistics over the full title…" : $"Statistics window {++n}/{windows.Count}");
            var args = new List<string> { "-hide_banner", "-nostdin", "-nostats" };
            var input = InputBuilder.Input(title, start, len, tempDir);
            args.AddRange(input.Args);
            args.AddRange(["-map", $"0:a:{audioIndex}", "-vn", "-sn", "-dn", "-af", input.ATrimFilter + "astats=measure_overall=all:measure_perchannel=Peak_level+RMS_level+DC_offset+Noise_floor+Dynamic_range+Bit_depth,ebur128=peak=true:framelog=quiet", "-f", "null", "-"]);
            var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct, workingDirectory: tempDir).ConfigureAwait(false);
            stats.Add(ParseAstats(res.StdErr));
            var l = ParseLoudness(res.StdErr);
            if (!double.IsNaN(l.I))
                loud.Add(l);
        }
        if (loud.Count > 0)
        {
            // windows are equally long: energy-average the integrated loudness
            rep.IntegratedLufs = 10 * Math.Log10(loud.Average(x => Math.Pow(10, x.I / 10)));
            rep.LoudnessRange = loud.Average(x => x.Lra);
            rep.TruePeakDb = loud.Max(x => x.Tp);
        }
        if (stats.Count > 0)
        {
            rep.Overall = Merge(stats.Select(s => s.Overall).ToList());
            var chCount = stats.Max(s => s.Ch.Count);
            for (var c = 0; c < chCount; c++)
            {
                var m = Merge(stats.Where(s => c < s.Ch.Count).Select(s => s.Ch[c]).ToList());
                m.Name = ChannelName(stream, c);
                rep.Channels.Add(m);
            }
        }

        // 2) spectrum of the sampled windows
        progress?.Report("Spectrum…");
        var specWindows = opt.FullLength ? Windows(title, offset, new AudioAnalysisOptions { Windows = opt.Windows, WindowSeconds = opt.WindowSeconds }) : windows;
        await SpectrumAsync(title, audioIndex, specWindows, rep, ct).ConfigureAwait(false);

        // 3) spectrogram picture of one window in the middle
        if (opt.Spectrogram)
        {
            progress?.Report("Spectrogram…");
            rep.SpectrogramPng = await SpectrogramAsync(title, audioIndex, title.Duration / 2 + offset, 60, opt, ct).ConfigureAwait(false);
        }
        BuildFindings(rep);
        return rep;
    }

    private static List<(double Start, double Length)> Windows(MediaTitle title, double offset, AudioAnalysisOptions opt)
    {
        if (opt.FullLength || title.Duration <= opt.Windows * opt.WindowSeconds * 1.5)
            return [(Math.Max(0, offset), title.Duration - Math.Max(0, offset))];
        return InputBuilder.EvenPoints(title.Duration, opt.Windows, 6, 10)
            .Select(t => (Math.Max(0, t + offset), opt.WindowSeconds))
            .Where(w => w.Item1 + 1 < title.Duration)
            .ToList();
    }

    private async Task SpectrumAsync(MediaTitle title, int audioIndex, List<(double Start, double Length)> windows, AudioReport rep, CancellationToken ct)
    {
        var rate = rep.Stream.SampleRate > 0 ? rep.Stream.SampleRate : 48000;
        const int size = 8192;
        double[]? acc = null;
        var count = 0;
        foreach (var (start, len) in windows.Take(12))
        {
            var l = Math.Min(len, 30);
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
            var input = InputBuilder.Input(title, start, l, tempDir);
            args.AddRange(input.Args);
            if (input.Skip > 0)
                args.AddRange(["-af", input.ATrimFilter.TrimEnd(',')]);
            args.AddRange(["-map", $"0:a:{audioIndex}", "-vn", "-sn", "-dn", "-ac", "1", "-ar", rate.ToString(Inv), "-f", "f32le", "-acodec", "pcm_f32le", "-"]);
            var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
            var samples = MemoryMarshal.Cast<byte, float>(res.StdOut.AsSpan(0, res.StdOut.Length / 4 * 4)).ToArray();
            if (samples.Length < size * 2)
                continue;
            var db = await Task.Run(() => Fft.PowerSpectrumDb(samples, size), ct).ConfigureAwait(false);
            acc ??= new double[db.Length];
            for (var k = 0; k < db.Length; k++)
                acc[k] += Math.Pow(10, db[k] / 10);
            count++;
        }
        if (acc == null)
            return;
        rep.Spectrum = acc.Select(p => 10 * Math.Log10(Math.Max(p / count, 1e-30))).ToArray();
        rep.SpectrumRate = rate;
        (rep.CutoffHz, rep.CutoffSteepnessDb) = FindCutoff(rep.Spectrum, rate);
    }

    /// <summary>
    /// Highest frequency with real content: where the smoothed spectrum finally falls to the noise floor at the top.
    /// Returns the steepness of that edge (dB lost within ~1 kHz) so a brick-wall lowpass can be told from a natural roll-off.
    /// </summary>
    internal static (double Hz, double Steepness) FindCutoff(double[] db, int rate)
    {
        var n = db.Length;
        if (n < 64)
            return (double.NaN, double.NaN);
        var binHz = rate / 2.0 / (n - 1);
        var smoothBins = Math.Max(1, (int)(150 / binHz));
        var sm = new double[n];
        for (var i = 0; i < n; i++)
        {
            double s = 0;
            var c = 0;
            for (var j = Math.Max(0, i - smoothBins); j <= Math.Min(n - 1, i + smoothBins); j++)
            {
                s += db[j];
                c++;
            }
            sm[i] = s / c;
        }
        // noise floor: the quietest 1 kHz band above 5 kHz
        var from = (int)(5000 / binHz);
        var band = Math.Max(4, (int)(1000 / binHz));
        var floor = double.MaxValue;
        for (var i = from; i + band < n; i += band / 2)
            floor = Math.Min(floor, sm.Skip(i).Take(band).Average());
        var reference = sm.Skip((int)(1000 / binHz)).Take((int)(3000 / binHz)).Average();
        if (floor == double.MaxValue || reference - floor < 20)
            return (rate / 2.0, double.NaN); // content all the way up (or flat noise)
        var threshold = floor + Math.Min(10, (reference - floor) / 4);
        var cut = n - 1;
        while (cut > from && sm[cut] < threshold)
            cut--;
        var hz = cut * binHz;
        var k = Math.Max(1, (int)(1000 / binHz));
        var steep = sm[Math.Max(0, cut - k)] - sm[Math.Min(n - 1, cut + k)];
        return (cut >= n - 1 - smoothBins ? rate / 2.0 : hz, steep);
    }

    public async Task<string?> SpectrogramAsync(MediaTitle title, int audioIndex, double start, double length, AudioAnalysisOptions opt, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var png = Path.Combine(tempDir, $"spectrogram-{Guid.NewGuid():N}.png");
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
        var input = InputBuilder.Input(title, Math.Max(0, Math.Min(start, title.Duration - length)), length, tempDir);
        args.AddRange(input.Args);
        // stereo/multichannel: L+R mix; mono sources have no c1, so fall back to the plain stream
        var size = $"s={opt.SpectrogramWidth}x{opt.SpectrogramHeight}:legend=1:color=intensity:scale=log:fscale=lin:drange=120";
        var mix = (title.Info?.Audio.ElementAtOrDefault(audioIndex)?.Channels ?? 2) >= 2 ? "pan=mono|c0=c0+c1," : "";
        args.AddRange(["-filter_complex", $"[0:a:{audioIndex}]{input.ATrimFilter}{mix}showspectrumpic={size}[spec]", "-map", "[spec]", "-frames:v", "1", png]);
        await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        return File.Exists(png) ? png : null;
    }

    private static void BuildFindings(AudioReport rep)
    {
        var s = rep.Stream;
        var f = rep.Findings;
        var lossless = s.Codec is "flac" or "alac" or "truehd" or "mlp" or "wavpack" or "tta" or "ape" ||
                       s.Codec.StartsWith("pcm", StringComparison.Ordinal) ||
                       (s.Codec == "dts" && s.Profile.Contains("MA", StringComparison.Ordinal));
        var nyq = rep.SpectrumRate / 2.0;
        if (!double.IsNaN(rep.CutoffHz) && rep.SpectrumRate > 0)
        {
            var steep = !double.IsNaN(rep.CutoffSteepnessDb) && rep.CutoffSteepnessDb > 25;
            if (rep.CutoffHz >= nyq * 0.97)
                f.Add(new Finding { Area = "Spectrum", Level = 0, Text = $"Content up to {nyq / 1000:0.#} kHz (Nyquist)." });
            else if (lossless && steep && rep.CutoffHz < 20500)
                f.Add(new Finding { Area = "Spectrum", Level = 2, Text = $"Lossless track with a brick-wall cut at {rep.CutoffHz / 1000:0.0} kHz ({rep.CutoffSteepnessDb:0} dB edge): typical of a lossy source (MP3/AAC/AC-3) converted to lossless." });
            else if (rep.SpectrumRate > 48000 && rep.CutoffHz <= 24500)
                f.Add(new Finding { Area = "Spectrum", Level = 2, Text = $"{rep.SpectrumRate / 1000.0:0.#} kHz track with nothing above {rep.CutoffHz / 1000:0.0} kHz: upsampled from 44.1/48 kHz." });
            else
                f.Add(new Finding { Area = "Spectrum", Level = steep ? 1 : 0, Text = $"Content up to ≈{rep.CutoffHz / 1000:0.0} kHz{(steep ? $" with a sharp lowpass ({rep.CutoffSteepnessDb:0} dB edge)" : " (natural roll-off)")}." });
        }
        if (lossless && s.BitDepth > 16 && rep.Overall.BitDepth.Length > 0)
        {
            var used = rep.Overall.BitDepth.Split('/')[0];
            if (int.TryParse(used, NumberStyles.Integer, Inv, out var u) && u <= 16)
                f.Add(new Finding { Area = "Bit depth", Level = 2, Text = $"{s.BitDepth}-bit track but only {u} bits are used: padded 16-bit audio." });
            else
                f.Add(new Finding { Area = "Bit depth", Level = 0, Text = $"Bits used: {rep.Overall.BitDepth}." });
        }
        var silent = rep.Channels.Where(c => c.RmsDb < -75 || double.IsNegativeInfinity(c.RmsDb)).Select(c => c.Name).ToList();
        if (silent.Count > 0 && rep.Channels.Count > 2)
            f.Add(new Finding { Area = "Channels", Level = silent.Count >= rep.Channels.Count - 2 ? 2 : 1, Text = $"(Nearly) silent channels in the analysed parts: {string.Join(", ", silent)}." });
        if (!double.IsNaN(rep.IntegratedLufs))
            f.Add(new Finding { Area = "Loudness", Level = 0, Text = $"Integrated {rep.IntegratedLufs:0.0} LUFS, LRA {rep.LoudnessRange:0.0} LU, true peak {rep.TruePeakDb:0.0} dBTP{(rep.TruePeakDb > -0.1 ? " (clipping likely)" : "")}." });
        if (!double.IsNaN(rep.Overall.DcOffset) && Math.Abs(rep.Overall.DcOffset) > 0.005)
            f.Add(new Finding { Area = "DC offset", Level = 1, Text = $"DC offset {rep.Overall.DcOffset:0.0000}." });
    }

    private static string ChannelName(StreamInfo s, int c)
    {
        var layout = s.ChannelLayout;
        string[] names = layout switch
        {
            "stereo" => ["L", "R"],
            "mono" => ["C"],
            "5.1" or "5.1(side)" => ["L", "R", "C", "LFE", layout == "5.1" ? "Ls" : "Ls", "Rs"],
            "7.1" => ["L", "R", "C", "LFE", "Lb", "Rb", "Ls", "Rs"],
            "6.1" => ["L", "R", "C", "LFE", "Cs", "Ls", "Rs"],
            _ => [],
        };
        return c < names.Length ? names[c] : $"Ch {c + 1}";
    }

    internal static (ChannelStats Overall, List<ChannelStats> Channels) ParseAstats(string stderr)
    {
        var overall = new ChannelStats { Name = "Overall" };
        var channels = new List<ChannelStats>();
        ChannelStats? cur = null;
        foreach (var raw in stderr.Split('\n'))
        {
            if (!raw.Contains("Parsed_astats", StringComparison.Ordinal))
                continue;
            var line = raw[(raw.IndexOf(']') + 1)..].Trim();
            if (line.StartsWith("Channel:", StringComparison.Ordinal))
            {
                cur = new ChannelStats();
                channels.Add(cur);
                continue;
            }
            if (line.StartsWith("Overall", StringComparison.Ordinal))
            {
                cur = overall;
                continue;
            }
            if (cur == null)
                continue;
            var i = line.IndexOf(':');
            if (i < 0)
                continue;
            var key = line[..i].Trim();
            var val = line[(i + 1)..].Trim();
            switch (key)
            {
                case "Peak level dB": cur.PeakDb = Dbl(val); break;
                case "RMS level dB": cur.RmsDb = Dbl(val); break;
                case "DC offset": cur.DcOffset = Dbl(val); break;
                case "Noise floor dB": cur.NoiseFloorDb = Dbl(val); break;
                case "Dynamic range": cur.DynamicRangeDb = Dbl(val); break;
                case "Bit depth": cur.BitDepth = val; break;
            }
        }
        return (overall, channels);
    }

    internal static (double I, double Lra, double Tp) ParseLoudness(string stderr)
    {
        var sum = stderr.LastIndexOf("Summary:", StringComparison.Ordinal);
        if (sum < 0)
            return (double.NaN, double.NaN, double.NaN);
        var t = stderr[sum..];
        var i = IRegex().Match(t);
        var lra = LraRegex().Match(t);
        var tp = PeakRegex().Match(t);
        return (i.Success ? Dbl(i.Groups[1].Value) : double.NaN, lra.Success ? Dbl(lra.Groups[1].Value) : double.NaN, tp.Success ? Dbl(tp.Groups[1].Value) : double.NaN);
    }

    private static ChannelStats Merge(List<ChannelStats> list)
    {
        if (list.Count == 0)
            return new ChannelStats();
        double Avg(Func<ChannelStats, double> sel)
        {
            var v = list.Select(sel).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
            return v.Count > 0 ? v.Average() : list.Select(sel).FirstOrDefault(double.NaN);
        }
        double Max(Func<ChannelStats, double> sel)
        {
            var v = list.Select(sel).Where(x => !double.IsNaN(x)).ToList();
            return v.Count > 0 ? v.Max() : double.NaN;
        }
        double Min(Func<ChannelStats, double> sel)
        {
            var v = list.Select(sel).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
            return v.Count > 0 ? v.Min() : double.NaN;
        }
        // RMS: energy average
        var rms = list.Select(c => c.RmsDb).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
        return new ChannelStats
        {
            Name = list[0].Name,
            PeakDb = Max(c => c.PeakDb),
            RmsDb = rms.Count > 0 ? 10 * Math.Log10(rms.Average(x => Math.Pow(10, x / 10))) : double.NegativeInfinity,
            DcOffset = Avg(c => c.DcOffset),
            NoiseFloorDb = Min(c => c.NoiseFloorDb),
            DynamicRangeDb = Max(c => c.DynamicRangeDb),
            BitDepth = list.Select(c => c.BitDepth).Where(b => b.Length > 0).OrderByDescending(b => int.TryParse(b.Split('/')[0], NumberStyles.Integer, Inv, out var x) ? x : 0).FirstOrDefault() ?? "",
        };
    }

    private static double Dbl(string s) =>
        s.StartsWith("-inf", StringComparison.Ordinal) ? double.NegativeInfinity :
        double.TryParse(s.Split(' ')[0], NumberStyles.Float, Inv, out var v) ? v : double.NaN;

    [GeneratedRegex(@"I:\s*(-?[\d.]+|-inf)\s*LUFS")]
    private static partial Regex IRegex();

    [GeneratedRegex(@"LRA:\s*(-?[\d.]+)\s*LU")]
    private static partial Regex LraRegex();

    [GeneratedRegex(@"True peak:\s*Peak:\s*(-?[\d.]+|-inf)\s*dBFS", RegexOptions.Singleline)]
    private static partial Regex PeakRegex();
}
