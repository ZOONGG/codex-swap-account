namespace CodexProfileOverlay.Core.Services;

public sealed class AtomicFileReplacer : IAtomicFileReplacer
{
    public void ReplaceFromSource(string sourceFile, string destinationFile)
    {
        string destinationDirectory = Path.GetDirectoryName(destinationFile)
            ?? throw new InvalidOperationException("Destination file has no directory.");
        Directory.CreateDirectory(destinationDirectory);

        string tempFile = Path.Combine(destinationDirectory, $".auth-{Guid.NewGuid():N}.tmp");
        File.Copy(sourceFile, tempFile, overwrite: false);

        try
        {
            if (File.Exists(destinationFile))
            {
                File.Replace(tempFile, destinationFile, null);
            }
            else
            {
                File.Move(tempFile, destinationFile);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
