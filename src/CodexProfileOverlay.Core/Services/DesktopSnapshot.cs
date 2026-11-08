using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexProfileOverlay.Core.Services;

internal static class DesktopSnapshot
{
    internal const string DirectoryName = "desktop-session";
    internal const string ManifestName = "desktop-session.json";

    internal sealed record Manifest(string Identity, string ChromiumHash, string LiveFingerprint);

    internal static bool Exists(string path) => Attributes(path) is not null;

    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void RejectLinks(string path)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        while (ancestors.TryPop(out string? current))
        {
            if (Attributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Desktop sessions require local paths without filesystem links.");
        }
    }

    internal static bool HasSnapshot(string profile) =>
        Exists(Path.Combine(profile, ManifestName)) || Exists(Path.Combine(profile, DirectoryName));

    internal static string ReadIdentity(string auth)
    {
        using var json = ReadObject(auth);
        var tokens = json.RootElement.GetProperty("tokens");
        string account = RequiredString(tokens, "account_id");
        _ = ReadToken(tokens, "access");
        _ = ReadToken(tokens, "refresh");
        string[] jwt = ReadToken(tokens, "id").Split('.');
        if (jwt.Length != 3) throw new InvalidDataException("Desktop capture requires a ChatGPT sign-in.");
        string body = jwt[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(body.PadRight((body.Length + 3) / 4 * 4, '=')));
        string subject = RequiredString(claims.RootElement, "sub");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { account, subject })));
    }

    internal static void ValidateChromium(string root)
    {
        using var state = ReadObject(Path.Combine(root, "Local State"));
        using var preferences = ReadObject(Path.Combine(root, "Default", "Preferences"));
    }

    internal static void ValidateAuth(string auth)
    {
        using var json = ReadObject(auth);
    }

    private static JsonDocument ReadObject(string path)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var json = JsonDocument.Parse(stream);
        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Any()) return json;
        json.Dispose();
        throw new InvalidDataException("Required Desktop session data is invalid.");
    }

    private static string RequiredString(JsonElement element, string name)
    {
        string? value = element.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Desktop capture requires a ChatGPT sign-in.");
    }

    private static string ReadToken(JsonElement tokens, string kind) => RequiredString(tokens, kind + "_token");

    internal static void Save(string profile, string live, string auth, string fingerprint, CancellationToken token)
    {
        Directory.CreateDirectory(profile);
        string savedAuth = Path.Combine(profile, "auth.json");
        CopyFile(auth, savedAuth, token);
        string identity = ReadIdentity(savedAuth);
        string chromium = Path.Combine(profile, DirectoryName);
        ValidateChromium(live);
        string hash = HashTree(live, chromium, token);
        ValidateChromium(chromium);
        if (HashTree(chromium, null, token) != hash) throw new IOException("Desktop snapshot copy failed verification.");
        WriteNew(Path.Combine(profile, ManifestName), JsonSerializer.SerializeToUtf8Bytes(new Manifest(identity, hash, fingerprint)));
    }

    internal static Manifest Validate(string profile, CancellationToken token, string? destination = null)
    {
        var snapshot = ReadManifest(profile);
        string chromium = Path.Combine(profile, DirectoryName);
        ValidateChromium(chromium);
        if (snapshot.Identity != ReadIdentity(Path.Combine(profile, "auth.json"))
            || snapshot.ChromiumHash != HashTree(chromium, destination, token)
            || (destination is not null && snapshot.ChromiumHash != HashTree(destination, null, token)))
            throw new InvalidDataException("Desktop snapshot failed integrity verification.");
        return snapshot;
    }

    internal static Manifest ReadManifest(string profile)
    {
        string manifest = Path.Combine(profile, ManifestName);
        RejectLinks(manifest);
        using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<Manifest>(stream) ?? throw new InvalidDataException("Desktop snapshot manifest is missing.");
    }

    private static string HashTree(string root, string? destination, CancellationToken token)
    {
        RejectLinks(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, directory);
            if (destination is not null) Directory.CreateDirectory(Path.Combine(destination, relative));
            hash.AppendData(Encoding.UTF8.GetBytes("D" + relative + "\0"));
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A Desktop session contains a filesystem link.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                string file = Path.GetRelativePath(root, entry);
                hash.AppendData(Encoding.UTF8.GetBytes("F" + file + "\0"));
                hash.AppendData(CopyAndHash(entry, destination is null ? null : Path.Combine(destination, file), token));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void CopyFile(string source, string destination, CancellationToken token) =>
        _ = CopyAndHash(source, destination, token);

    private static byte[] CopyAndHash(string source, string? destination, CancellationToken token)
    {
        RejectLinks(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = destination is null ? null : new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            token.ThrowIfCancellationRequested();
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                output?.Write(buffer, 0, count);
                hash.AppendData(buffer, 0, count);
            }
            output?.Flush(flushToDisk: true);
            return hash.GetHashAndReset();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    internal static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
