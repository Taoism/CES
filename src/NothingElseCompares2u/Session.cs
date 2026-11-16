using System.Text.Json;
using System.Text.Json.Serialization;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;

namespace NothingElseCompares2u;

/// <summary>A saved comparison (.nec2u): sources and their time maps, screenshot times, analysis and metric results.</summary>
public sealed class SessionFile
{
    public int Version { get; set; } = 1;
    public string App { get; set; } = "NothingElseCompares2u";
    public List<SessionSource> Sources { get; set; } = [];
    public List<SessionRow> Rows { get; set; } = [];
    public List<SessionMetric> Metrics { get; set; } = [];
    public List<SessionRank> Ranking { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task SaveAsync(string path)
    {
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, this, Json);
        File.Move(tmp, path, true);
    }

    public static async Task<SessionFile> LoadAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionFile>(fs, Json) ?? throw new InvalidDataException("Empty comparison file.");
    }
}

public sealed class SessionSource
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Label { get; set; } = "";
    public double Offset { get; set; }
    public double Speed { get; set; } = 1;
    public bool Synced { get; set; }
    public List<SyncSection> Sections { get; set; } = [];
    public VideoReport? Video { get; set; }
    public List<AudioReport> Audio { get; set; } = [];

    /// <summary>Spectrogram PNGs (base64) by audio track index: the originals live in a temp folder.</summary>
    public Dictionary<int, string> Spectrograms { get; set; } = [];
}

public sealed class SessionRow
{
    public double Time { get; set; }
    public string Caption { get; set; } = "";
}

public sealed class SessionMetric
{
    public int Reference { get; set; }
    public int Distorted { get; set; }
    public MetricResult? Result { get; set; }
}

public sealed class SessionRank
{
    public int Item { get; set; }
    public int Rank { get; set; }
    public string? Error { get; set; }
}
