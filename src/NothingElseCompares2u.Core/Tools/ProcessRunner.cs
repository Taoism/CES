using System.Diagnostics;
using System.Text;

namespace NothingElseCompares2u.Tools;

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
        foreach (var a in args)
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
