using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Ces.Spot;

namespace Ces.SpotUi;

/// <summary>Saved Spot results (.cess): the analysed files with everything the list and details windows show.</summary>
public sealed class SpotSession
{
    public const string Extension = ".cess";

    public static readonly FilePickerFileType FileType = new("CES spot results") { Patterns = ["*" + Extension] };

    public int Version { get; set; } = 1;
    public string App { get; set; } = "CES";
    public int Format { get; set; } = SpotJson.FormatVersion;

    /// <summary>What was opened (folders / files), so Rescan works after loading.</summary>
    public List<string> Inputs { get; set; } = [];
    public List<AnalysisResult> Results { get; set; } = [];

    public async Task SaveAsync(string path)
    {
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            await JsonSerializer.SerializeAsync(gz, this, SpotJson.Options);
        File.Move(tmp, path, true);
    }

    public static async Task<SpotSession> LoadAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        // gzip'd normally; plain JSON is accepted too (hand-edited files)
        var gzip = fs.ReadByte() == 0x1f && fs.ReadByte() == 0x8b;
        fs.Position = 0;
        await using Stream body = gzip ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        return await JsonSerializer.DeserializeAsync<SpotSession>(body, SpotJson.Options) ?? throw new InvalidDataException("Empty spot results file.");
    }
}

/// <summary>Saves the details of one or more analysed files as text or JSON.</summary>
public static class SpotDetailsFile
{
    private static readonly FilePickerFileType Text = new("Text") { Patterns = ["*.txt"] };
    private static readonly FilePickerFileType Json = new("JSON (everything, for scripts)") { Patterns = ["*.json"] };

    public static async Task<string?> SaveAsync(TopLevel? top, IReadOnlyList<AnalysisResult> results)
    {
        if (top?.StorageProvider is not { } sp || results.Count == 0)
            return null;
        var name = results.Count == 1 ? Path.GetFileNameWithoutExtension(Vfs.Vfs.Display(results[0].Path)) + " - details" : "ces-spot-details";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = results.Count == 1 ? "Save details" : $"Save details of {results.Count} files",
            SuggestedFileName = Safe(name) + ".txt",
            FileTypeChoices = [Text, Json],
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
            return null;
        string content = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? results.Count == 1 ? SpotJson.Serialize(results[0], indented: true) : JsonSerializer.Serialize(results, SpotJson.Indented)
            : string.Join(Environment.NewLine, results.Select(r => SpotReport.Text(r)));
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), CancellationToken.None);
        return path;
    }

    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }
}
