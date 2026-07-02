using System.Text.Json;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string settingsFile;

    public SettingsService(string settingsFile)
    {
        this.settingsFile = Path.GetFullPath(settingsFile);
    }

    public OverlaySettings Load()
    {
        if (!File.Exists(settingsFile))
        {
            return new OverlaySettings();
        }

        using FileStream stream = File.OpenRead(settingsFile);
        return JsonSerializer.Deserialize<OverlaySettings>(stream, SerializerOptions) ?? new OverlaySettings();
    }

    public void Save(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);

        string temp = settingsFile + ".tmp";
        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, SerializerOptions);
        }

        if (File.Exists(settingsFile))
        {
            File.Replace(temp, settingsFile, null);
        }
        else
        {
            File.Move(temp, settingsFile);
        }
    }
}
