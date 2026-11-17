using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ces.Vfs;

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 that serves files inside RAR sets and disc images with byte ranges, so
/// ffmpeg, ffprobe and MediaInfo can read and seek them like normal files. Only registered paths are served.
/// </summary>
public sealed class MediaServer : IDisposable
{
    private static readonly Lazy<MediaServer> Instance = new(() => new MediaServer());
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, string> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private int _next;

    public static MediaServer Shared => Instance.Value;

    private MediaServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>The URL that serves <paramref name="path"/> (a virtual path); the file name stays at the end so tools see the extension.</summary>
    public string UrlFor(string path)
    {
        string id = _byPath.GetOrAdd(path, p =>
        {
            string n = Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
            _byId[n] = p;
            return n;
        });
        return $"http://127.0.0.1:{Port}/f/{id}/{Vfs.Utf8Escape(Vfs.FileName(path))}";
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client), _cts.Token);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var net = client.GetStream();
            Stream? file = null;
            string? openPath = null;
            try
            {
                while (true)
                {
                    var request = await ReadHeadAsync(net).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }
                    var lines = request.Split("\r\n");
                    var first = lines[0].Split(' ');
                    if (first.Length < 2 || first[0] is not ("GET" or "HEAD"))
                    {
                        await WriteAsync(net, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                        return;
                    }
                    bool head = first[0] == "HEAD";
                    var seg = first[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (seg.Length < 2 || seg[0] != "f" || !_byId.TryGetValue(seg[1], out var path))
                    {
                        await WriteAsync(net, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                        continue;
                    }
                    if (openPath != path)
                    {
                        if (file != null)
                        {
                            await file.DisposeAsync().ConfigureAwait(false);
                        }
                        file = Vfs.OpenRead(path);
                        openPath = path;
                    }
                    long length = file!.Length;
                    var (from, to, partial) = ParseRange(lines, length);
                    if (from > to || from >= length)
                    {
                        await WriteAsync(net, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                        continue;
                    }
                    long count = to - from + 1;
                    var sb = new StringBuilder();
                    sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                    sb.Append("Accept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nConnection: keep-alive\r\n");
                    sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {count}\r\n");
                    if (partial)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {from}-{to}/{length}\r\n");
                    }
                    sb.Append("\r\n");
                    await WriteAsync(net, sb.ToString()).ConfigureAwait(false);
                    if (!head)
                    {
                        await CopyAsync(file, net, from, count).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException or UnauthorizedAccessException)
            {
                // client went away (ffmpeg closes connections when it seeks) or the file could not be read
            }
            finally
            {
                if (file != null)
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task CopyAsync(Stream file, NetworkStream net, long from, long count)
    {
        var buf = new byte[1 << 20];
        file.Position = from;
        while (count > 0)
        {
            int n = await file.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count))).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }
            await net.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
            count -= n;
        }
    }

    private static (long From, long To, bool Partial) ParseRange(string[] lines, long length)
    {
        foreach (var l in lines)
        {
            if (!l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string v = l[6..].Trim();
            if (!v.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            var r = v[6..].Split(',')[0].Split('-');
            if (r[0].Length == 0 && long.TryParse(r[1], CultureInfo.InvariantCulture, out long suffix))
            {
                return (Math.Max(0, length - suffix), length - 1, true);
            }
            if (long.TryParse(r[0], CultureInfo.InvariantCulture, out long a))
            {
                long b = r.Length > 1 && long.TryParse(r[1], CultureInfo.InvariantCulture, out long bb) ? Math.Min(bb, length - 1) : length - 1;
                return (a, b, true);
            }
        }
        return (0, length - 1, false);
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream net)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 16384)
        {
            int n = await net.ReadAsync(one).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r')
            {
                return sb.ToString(0, sb.Length - 4);
            }
        }
        return null;
    }

    private static Task WriteAsync(NetworkStream net, string text) => net.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
