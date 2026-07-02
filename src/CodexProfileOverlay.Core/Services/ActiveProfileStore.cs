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

    public void Write(string profileName)
    {
        string validName = ProfileName.RequireValid(profileName);
        Directory.CreateDirectory(Path.GetDirectoryName(activeProfileFile)!);
        File.WriteAllText(activeProfileFile, validName, new UTF8Encoding(false));
    }
}
