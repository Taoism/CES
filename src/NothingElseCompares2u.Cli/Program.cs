using System.Globalization;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Cli;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Disc;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Probe;
using NothingElseCompares2u.Report;
using NothingElseCompares2u.Tools;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Help.Text);
    return args.Length == 0 ? 1 : 0;
}
if (args[0] is "--version" or "-V")
{
    Console.WriteLine("nec2u " + typeof(CompareItem).Assembly.GetName().Version?.ToString(3));
    return 0;
}

CliArgs cli;
try
{
    cli = CliArgs.Parse(args.Skip(1).ToArray());
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync("nec2u: " + ex.Message);
    return 2;
}

var (tools, err) = await FfTools.LocateAsync(cli.Get("ffmpeg"), cli.Get("mediainfo"));
if (tools == null)
{
    await Console.Error.WriteLineAsync("nec2u: " + err);
    return 2;
}
if (cli.Has("verbose"))
    ProcessRunner.CommandStarted += c => Console.Error.WriteLine("> " + c);
if (cli.Has("hw"))
    InputBuilder.HardwareDecode = OperatingSystem.IsWindows() ? "d3d11va" : "auto";
if (cli.Has("no-cache"))
    ShotCache.Dir = null;

var temp = Path.Combine(Path.GetTempPath(), "nec2u", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var progress = new Progress<string>(s => Console.Error.WriteLine("  " + s));

try
{
    var items = new List<CompareItem>();
    // rank: folders after the reference stand for every release inside them
    var specs = args[0] == "rank"
        ? cli.Positional.Take(1).Concat(cli.Positional.Skip(1).SelectMany(p => Directory.Exists(p) ? SourceOpener.ExpandFolder(p) : [p])).ToList()
        : cli.Positional;
    foreach (var spec in specs)
        items.Add(await OpenItemAsync(spec, cli, tools, cts.Token));
    // pictures have no timeline to sync
    foreach (var it in items.Where(i => i.Title.IsStill))
        it.Synced = true;
    // --offset 2=5.0 (source 2 is 5 s later) or --offset 5.0 for the second source
    foreach (var o in cli.All("offset"))
    {
        var p = o.Split('=');
        var (idx, val) = p.Length == 2 ? (int.Parse(p[0], CultureInfo.InvariantCulture) - 1, p[1]) : (1, p[0]);
        if (idx < 0 || idx >= items.Count || !Format.TryParseTime(val, out var off))
            throw new ArgumentException("Bad --offset " + o);
        items[idx].Offset = off;
        items[idx].Synced = true;
    }
    // --speed 2=pal (23.976 film sped up to 25), 2=0.95904, or a ratio 2=24000/25025
    foreach (var o in cli.All("speed"))
    {
        var p = o.Split('=');
        var (idx, val) = p.Length == 2 ? (int.Parse(p[0], CultureInfo.InvariantCulture) - 1, p[1]) : (1, p[0]);
        var sp = val.ToLowerInvariant() switch
        {
            "pal" => 24000 / 1001.0 / 25,
            "pal24" => 24 / 25.0,
            _ when val.Contains('/') => double.Parse(val.Split('/')[0], CultureInfo.InvariantCulture) / double.Parse(val.Split('/')[1], CultureInfo.InvariantCulture),
            _ => double.Parse(val, CultureInfo.InvariantCulture),
        };
        if (idx < 0 || idx >= items.Count || sp <= 0)
            throw new ArgumentException("Bad --speed " + o);
        items[idx].Speed = sp;
    }

    switch (args[0])
    {
        case "info":
            Need(items, 1);
            foreach (var it in items)
                PrintInfo(it, cli.Has("titles"));
            if (items.Count > 1)
                PrintSpecs(items);
            break;

        case "sync":
            Need(items, 2);
            await SyncAsync(items, tools, temp, cli, progress, cts.Token);
            break;

        case "shots":
        {
            Need(items, 1);
            if (cli.Has("auto-sync"))
                await SyncAsync(items, tools, temp, cli, progress, cts.Token);
            var outDir = cli.Get("out") ?? Path.Combine(Environment.CurrentDirectory, "nec2u-shots");
            var rows = await ShotsAsync(items, tools, temp, outDir, cli, cts.Token);
            Console.WriteLine($"{rows.Count} × {items.Count} screenshots in {outDir}");
            string? url = null;
            if (cli.Has("slowpics"))
            {
                // an explicit flag: the user asked to publish these pictures
                using var sp = new NothingElseCompares2u.Share.SlowPics();
                var prog = new Progress<(double Fraction, string Text)>(p => Console.Error.Write("\r  " + p.Text + "      "));
                url = await sp.UploadAsync(cli.Get("title") ?? string.Join(" vs ", items.Select(i => i.Name)), NothingElseCompares2u.Share.SlowPics.FromRows(items, rows), cli.Has("public"), prog, cts.Token);
                await Console.Error.WriteLineAsync();
                Console.WriteLine("slow.pics: " + url);
            }
            if (cli.Get("summary") is { } sf)
                Console.WriteLine(NothingElseCompares2u.Share.Summary.Build(sf.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? NothingElseCompares2u.Share.SummaryFormat.Markdown : NothingElseCompares2u.Share.SummaryFormat.BBCode, items, null, url));
            break;
        }

        case "video":
            Need(items, 1);
            foreach (var it in items)
            {
                Console.WriteLine($"== {it.Name}");
                it.Video = await new VideoAnalyzer(tools, temp).AnalyzeAsync(it.Title, it.Offset, VideoOptions(cli), progress, cts.Token);
                PrintFindings(it.Video.Findings);
                if (cli.Has("curve") && it.Video.NativeRes is { } nr)
                {
                    foreach (var p in nr.Curve)
                        Console.WriteLine($"     {p.Height,5}p  {p.Error * 1000:0.000}  {p.Kernel}");
                }
            }
            if (items.Count > 1)
            {
                PrintSpecs(items, "Analysis");
                PrintCross(items, CrossFindings.Video);
            }
            break;

        case "audio":
            Need(items, 1);
            foreach (var it in items)
            {
                foreach (var track in Tracks(it, cli))
                {
                    Console.WriteLine($"== {it.Name} · audio {track + 1}: {it.Title.Info!.Audio.ElementAt(track).Label}");
                    var rep = await new AudioAnalyzer(tools, temp).AnalyzeAsync(it.Title, track, it.Offset, AudioOptions(cli), progress, cts.Token);
                    it.Audio.Add(rep);
                    PrintFindings(rep.Findings);
                    foreach (var c in rep.Channels)
                        Console.WriteLine($"     {c.Name,-4} peak {Format.Db(c.PeakDb),9}  rms {Format.Db(c.RmsDb),9}  floor {Format.Db(c.NoiseFloorDb),9}  crest {Format.Db(c.CrestDb),8}  bits {c.BitsUsedText}");
                    if (rep.SpectrogramPng != null && cli.Get("out") is { } od)
                    {
                        Directory.CreateDirectory(od);
                        var dest = Path.Combine(od, $"spectrogram-{Safe(it.Name)}-a{track + 1}.png");
                        File.Copy(rep.SpectrogramPng, dest, true);
                        Console.WriteLine("     spectrogram: " + dest);
                    }
                }
            }
            PrintCross(items, CrossFindings.Audio);
            break;

        case "metrics":
        {
            Need(items, 2);
            if (cli.Has("auto-sync"))
                await SyncAsync(items, tools, temp, cli, progress, cts.Token);
            var opt = MetricOpts(cli);
            for (var i = 1; i < items.Count; i++)
            {
                var res = await RunMetricsAsync(items[0], items[i], opt, tools, temp, cts.Token);
                PrintMetrics(items[0], items[i], res);
            }
            break;
        }

        case "rank":
        {
            Need(items, 2);
            if (!cli.Has("no-sync"))
                await SyncAsync(items, tools, temp, cli, progress, cts.Token);
            var opt = MetricOpts(cli);
            if (cli.Get("length") == null)
                opt.Length = 20;
            var windows = cli.Get("windows") is { } wn ? int.Parse(wn, CultureInfo.InvariantCulture) : 5;
            var last = "";
            var prog = new Progress<(double Fraction, string Text)>(p =>
            {
                var line = $"\r  {p.Fraction:P0} {p.Text}";
                if (line != last)
                    Console.Error.Write(line.Length > 110 ? line[..110] : line.PadRight(110));
                last = line;
            });
            var rows = await Ranker.RankAsync(tools, temp, items[0], items.Skip(1).ToList(), opt, windows, prog, cts.Token);
            await Console.Error.WriteLineAsync();
            Console.WriteLine($"Ranked against {items[0].Name} ({windows} windows × {opt.Length:0} s):");
            Console.Write(Ranker.Table(rows));
            break;
        }

        case "grade":
        {
            Need(items, 2);
            if (items.Skip(1).Any(i => !i.Synced) && !cli.Has("no-sync"))
                await SyncAsync(items, tools, temp, cli, progress, cts.Token);
            var gc = new GradeCompare(tools, temp);
            var start = items.Where(i => !i.Title.IsStill).Select(i => i.Coverage().Start).DefaultIfEmpty(0).Max();
            var end = items.Where(i => !i.Title.IsStill).Select(i => i.Coverage().End).DefaultIfEmpty(0).Min();
            if (items.All(i => i.Title.IsStill))
                cli = cli.WithDefault("at", "0");
            var times = cli.Get("at") is { } at
                ? at.Split(',').Select(x => Format.TryParseTime(x, out var t) ? t : throw new ArgumentException("Bad time " + x)).ToList()
                : InputBuilder.EvenPoints(end - Math.Max(0, start), cli.Get("n") is { } nn ? int.Parse(nn, CultureInfo.InvariantCulture) : 8).Select(t => t + Math.Max(0, start)).ToList();
            for (var i = 1; i < items.Count; i++)
            {
                var g = await gc.CompareAsync(items[0], items[i], times, progress, cts.Token);
                Console.WriteLine($"== {items[i].Name} compared with {items[0].Name} ({g.Frames.Count} frames)");
                PrintFindings(g.Findings);
                if (cli.Has("curve"))
                {
                    foreach (var fr in g.Frames)
                        Console.WriteLine($"     {Format.Time(fr.Time, false)}  scale {fr.Scale:0.000}  shift {fr.ShiftX:+0.0;-0.0;0.0}/{fr.ShiftY:+0.0;-0.0;0.0} %  match {fr.Match:0.00}  sat {fr.SaturationRatio:0.00}  a {fr.DeltaA:+0.0;-0.0;0.0} b {fr.DeltaB:+0.0;-0.0;0.0}");
                }
            }
            break;
        }

        case "subs":
        {
            Need(items, 2);
            if (items.Skip(1).Any(i => !i.Synced) && !cli.Has("no-sync"))
                await SyncAsync(items, tools, temp, cli, progress, cts.Token);
            var spec = cli.Get("subs") ?? "1";
            var st = new SubtitleTiming(tools);
            var refTrack = SubtitleChoice.Pick(items[0].Title, spec) ?? throw new ArgumentException($"Reference has no subtitle track '{spec}'.");
            var refLang = items[0].Title.Info!.Subtitles.ElementAt(refTrack).Language;
            var refEvents = await st.ReadAsync(items[0].Title, refTrack, null, cts.Token);
            Console.WriteLine($"Reference [{refTrack + 1}] {items[0].Title.Info!.Subtitles.ElementAt(refTrack).Label}: {refEvents.Count} lines");
            for (var i = 1; i < items.Count; i++)
            {
                var track = SubtitleChoice.Pick(items[i].Title, int.TryParse(spec, out _) ? spec : (refLang.Length > 0 ? refLang : spec));
                if (track == null)
                {
                    Console.WriteLine($"== {items[i].Name}: no matching subtitle track");
                    continue;
                }
                var ev = await st.ReadAsync(items[i].Title, track.Value, null, cts.Token);
                var rep = SubtitleTiming.Compare(items[0], refEvents, items[i], track.Value, ev);
                Console.WriteLine($"== {items[i].Name} · [{track + 1}] {rep.TrackLabel}");
                PrintFindings(rep.Findings);
            }
            break;
        }

        case "report":
        {
            Need(items, 1);
            var outDir = cli.Get("out") ?? Path.Combine(Environment.CurrentDirectory, "nec2u-report");
            if (items.Count > 1 && !cli.Has("no-sync") && items.Skip(1).All(i => i.Offset == 0))
                await SyncAsync(items, tools, temp, cli, progress, cts.Token);
            if (!cli.Has("no-video"))
            {
                foreach (var it in items.Where(i => i.Title.Info?.Video != null))
                {
                    await Console.Error.WriteLineAsync($"Video analysis: {it.Name}");
                    it.Video = await new VideoAnalyzer(tools, temp).AnalyzeAsync(it.Title, it.Offset, VideoOptions(cli), progress, cts.Token);
                }
            }
            if (!cli.Has("no-audio"))
            {
                foreach (var it in items.Where(i => i.Title.Info?.Audio.Any() == true))
                {
                    await Console.Error.WriteLineAsync($"Audio analysis: {it.Name}");
                    foreach (var track in Tracks(it, cli))
                        it.Audio.Add(await new AudioAnalyzer(tools, temp).AnalyzeAsync(it.Title, track, it.Offset, AudioOptions(cli), progress, cts.Token));
                }
            }
            var metrics = new List<(CompareItem, CompareItem, MetricResult)>();
            if (cli.Has("metrics") && items.Count > 1)
            {
                var opt = MetricOpts(cli);
                for (var i = 1; i < items.Count; i++)
                    metrics.Add((items[0], items[i], await RunMetricsAsync(items[0], items[i], opt, tools, temp, cts.Token)));
            }
            var shotDir = Path.Combine(temp, "shots");
            var rows = items.Any(i => i.Title.Info?.Video != null) ? await ShotsAsync(items, tools, temp, shotDir, cli, cts.Token) : [];
            var html = HtmlReport.Write(outDir, cli.Get("title") ?? string.Join(" vs ", items.Select(i => i.Name)), items, rows, metrics);
            Console.WriteLine("Report: " + html);
            break;
        }

        default:
            await Console.Error.WriteLineAsync($"nec2u: unknown command '{args[0]}' (see nec2u --help)");
            return 2;
    }
    return 0;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or FormatException or System.ComponentModel.Win32Exception)
{
    await Console.Error.WriteLineAsync("nec2u: " + ex.Message);
    return 1;
}
finally
{
    await SourceOpener.CloseAllAsync();
    try
    {
        if (Directory.Exists(temp))
            Directory.Delete(temp, true);
    }
    catch (IOException)
    {
        // leave temp files
    }
}

static void Need(List<CompareItem> items, int n)
{
    if (items.Count < n)
        throw new ArgumentException(n == 1 ? "Give at least one source." : $"Give at least {n} sources.");
}

static async Task<CompareItem> OpenItemAsync(string spec, CliArgs cli, FfTools tools, CancellationToken ct)
{
    // "path@00800.mpls" or "path@3" picks a title
    var path = spec;
    string? titleSel = null;
    var at = spec.LastIndexOf('@');
    if (at > 2 && !File.Exists(spec) && !Directory.Exists(spec))
    {
        path = spec[..at];
        titleSel = spec[(at + 1)..];
    }
    var src = await SourceOpener.OpenAsync(path, cli.Has("all-playlists"), ct);
    var title = src.MainTitle!;
    if (titleSel != null)
    {
        title = src.Titles.FirstOrDefault(t => t.Name.Equals(titleSel, StringComparison.OrdinalIgnoreCase) || t.Name.Equals("Title " + titleSel, StringComparison.OrdinalIgnoreCase))
                ?? (int.TryParse(titleSel, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= src.Titles.Count ? src.Titles[n - 1] : null)
                ?? throw new ArgumentException($"No title '{titleSel}' in {src.DisplayName}. Use 'nec2u info --titles' to list them.");
    }
    await new MediaProber(tools).ProbeAsync(src, title, ct);
    return new CompareItem { Source = src, Title = title };
}

static void PrintInfo(CompareItem it, bool titles)
{
    var src = it.Source;
    Console.WriteLine($"{src.DisplayName}  ({src.Kind}{(src.MountedRoot != null ? ", mounted at " + src.MountedRoot : "")})");
    if (src.Kind != SourceKind.File && (titles || src.Titles.Count <= 12))
    {
        for (var i = 0; i < src.Titles.Count; i++)
        {
            var t = src.Titles[i];
            Console.WriteLine($"  {(t == it.Title ? ">" : " ")}{i + 1,3}. {t}{(t.IsMain ? "  [main]" : "")}");
        }
    }
    var info = it.Title.Info!;
    Console.WriteLine($"  {info.Container}, {Format.Time(info.Duration)}, {Format.Size(info.SizeBytes)}, {Format.BitRate(info.OverallBitRate)}, {info.Chapters.Count} chapters");
    foreach (var s in info.Streams.Where(s => s.Kind != StreamKind.Other))
        Console.WriteLine($"  {s.Kind,-8} {s.Label}");
    if (info.Video is { } v)
    {
        var extra = new[] { v.HdrFormat, v.MasteringDisplay, v.ContentLight, string.Join("/", new[] { v.ColorPrimaries, v.ColorTransfer, v.ColorSpace }.Where(x => x.Length > 0)) }.Where(x => x.Length > 0);
        foreach (var e in extra)
            Console.WriteLine("           " + e);
    }
    Console.WriteLine();
}

static void PrintSpecs(List<CompareItem> items, string? onlyGroup = null)
{
    var rows = SpecTable.Build(items).Where(r => onlyGroup == null || r.Group == onlyGroup).ToList();
    var w = Math.Max(14, rows.Count > 0 ? rows.Max(r => r.Label.Length) : 0);
    var width = Console.IsOutputRedirected ? 160 : Math.Max(80, Console.WindowWidth);
    var colW = Math.Max(18, (width - w - 4) / items.Count - 2);
    Console.WriteLine(new string(' ', w + 2) + string.Join("  ", items.Select((i, k) => Fit($"[{k + 1}] {i.Name}", colW))));
    string? group = null;
    foreach (var r in rows)
    {
        if (r.Group != group)
        {
            group = r.Group;
            Console.WriteLine("— " + group);
        }
        Console.WriteLine((r.Differs ? "* " : "  ") + r.Label.PadRight(w) + string.Join("  ", r.Values.Select(v => Fit(v, colW))));
    }
}

static string Fit(string s, int w) => s.Length > w ? s[..Math.Max(1, w - 1)] + "…" : s.PadRight(w);

static void PrintCross(List<CompareItem> items, Func<CompareItem, CompareItem, List<Finding>> cross)
{
    for (var i = 1; i < items.Count; i++)
    {
        var f = cross(items[0], items[i]);
        if (f.Count == 0)
            continue;
        Console.WriteLine($"== {items[i].Name} compared with {items[0].Name}");
        PrintFindings(f);
    }
}

static void PrintFindings(List<Finding> findings)
{
    foreach (var f in findings.OrderByDescending(f => f.Level))
        Console.WriteLine($"  {(f.Level == 2 ? "!!" : f.Level == 1 ? " !" : "  ")} {f.Area}: {f.Text}");
}

static async Task SyncAsync(List<CompareItem> items, FfTools tools, string temp, CliArgs cli, IProgress<string> progress, CancellationToken ct)
{
    var max = cli.Get("max-shift") is { } m && double.TryParse(m, CultureInfo.InvariantCulture, out var mv) ? mv : 60;
    for (var i = 1; i < items.Count; i++)
    {
        if (items[0].Title.Info?.Audio.Any() != true || items[i].Title.Info?.Audio.Any() != true)
        {
            Console.WriteLine($"Sync [{i + 1}]: no audio to match.");
            continue;
        }
        var sopt = new SyncOptions { MaxShift = max, WholeTitle = cli.Has("whole"), DetectEdits = cli.Has("edits") };
        var r = await new SyncDetector(tools, temp).DetectAsync(items[0].Title, items[i].Title, sopt, progress, ct);
        foreach (var p in r.Probes)
            await Console.Error.WriteLineAsync($"    at {Format.Time(p.RefTime, false)}: {p.Offset:+0.000;-0.000;0.000} s (confidence {p.Confidence:0.0}, ncc {p.Ncc:0.00})");
        Console.WriteLine($"Sync [{i + 1}] {items[i].Name}: {r.Summary}");
        if (r.Reliable)
            Apply(items[i], r);
    }
}

static void Apply(CompareItem item, SyncResult r)
{
    item.Sections.Clear();
    item.Speed = r.Speed;
    item.Offset = r.Offset;
    item.Sections.AddRange(r.Sections);
}

static async Task<List<ShotRow>> ShotsAsync(List<CompareItem> items, FfTools tools, string temp, string outDir, CliArgs cli, CancellationToken ct)
{
    var count = cli.Get("n") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : 6;
    var start = items.Where(i => !i.Title.IsStill).Select(i => i.Coverage().Start).DefaultIfEmpty(0).Max();
    var common = items.Where(i => !i.Title.IsStill).Select(i => i.Coverage().End).DefaultIfEmpty(0).Min() - Math.Max(0, start);
    if (items.All(i => i.Title.IsStill))
        cli = cli.WithDefault("at", "0");
    var times = cli.Get("at") is { } at
        ? at.Split(',').Select(s => Format.TryParseTime(s, out var t) ? t : throw new ArgumentException("Bad time " + s)).ToList()
        : cli.Has("random") ? InputBuilder.RandomPoints(common, count, cli.Get("seed") is { } sd ? int.Parse(sd, CultureInfo.InvariantCulture) : 2024).Select(t => t + Math.Max(0, start)).ToList()
        : cli.Has("smart") ? ShotPicker.Pick(await new ShotPicker(tools, temp).ScanAsync(items[0].Title, null, ct: ct), count, Math.Max(0, start), Math.Max(0, start) + common).Select(p => p.Time).ToList()
        : cli.Has("chapters") ? items[0].Title.Info!.Chapters.Select(c => c.Start + 10).Where(t => t < common).ToList()
        : InputBuilder.EvenPoints(common, count).Select(t => t + Math.Max(0, start)).ToList();
    var opt = new ShotOptions
    {
        CorrectAspect = !cli.Has("no-aspect"),
        Deinterlace = cli.Has("deinterlace"),
        ToneMap = cli.Get("tonemap") switch
        {
            "on" => ToneMapMode.On,
            "off" => ToneMapMode.Off,
            _ => ToneMapMode.Auto,
        },
        ScaleToHeight = cli.Get("height") is { } h ? int.Parse(h, CultureInfo.InvariantCulture) : 0,
    };
    var shooter = new Screenshotter(tools, temp);
    var rows = new List<ShotRow>();
    var k = 0;
    foreach (var t in times)
    {
        var row = new ShotRow { Time = t };
        k++;
        double? refFrame = null;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var png = Path.Combine(outDir, $"{k:00}_{Format.Time(t).Replace(':', '.')}_{i + 1}_{Safe(it.Name)}.png");
            var r = await shooter.TakeAsync(it.Title, FrameMatch.TargetTime(items[0], refFrame, t, it), png, opt.With(SubtitleChoice.Pick(it.Title, cli.Get("subs"))), ct);
            if (i == 0 && r.Error == null)
                refFrame = r.FrameTime;
            row.Shots.Add(r);
            await Console.Error.WriteLineAsync($"  {Format.Time(t)} [{i + 1}] {r.Error ?? $"{r.PictType}-frame {r.Width}×{r.Height}"}");
        }
        rows.Add(row);
    }
    return rows;
}

static VideoAnalysisOptions VideoOptions(CliArgs cli) => new()
{
    SamplePoints = cli.Get("points") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 8,
    NativeResolution = !cli.Has("no-native"),
    Deinterlace = cli.Has("deinterlace"),
};

static AudioAnalysisOptions AudioOptions(CliArgs cli) => new()
{
    FullLength = cli.Has("full"),
    Windows = cli.Get("windows") is { } w ? int.Parse(w, CultureInfo.InvariantCulture) : 8,
};

static IEnumerable<int> Tracks(CompareItem it, CliArgs cli)
{
    var count = it.Title.Info?.Audio.Count() ?? 0;
    if (cli.Get("track") is { } t)
    {
        return t == "all"
            ? Enumerable.Range(0, count)
            : t.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture) - 1).Where(x => x >= 0 && x < count).ToList();
    }
    return count > 0 ? [0] : [];
}

