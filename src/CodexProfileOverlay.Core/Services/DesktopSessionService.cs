using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexProfileOverlay.Core.Services;

public sealed class DesktopAccountChangedException : Exception { }

/// <summary>Writes each rename intent before touching live files. All renames stay on their source volume.</summary>
public sealed class DesktopSessionService
{
    private readonly AppPaths paths;
    private readonly ActiveProfileStore active;
    private readonly Action<string, string> move;
    private string JournalRoot => Path.Combine(paths.ApplicationDataDirectory, "desktop-recovery");
    private string PendingFile => Path.Combine(JournalRoot, "pending.json");

    public DesktopSessionService(AppPaths paths, Action<string, string>? move = null)
    {
        this.paths = paths;
        active = new ActiveProfileStore(paths.ActiveProfileFile);
        this.move = move ?? MoveDurably;
    }

    public bool HasPendingOperation => DesktopSnapshot.Exists(PendingFile);

    public void RequireNoPendingOperation()
    {
        if (HasPendingOperation) throw new InvalidOperationException("Recover the pending Desktop session operation first.");
        if (new BackupMaintenanceService(paths).FindPendingAuthSwitch() is not null)
            throw new InvalidOperationException("Recover the pending authorization switch first.");
    }

    public bool IsDesktopProfile(string name) => DesktopSnapshot.HasSnapshot(Profile(name))
        || new ProfileMetadataStore(paths.ProfilesMetadataFile).Load().Profiles.Any(profile =>
            !profile.Hidden && profile.DesktopSession && profile.DirectoryName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool RequiresDesktopSwitch(string name) => IsDesktopProfile(name)
        || (active.Read() is { } previous && IsDesktopProfile(previous));

    public bool MatchesCapturedPath(string name, string live) =>
        DesktopSnapshot.ReadManifest(Profile(name)).LiveFingerprint == Fingerprint(live);

    public bool MatchesPendingPath(string live) => ReadJournal().LiveFingerprint == Fingerprint(live);

    public bool HasSupportedLayout(string live)
    {
        ValidateLivePath(live);
        string state = Path.Combine(live, "Local State");
        string preferences = Path.Combine(live, "Default", "Preferences");
        DesktopSnapshot.RejectLinks(state);
        DesktopSnapshot.RejectLinks(preferences);
        return File.Exists(state) && File.Exists(preferences);
    }

    public IReadOnlyList<string> GetAttestationFiles(string live)
    {
        ValidateLivePath(live);
        string profile = Path.Combine(live, "Default");
        string[] files =
        [
            Path.Combine(profile, "Network", "Cookies"), Path.Combine(profile, "Cookies"),
            Path.Combine(profile, "Local Storage", "leveldb", "LOCK"),
            Path.Combine(profile, "Session Storage", "LOCK"),
        ];
        foreach (string path in files) DesktopSnapshot.RejectLinks(path);
        return files.Where(File.Exists).ToArray();
    }

    public void PreflightCapture(string name, string live)
    {
        RequireNoPendingOperation();
        ValidateLivePath(live);
        DesktopSnapshot.RejectLinks(Profile(name));
        if (DesktopSnapshot.Exists(Profile(name))) throw new InvalidOperationException("A profile with this name already exists.");
        _ = DesktopSnapshot.ReadIdentity(paths.SharedAuthFile);
        DesktopSnapshot.ValidateChromium(live);
    }

    public void PreflightSwitch(string name, string live)
    {
        RequireNoPendingOperation();
        ValidateLivePath(live);
        DesktopSnapshot.ValidateAuth(Path.Combine(Profile(name), "auth.json"));
        if (IsDesktopProfile(name) && !MatchesCapturedPath(name, live))
            throw new InvalidDataException("Desktop snapshot belongs to a different data directory.");
        DesktopSnapshot.ValidateAuth(paths.SharedAuthFile);
        DesktopSnapshot.ValidateChromium(live);
        if (active.Read() is { } previous && IsDesktopProfile(previous))
        {
            var saved = DesktopSnapshot.ReadManifest(Profile(previous));
            if (saved.LiveFingerprint != Fingerprint(live))
                throw new InvalidDataException("Desktop snapshot belongs to a different data directory.");
            if (saved.Identity != DesktopSnapshot.ReadIdentity(paths.SharedAuthFile))
                throw new DesktopAccountChangedException();
        }
    }

    public void Capture(string name, string live, Action verifyStopped, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        verifyStopped();
        PreflightCapture(name, live);
        var journal = Begin(name, live, capture: true);
        var layout = new Layout(paths, journal, live);
        DesktopSnapshot.Save(layout.ProfileStage, live, paths.SharedAuthFile, journal.LiveFingerprint, token);
        DesktopSnapshot.WriteNew(Path.Combine(layout.ProfileStage, "config.toml"),
            "cli_auth_credentials_store = \"file\"\n"u8.ToArray());

        var metadataStore = new ProfileMetadataStore(paths.ProfilesMetadataFile);
        var metadata = metadataStore.Load();
        ProfileManagerService.UpsertMetadata(metadata, name, name, desktopSession: true);
        new ProfileMetadataStore(layout.NewMetadata).Save(metadata);
        FlushFile(layout.NewMetadata);
        StageActive(layout, name);
        Apply(layout, verifyStopped, token);
    }

    public void Switch(string name, string live, Action verifyStopped, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        verifyStopped();
        PreflightSwitch(name, live);
        var journal = Begin(name, live, capture: false);
        var layout = new Layout(paths, journal, live);
        var targetAuth = Path.Combine(layout.Target, "auth.json");

        Directory.CreateDirectory(layout.AuthWorkspace);
        DesktopSnapshot.CopyFile(targetAuth, layout.NewAuth, token);
        DesktopSnapshot.ValidateAuth(layout.NewAuth);
        if (journal.TargetDesktop)
        {
            Directory.CreateDirectory(layout.LiveWorkspace);
            var target = DesktopSnapshot.Validate(layout.Target, token, layout.NewLive);
            if (target.Identity != DesktopSnapshot.ReadIdentity(layout.NewAuth))
                throw new InvalidDataException("Desktop authorization changed during staging.");
        }
        if (journal.PreviousDesktop)
        {
            var previous = DesktopSnapshot.Validate(layout.Previous!, token);
            DesktopSnapshot.Save(layout.ProfileStage, live, paths.SharedAuthFile, journal.LiveFingerprint, token);
            if (previous.Identity != DesktopSnapshot.ReadIdentity(Path.Combine(layout.ProfileStage, "auth.json")))
                throw new DesktopAccountChangedException();
        }

        StageActive(layout, name);
        Apply(layout, verifyStopped, token);
    }

    public void Complete(string live)
    {
        var layout = ReadLayout(live);
        DesktopSnapshot.RejectLinks(layout.Applied);
        if (!File.Exists(layout.Applied)) throw new IOException("The Desktop operation has not applied all changes.");
        int count = layout.Steps().Count;
        for (int i = 0; i < count; i++)
        {
            DesktopSnapshot.RejectLinks(layout.Intent(i));
            if (!File.Exists(layout.Intent(i)))
                throw new IOException("The Desktop operation did not apply all of its changes.");
        }
        Finish(layout, "committed.json");
    }

    public void Recover(string live, Action verifyStopped)
    {
        var layout = ReadLayout(live);
        var steps = layout.Steps();
        ValidateIntents(layout, steps.Count);
        for (int index = steps.Count - 1; index >= 0; index--)
        {
            string intent = layout.Intent(index);
            DesktopSnapshot.RejectLinks(intent);
            if (!DesktopSnapshot.Exists(intent)) continue;
            verifyStopped();
            var step = steps[index];
            DesktopSnapshot.RejectLinks(step.Source);
            DesktopSnapshot.RejectLinks(step.Destination);
            bool source = DesktopSnapshot.Exists(step.Source);
            bool destination = DesktopSnapshot.Exists(step.Destination);
            if (source == destination)
                throw new IOException("Desktop recovery found ambiguous files. Keep all recovery data.");
            if (destination) Rename(step.Destination, step.Source, step.Directory);
            MoveDurably(intent, intent + ".undone");
        }
        verifyStopped();
        Finish(layout, "rolled-back.json");
    }

    private static void ValidateIntents(Layout layout, int count)
    {
        bool seenMissing = false;
        bool seenUndone = false;
        for (int index = 0; index < count; index++)
        {
            string intent = layout.Intent(index);
            string undone = intent + ".undone";
            DesktopSnapshot.RejectLinks(intent);
            DesktopSnapshot.RejectLinks(undone);
            bool pending = DesktopSnapshot.Exists(intent);
            bool restored = DesktopSnapshot.Exists(undone);
            if (!pending && !restored) { seenMissing = true; continue; }
            if ((pending && restored) || seenMissing || (pending && seenUndone))
                throw new InvalidDataException("Desktop recovery journal has inconsistent rename intents.");
            using var marker = File.OpenRead(pending ? intent : undone);
            if (marker.Length != 1 || marker.ReadByte() != 1)
                throw new InvalidDataException("Desktop recovery journal has an invalid rename intent.");
            seenUndone |= restored;
        }
    }

    private Journal Begin(string name, string live, bool capture)
    {
        RequireNoPendingOperation();
        ValidateLivePath(live);
        DesktopSnapshot.RejectLinks(paths.SharedAuthFile);
        DesktopSnapshot.RejectLinks(paths.ActiveProfileFile);
        DesktopSnapshot.RejectLinks(paths.ProfilesMetadataFile);
        DesktopSnapshot.RejectLinks(PendingFile);
        RequireLocalVolume(paths.UserProfile);
        RequireLocalVolume(paths.LocalAppData);
        string? previous = active.Read();
        var journal = new Journal(Guid.NewGuid(), name, previous, Fingerprint(live), capture,
            !capture && IsDesktopProfile(name), !capture && previous is not null && IsDesktopProfile(previous),
            DesktopSnapshot.Exists(paths.ActiveProfileFile), DesktopSnapshot.Exists(paths.ProfilesMetadataFile));
        if (!capture && (!journal.TargetDesktop && !journal.PreviousDesktop
            || string.Equals(previous, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A Desktop switch requires a different captured profile.");

        var layout = new Layout(paths, journal, live);
        Directory.CreateDirectory(layout.JournalDirectory);
        WriteMarker(layout.JournalDirectory, PendingFile, JsonSerializer.SerializeToUtf8Bytes(journal));
        return journal;
    }

    private Journal ReadJournal()
    {
        DesktopSnapshot.RejectLinks(PendingFile);
        using var input = new FileStream(PendingFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<Journal>(input) ?? throw new InvalidDataException("Desktop recovery journal is invalid.");
    }

    private Layout ReadLayout(string live)
    {
        ValidateLivePath(live);
        RequireLocalVolume(paths.UserProfile);
        RequireLocalVolume(paths.LocalAppData);
        var journal = ReadJournal();
        if (journal.Id == Guid.Empty || journal.LiveFingerprint != Fingerprint(live)
            || !ProfileName.IsValid(journal.Target) || (journal.Previous is not null && !ProfileName.IsValid(journal.Previous))
            || (journal.PreviousDesktop && (journal.Previous is null || journal.Target.Equals(journal.Previous, StringComparison.OrdinalIgnoreCase)))
            || (journal.Capture && (journal.TargetDesktop || journal.PreviousDesktop))
            || (!journal.Capture && !journal.TargetDesktop && !journal.PreviousDesktop))
            throw new InvalidDataException("Desktop recovery journal does not match this installation.");
        var layout = new Layout(paths, journal, live);
        DesktopSnapshot.RejectLinks(layout.JournalDirectory);
        return layout;
    }

    private void Finish(Layout layout, string filename)
    {
        DesktopSnapshot.RejectLinks(layout.JournalDirectory);
        MoveDurably(PendingFile, Path.Combine(layout.JournalDirectory, filename));
    }

    private static void StageActive(Layout layout, string name) =>
        DesktopSnapshot.WriteNew(layout.NewActive, Encoding.UTF8.GetBytes(name));

    private void Apply(Layout layout, Action verifyStopped, CancellationToken token)
    {
        var steps = layout.Steps();
        for (int index = 0; index < steps.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            verifyStopped();
            var step = steps[index];
            VerifyRename(step.Source, step.Destination, step.Directory);
            WriteMarker(layout.JournalDirectory, layout.Intent(index), [1]);
            Rename(step.Source, step.Destination, step.Directory);
        }
        verifyStopped();
        token.ThrowIfCancellationRequested();
        WriteMarker(layout.JournalDirectory, layout.Applied, [1]);
    }

    private void Rename(string source, string destination, bool directory)
    {
        VerifyRename(source, destination, directory);
        move(source, destination);
    }

    private static void VerifyRename(string source, string destination, bool directory)
    {
        DesktopSnapshot.RejectLinks(source);
        DesktopSnapshot.RejectLinks(destination);
        if (DesktopSnapshot.Attributes(source) is not { } attributes
            || ((attributes & FileAttributes.Directory) != 0) != directory || DesktopSnapshot.Exists(destination))
            throw new IOException("Desktop session rename endpoints changed.");
    }

    private static void WriteMarker(string directory, string destination, byte[] content)
    {
        DesktopSnapshot.RejectLinks(destination);
        DesktopSnapshot.RejectLinks(directory);
        string temp = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        DesktopSnapshot.WriteNew(temp, content);
        MoveDurably(temp, destination);
    }

    private static void FlushFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        file.Flush(flushToDisk: true);
    }

    private string Profile(string name) => Path.Combine(paths.ProfilesDirectory, ProfileName.RequireValid(name));

    private static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    private void ValidateLivePath(string live)
    {
        if (!Path.IsPathFullyQualified(live) || !string.Equals(Path.GetFullPath(live), live, StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && (live.Length < 4 || live[1] != ':')))
            throw new IOException("Desktop sessions require a verified local data directory.");
        foreach (string reserved in new[] { paths.SharedCodexDirectory, paths.ProfilesDirectory, paths.ApplicationDataDirectory })
        {
            if (Within(live, reserved) || Within(reserved, live))
                throw new IOException("Desktop session storage overlaps another Codex data directory.");
        }
        DesktopSnapshot.RejectLinks(live);
        RequireLocalVolume(live);

        static bool Within(string candidate, string root) =>
            candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireLocalVolume(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat is not ("NTFS" or "ReFS"))
            throw new IOException("Desktop recovery requires a local NTFS or ReFS volume.");
    }

    private static void MoveDurably(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
            return;
        }
        // No REPLACE_EXISTING or COPY_ALLOWED. Never fall back to copy/delete for journaled renames.
        if (!MoveFileEx(source, destination, 8))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, int flags);

    private sealed record Journal(Guid Id, string Target, string? Previous, string LiveFingerprint, bool Capture,
        bool TargetDesktop, bool PreviousDesktop, bool HadActive, bool HadMetadata);

    private sealed record Step(string Source, string Destination, bool Directory = false);

    private sealed class Layout(AppPaths paths, Journal journal, string live)
    {
        private string Suffix => " .desktop-" + journal.Id.ToString("N");
        public string JournalDirectory => Path.Combine(paths.ApplicationDataDirectory, "desktop-recovery", journal.Id.ToString("N"));
        public string ProfileStage => Path.Combine(ProfileWorkspace, "staged");
        public string AuthWorkspace => Path.Combine(paths.UserProfile, Suffix);
        public string LiveWorkspace => Path.Combine(Path.GetDirectoryName(live)!, Suffix);
        private string ProfileWorkspace => Path.Combine(paths.ProfilesDirectory, Suffix);
        public string NewAuth => Path.Combine(AuthWorkspace, "new-auth.json");
        public string NewLive => Path.Combine(LiveWorkspace, "incoming");
        public string NewActive => Path.Combine(JournalDirectory, "incoming-active.txt");
        public string NewMetadata => Path.Combine(JournalDirectory, "incoming-profiles.json");
        public string Applied => Path.Combine(JournalDirectory, "applied");
        public string Target => Path.Combine(paths.ProfilesDirectory, journal.Target);
        public string? Previous => journal.Previous is null ? null : Path.Combine(paths.ProfilesDirectory, journal.Previous);
        public string Intent(int index) => Path.Combine(JournalDirectory, $"move-{index:D2}.intent");

        public List<Step> Steps()
        {
            var result = new List<Step>();
            if (journal.Capture)
            {
                result.Add(new(ProfileStage, Target, true));
                if (journal.HadMetadata) result.Add(new(paths.ProfilesMetadataFile, Path.Combine(JournalDirectory, "original-profiles.json")));
                result.Add(new(NewMetadata, paths.ProfilesMetadataFile));
            }
            else
            {
                if (journal.PreviousDesktop)
                {
                    foreach (string file in new[] { "auth.json", DesktopSnapshot.DirectoryName, DesktopSnapshot.ManifestName })
                    {
                        bool directory = file == DesktopSnapshot.DirectoryName;
                        result.Add(new(Path.Combine(Previous!, file), Path.Combine(ProfileWorkspace, "original-" + file), directory));
                        result.Add(new(Path.Combine(ProfileStage, file), Path.Combine(Previous!, file), directory));
                    }
                }
                if (journal.TargetDesktop)
                {
                    result.Add(new(live, Path.Combine(LiveWorkspace, "original"), true));
                    result.Add(new(NewLive, live, true));
                }
                result.Add(new(paths.SharedAuthFile, Path.Combine(AuthWorkspace, "original-auth.json")));
                result.Add(new(NewAuth, paths.SharedAuthFile));
            }
            if (journal.HadActive) result.Add(new(paths.ActiveProfileFile, Path.Combine(JournalDirectory, "original-active.txt")));
            result.Add(new(NewActive, paths.ActiveProfileFile));
            return result;
        }
    }
}
