using BDInfo;
using Ces.Disc;

namespace Ces.Cli;

/// <summary><c>ces bdinfo</c>: the BDInfo report of a Blu-ray (folder, ISO, RAR set); no ffmpeg needed.</summary>
internal static class BdInfoCommand
{
    public const string Usage = """
        Usage: ces bdinfo <Blu-ray folder | ISO | RAR set | release folder> [options]

        Prints the BDInfo report (disc, playlist, video/audio/subtitle streams, forum paste, quick summary).
        By default the main (longest) playlist is scanned in full for exact bitrates.

          -p, --playlist NAME[,NAME]  playlists to report (00800 or 00800.MPLS); repeatable
          -a, --all                   every playlist BDInfo lists (short/looping ones are left out)
          -q, --quick                 no full scan: stream details from the start of each file, estimated
                                      bitrates (BDInfo marks the report as incomplete)
          -l, --list                  only list the playlists
          -o, --out FILE              write the report to FILE instead of the console
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        string? path = null, outFile = null;
        var names = new List<string>();
        bool all = false, quick = false, list = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                case "-a" or "--all":
                    all = true;
                    break;
                case "-q" or "--quick":
                    quick = true;
                    break;
                case "-l" or "--list":
                    list = true;
                    break;
                case "-p" or "--playlist" when i + 1 < args.Length:
                    names.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "-o" or "--out" when i + 1 < args.Length:
                    outFile = args[++i];
                    break;
                default:
                    if (a.StartsWith('-') || path != null)
                    {
                        await Console.Error.WriteLineAsync("ces bdinfo: unexpected argument " + a + "\n\n" + Usage);
                        return 2;
                    }
                    path = a;
                    break;
            }
        }
        if (path == null)
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            using var disc = await BdInfoDisc.OpenAsync(path, cts.Token);
            foreach (var problem in disc.Problems)
                await Console.Error.WriteLineAsync("ces bdinfo: could not read " + problem);
            if (list)
            {
                PrintList(disc);
                return 0;
            }
            var playlists = Pick(disc, names, all);
            if (!quick)
                await ScanAsync(disc, playlists, cts.Token);
            var report = disc.Report(playlists);
            if (outFile != null)
            {
                await File.WriteAllTextAsync(outFile, report, cts.Token);
                await Console.Error.WriteLineAsync("Report written to " + outFile);
            }
            else
            {
                Console.Write(report);
            }
            return disc.ScanResult?.FileExceptions.Count > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("ces bdinfo: cancelled");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            await Console.Error.WriteLineAsync("ces bdinfo: " + ex.Message);
            return 2;
        }
    }

    private static List<TSPlaylistFile> Pick(BdInfoDisc disc, List<string> names, bool all)
    {
        if (names.Count > 0)
            return names.Select(n => disc.FindPlaylist(n) ?? throw new ArgumentException("No playlist " + n + " on the disc (see --list).")).ToList();
        if (all)
            return [.. disc.Playlists];
        return disc.MainPlaylist is { } main ? [main] : throw new InvalidDataException("BDInfo found no playlists on the disc.");
    }

    private static async Task ScanAsync(BdInfoDisc disc, List<TSPlaylistFile> playlists, CancellationToken ct)
    {
        var interactive = !Console.IsErrorRedirected;
        var last = -1;
        var progress = new Progress<double>(p =>
        {
            var pct = (int)(p * 100);
            if (pct == last)
                return;
            last = pct;
            if (interactive)
                Console.Error.Write($"\rScanning {string.Join(", ", playlists.Select(x => x.Name))}: {pct,3}%");
        });
        await disc.ScanAsync(playlists, progress, ct);
        if (interactive)
            await Console.Error.WriteLineAsync();
    }

    private static void PrintList(BdInfoDisc disc)
    {
        Console.WriteLine($"{disc.DisplayName}  ({disc.VolumeLabel}, {disc.Size:N0} bytes{(disc.IsUhd ? ", UHD" : "")}{(disc.Is3D ? ", 3D" : "")})");
        foreach (var p in disc.Playlists)
        {
            var len = TimeSpan.FromSeconds(p.TotalLength);
            var video = p.VideoStreams.FirstOrDefault();
            Console.WriteLine($"  {p.Name}  {(int)len.TotalHours}:{len:mm\\:ss}  {p.FileSize,16:N0}  {p.StreamClips.Count,3} clip(s)  {video?.CodecShortName} {video?.Description}");
        }
    }
}