static MetricOptions MetricOpts(CliArgs cli)
{
    var o = new MetricOptions();
    if (cli.Get("metrics") is { } m && m != "true")
    {
        o.Metrics = Metric.None;
        foreach (var x in m.Split(','))
            o.Metrics |= Enum.Parse<Metric>(x, true);
    }
    if (cli.Get("start") is { } s && Format.TryParseTime(s, out var st))
        o.Start = st;
    if (cli.Get("length") is { } l && Format.TryParseTime(l, out var len))
        o.Length = len;
    if (cli.Get("subsample") is { } ss)
        o.Subsample = int.Parse(ss, CultureInfo.InvariantCulture);
    o.DeinterlaceReference = cli.Has("deinterlace");
    o.AutoCrop = !cli.Has("no-crop");
    return o;
}

static async Task<MetricResult> RunMetricsAsync(CompareItem rf, CompareItem ds, MetricOptions opt, FfTools tools, string temp, CancellationToken ct)
{
    var last = -1;
    var prog = new Progress<(double Fraction, string Text)>(p =>
    {
        var pct = (int)(p.Fraction * 100);
        if (pct / 5 != last / 5)
        {
            last = pct;
            Console.Error.Write($"\r  {ds.Name}: {pct}%   ");
        }
    });
    // offset of the distorted source for the measured window (its section, when it is a different edit)
    var t0 = double.IsNaN(opt.Start) ? rf.Title.Duration / 2 : opt.Start;
    var off = ds.SourceTime(t0) is { } st ? st - (rf.SourceTime(t0) ?? t0) * ds.Speed : ds.Offset - rf.Offset;
    var res = await new QualityMetrics(tools, temp).RunAsync(rf.Title, ds.Title, off, opt, prog, ds.Speed, ct);
    await Console.Error.WriteLineAsync();
    return res;
}

static string Safe(string s)
{
    var bad = Path.GetInvalidFileNameChars();
    var r = new string(s.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray());
    return r.Length > 40 ? r[..40] : r;
}

static void PrintMetrics(CompareItem rf, CompareItem ds, MetricResult res)
{
    Console.WriteLine($"== {ds.Name} vs {rf.Name}: {res.Frames} frames from {Format.Time(res.Start)} ({res.ReferenceGeometry}){(res.Notes.Length > 0 ? "\n   " + res.Notes : "")}");
    foreach (var (m, s) in res.Series)
    {
        var f = m == Metric.Ssim ? "0.0000" : "0.00";
        Console.WriteLine($"   {s.Name,-6} mean {Format.Num(s.Mean, f),8}   1% low {Format.Num(s.Low1, f),8}   min {Format.Num(s.Min, f),8}" + (m == Metric.Vmaf ? $"   harmonic {Format.Num(s.HarmonicMean, f)}" : ""));
    }
    Console.WriteLine("   worst frames: " + string.Join(", ", res.Worst(5).Select(w => $"{Format.Time(res.TimeOf(w.Frame))} ({w.Value:0.###})")));
}
