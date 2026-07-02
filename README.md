# Codex Profile Overlay

Codex Profile Overlay is an unofficial community companion app for Windows that adds a local account switcher overlay to the Codex desktop window.

It keeps the normal shared `%USERPROFILE%\.codex` directory intact for chats, projects, sessions, settings, caches, databases, logs, and history. Only `%USERPROFILE%\.codex\auth.json` is switched between saved local profiles.

> This project is not affiliated with, endorsed by, or sponsored by OpenAI. Never upload or commit `auth.json`.

## Screenshots

Screenshot placeholders:

- `docs/images/compact-mode.png`
- `docs/images/expanded-mode.png`
- `docs/images/settings.png`

## Features

- Tray-based background app with no normal taskbar or Alt+Tab window.
- Single running instance; a second launch reveals the existing app.
- Verified Codex window attachment with multi-monitor follow behavior.
- Compact, Expanded, and Auto display modes.
- Profile discovery under `%USERPROFILE%\.codex-profiles`.
- Safe `auth.json` switch transaction with backup and rollback.
- Per-user Start with Windows toggle.
- Configurable global hotkeys.
- Profile manager for add, rename, reorder, remove, refresh, and folder actions.
- In-app notifications attached to the Codex client area.
- Token-safe logs and repository safety scanner.

## Privacy And Security

The app is local-only, telemetry-free, administrator-free, service-free, reverse-proxy-free, and token-safe.

It does not parse, print, display, upload, or log `auth.json` contents. The application validates only that target credential files exist and are readable.

Local files:

- Profiles: `%USERPROFILE%\.codex-profiles\<profile>\auth.json`
- Settings: `%LOCALAPPDATA%\CodexProfileOverlay\settings.json`
- Profile metadata: `%LOCALAPPDATA%\CodexProfileOverlay\profiles.json`
- Backups: `%LOCALAPPDATA%\CodexProfileOverlay\backups`
- Logs: `%LOCALAPPDATA%\CodexProfileOverlay\logs`

## Profile Setup

Ready profiles are physical directories under:

```text
%USERPROFILE%\.codex-profiles
```

Only directories containing `auth.json` appear in the switcher. The Add profile flow creates a profile directory, writes `config.toml` with:

```toml
cli_auth_credentials_store = "file"
```

Then it runs `codex login` with `CODEX_HOME` set only for that login process.

## Display Modes

Compact mode shows a single active-profile control with an initials avatar, status dot, name, and dropdown.

Expanded mode shows scrollable segmented profile tabs with active state, initials, and an add/menu button.

Auto mode uses Expanded when the Codex window is wide enough and Compact when it is narrow, with hysteresis to prevent flicker during resize.

## Hotkeys

Defaults:

- `Ctrl + Alt + C`: show or hide the overlay
- `Ctrl + Alt + 1` through `Ctrl + Alt + 9`: switch to profiles by order

Hotkeys can be changed or cleared in Settings. Conflicts are reported when Windows refuses registration.

## Multi-Monitor Behavior

The overlay uses client-relative placement against the verified Codex desktop window. It follows window movement and resizing across monitors, including different DPI scaling. Custom placement is available with `Alt + left mouse drag`.

## Build

Requirements:

- Windows x64
- .NET 8 SDK, or the repository-local `.dotnet\dotnet.exe`

```powershell
.\build.ps1
```

## Test

```powershell
.\test.ps1
.\verify-repository-safety.ps1
```

Tests use temporary directories and dummy auth files only.

## Publish

```powershell
.\publish.ps1
```

Outputs:

```text
artifacts\publish\CodexProfileOverlay.exe
artifacts\CodexProfileOverlay-win-x64-portable.zip
```

## Portable Usage

Run:

```powershell
.\artifacts\publish\CodexProfileOverlay.exe
```

## Per-User Install

```powershell
.\install.ps1 -Launch
```

Optional startup registration:

```powershell
.\install.ps1 -StartWithWindows -Launch
```

Install location:

```text
%LOCALAPPDATA%\CodexProfileOverlay
```

## Uninstall

```powershell
.\uninstall.ps1
```

The uninstaller removes this app, its shortcut, and its startup entry only. It never removes:

- `%USERPROFILE%\.codex`
- `%USERPROFILE%\.codex-profiles`
- `auth.json` files
- Codex chats
- Codex settings
- Codex databases
- Codex history

Use `-RemoveNonSecretSettings` only if you also want to remove this app's non-secret settings and logs.

## Limitations

- Codex process validation uses visible window and process metadata available to a normal user process.
- If the installed Codex Start menu entry cannot be resolved, launch falls back to `codex app` from `PATH`.
- Installer support is a PowerShell per-user installer. No third-party installer tooling is installed automatically.
