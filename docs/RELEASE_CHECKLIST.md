# Release Checklist

- Run `.\test.ps1`.
- Run `.\verify-repository-safety.ps1`.
- Build Release with `.\build.ps1`.
- Publish with `.\publish.ps1`.
- Confirm portable zip exists under `artifacts`.
- Launch the published executable.
- Verify tray menu, compact mode, expanded mode, settings, and profile refresh.
- Do not upload `auth.json`, backups, logs, `.codex-profiles`, or real local fixtures.
