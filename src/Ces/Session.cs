using System.Text.Json;
using System.Text.Json.Serialization;
using Ces.Analysis;
using Ces.Compare;

namespace Ces;

/// <summary>A saved comparison (.cesc): sources and their time maps, screenshot times, analysis and metric results.</summary>
public sealed class SessionFile
{
    public int Version { get; set; } = 1;
    public string App { get; set; } = "CES";
    public List<SessionSource> Sources { get; set; } = [];
    public List<SessionRow> Rows { get; set; } = [];
    public List<SessionMetric> Metrics { get; set; } = [];
    public List<SessionRank> Ranking { get; set; } = [];
    public List<SessionSubtitles> Subtitles { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        // set explicitly: the results cache asks these options for type info before any (de)serialization has run
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>For a report: source paths from the release folder down (no drive, no folders above the release).</summary>
    public SessionFile WithReportPaths()
    {
        foreach (var src in Sources)
            src.Path = Report.ReportPath.Short(src.Path);
        return this;
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            JsonSerializer.Serialize(fs, this, Json);
        File.Move(tmp, path, true);
    }

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

    /// <summary>Grade &amp; framing against source 1 (frames and findings).</summary>
    public List<GradeFrame> GradeFrames { get; set; } = [];
    public List<Finding> GradeFindings { get; set; } = [];

    /// <summary>Frame timing, dirt and stabilisation against source 1.</summary>
    public List<Finding> TemporalFindings { get; set; } = [];
    public List<FrameEvent> FrameEvents { get; set; } = [];

    /// <summary>Inverse telecine applied to this source (29.97 pulldown → 23.976).</summary>
    public bool Ivtc { get; set; }

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

/// <summary>Subtitle timing of one source against the reference track.</summary>
public sealed class SessionSubtitles
{
    public int Item { get; set; }
    public int Track { get; set; }
    public string TrackLabel { get; set; } = "";
    public int Events { get; set; }
    public int ReferenceEvents { get; set; }
    public int Matched { get; set; }

    /// <summary>[timeline time, delta] per matched line.</summary>
    public List<double[]> Deltas { get; set; } = [];

    public double MedianDelta { get; set; } = double.NaN;
    public double P90AbsDelta { get; set; } = double.NaN;
    public double WithinFrameShare { get; set; } = double.NaN;
    public double DriftPerHour { get; set; } = double.NaN;
    public List<Finding> Findings { get; set; } = [];

    public static SessionSubtitles From(SubtitleTimingReport r, int item) => new()
    {
        Item = item,
        Track = r.Track,
        TrackLabel = r.TrackLabel,
        Events = r.Events,
        ReferenceEvents = r.ReferenceEvents,
        Matched = r.Matched,
        Deltas = r.Deltas.Select(d => new[] { d.Time, d.Delta }).ToList(),
        MedianDelta = r.MedianDelta,
        P90AbsDelta = r.P90AbsDelta,
        WithinFrameShare = r.WithinFrameShare,
        DriftPerHour = r.DriftPerHour,
        Findings = r.Findings,
    };

    public SubtitleTimingReport ToReport(CompareItem item)
    {
        var r = new SubtitleTimingReport
        {
            Item = item,
            Track = Track,
            TrackLabel = TrackLabel,
            Events = Events,
            ReferenceEvents = ReferenceEvents,
            Matched = Matched,
            MedianDelta = MedianDelta,
            P90AbsDelta = P90AbsDelta,
            WithinFrameShare = WithinFrameShare,
            DriftPerHour = DriftPerHour,
        };
        r.Deltas.AddRange(Deltas.Where(d => d.Length == 2).Select(d => (d[0], d[1])));
        r.Findings.AddRange(Findings);
        return r;
    }
}

public sealed class SessionRank
{
    public int Item { get; set; }
    public int Rank { get; set; }
    public string? Error { get; set; }
}
