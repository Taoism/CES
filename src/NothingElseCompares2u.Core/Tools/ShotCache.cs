using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Tools;

/// <summary>
/// Keeps grabbed screenshots on disk, keyed by the source files (path, size, date), the exact time and the options:
/// re-taking a row, reopening a comparison or moving back and forth costs nothing the second time.
/// </summary>
public static class ShotCache
{
    public static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NothingElseCompares2u", "cache", "shots");

    /// <summary>Cache folder; null switches the cache off.</summary>
    public static string? Dir { get; set; } = DefaultDir;

    /// <summary>Upper size of the cache; the least recently used shots go first.</summary>
    public static long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    private static int _puts;

    private static readonly string BuildStamp = BuildId();

    private static string BuildId()
    {
        var asm = typeof(ShotCache).Assembly;
        try
        {
            return asm.GetName().Version + "-" + File.GetLastWriteTimeUtc(asm.Location).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return asm.GetName().Version?.ToString() ?? "0";
        }
    }

    public static string? Key(MediaTitle title, double time, string options)
    {
        if (Dir == null)
            return null;
        // the build is part of the key: a changed screenshot pipeline must not reuse old pictures
        var sb = new StringBuilder("v2|").Append(BuildStamp).Append('|');
        foreach (var f in title.Segments.SelectMany(s => Files(s.Url)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fi = new FileInfo(f);
                sb.Append(fi.FullName).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('|');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
        sb.Append(title.Name).Append('|').Append(time.ToString("0.0000", CultureInfo.InvariantCulture)).Append('|').Append(options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    /// <summary>The plain file paths behind a segment URL (subfile/concat protocol wrappers removed).</summary>
    internal static IEnumerable<string> Files(string url)
    {
        var u = url.StartsWith("concat:", StringComparison.Ordinal) ? url[7..] : url;
        foreach (var part in u.Split('|'))
        {
            var i = part.IndexOf(",,:", StringComparison.Ordinal);
            yield return i >= 0 ? part[(i + 3)..] : part;
        }
    }

    public static ShotResult? TryGet(string? key, string outPng)
    {
        if (key == null || Dir == null)
            return null;
        var png = Path.Combine(Dir, key + ".png");
        var meta = Path.Combine(Dir, key + ".json");
        try
        {
            if (!File.Exists(png) || !File.Exists(meta))
                return null;
            var cached = JsonSerializer.Deserialize(File.ReadAllText(meta), ShotCacheJson.Default.ShotResult);
            if (cached == null)
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
            File.Copy(png, outPng, true);
            File.SetLastAccessTimeUtc(png, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(meta, DateTime.UtcNow);
            return new ShotResult
            {
                ImagePath = outPng,
                RequestedTime = cached.RequestedTime,
                FrameTime = cached.FrameTime,
                PictType = cached.PictType,
                KeyFrame = cached.KeyFrame,
                Width = cached.Width,
                Height = cached.Height,
                Stats = cached.Stats,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Put(string? key, ShotResult result)
    {
        if (key == null || Dir == null || result.Error != null || !File.Exists(result.ImagePath))
            return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.Copy(result.ImagePath, Path.Combine(Dir, key + ".png"), true);
            File.WriteAllText(Path.Combine(Dir, key + ".json"), JsonSerializer.Serialize(result, ShotCacheJson.Default.ShotResult));
            if (Interlocked.Increment(ref _puts) % 50 == 0)
                Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a cache that cannot be written is just a slower run
        }
    }

    /// <summary>Removes the least recently used shots until the cache fits <see cref="MaxBytes"/>.</summary>
    public static void Trim()
    {
        if (Dir == null || !Directory.Exists(Dir))
            return;
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.json").Select(j => (Meta: j, Png: new FileInfo(Path.ChangeExtension(j.FullName, ".png"))))
                .OrderByDescending(x => x.Meta.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var (meta, png) in files)
            {
                total += meta.Length + (png.Exists ? png.Length : 0);
                if (total > MaxBytes)
                {
                    meta.Delete();
                    if (png.Exists)
                        png.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // try again next time
        }
    }

    public static long SizeBytes() =>
        Dir != null && Directory.Exists(Dir) ? new DirectoryInfo(Dir).GetFiles().Sum(f => f.Length) : 0;

    public static void Clear()
    {
        if (Dir != null && Directory.Exists(Dir))
        {
            foreach (var f in new DirectoryInfo(Dir).GetFiles())
            {
                try
                {
                    f.Delete();
                }
                catch (IOException)
                {
                    // in use
                }
            }
        }
    }
}

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(ShotResult))]
internal sealed partial class ShotCacheJson : JsonSerializerContext;
