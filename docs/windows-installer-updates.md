# Windows installer and updates

The product version is **1.1.0**, from `Directory.Build.props`. `publish.ps1`
reads the published executable's product version for Inno Setup. No release,
tag, push or external publishing is performed by the build.

## Distribution

Install Inno Setup 6 (validated with 6.7.3) and run `./publish.ps1`. You can
provide an explicit compiler path with `-InnoCompiler`. Outputs:

- `artifacts/CodexSwapAccount-win-x64-setup.exe`
- `artifacts/CodexProfileOverlay-win-x64-portable.zip`
- `artifacts/SHA256SUMS.txt` (installer, ZIP and standalone executable)
- `artifacts/publish/CodexProfileOverlay.exe` (self-contained .NET 8)

The installer uses HKCU, requires no administrator privileges, installs into
`%LOCALAPPDATA%\Programs\CodexSwapAccount`, and creates a Start Menu shortcut.
The desktop shortcut is optional. A stable AppId upgrades the existing install.
An enabled startup entry is migrated to the installed executable; disabled
startup stays disabled. Uninstall removes only installed files, shortcuts and
its matching startup registration. It does not delete `.codex`,
`.codex-profiles`, or `%LOCALAPPDATA%\CodexProfileOverlay`. The installer also
rejects those directories as installation destinations.

## Update flow and trust boundary

Settings → Advanced → Updates retains the current GitHub client, release notes,
version comparison and 24-hour interval. The background loop wakes every 15
minutes for download retries; metadata checks retain the 24-hour throttle.
Startup work runs off the UI thread. No GitHub token or account credentials are used.

Installed mode requires a matching HKCU uninstall InstallLocation, installation
marker and uninstaller beside the running executable. A copied EXE is portable.
Portable mode can save the verified installer or ZIP; it never launches the
installer or changes the portable copy. With no newer version it links to the
official releases page for installation.

Installed copies automatically download stable updates by default. The RU/EN
“Automatically download updates” setting disables background transfers and
cancels an active transfer. Downloads never start an installer. The existing
card shows progress, retry/cancellation, and “Update vX.Y.Z is ready to install”.
The overlay announces readiness once per version per app session.

Verified installers use random `codex-update-<guid>.exe` files in the current
user's temporary directory. The existing update cache stores only the release
tag, constrained file name, and verified SHA-256. On restart, the service
revalidates the cached file asynchronously, including offline. Missing or
damaged packages are downloaded again; older packages and packages for the
already installed version are discarded. No arbitrary cached paths are used.
OS cleanup of temporary files is handled by downloading again.

“Install and restart” asks for explicit approval only when a verified package
is ready. Downloads
support progress, cancellation, size/time bounds and cleanup on failure. Only
the exact official repository/tag/installer filename and a single exact
SHA-256 manifest entry are accepted. Stable releases only are eligible.
The file is rehashed with writes/deletion denied immediately before launch.
A shared gate prevents overlapping downloads/installer launches; Inno's setup
mutex prevents simultaneous installers. Active account switching is checked
before confirmation and again immediately before launch on the UI dispatcher.
New account operations are blocked while a confirmed installer is being
revalidated and launched. Failed launches release this block for retry.

The external installer uses fixed `/UPDATE=1 /SILENT /NORESTART` arguments.
Inno `/SILENT` displays installation progress (not `/VERYSILENT`). It waits up
to 30 seconds for the switcher's named mutex to disappear. It never enumerates
or terminates processes; application auto-closing and auto-restarting are
disabled. Only after verified installer launch does the switcher exit.
After successful installation it starts the installed EXE with `--updated`.
That startup suppresses the usual optional Codex Desktop launch and displays
the actual executable version. No machine restart is requested.

If download/verification/launch fails, the switcher stays open and offers retry
and GitHub recovery. If installation is cancelled or fails after shutdown,
Inno displays the failure and offers to reopen the existing switcher; alternatively
rerun the installer. Recovery uses the installed EXE with the Codex-launch
suppression flag, never a desktop client process.
The verified temporary installer is retained after launch for recovery, since
deleting a running installer is unsafe. Other failed/cancelled downloads are
deleted. Profiles/settings are outside installer ownership.

## Validation

Targeted `UpdateServiceTests` and `InstallerUpdateTests` cover version ordering,
stable release eligibility, throttle/cache/offline behavior, installed detection,
exact asset selection, URL rejection, missing/duplicate/incorrect checksums,
tampering after verification, cancellation, interrupted downloads, operation
gates, switching refusal, fixed launch arguments and RU/EN strings.

`./test-installer-smoke.ps1` compiles the production Inno script with a unique
test AppId/mutex/startup value, fake payload and no shortcuts or application
launch. It installs and upgrades in `artifacts`, verifies registered versions,
InstallLocation and startup migration, uninstalls, and verifies fake user-data
sentinels survive. It never uses real credentials or switches accounts. Logs
remain in the isolated artifact directory. This checks installer mechanics;
it does not simulate a real GitHub release or run the actual application.

Manual checks still required on a disposable Windows user/VM:

- Install the actual Release payload; check Start Menu/optional desktop shortcut,
  startup toggle, uninstall and retained settings.
- Exercise a future newer official release end to end, including automatic
  restart/version notification, installer cancellation and disk/file-lock failure.
- Inspect the update card and confirmation in RU/EN, dark/light, and 100–200% DPI.
- Confirm Codex/ChatGPT Desktop remain untouched during installation and restart.

Inno reference: [non-administrative installation](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm),
[AppMutex](https://jrsoftware.org/ishelp/topic_setup_appmutex.htm).
