using System.Collections.Concurrent;
using System.Text;

namespace Ces.Vfs;

/// <summary>
/// Paths that reach inside archives and disc images. A virtual path is a real file followed by "::" and a path
/// inside it, possibly nested: <c>D:\rel\set.rar::movie.iso::BDMV\PLAYLIST\00800.mpls</c>. Plain paths behave like
/// normal files, so callers can use these helpers everywhere.
/// </summary>
public static class Vfs
{
    public const string Separator = "::";

    private static readonly ConcurrentDictionary<string, Lazy<object>> Containers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Media inside these container files can be opened.</summary>
    public static bool IsContainerName(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".rar" or ".iso" || IsOldStyleVolume(ext);
    }

    private static bool IsOldStyleVolume(string ext) =>
        ext.Length == 4 && ext[1] is >= 'r' and <= 'z' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]);

    public static bool IsVirtual(string path) => path.Contains(Separator, StringComparison.Ordinal);

    public static string Combine(string container, string inner) => container + Separator + inner.Replace('\\', '/');

    /// <summary>The real file the path starts in (the first RAR volume or the ISO on disk).</summary>
    public static string RealFile(string path)
    {
        int i = path.IndexOf(Separator, StringComparison.Ordinal);
        return i < 0 ? path : path[..i];
    }

    /// <summary>The last component's name ("00800.mpls", "movie.mkv").</summary>
    public static string FileName(string path)
    {
        string p = path.Replace('\\', '/');
        int sep = p.LastIndexOf(Separator, StringComparison.Ordinal);
        string tail = sep >= 0 ? p[(sep + Separator.Length)..] : p;
        return tail[(tail.LastIndexOf('/') + 1)..];
    }

    /// <summary>How the path is shown to people: "set.rar › movie.iso › BDMV/PLAYLIST/00800.mpls".</summary>
    public static string Display(string path) =>
        !IsVirtual(path) ? path : string.Join(" › ", path.Split(Separator).Select((p, i) => i == 0 ? Path.GetFileName(p) : p.Replace('\\', '/')));

    // ---------------------------------------------------------------- file system operations

    public static bool FileExists(string path)
    {
        if (!IsVirtual(path))
        {
            return File.Exists(path);
        }
        try
        {
            var (container, inner) = Resolve(path);
            return container switch
            {
                RarArchive rar => rar.Find(inner) is { IsDirectory: false },
                UdfImage udf => udf.FileExists(inner),
                _ => false,
            };
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return false;
        }
    }

    public static bool DirectoryExists(string path)
    {
        if (!IsVirtual(path))
        {
            return Directory.Exists(path);
        }
        try
        {
            var (container, inner) = Resolve(path);
            return container switch
            {
                RarArchive rar => inner.Length == 0 || rar.Entries.Any(e => e.Name.StartsWith(inner.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)),
                UdfImage udf => inner.Length == 0 || udf.DirectoryExists(inner),
                _ => false,
            };
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return false;
        }
    }

    public static long Length(string path)
    {
        if (!IsVirtual(path))
        {
            return new FileInfo(path).Length;
        }
        var (container, inner) = Resolve(path);
        return container switch
        {
            RarArchive rar => rar.Find(inner)?.Size ?? throw new FileNotFoundException(path),
            UdfImage udf => udf.Length(inner),
            _ => throw new FileNotFoundException(path),
        };
    }

    public static Stream OpenRead(string path)
    {
        if (!IsVirtual(path))
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        }
        var (container, inner) = Resolve(path);
        return container switch
        {
            RarArchive rar => RarArchive.OpenRead(rar.Find(inner) ?? throw new FileNotFoundException("Not in the RAR set: " + inner)),
            UdfImage udf => udf.OpenRead(inner),
            _ => throw new FileNotFoundException(path),
        };
    }

    public static byte[] ReadAllBytes(string path)
    {
        if (!IsVirtual(path))
        {
            return File.ReadAllBytes(path);
        }
        using var s = OpenRead(path);
        var data = new byte[s.Length];
        s.ReadExactly(data);
        return data;
    }

    /// <summary>Files directly in <paramref name="dir"/> (optionally with an extension like ".mpls").</summary>
    public static IEnumerable<string> EnumerateFiles(string dir, string? extension = null)
    {
        IEnumerable<string> all;
        if (!IsVirtual(dir))
        {
            all = Directory.Exists(dir) ? Directory.EnumerateFiles(dir) : [];
        }
        else
        {
            var (container, inner) = Resolve(dir);
                string innerDir = inner.TrimEnd('/');
            all = container switch
            {
                UdfImage udf => udf.List(innerDir).Where(e => !e.IsDirectory).Select(e => Rebase(dir, innerDir, e.Path)),
                RarArchive rar => rar.Entries.Where(e => !e.IsDirectory && ParentOf(e.Name) == innerDir).Select(e => Rebase(dir, innerDir, e.Name)),
                _ => [],
            };
        }
        return extension is null ? all : all.Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every file inside a container (RAR set or ISO), as virtual paths.</summary>
    public static List<string> ListContainer(string containerPath)
    {
        var c = Container(containerPath);
        return c switch
        {
            RarArchive rar => rar.Entries.Where(e => !e.IsDirectory).Select(e => Combine(containerPath, e.Name)).ToList(),
            UdfImage udf => Walk(udf, "").Select(p => Combine(containerPath, p)).ToList(),
            _ => [],
        };
    }

    /// <summary>Files inside a RAR set that match <paramref name="filter"/> (empty when the set cannot be read).</summary>
    public static List<string> FilesInside(string rarPath, Func<string, bool> filter)
    {
        try
        {
            return ListContainer(rarPath).Where(filter).ToList();
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return [];
        }
    }

    /// <summary>True for the file that names a whole RAR set (x.rar, x.part1.rar), not its other volumes.</summary>
    public static bool IsRarSet(string path) =>
        Path.GetExtension(path).Equals(".rar", StringComparison.OrdinalIgnoreCase) && RarArchive.IsFirstVolumeName(path);

    /// <summary>Why a file in a RAR set cannot be read in place (compressed, encrypted, volumes missing), or null.</summary>
    public static string? Problem(string path)
    {
        if (!IsVirtual(path))
        {
            return null;
        }
        try
        {
            var (container, inner) = Resolve(path);
            if (container is RarArchive rar && rar.Find(inner) is { } e)
            {
                return e.IsEncrypted ? "encrypted" : !e.IsStored ? "compressed (extract it first)" : !e.IsComplete ? "RAR volumes missing" : null;
            }
            return null;
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return ex.Message;
        }
    }

    private static IEnumerable<string> Walk(UdfImage udf, string dir)
    {
        foreach (var (p, isDir) in udf.List(dir))
        {
            if (isDir)
            {
                foreach (var f in Walk(udf, p))
                {
                    yield return f;
                }
            }
            else
            {
                yield return p;
            }
        }
    }

    private static string Rebase(string dir, string innerDir, string innerPath)
    {
        string rel = innerDir.Length == 0 ? innerPath : innerPath[(innerDir.Length + 1)..];
        string baseDir = dir.TrimEnd('/', '\\');
        return baseDir.EndsWith(Separator, StringComparison.Ordinal) ? baseDir + rel : baseDir + "/" + rel;
    }

    private static string ParentOf(string inner)
    {
        int i = inner.LastIndexOf('/');
        return i < 0 ? "" : inner[..i];
    }

    /// <summary>A stable identity for caches: real file size and time plus the inner path.</summary>
    public static string Identity(string path)
    {
        string real = RealFile(path);
        var fi = new FileInfo(real);
        return $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{(IsVirtual(path) ? path[real.Length..] : "")}";
    }

    // ---------------------------------------------------------------- containers

    /// <summary>Splits a virtual path into the innermost container object and the path inside it.</summary>
    private static (object Container, string Inner) Resolve(string path)
    {
        var parts = path.Split(Separator);
        string containerPath = parts[0];
        object container = Container(containerPath);
        for (int i = 1; i < parts.Length - 1; i++)
        {
            containerPath += Separator + parts[i];
            container = Container(containerPath);
        }
        return (container, UdfImage.Norm(parts[^1]));
    }

    /// <summary>Opens (and caches) the RAR set or UDF image at a real or virtual path.</summary>
    public static object Container(string path)
    {
        var lazy = Containers.GetOrAdd(path, p => new Lazy<object>(() => OpenContainer(p)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            Containers.TryRemove(path, out _);
            throw;
        }
    }

    private static object OpenContainer(string path)
    {
        string name = FileName(path);
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".iso")
        {
            return IsVirtual(path) ? UdfImage.Open(() => OpenRead(path)) : UdfImage.Open(path);
        }
        if (!IsVirtual(path) && (ext == ".rar" || IsOldStyleVolume(ext)))
        {
            return RarArchive.Open(path);
        }
        throw new InvalidDataException("Cannot look inside " + name + " (RAR sets and ISO images only).");
    }

    /// <summary>Drops cached containers (files changed on disk).</summary>
    public static void Forget(string realFile)
    {
        foreach (var key in Containers.Keys.Where(k => k.StartsWith(realFile, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Containers.TryRemove(key, out _);
        }
    }

    private static bool IsReadError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or EndOfStreamException;

    // ---------------------------------------------------------------- external tools

    /// <summary>
    /// Rewrites a command-line argument for ffmpeg/ffprobe/MediaInfo: virtual paths become URLs of the built-in
    /// media server, also inside "concat:a|b" lists and "subfile,,…,:path" wrappers. Other arguments are unchanged.
    /// </summary>
    public static string Externalize(string arg)
    {
        if (!arg.Contains(Separator, StringComparison.Ordinal))
        {
            return arg;
        }
        if (arg.StartsWith("concat:", StringComparison.Ordinal))
        {
            return "concat:" + string.Join("|", arg[7..].Split('|').Select(Externalize));
        }
        int sub = arg.IndexOf(",:", StringComparison.Ordinal);
        if (arg.StartsWith("subfile,", StringComparison.Ordinal) && sub > 0)
        {
            return arg[..(sub + 2)] + Externalize(arg[(sub + 2)..]);
        }
        return FileExists(arg) ? MediaServer.Shared.UrlFor(arg) : arg;
    }

    /// <summary>True when an argument list reads something through the media server (needs http in protocol whitelists).</summary>
    public static bool UsesServer(IEnumerable<string> args) => args.Any(a => a.Contains(Separator, StringComparison.Ordinal));

    internal static string Utf8Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            bool ok = b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'.' or (byte)'-' or (byte)'_';
            sb.Append(ok ? ((char)b).ToString() : $"%{b:X2}");
        }
        return sb.ToString();
    }
}
