using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaGenie.Export;

/// <summary>Small settings file so the app remembers where FFmpeg lives.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? FfmpegPath { get; set; }

    public string? LastOutputFolder { get; set; }

    public string? LastInputFolder { get; set; }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MediaGenie",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // A corrupt settings file should never stop the app from starting.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string? folder = Path.GetDirectoryName(SettingsPath);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Settings are a convenience, not a requirement.
        }
    }
}
