# Codex Profile Overlay

Windows MVP for a background WPF overlay that attaches a compact profile selector to the official Codex desktop window.

## Behavior

- Discovers profiles under `%USERPROFILE%\.codex-profiles`.
- Shows only directories containing `auth.json`.
- Keeps `%USERPROFILE%\.codex` shared.
- Switches only `%USERPROFILE%\.codex\auth.json`.
- Does not parse, print, or log auth file contents.
- Saves overlay position in `%LOCALAPPDATA%\CodexProfileOverlay\settings.json`.
- Stores active profile name in `%LOCALAPPDATA%\CodexProfileOverlay\active-profile.txt`.
- Writes safe logs under `%LOCALAPPDATA%\CodexProfileOverlay\logs`.

## Build

```powershell
.\build.ps1
```

## Publish

```powershell
.\publish.ps1
```

The default publish output is:

```text
artifacts\publish\CodexProfileOverlay.exe
```

## Launch

```powershell
.\artifacts\publish\CodexProfileOverlay.exe
```

The app has no taskbar or Alt+Tab window. It runs in the background and shows the selector only while a Codex desktop window is visible.

## Switching Notes

The switch flow closes Codex, waits up to five seconds, terminates only Codex-named desktop processes that remain, copies the latest shared `auth.json` back to the previous active profile, backs up the shared file, atomically installs the target profile `auth.json`, writes the active profile, and launches Codex normally.

## MVP Limitations

- Codex process validation is conservative but based on visible window/process metadata available to a normal user process.
- If the installed Codex Start menu AppUserModelID cannot be resolved, launch falls back to `codex app` from `PATH`.
- Account login and profile deletion are intentionally not implemented.
