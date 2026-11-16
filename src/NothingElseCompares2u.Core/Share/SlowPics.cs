using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Share;

/// <summary>
/// Uploads a screenshot comparison to slow.pics (anonymous, the same protocol the open-source vs-preview plugin uses):
/// XSRF cookie from /comparison, the comparison layout to /upload/comparison, then each PNG to /upload/image/{uuid}.
/// Publishing is outward-facing: callers must ask the user first.
/// </summary>
public sealed class SlowPics : IDisposable
{
    private const string Base = "https://slow.pics";
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly string _browserId = Guid.NewGuid().ToString();

    public SlowPics()
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
        var v = typeof(SlowPics).Assembly.GetName().Version?.ToString(3) ?? "0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"NothingElseCompares2u/{v}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http.DefaultRequestHeaders.Referrer = new Uri(Base + "/comparison");
        _http.DefaultRequestHeaders.Add("Origin", Base);
    }

    public sealed record Upload(string Name, IReadOnlyList<(string Name, string Png)> Images);

    /// <summary>Uploads rows of pictures (one row = one moment, one picture per source); returns the comparison URL.</summary>
    public async Task<string> UploadAsync(string collectionName, IReadOnlyList<Upload> rows, bool isPublic, IProgress<(double Fraction, string Text)>? progress = null, CancellationToken ct = default)
    {
        if (rows.Count == 0 || rows.Any(r => r.Images.Count == 0))
            throw new ArgumentException("Nothing to upload.");
        progress?.Report((0, "Connecting to slow.pics…"));
        using (var hello = await _http.GetAsync(Base + "/comparison", ct).ConfigureAwait(false))
            hello.EnsureSuccessStatusCode();
        var xsrf = _cookies.GetCookies(new Uri(Base)).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value
                   ?? throw new IOException("slow.pics did not hand out a session token (XSRF-TOKEN); the site may have changed.");
        var token = Uri.UnescapeDataString(xsrf);

        // 1) the layout
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(collectionName), "collectionName");
        form.Add(new StringContent("false"), "hentai");
        form.Add(new StringContent("true"), "optimizeImages");
        form.Add(new StringContent(_browserId), "browserId");
        form.Add(new StringContent(isPublic ? "true" : "false"), "public");
        for (var j = 0; j < rows.Count; j++)
        {
            form.Add(new StringContent(rows[j].Name), $"comparisons[{j}].name");
            for (var i = 0; i < rows[j].Images.Count; i++)
                form.Add(new StringContent(rows[j].Images[i].Name), $"comparisons[{j}].imageNames[{i}]");
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/upload/comparison") { Content = form };
        req.Headers.Add("X-XSRF-TOKEN", token);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"slow.pics refused the comparison ({(int)resp.StatusCode}): {Short(body)}");
        string key, collection;
        var ids = new List<List<string>>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            key = doc.RootElement.GetProperty("key").GetString() ?? throw new IOException("No key in the slow.pics answer.");
            collection = doc.RootElement.GetProperty("collectionUuid").GetString() ?? "";
            foreach (var row in doc.RootElement.GetProperty("images").EnumerateArray())
                ids.Add(row.EnumerateArray().Select(x => x.GetString() ?? "").ToList());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException("Unexpected answer from slow.pics: " + Short(body), ex);
        }

        // 2) the pictures, a few at a time
        var total = rows.Sum(r => r.Images.Count);
        var done = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = new List<Task>();
        for (var j = 0; j < rows.Count; j++)
        {
            for (var i = 0; i < rows[j].Images.Count; i++)
            {
                var png = rows[j].Images[i].Png;
                var uuid = j < ids.Count && i < ids[j].Count ? ids[j][i] : throw new IOException("slow.pics returned fewer image slots than pictures.");
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await UploadImageAsync(collection, uuid, png, token, ct).ConfigureAwait(false);
                        var n = Interlocked.Increment(ref done);
                        progress?.Report(((double)n / total, $"Uploaded {n}/{total} pictures"));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return $"{Base}/c/{key}";
    }

    private async Task UploadImageAsync(string collection, string uuid, string png, string token, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(collection), "collectionUuid");
            form.Add(new StringContent(uuid), "imageUuid");
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(png, ct).ConfigureAwait(false));
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", Path.GetFileName(png));
            form.Add(new StringContent(_browserId), "browserId");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/upload/image/{uuid}") { Content = form };
            req.Headers.Add("X-XSRF-TOKEN", token);
            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return;
                if (attempt >= 3)
                    throw new IOException($"slow.pics refused a picture ({(int)resp.StatusCode}): {Short(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                // retry below
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 3)
            {
                // timeout: retry below
            }
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct).ConfigureAwait(false);
        }
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// <summary>Screenshot rows → slow.pics rows (only rows where every source has a picture).</summary>
    public static List<Upload> FromRows(IReadOnlyList<CompareItem> items, IEnumerable<ShotRow> rows) =>
        rows.Where(r => r.Shots.Count >= items.Count && r.Shots.Take(items.Count).All(s => s is { Error: null } && File.Exists(s.ImagePath)))
            .Select(r => new Upload(
                Format.Time(r.Time) + (r.Caption.Length > 0 ? " · " + r.Caption : ""),
                items.Select((it, i) => ($"{it.Name} ({r.Shots[i]!.PictType}-frame)", r.Shots[i]!.ImagePath)).ToList()))
            .ToList();

    public void Dispose() => _http.Dispose();
}
