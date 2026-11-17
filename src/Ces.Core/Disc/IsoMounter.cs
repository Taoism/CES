using System.Text.RegularExpressions;
using Ces.Tools;

namespace Ces.Disc;

/// <summary>
/// Mounts ISO images read-only with the operating system's own tools (Windows Mount-DiskImage, Linux udisksctl,
/// macOS hdiutil) — Blu-ray ISOs are UDF 2.50, which is easiest left to the OS.
/// </summary>
public static partial class IsoMounter
{
    public sealed record Mount(string Root, bool MountedByUs, string? Device);

    public static async Task<Mount> MountAsync(string iso, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
            return await MountWindowsAsync(iso, ct).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS())
            return await MountMacAsync(iso, ct).ConfigureAwait(false);
        return await MountLinuxAsync(iso, ct).ConfigureAwait(false);
    }

    public static async Task UnmountAsync(string iso, Mount mount)
    {
        if (!mount.MountedByUs)
            return;
        try
        {
            if (OperatingSystem.IsWindows())
                await Ps($"Dismount-DiskImage -ImagePath {PsQuote(iso)} | Out-Null", default).ConfigureAwait(false);
            else if (OperatingSystem.IsMacOS())
                await ProcessRunner.RunAsync("hdiutil", ["detach", mount.Device ?? mount.Root, "-quiet"]).ConfigureAwait(false);
            else if (mount.Device != null)
            {
                await ProcessRunner.RunAsync("udisksctl", ["unmount", "-b", mount.Device, "--no-user-interaction"]).ConfigureAwait(false);
                await ProcessRunner.RunAsync("udisksctl", ["loop-delete", "-b", mount.Device, "--no-user-interaction"]).ConfigureAwait(false);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // tool vanished; nothing more we can do
        }
    }

    private static async Task<Mount> MountWindowsAsync(string iso, CancellationToken ct)
    {
        var q = PsQuote(Path.GetFullPath(iso));
        // already attached (e.g. by Explorer)? use it and leave it mounted afterwards
        var script =
            $"$i = Get-DiskImage -ImagePath {q}; $ours = 0; " +
            $"if (-not $i.Attached) {{ $i = Mount-DiskImage -ImagePath {q} -Access ReadOnly -StorageType ISO -PassThru; $ours = 1 }}; " +
            "$l = $null; for ($n = 0; $n -lt 40 -and -not $l; $n++) { $l = ($i | Get-Volume -ErrorAction SilentlyContinue).DriveLetter; if (-not $l) { Start-Sleep -Milliseconds 250 } }; " +
            "\"$ours|$l\"";
        var res = await Ps(script, ct).ConfigureAwait(false);
        var line = res.StdOutText.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        var parts = line.Split('|');
        if (!res.Ok || parts.Length != 2 || parts[1].Length != 1)
            throw new IOException("Could not mount the ISO: " + FirstError(res.StdErr, line));
        return new Mount(parts[1] + ":\\", parts[0] == "1", null);
    }

    private static async Task<Mount> MountLinuxAsync(string iso, CancellationToken ct)
    {
        var setup = await ProcessRunner.RunAsync("udisksctl", ["loop-setup", "-r", "-f", iso, "--no-user-interaction"], ct).ConfigureAwait(false);
        var dev = LoopRegex().Match(setup.StdOutText);
        if (!setup.Ok || !dev.Success)
            throw new IOException("Could not attach the ISO (udisksctl loop-setup): " + FirstError(setup.StdErr, setup.StdOutText));
        var device = dev.Value.TrimEnd('.');
        // desktops often auto-mount the loop device; ask udisks where it is or mount it
        var mount = await ProcessRunner.RunAsync("udisksctl", ["mount", "-b", device, "-o", "ro", "--no-user-interaction"], ct).ConfigureAwait(false);
        var at = MountedAtRegex().Match(mount.StdOutText + mount.StdErr);
        if (!at.Success)
        {
            await ProcessRunner.RunAsync("udisksctl", ["loop-delete", "-b", device, "--no-user-interaction"], CancellationToken.None).ConfigureAwait(false);
            throw new IOException("Could not mount the ISO (udisksctl mount): " + FirstError(mount.StdErr, mount.StdOutText));
        }
        return new Mount(at.Groups[1].Value.Trim().Trim('`', '\'', '.'), true, device);
    }

    private static async Task<Mount> MountMacAsync(string iso, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync("hdiutil", ["attach", "-readonly", "-nobrowse", iso], ct).ConfigureAwait(false);
        if (!res.Ok)
            throw new IOException("Could not mount the ISO (hdiutil): " + FirstError(res.StdErr, ""));
        foreach (var line in res.StdOutText.Split('\n'))
        {
            var cols = line.Split('\t');
            if (cols.Length >= 3 && cols[^1].Trim().StartsWith('/'))
                return new Mount(cols[^1].Trim(), true, cols[0].Trim());
        }
        throw new IOException("hdiutil did not report a mount point.");
    }

    private static Task<ProcessResult> Ps(string script, CancellationToken ct) =>
        ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], ct);

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string FirstError(string err, string fallback)
    {
        var l = err.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
        return l ?? (fallback.Length > 0 ? fallback : "unknown error");
    }

    [GeneratedRegex(@"/dev/loop\d+")]
    private static partial Regex LoopRegex();

    [GeneratedRegex(@"(?:Mounted \S+ at|already mounted at) (.+)$", RegexOptions.Multiline)]
    private static partial Regex MountedAtRegex();
}
