namespace CodexProfileOverlay.Core.Services;

public sealed class AtomicFileReplacer : IAtomicFileReplacer
{
    public void ReplaceFromSource(string sourceFile, string destinationFile)
        => ReplaceFromSource(sourceFile, destinationFile, durable: false);

    internal static void ReplaceDurably(string sourceFile, string destinationFile)
        => ReplaceFromSource(sourceFile, destinationFile, durable: true);

    private static void ReplaceFromSource(string sourceFile, string destinationFile, bool durable)
    {
        string destinationDirectory = Path.GetDirectoryName(destinationFile)
            ?? throw new InvalidOperationException("Destination file has no directory.");
        Directory.CreateDirectory(destinationDirectory);

        string tempFile = Path.Combine(destinationDirectory, $".auth-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourceFile, tempFile, overwrite: false);
            if (durable)
            {
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Write, FileShare.None))
                    stream.Flush(flushToDisk: true);
                AuthSwitchFiles.MoveDurably(tempFile, destinationFile, overwrite: true);
            }
            else if (File.Exists(destinationFile))
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
