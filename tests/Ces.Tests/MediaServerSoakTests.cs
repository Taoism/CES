using System.Diagnostics;
using Ces.Vfs;

namespace Ces.Tests;

/// <summary>
/// Diagnostics, off by default: CES_SOAK_FILE = a (virtual) path; reads it through the media server at about
/// 20 MB/s, as slowly as ffmpeg decoding a film, and fails on 20 s without data. CES_SERVER_TRACE logs slow calls.
/// </summary>
public sealed class MediaServerSoakTests
{
    [Fact]
    public async Task SlowReaderGetsEveryByte()
    {
        var path = Environment.GetEnvironmentVariable("CES_SOAK_FILE");
        if (string.IsNullOrEmpty(path))
            return;
        var limit = long.Parse(Environment.GetEnvironmentVariable("CES_SOAK_BYTES") ?? "4000000000", System.Globalization.CultureInfo.InvariantCulture);
        var url = MediaServer.Shared.UrlFor(path);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[256 * 1024];
        long total = 0;
        var clock = Stopwatch.StartNew();
        while (total < limit)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            int n;
            try
            {
                n = await body.ReadAsync(buf, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"no data for 20 s after {total} bytes ({clock.Elapsed.TotalSeconds:0} s)");
                return;
            }
            if (n == 0)
                break;
            total += n;
            // pace: 20 MB/s
            var due = total / 20e6;
            var wait = due - clock.Elapsed.TotalSeconds;
            if (wait > 0)
                await Task.Delay(TimeSpan.FromSeconds(wait));
        }
        Assert.True(total >= Math.Min(limit, 1), $"read {total} bytes");
    }
}
