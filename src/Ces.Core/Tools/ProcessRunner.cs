using System.Diagnostics;
using System.Text;

namespace Ces.Tools;

public sealed record ProcessResult(int ExitCode, byte[] StdOut, string StdErr)
{
    public string StdOutText => Encoding.UTF8.GetString(StdOut);
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs ffmpeg/ffprobe/mediainfo with an argument list (no shell quoting), capturing output.</summary>
public static class ProcessRunner
{
    /// <summary>Every command line that is started (for the log tab / --verbose).</summary>
    public static event Action<string>? CommandStarted;

    private const string NestedWhitelist = "file,subfile,concat,crypto,data,http,tcp";

    /// <summary>
    /// Files inside RAR sets / disc images are handed over as URLs of the built-in media server. A "concat:" or
    /// "subfile," input that now reads over HTTP needs http/tcp on ffmpeg's protocol whitelist.
    /// </summary>
    internal static List<string> ExternalizeArgs(string exe, IEnumerable<string> args)
    {
        var list = new List<string>();
        bool ffprobe = Path.GetFileNameWithoutExtension(exe).Equals("ffprobe", StringComparison.OrdinalIgnoreCase);
        foreach (var a in args)
        {
            var x = Ces.Vfs.Vfs.Externalize(a);
            bool nestedHttp = !ReferenceEquals(x, a) && x != a && (x.StartsWith("concat:", StringComparison.Ordinal) || x.StartsWith("subfile,", StringComparison.Ordinal));
            if (nestedHttp)
            {
                if (list.Count > 0 && list[^1] == "-i")
                    list.InsertRange(list.Count - 1, ["-protocol_whitelist", NestedWhitelist]);
                else if (ffprobe)
                    list.InsertRange(0, ["-protocol_whitelist", NestedWhitelist]);
            }
            list.Add(x);
        }
        return list;
    }

    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        CancellationToken ct = default,
        Action<string>? stderrLine = null,
        string? workingDirectory = null,
        Stream? stdoutSink = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in ExternalizeArgs(exe, args))
            psi.ArgumentList.Add(a);
        if (workingDirectory != null)
        {
            Directory.CreateDirectory(workingDirectory);
            psi.WorkingDirectory = workingDirectory;
        }

        CommandStarted?.Invoke(Describe(exe, psi.ArgumentList));

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();

        var err = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lock (err)
                {
                    // keep the tail only: ffmpeg can be chatty on long runs
                    if (err.Length > 4_000_000)
                        err.Remove(0, 1_000_000);
                    err.AppendLine(line);
                }
                stderrLine?.Invoke(line);
            }
        }, CancellationToken.None);

        var ms = stdoutSink == null ? new MemoryStream() : null;
        var outTask = proc.StandardOutput.BaseStream.CopyToAsync(stdoutSink ?? ms!, CancellationToken.None);

        using (ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }))
        {
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        string errText;
        lock (err)
            errText = err.ToString();
        return new ProcessResult(proc.ExitCode, ms?.ToArray() ?? [], errText);
    }

    public static string Describe(string exe, IEnumerable<string> args) =>
        Quote(Path.GetFileNameWithoutExtension(exe)) + " " + string.Join(' ', args.Select(Quote));

    private static string Quote(string a) =>
        a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or ';' or '[' or '|') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
