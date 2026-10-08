# Update checker verification

The update card is in Settings > Advanced. A dot on Advanced indicates an available release. Startup checks use public GitHub metadata, run asynchronously, and are limited to one attempt per 24 hours (including unsuccessful attempts). Manual checks bypass the time limit. Successful metadata survives offline failures. No popup notifications are generated.

Only the portable ZIP and SHA256SUMS.txt from the selected official release are accepted. HTTPS redirects are limited to GitHub asset hosts. Metadata, checksums, and ZIP have 1 MiB, 64 KiB, and 512 MiB limits respectively. The ZIP is temporary until SHA-256 verification succeeds. The save dialog appears afterwards; installation is manual.

Offline tests use injected HttpClient handlers and never contact GitHub. Run `./build.ps1` for Release build and tests; `./publish.ps1` writes the portable ZIP and checksums into artifacts.

## Manual states

1. Up to date: with the installed version 1.0.0 and latest official release v1.0.0, open Advanced and click Check for updates. Expect a green check and installed version.
2. Update available: without publishing a release, close the app and back up `%LOCALAPPDATA%\CodexProfileOverlay\updates.json`. Seed that file with the JSON below, replacing both timestamps with the current UTC time. Restart and open Advanced. Expect a dot, version comparison, date, notes, download and GitHub actions. This is a display fixture; downloads must fail safely because this hypothetical release has no real assets. Restore the original cache afterwards.
3. Offline: disconnect networking and manually check. Expect a nonintrusive connection message and retry button. With a previously valid update cache, the version, notes, and actions remain visible.

```json
{
  "LastAttempt": "2026-10-08T00:00:00Z",
  "LastSuccess": "2026-10-08T00:00:00Z",
  "Release": {
    "Tag": "v1.1.0",
    "Url": "https://github.com/ZOONGG/codex-swap-account/releases/tag/v1.1.0",
    "Published": "2026-10-08T00:00:00Z",
    "Notes": "Preview of update information.\nSecond release note.",
    "Zip": null,
    "Checksums": null
  }
}
```

Check RU/EN, light/dark, keyboard focus, and 100/125/150/200% display scaling manually. Cards reuse existing rounded borders, theme brushes, layout rounding, and wrapping buttons; physical multi-DPI appearance has not been visually verified by automated tests.

Support payments, authorization, switch implementation, and Windows lifecycle implementation are unchanged. Controller changes only create/dispose the update service, launch its check, and pass it to Settings. Product version remains 1.0.0. Nothing is installed, executed, pushed, tagged, or published to GitHub by this feature.
