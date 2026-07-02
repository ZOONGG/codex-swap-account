namespace CodexProfileOverlay.Core.Services;

public interface IAtomicFileReplacer
{
    void ReplaceFromSource(string sourceFile, string destinationFile);
}
