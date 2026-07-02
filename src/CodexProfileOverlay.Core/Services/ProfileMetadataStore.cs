using System.Text.Json;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class ProfileMetadataStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string metadataFile;

    public ProfileMetadataStore(string metadataFile)
    {
        this.metadataFile = Path.GetFullPath(metadataFile);
    }

    public ProfileMetadataDocument Load()
    {
        if (!File.Exists(metadataFile))
        {
            return new ProfileMetadataDocument();
        }

        using FileStream stream = File.OpenRead(metadataFile);
        return JsonSerializer.Deserialize<ProfileMetadataDocument>(stream, SerializerOptions) ?? new ProfileMetadataDocument();
    }

    public void Save(ProfileMetadataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataFile)!);

        string temp = metadataFile + ".tmp";
        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, document, SerializerOptions);
        }

        if (File.Exists(metadataFile))
        {
            File.Replace(temp, metadataFile, null);
        }
        else
        {
            File.Move(temp, metadataFile);
        }
    }
}
