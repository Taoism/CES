using Ces.SpotUi;

namespace Ces.Tests;

public sealed class FolderTreeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ces-tree").FullName;

    public FolderTreeTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Concert.BluRay", "BDMV", "STREAM"));
        Directory.CreateDirectory(Path.Combine(_dir, "Concert.DVD", "VIDEO_TS"));
        Directory.CreateDirectory(Path.Combine(_dir, "Plain", "Sub"));
        File.WriteAllText(Path.Combine(_dir, "movie.mkv"), "x");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Compare_tree_lists_media_files_and_marks_discs()
    {
        var node = new FolderNode("t", _dir, files: p => Path.GetExtension(p) == ".mkv");
        node.IsExpanded = true;

        var byName = node.Children.ToDictionary(c => c.Name);
        Assert.Equal(["Concert.BluRay", "Concert.DVD", "Plain", "movie.mkv"], node.Children.Select(c => c.Name));
        Assert.Equal(FolderNodeKind.Disc, byName["Concert.BluRay"].Kind);
        Assert.Equal("BD", byName["Concert.BluRay"].Tag);
        Assert.Equal("DVD", byName["Concert.DVD"].Tag);
        Assert.Empty(byName["Concert.BluRay"].Children); // a disc is one source, not expanded
        Assert.Equal(FolderNodeKind.Folder, byName["Plain"].Kind);
        Assert.Single(byName["Plain"].Children); // lazy placeholder
        Assert.Equal(FolderNodeKind.File, byName["movie.mkv"].Kind);
        Assert.Equal("MKV", byName["movie.mkv"].Tag);
        Assert.True(byName["movie.mkv"].IsSource);
        Assert.False(byName["Plain"].IsSource);
    }

    [Fact]
    public void Spot_tree_lists_folders_only()
    {
        var node = new FolderNode("t", _dir);
        node.IsExpanded = true;

        Assert.Equal(["Concert.BluRay", "Concert.DVD", "Plain"], node.Children.Select(c => c.Name));
        Assert.All(node.Children, c => Assert.Equal(FolderNodeKind.Folder, c.Kind));
    }
}
