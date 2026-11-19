using Finding = Ces.Analysis.Finding;
using Ces.Cache;
using Ces.Report;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ces.Spot;
using Ces.SpotUi;

namespace Ces.Tests;

public sealed class ResultsCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ces-cache-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly JsonTypeInfo<string> Str = (JsonTypeInfo<string>)new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(string));

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void StoresAndReturnsSpotResults()
    {
        var cache = new ResultsCache(Path.Combine(_dir, "c"), 10_000_000);
        var result = AudioAnalyzer.Analyze(Fixture("lame3100-v2.mp3"));
        var key = ResultsCache.Key("spot", ResultsCache.FileIdentity(Fixture("lame3100-v2.mp3")));

        Assert.Null(cache.Get("spot", key, SpotJson.Result));
        cache.Put("spot", key, result, SpotJson.Result);
        var back = cache.Get("spot", key, SpotJson.Result);

        Assert.NotNull(back);
        Assert.Equal(SpotJson.Serialize(result), SpotJson.Serialize(back));
        Assert.True(cache.SizeBytes() > 0);
    }

    [Fact]
    public void ChangedFileGetsANewKey()
    {
        var file = Path.Combine(_dir, "a.mp3");
        File.Copy(Fixture("lame3100-v2.mp3"), file);
        var before = ResultsCache.FileIdentity(file);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));
        Assert.NotEqual(before, ResultsCache.FileIdentity(file));

        File.AppendAllText(file, "x");
        Assert.DoesNotContain("|" + new FileInfo(Fixture("lame3100-v2.mp3")).Length + "|", ResultsCache.FileIdentity(file));
    }

    [Fact]
    public void FileInsideRarSetHasAnIdentity()
    {
        var inner = Vfs.Vfs.FilesInside(Fixture(Path.Combine("rar", "set4.rar")), AudioAnalyzer.IsSupported).First();
        Assert.Contains(Path.GetFileName(inner.Split("::")[^1]), ResultsCache.FileIdentity(inner), StringComparison.Ordinal);
    }

    [Fact]
    public void TrimDropsLeastRecentlyUsed()
    {
        var cache = new ResultsCache(Path.Combine(_dir, "t"), long.MaxValue);
        var blob = new string('x', 20_000);
        for (var i = 0; i < 5; i++)
        {
            cache.Put("k", "e" + i, blob + i, Str);
            File.SetLastWriteTimeUtc(Path.Combine(_dir, "t", "k", "e" + i + ".json.gz"), DateTime.UtcNow.AddMinutes(i - 10));
        }
        // reading e0 makes it the most recently used
        Assert.NotNull(cache.Get("k", "e0", Str));
        var one = new FileInfo(Path.Combine(_dir, "t", "k", "e1.json.gz")).Length;
        cache.MaxBytes = one * 2 + one / 2;
        cache.Trim();

        var left = Directory.GetFiles(Path.Combine(_dir, "t", "k")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["e0.json.gz", "e4.json.gz"], left);
    }

    [Fact]
    public void ZeroLimitTurnsTheCacheOff()
    {
        var cache = new ResultsCache(Path.Combine(_dir, "off"), 0);
        cache.Put("k", "a", "value", Str);
        Assert.False(Directory.Exists(Path.Combine(_dir, "off")));
        Assert.Null(cache.Get("k", "a", Str));
    }

    [Fact]
    public void TextFindingsLookLikeTheCards()
    {
        var text = TextReport.Findings("Title", [
            new Finding { Area = "Note", Text = "fine", Level = 0 },
            new Finding { Area = "Bad", Text = "broken", Level = 2 },
            new Finding { Area = "Odd", Text = "check", Level = 1 },
        ]);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal("Title", lines[0]);
        Assert.Equal("⚠ Bad: broken", lines[2]);
        Assert.Equal("• Odd: check", lines[3]);
        Assert.Equal("  Note: fine", lines[4]);
    }
}

public sealed class CompareCacheTypesTests
{
    /// <summary>Regression (0.3.2): the Compare cache asked SessionFile.Json for type info before any serialization ran.</summary>
    [Fact]
    public void SessionOptionsGiveTypeInfoUpFront()
    {
        Assert.NotNull(SessionFile.Json.GetTypeInfo(typeof(SessionSource)));
        Assert.NotNull(SessionFile.Json.GetTypeInfo(typeof(MainWindow.CachedPair)));
        Assert.NotNull(SessionFile.Json.GetTypeInfo(typeof(string)));
    }
}
