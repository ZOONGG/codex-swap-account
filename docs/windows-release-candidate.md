# Windows 1.0.0 release candidate

Prepared from local integrated development HEAD `e123284`, without a version bump.
The release candidate removes only the abandoned token-statistics Settings card,
its isolated provider/models/parser, localization, tests and feature document.
Existing 5-hour/weekly limits, usage popup, recommendation and app-server provider
remain intact.

## Focused integration validation

- 210 existing targeted tests passed: authorization switching and rollback,
  browser login/reauthorization, shared chat-state migration, Windows lifecycle
  adapters, overlay layout/visibility, hotkeys, tray coordination, usage
  presentation/recommendation, Settings persistence, RU/EN localization,
  mock Support API/payment flows and update verification/installation behavior.
- Actual WPF Settings: all seven pages rendered in dark/light and RU/EN (28
  cases), using disposable storage and an unavailable usage provider. Settings
  persistence and the transient Applied notification were checked with the WPF
  dispatcher. No controller startup, real login or account switch was invoked.
- Existing ignored WPF harnesses rendered embedded Support (30 cases, fake API)
  and the usage popup (20 theme/language/DPI cases, synthetic usage).
- Reviewed controller event wiring, shutdown versus account-operation paths,
  Settings handlers and cleanup, embedded Support disposal, update checksum
  verification and installed/portable gating, resource packaging, and installer
  user-data preservation. No additional demonstrated integration regression was
  identified. This is a focused check, not a repository-wide audit.

## Local packaging and evidence

Run `publish.ps1` after committing, from a clean working tree. It creates both
distributions from the same self-contained Windows x64 Release payload and records
the source commit and executable SHA-256 in `artifacts/publish/build-info.json`.
Product version remains `1.0.0`. No GitHub release, push, tag or merge is involved.

Run `test-installer-smoke.ps1 -PublishedPayload ./artifacts/publish` to compile
the production Inno script under a disposable identity, install, upgrade and
uninstall without launching the application. Its synthetic 1.0.1 upgrade is
test-only. It checks installed payload hashes, startup migration, uninstall and
fake-data preservation; it does not modify the production product version.

Automated evidence and Settings images are in ignored `artifacts/rc-validation`;
Support and popup images are in `artifacts/ui-qa` and `artifacts/usage-render`.
Installer logs are under the unique `artifacts/CodexSwapAccountSmoke-*` directory
reported by the smoke script. Static rendering does not establish interactive
focus, native placement, taskbar readability or mixed-monitor DPI behavior.

## Manual Windows checklist

Use test accounts and back up user data before tests that intentionally change it.

1. With Codex Desktop open, open/close/reopen the switcher; verify Codex stays
   running and a second switcher launch activates the existing instance.
2. Move/resize/minimize Codex, test Compact/Expanded/Auto and overlay dragging,
   monitor transitions and mixed DPI. Check tray activation and configured hotkeys.
3. Switch test profiles; verify the correct account, shared chat history, safe
   shutdown/relaunch and rollback on launch failure.
4. Add a test account through browser login; cancel and retry. Reauthorize an
   inactive and active test profile, including cancellation and failure rollback.
5. Open usage details independently of profile switching; test refresh, stale
   data, optional tray percentages and unchanged recommendations.
6. Change settings, reopen the app, verify persistence and Applied fading; check
   RU/EN, both themes and all pages. No token-statistics section should remain.
7. Test GitHub/heart buttons and embedded Support open/close, Escape, resize and
   focus recovery. Inspect payment UI without executing a real payment.
8. Check updates in portable and installed modes, cancellation and checksum
   failures. Installed updates require a newer public version; this candidate
   intentionally retains 1.0.0 and is not publicly published.
9. Manually upgrade an existing test installation; verify settings, profiles,
   shared history and startup preference. Verify uninstall preserves user data.

No automated test authenticates real accounts, changes real Codex credentials,
executes real payments or terminates the user's live Codex Desktop. Native
interactive scenarios above remain required before public release.
