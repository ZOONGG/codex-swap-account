using System.Text;

namespace CodexProfileOverlay.Core.Services;

public sealed class ActiveProfileStore
{
    private readonly string activeProfileFile;

    public ActiveProfileStore(string activeProfileFile)
    {
        this.activeProfileFile = Path.GetFullPath(activeProfileFile);
    }

    public string? Read()
    {
        if (!File.Exists(activeProfileFile))
        {
            return null;
        }

        string value = File.ReadAllText(activeProfileFile, Encoding.UTF8).Trim();
        return ProfileName.IsValid(value) ? value : null;
    }

    internal string? ReadForSwitch()
    {
        DesktopSnapshot.RejectLinks(activeProfileFile);
        try
        {
            using var stream = File.OpenRead(activeProfileFile);
            if (stream.Length > 1024) throw new InvalidDataException("Active profile marker is too large.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            string value = reader.ReadToEnd().TrimStart('\uFEFF').Trim();
            if (!ProfileName.IsValid(value)) throw new InvalidDataException("Active profile marker is invalid.");
            return value;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal void WriteForSwitch(string? expected, string? profile)
    {
        if (!string.Equals(ReadForSwitch(), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Active profile marker changed during the authorization switch.");
        string directory = Path.GetDirectoryName(activeProfileFile)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".active-profile-{Guid.NewGuid():N}.tmp");
        try
        {
            if (profile is null)
            {
                AuthSwitchFiles.MoveDurably(activeProfileFile, temporary);
            }
            else
            {
                DesktopSnapshot.WriteNew(temporary, Encoding.UTF8.GetBytes(ProfileName.RequireValid(profile)));
                AuthSwitchFiles.MoveDurably(temporary, activeProfileFile, overwrite: true);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Write(string profileName)
    {
        string validName = ProfileName.RequireValid(profileName);
        string directory = Path.GetDirectoryName(activeProfileFile)!;
        Directory.CreateDirectory(directory);
        string temporaryFile = Path.Combine(directory, $".active-profile-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporaryFile, validName, new UTF8Encoding(false));
        try
        {
            if (File.Exists(activeProfileFile))
            {
                File.Replace(temporaryFile, activeProfileFile, null);
            }
            else
            {
                File.Move(temporaryFile, activeProfileFile);
            }
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }
}
