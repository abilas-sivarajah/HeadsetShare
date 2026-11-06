using System.IO;
using System.Text.Json;

namespace HeadsetShare;

public sealed class SavedTarget
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Kind { get; set; }

    public double Volume { get; set; } = 1.0;

    public int DelayMs { get; set; }

    public bool Muted { get; set; }
}

public sealed class AppSettings
{
    // Die Umgebungsvariable erlaubt Testläufe, ohne die echten Einstellungen zu berühren.
    private static readonly string FilePath = Environment.GetEnvironmentVariable("HEADSETSHARE_SETTINGS")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HeadsetShare",
            "settings.json");

    public string? SourceId { get; set; }

    public bool MuteSource { get; set; }

    public List<SavedTarget> Targets { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings(); // Eine kaputte Datei soll den Start nicht verhindern.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nicht gespeicherte Einstellungen dürfen den laufenden Ton nicht stören.
        }
    }
}
