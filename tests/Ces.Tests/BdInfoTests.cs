using Ces.Disc;
using Ces.Model;

namespace Ces.Tests;

public sealed class BdInfoTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public async Task Non_bluray_sources_are_refused_with_a_clear_message()
    {
        var mp3 = await Assert.ThrowsAsync<InvalidDataException>(() => BdInfoDisc.OpenAsync(Fx("lame3100-v2.mp3")));
        Assert.Contains("Blu-ray discs only", mp3.Message, StringComparison.Ordinal);
        var file = new MediaSource { Path = Fx("src.wav"), Kind = SourceKind.File };
        Assert.Throws<InvalidDataException>(() => BdInfoDisc.FromSource(file));
    }

    [Fact]
    public void Empty_bdmv_tree_opens_and_reports_without_playlists()
    {
        var root = Path.Combine(Path.GetTempPath(), "ces-bdinfo-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var d in new[] { "PLAYLIST", "CLIPINF", "STREAM" })
                Directory.CreateDirectory(Path.Combine(root, "BDMV", d));
            using var disc = BdInfoDisc.FromSource(new MediaSource { Path = root, Kind = SourceKind.BluRay, DisplayName = "empty" });
            Assert.Empty(disc.Playlists);
            Assert.Null(disc.MainPlaylist);
            Assert.Empty(disc.Problems);
            var report = disc.Report();
            Assert.Contains("Disc Label:", report, StringComparison.Ordinal);
            Assert.Contains("Scan has not been run", report, StringComparison.Ordinal);
            Assert.DoesNotContain("PLAYLIST:", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Missing_bd_structure_is_an_invalid_data_error()
    {
        var root = Path.Combine(Path.GetTempPath(), "ces-bdinfo-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "BDMV")); // no PLAYLIST / CLIPINF
            var ex = Assert.Throws<InvalidDataException>(() => BdInfoDisc.FromSource(new MediaSource { Path = root, Kind = SourceKind.BluRay }));
            Assert.StartsWith("BDInfo:", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Real disc (folder, ISO or RAR set) named by CES_BD_SAMPLE; skipped when it is not set. Scans the shortest
    /// playlist in full so it stays quick.
    /// </summary>
    [Fact]
    public async Task Real_disc_quick_read_and_full_scan()
    {
        var sample = Environment.GetEnvironmentVariable("CES_BD_SAMPLE");
        if (string.IsNullOrEmpty(sample) || !(File.Exists(sample) || Directory.Exists(sample)))
            return;
        using var disc = await BdInfoDisc.OpenAsync(sample);
        Assert.NotEmpty(disc.Playlists);
        var main = disc.MainPlaylist!;
        Assert.True(main.TotalLength > 0);
        Assert.NotEmpty(main.VideoStreams);
        var quick = disc.Report([main]);
        Assert.Contains("PLAYLIST: " + main.Name, quick, StringComparison.Ordinal);
        Assert.Contains("Scan has not been run", quick, StringComparison.Ordinal);

        var shortest = disc.Playlists.OrderBy(p => p.TotalLength).First();
        var seen = new List<double>();
        await disc.ScanAsync([shortest], new SyncProgress(seen), CancellationToken.None);
        Assert.NotNull(disc.ScanResult);
        Assert.Null(disc.ScanResult!.ScanException);
        Assert.Equal(1.0, seen[^1]);
        var full = disc.Report([shortest]);
        Assert.DoesNotContain("Report is incomplete", full, StringComparison.Ordinal);
        Assert.True(shortest.TotalBitRate > 0);
        Assert.True(shortest.VideoStreams[0].BitRate > 0);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disc.ScanAsync([main], null, cts.Token));
    }

    private sealed class SyncProgress(List<double> seen) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (seen)
                seen.Add(value);
        }
    }
}
