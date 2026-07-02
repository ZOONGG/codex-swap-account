# Contributing

This project is a Windows x64 .NET 8 WPF application. Keep changes local-only, telemetry-free, administrator-free, service-free, and token-safe.

## Development

```powershell
.\build.ps1
.\test.ps1
.\verify-repository-safety.ps1
```

Do not run `git add .` until the safety scan passes. Do not add tests that depend on your real Codex login.

## Pull Requests

- Preserve the shared `%USERPROFILE%\.codex` directory.
- Switch only `%USERPROFILE%\.codex\auth.json`.
- Do not set `CODEX_HOME` for normal Codex launches.
- Use `CODEX_HOME` only for isolated `codex login` profile creation.
- Document user-visible behavior changes.
