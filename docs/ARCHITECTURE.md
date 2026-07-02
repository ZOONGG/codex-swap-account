# Architecture

Codex Profile Overlay has two assemblies:

- `CodexProfileOverlay.Core`: token-safe services and models that can be unit-tested without WPF.
- `CodexProfileOverlay`: WPF shell, tray lifecycle, window attachment, hotkeys, settings/profile windows, and Codex process launching.

The switcher keeps `%USERPROFILE%\.codex` shared and replaces only `%USERPROFILE%\.codex\auth.json`. Saved profiles live under `%USERPROFILE%\.codex-profiles\<profile>\auth.json`. Non-secret UI metadata lives under `%LOCALAPPDATA%\CodexProfileOverlay`.

Normal Codex launches do not set `CODEX_HOME`. The add-profile login flow sets `CODEX_HOME` only for that isolated `codex login` process.
