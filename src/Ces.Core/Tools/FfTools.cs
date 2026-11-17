namespace Ces.Tools;

/// <summary>Where ffmpeg, ffprobe and mediainfo live, and what the ffmpeg build can do.</summary>
public sealed class FfTools
{
    public string FFmpeg { get; }
    public string FFprobe { get; }
    public string? MediaInfo { get; }

    public bool HasVmaf { get; private set; }
    public bool HasZscale { get; private set; }
    public bool HasXpsnr { get; private set; }
    public string Version { get; private set; } = "";

    private FfTools(string ffmpeg, string ffprobe, string? mediainfo)
    {
        FFmpeg = ffmpeg;
        FFprobe = ffprobe;
        MediaInfo = mediainfo;
    }

    /// <summary>
    /// Finds the tools: explicit folder/paths first, then an "ffmpeg" folder next to the program, then PATH.
    /// Returns null with a reason when ffmpeg/ffprobe are missing.
    /// </summary>
    public static async Task<(FfTools? Tools, string? Error)> LocateAsync(string? ffmpegFolder = null, string? mediaInfoPath = null)
    {
        string? ffmpeg = null, ffprobe = null;
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(ffmpegFolder))
            dirs.Add(File.Exists(ffmpegFolder) ? Path.GetDirectoryName(ffmpegFolder)! : ffmpegFolder);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        dirs.Add(AppContext.BaseDirectory);
        foreach (var d in dirs)
        {
            ffmpeg ??= Existing(Path.Combine(d, Exe("ffmpeg")));
            ffprobe ??= Existing(Path.Combine(d, Exe("ffprobe")));
        }
        ffmpeg ??= FindOnPath("ffmpeg");
        ffprobe ??= FindOnPath("ffprobe");
        if (ffmpeg == null || ffprobe == null)
            return (null, "ffmpeg and ffprobe were not found. Install ffmpeg (https://ffmpeg.org) or set its folder in Settings.");

        string? mi = !string.IsNullOrWhiteSpace(mediaInfoPath) && File.Exists(mediaInfoPath) ? mediaInfoPath : null;
        mi ??= Existing(Path.Combine(AppContext.BaseDirectory, "mediainfo", Exe("mediainfo")));
        mi ??= FindOnPath("mediainfo");

        var t = new FfTools(ffmpeg, ffprobe, mi);
        await t.DetectCapabilitiesAsync().ConfigureAwait(false);
        return (t, null);
    }

    private async Task DetectCapabilitiesAsync()
    {
        try
        {
            var v = await ProcessRunner.RunAsync(FFmpeg, ["-hide_banner", "-version"]).ConfigureAwait(false);
            Version = v.StdOutText.Split('\n').FirstOrDefault()?.Trim() ?? "";
            var f = await ProcessRunner.RunAsync(FFmpeg, ["-hide_banner", "-filters"]).ConfigureAwait(false);
            var filters = f.StdOutText;
            HasVmaf = filters.Contains(" libvmaf ", StringComparison.Ordinal);
            HasZscale = filters.Contains(" zscale ", StringComparison.Ordinal);
            HasXpsnr = filters.Contains(" xpsnr ", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // leave capabilities off; the first real run reports the error
        }
    }

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? Existing(string p) => File.Exists(p) ? p : null;

    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), Exe(name));
                if (File.Exists(p))
                    return p;
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }
        if (!OperatingSystem.IsWindows())
        {
            foreach (var dir in new[] { "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p))
                    return p;
            }
        }
        return null;
    }
}
