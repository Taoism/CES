using System.Text.Json;
using System.Text.Json.Serialization;

namespace NothingElseCompares2u;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string FFmpegFolder { get; set; } = "";
    public string MediaInfoPath { get; set; } = "";
    public string LastFolder { get; set; } = "";
    public string ReportFolder { get; set; } = "";

    // screenshots
    public int ShotCount { get; set; } = 8;
    public string ShotMode { get; set; } = "Even";
    public bool CorrectAspect { get; set; } = true;
    public bool Deinterlace { get; set; }
    public string ToneMap { get; set; } = "Auto";
    public int RandomSeed { get; set; } = 2024;

    // analysis
    public int VideoPoints { get; set; } = 8;
    public bool NativeResolution { get; set; } = true;
    public bool AudioFull { get; set; }
    public double MetricLength { get; set; } = 60;
    public bool MetricPsnr { get; set; } = true;
    public bool MetricSsim { get; set; } = true;
    public bool MetricVmaf { get; set; } = true;
    public bool MetricXpsnr { get; set; }
    public int MetricSubsample { get; set; } = 1;
    public bool IncludeShortPlaylists { get; set; }

    /// <summary>"Near" (±90 s), "Whole" (whole title) or "Edits" (whole title, sections for a different edit).</summary>
    public string SyncMode { get; set; } = "Near";

    public List<string> RecentSessions { get; set; } = [];

    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 920;
    public bool Maximized { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NothingElseCompares2u");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are a convenience
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
