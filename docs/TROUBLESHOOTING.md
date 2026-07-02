# Troubleshooting

## Overlay Does Not Appear

- Confirm Codex desktop is running and not minimized.
- Use the tray icon and choose **Show switcher**.
- Open Settings and reset the overlay position.

## Codex Does Not Launch

The app first tries the installed Start menu Codex entry, then falls back to `codex app` from `PATH`.

## Hotkey Conflict

Open Settings and change or clear the conflicting hotkey. The app does not override combinations owned by another application.

## Profile Does Not Appear

Only directories under `%USERPROFILE%\.codex-profiles` containing an `auth.json` file are shown as ready profiles.
