using System.Text.Json;
using System.Text.Json.Serialization;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
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

        try
        {
            using FileStream stream = File.OpenRead(settingsFile);
            return Normalize(JsonSerializer.Deserialize<OverlaySettings>(stream, SerializerOptions));
        }
        catch (JsonException)
        {
            return new OverlaySettings();
        }
        catch (IOException)
        {
            return new OverlaySettings();
        }
    }

    public void Save(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);
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

    private static OverlaySettings Normalize(OverlaySettings? settings)
    {
        settings ??= new OverlaySettings();
        settings.OffsetX = ClampFinite(settings.OffsetX, 0, 4000, 376);
        settings.OffsetY = ClampFinite(settings.OffsetY, 0, 4000, 6);
        settings.Scale = ClampFinite(settings.Scale, 0.8, 1.4, 1);
        settings.GracefulCloseTimeoutSeconds = Math.Clamp(settings.GracefulCloseTimeoutSeconds, 1, 60);
        settings.SettingsWindowWidth = ClampFinite(settings.SettingsWindowWidth, 900, 1800, 1000);
        settings.SettingsWindowHeight = ClampFinite(settings.SettingsWindowHeight, 620, 1400, 720);
        settings.SettingsWindowLeft = NormalizeCoordinate(settings.SettingsWindowLeft, -1);
        settings.SettingsWindowTop = NormalizeCoordinate(settings.SettingsWindowTop, -1);
        settings.Hotkeys ??= HotkeySettings.CreateDefault();
        settings.Hotkeys.ProfileHotkeys ??= [];
        return settings;
    }

    private static double NormalizeCoordinate(double value, double fallback)
    {
        return double.IsFinite(value) && Math.Abs(value) <= 100000
            ? value
            : fallback;
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}
