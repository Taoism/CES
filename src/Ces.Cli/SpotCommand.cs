using System.Globalization;
using System.Text.Json;
using Ces.Spot;

namespace Ces.Cli;

/// <summary><c>ces spot</c>: encoder spotting and stream checks for MP3 / FLAC.</summary>
internal static class SpotCommand
{
    private const string Usage = """
        ces spot - MP3 / FLAC encoder spotter and stream analyzer

        usage: ces spot [options] <file or folder>...

          -r, --recursive   include sub folders
          -d, --details     print every detail for each file
          -j, --json        JSON output (one array)
          -q, --quick       FLAC: read metadata only (no decoding, no MD5 check)
          -p, --problems    only list files with problems
          -t, --threads N   files analyzed in parallel (default: CPU count)
          -h, --help        this help

        exit code: 0 ok, 1 some files have problems, 2 usage error
        """;

    public static int Run(string[] args)
    {
        bool recursive = false, details = false, json = false, quick = false, problemsOnly = false;
        int threads = Environment.ProcessorCount;
        string? dumpPcm = null;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-r" or "--recursive":
                    recursive = true;
                    break;
                case "-d" or "--details":
                    details = true;
                    break;
                case "-j" or "--json":
                    json = true;
                    break;
                case "-q" or "--quick":
                    quick = true;
                    break;
                case "-p" or "--problems":
                    problemsOnly = true;
                    break;
                case "-t" or "--threads" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int n) && n > 0:
                    threads = n;
                    i++;
                    break;
                case "--dump-pcm" when i + 1 < args.Length:
                    dumpPcm = args[++i];
                    break;
                case "-h" or "--help" or "/?":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"unknown option {args[i]}");
                        return 2;
                    }
                    inputs.Add(args[i]);
                    break;
            }
        }
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                foreach (var f in Directory.EnumerateFiles(input, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (AudioAnalyzer.IsSupported(f))
                    {
                        files.Add(f);
                    }
                    else if (Ces.Vfs.Vfs.IsRarSet(f))
                    {
                        files.AddRange(Ces.Vfs.Vfs.FilesInside(f, AudioAnalyzer.IsSupported)); // MP3 / FLAC packed in RAR sets
                    }
                }
            }
            else if (File.Exists(input) && Ces.Vfs.Vfs.IsRarSet(input))
            {
                files.AddRange(Ces.Vfs.Vfs.FilesInside(Path.GetFullPath(input), AudioAnalyzer.IsSupported));
            }
            else if (File.Exists(input) || Ces.Vfs.Vfs.FileExists(input))
            {
                files.Add(input); // also "set.rar::track.flac" or "disc.iso::path"
            }
            else
            {
                Console.Error.WriteLine($"not found: {input}");
            }
        }

        using var pcm = dumpPcm is null ? null : File.Create(dumpPcm);
        var options = new AnalyzeOptions { DecodeFlac = !quick, PcmOutput = pcm };
        if (pcm is not null)
        {
            threads = 1;
        }
        var results = new AnalysisResult[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = threads },
            i => results[i] = AudioAnalyzer.Analyze(files[i], options));

        var shown = results.Where(r => !problemsOnly || r.Problems.Count > 0 || r.Error is not null).ToList();
        if (json)
        {
            WriteJson(shown);
        }
        else
        {
            foreach (var r in shown)
            {
                WriteText(r, details);
            }
            if (files.Count > 1)
            {
                int bad = results.Count(r => r.Problems.Count > 0 || r.Error is not null);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{files.Count} files, {bad} with problems"));
            }
        }
        return results.Any(r => r.Problems.Count > 0 || r.Error is not null) ? 1 : 0;
    }

    internal static void WriteText(AnalysisResult r, bool details)
    {
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(r.Path);
        if (r.Error is not null)
        {
            Console.WriteLine($"  error: {r.Error}");
            return;
        }
        string bits = r.BitsPerSample > 0 ? string.Create(inv, $"{r.BitsPerSample}-bit ") : "";
        Console.WriteLine(string.Create(inv,
            $"  {r.Format}  {r.Encoder}  [{r.Confidence}]  {r.BitrateKbps:0} kbps{(r.IsVbr && r.Format != "FLAC" ? " VBR" : "")}  {bits}{r.SampleRate} Hz {r.ModeName}  {Fmt.Duration(r.DurationSeconds)}  {r.Quality}"));
        if (r.EncoderSettings.Length > 0)
        {
            Console.WriteLine($"  settings: {r.EncoderSettings}");
        }
        if (r.SourceCheck.Length > 0)
        {
            Console.WriteLine($"  source: {r.SourceCheck}");
        }
        foreach (var p in r.Problems)
        {
            Console.WriteLine($"  ! {p}");
        }
        if (details)
        {
            string? group = null;
            foreach (var d in r.Details)
            {
                if (d.Group != group)
                {
                    group = d.Group;
                    Console.WriteLine($"  [{group}]");
                }
                Console.WriteLine($"    {d.Name,-26} {d.Value}");
            }
        }
    }

    private static void WriteJson(List<AnalysisResult> results)
    {
        using var stdout = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true });
        w.WriteStartArray();
        foreach (var r in results)
        {
            w.WriteStartObject();
            w.WriteString("path", r.Path);
            w.WriteString("format", r.Format);
            if (r.Error is not null)
            {
                w.WriteString("error", r.Error);
            }
            w.WriteString("encoder", r.Encoder);
            w.WriteString("confidence", r.Confidence.ToString());
            w.WriteString("reason", r.GuessReason);
            w.WriteString("settings", r.EncoderSettings);
            w.WriteString("quality", r.Quality.ToString());
            w.WriteNumber("durationSeconds", Math.Round(r.DurationSeconds, 3));
            w.WriteNumber("bitrateKbps", Math.Round(r.BitrateKbps, 1));
            w.WriteBoolean("vbr", r.IsVbr);
            w.WriteNumber("sampleRate", r.SampleRate);
            w.WriteNumber("channels", r.Channels);
            if (r.BitsPerSample > 0)
            {
                w.WriteNumber("bitsPerSample", r.BitsPerSample);
            }
            w.WriteNumber("frames", r.FrameCount);
            w.WriteStartArray("problems");
            foreach (var p in r.Problems)
            {
                w.WriteStringValue(p);
            }
            w.WriteEndArray();
            w.WriteStartObject("details");
            foreach (var g in r.Details.GroupBy(d => d.Group))
            {
                w.WriteStartObject(g.Key);
                foreach (var d in g)
                {
                    w.WriteString(d.Name, d.Value);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
