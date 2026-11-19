using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ces.Cache;

/// <summary>
/// Analysis results kept on disk so revisiting a file or folder is instant: one gzip'd JSON file per entry under
/// <c>root/kind/</c>, keyed on the file's path, size and modification time (a changed file is a new key). Reading an
/// entry touches it; <see cref="Trim"/> drops the least recently used entries above the size limit.
/// A limit of 0 turns the cache off.
/// </summary>
public sealed class ResultsCache(string root, long maxBytes)
{
    private const string Ext = ".json.gz";

    public static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CES", "results");

    /// <summary>The app's cache (Spot and Compare); the limit comes from the settings.</summary>
    public static ResultsCache Shared { get; } = new(DefaultDir, 512L * 1024 * 1024);

    public string Root { get; } = root;

    public long MaxBytes { get; set; } = maxBytes;

    public bool Enabled => MaxBytes > 0;

    /// <summary>Identity of a file (also a file inside a RAR set): path, size and the real file's mtime.</summary>
    public static string FileIdentity(string path)
    {
        var real = Vfs.Vfs.RealFile(path);
        var mtime = File.GetLastWriteTimeUtc(real).Ticks;
        var size = Vfs.Vfs.Length(path);
        var name = Vfs.Vfs.IsVirtual(path) ? path : Path.GetFullPath(path);
        return string.Create(CultureInfo.InvariantCulture, $"{name}|{size}|{mtime}");
    }

    /// <summary>Same as <see cref="FileIdentity"/>, or null when the file cannot be read.</summary>
    public static string? TryFileIdentity(string path)
    {
        try
        {
            return FileIdentity(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The build of the code that made a result (version + commit): part of every key, so results from an older build
    /// (before an analysis was improved) are not served after an update.
    /// </summary>
    public static string BuildOf(Type type) =>
        type.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? type.Assembly.GetName().Version?.ToString() ?? "";

    public static string Key(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts).ToUpperInvariant()));
        return Convert.ToHexStringLower(hash, 0, 20);
    }

    private string EntryPath(string kind, string key) => Path.Combine(Root, kind, key + Ext);

    public T? Get<T>(string kind, string key, JsonTypeInfo<T> type) where T : class
    {
        if (!Enabled)
            return null;
        var path = EntryPath(kind, key);
        try
        {
            if (!File.Exists(path))
                return null;
            T? value;
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                value = JsonSerializer.Deserialize(gz, type);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // recently used
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            TryDelete(path); // unreadable entry: drop it
            return null;
        }
    }

    public void Put<T>(string kind, string key, T value, JsonTypeInfo<T> type)
    {
        if (!Enabled)
            return;
        var path = EntryPath(kind, key);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, value, type);
            File.Move(tmp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(tmp); // a full or read-only disk only costs the cache
        }
    }

    public void Remove(string kind, string key) => TryDelete(EntryPath(kind, key));

    private IEnumerable<FileInfo> Entries()
    {
        var dir = new DirectoryInfo(Root);
        return dir.Exists ? dir.EnumerateFiles("*" + Ext, SearchOption.AllDirectories) : [];
    }

    public long SizeBytes()
    {
        try
        {
            return Entries().Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Deletes the least recently used entries until the cache fits its limit.</summary>
    public void Trim()
    {
        try
        {
            var files = Entries().OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= MaxBytes)
                    break;
                total -= f.Length;
                TryDelete(f.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    public void Clear()
    {
        foreach (var f in Entries().ToList())
            TryDelete(f.FullName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use or gone
        }
    }
}
