using System.Text.Json;
using System.Text.Json.Serialization;

namespace EncoderSpot.Services;

public sealed class AppSettings
{
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public string? LastFolder { get; set; }
    public bool IncludeSubfolders { get; set; }
    public bool DecodeFlac { get; set; } = true;
    public bool ProblemsOnly { get; set; }
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1300;
    public double WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; }
    public double TreeWidth { get; set; } = 260;
    public double DetailsX { get; set; } = double.NaN;
    public double DetailsY { get; set; } = double.NaN;
    public double DetailsWidth { get; set; } = 820;
    public double DetailsHeight { get; set; } = 620;

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "EncoderSpot");

    private static string FilePath => Path.Combine(ConfigDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new AppSettings();
            }
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
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are a convenience; ignore write failures
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
