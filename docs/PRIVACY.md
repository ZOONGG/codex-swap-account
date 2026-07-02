# Privacy

Codex Profile Overlay is local-only. It has no telemetry, no network service, no reverse proxy, and no background Windows service.

The app does not parse, display, upload, or log `auth.json`. It checks only file existence and basic readability before copying the selected profile credential file.

Logs are written under `%LOCALAPPDATA%\CodexProfileOverlay\logs` and are sanitized for token-like values and email addresses.
