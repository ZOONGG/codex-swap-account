# Windows Codex compatibility: implementation and manual verification

Starting checkout: `main`, `4000849db610999eab81eb49c5932b987703a576`.
The checkout was clean. After a safe origin fetch, the existing local Support
commit remained ahead of `origin/main` (`92b9dc6`). It is preserved in the working
branch `codex/windows-desktop-lifecycle`; no Support/payment code was changed.

## Reference and differences

Inspected HYFOAM commits:

- `2d200c0b4815c8a6e4012a000460036bcde5d351`: desktop identity, retained process
  sessions, shutdown coordination, AUMID activation, CLI discovery.
- `5dbfe6a495dc6707595a153b223339af41fdc881`: exact production tray GUID and
  post-shutdown cleanup.
- `881b8b7209a159d8ac8731dfb829c4cd18a33ebd`: tray release changes; branding,
  release versions, packaging changes and the integration probe were not adopted.

The fork was fetched as reference objects only. No fork branch was merged or
cherry-picked. Attribution is recorded under Unreleased in CHANGELOG.md.

Adapted concepts: retained process handles, descendant snapshots, a 750 ms quiet
period, verified registered application activation, versioned CLI discovery, and
removal of the single stale production tray registration.

Differences from the fork:

- Production process identity uses Windows package family/full-name/install-root
  APIs and the package's registered manifest executable. It does not guess from
  a process name, a description, or a fixed WindowsApps directory depth.
- Parent/child relationships also check process creation and exit times. PID
  reuse cannot authorize terminating a different process. An ambiguous reused
  parent fails shutdown before authorization changes.
- Graceful close is requested for processes found throughout shutdown, including
  new children and independent verified respawns. Forced termination addresses
  individual retained identities, never an unverified entire process tree.
- Independently running ChatGPT, CLI, editors, terminals, browsers and the
  switcher are excluded. Protected CLI/terminal/other OpenAI descendants and their
  trees are only observed; if they survive, shutdown fails rather than killing
  them or replacing authorization prematurely.
- Complete exit requires signaled Windows process handles and a stable empty
  period. Final checks surround cleanup and run again immediately before the
  existing auth-only transaction. Cancellation aborts before that transaction.
- Relaunch registration comes from the installed package manifest, not the Start
  Menu display name. A valid executable, exact family/AUMID and the activation
  service are prepared before shutdown when automatic relaunch is enabled.
  Activation checks HRESULT and returned PID; the controller then waits for a
  window whose process passes the same official identity check.
- Tray cleanup is best effort. Only GUID
  `e5768d8b-6936-4f45-b1ad-4c5fb414cb35` is queried/deleted, after this operation
  observed Desktop, all retained children exited, the quiet period succeeded,
  cancellation was checked, and a final snapshot was empty. Missing icons are
  harmless; cleanup failure logs a generic warning and continues. Even a failing
  warning sink is non-fatal. No Explorer settings/cache or unrelated icons change.
- CLI ranking uses product/file semantic version first, then semantic directory
  version, then modification time and ordinal path tie-breaking. Release and
  numeric prerelease identifiers are ordered semantically. The existing fixed
  official install has precedence, then current/versioned bundled installs, then
  explicit PATH. Known-location searches reject reparse points below LocalAppData
  and do not recursively search arbitrary folders. The structured app-server
  usage-limit implementation remains intact.

Read-only validation of the installed package confirmed the family
`OpenAI.Codex_2p2nqsd0c76g0`, application ID `App`, and `app/ChatGPT.exe`.
No automated test activates/closes real Codex or changes real user authorization.

## Changed files

Core services:

- `OfficialCodexDesktopIdentity.cs`
- `CodexLaunchIdentity.cs`
- `CodexSwitchPreparation.cs`
- `CodexCliLocator.cs`
- `CodexCliStatusUsageProvider.cs` (locator extracted; provider unchanged)
- `CodexDesktopProcessSession.cs`
- `CodexShutdownCoordinator.cs`
- `CodexTrayRegistration.cs`

Windows application:

- `WindowsDesktopProcessCatalog.cs`
- `CodexAppLauncher.cs`
- `WindowsTrayRegistration.cs`
- `CodexProcessService.cs`
- `CodexWindowFinder.cs`
- `OverlayController.cs`

Tests:

- `OfficialCodexDesktopIdentityTests.cs`: package/path/manifest identity, process
  names, lookalikes, CLI/overlay rejection and version/layout changes.
- `CodexLaunchIdentityTests.cs`: AUMID validation, whitespace, preflight ordering,
  preflight failure, cancellation and disabled relaunch.
- `CodexCliLocatorTests.cs`: all discovery sources, metadata precedence, semantic
  versions/prereleases, deterministic timestamp fallback, unrelated locations and
  missing LocalAppData.
- `CodexDesktopProcessSessionTests.cs`: roots, descendants, orphan/new children,
  respawns, protected trees, retained/exited identities, PID reuse and disposal.
- `CodexShutdownCoordinatorTests.cs`: graceful/forced shutdown, quiet period,
  surviving children, cancellation, final respawns, cleanup gates, non-fatal tray
  and warning failures; authorization assertions use disposable TestLayout files.
- `CodexTrayRegistrationTests.cs`: exact GUID, absent icon, unrelated icon and
  deletion failure.
- `WindowsDesktopAdapterTests.cs`: real native snapshot/handle of the test host
  only, plus rejection of unrelated GUID/AUMID before Shell/COM actions.
- `CodexProfileOverlay.Tests.csproj`: compile actual Windows adapters into tests
  without introducing test/probe binaries into production.

Documentation: `CHANGELOG.md`, this file.

## Validation and packaging

Use the repository-local .NET SDK when installed:

```powershell
.\.dotnet\dotnet.exe restore CodexProfileOverlay.sln
.\.dotnet\dotnet.exe format CodexProfileOverlay.sln --verify-no-changes --no-restore
.\.dotnet\dotnet.exe build CodexProfileOverlay.sln -c Release -p:Platform=x64 --no-restore -p:RunAnalyzers=true -p:RunAnalyzersDuringBuild=true
.\.dotnet\dotnet.exe test CodexProfileOverlay.sln -c Release -p:Platform=x64 --no-build
.\verify-repository-safety.ps1
.\publish.ps1 -Configuration Release
```

The final portable artifact is `artifacts/CodexProfileOverlay-win-x64-portable.zip`;
the runnable manual-test application is `artifacts/publish/CodexProfileOverlay.exe`.
Checksums are in `artifacts/SHA256SUMS.txt`.
The product version stays `1.0.0`. No tags/releases/pushes are part of this task.

Validated results (2026-10-05):

- Restore and full `dotnet format --verify-no-changes`: passed.
- Release x64 build with analyzers enabled: passed, zero errors/warnings.
- All tests: **230 passed, zero failed/skipped**, including **96 new regression
  cases**. Targeted identity/CLI/lifecycle/tray tests and real native adapter tests
  also passed independently.
- Repository safety scan and staged whitespace check: passed.
- `publish.ps1`: passed. ZIP contains exactly one `CodexProfileOverlay.exe`, with
  no authorization/profile/session/log/diagnostic/PDB/test/probe files. The binary
  was checked for the current user's private directory, workspace path and test
  assembly/probe markers in ASCII and both UTF-16 alignments; none were found.
  SHA256SUMS matches the EXE and ZIP.
- Source commits: `bce724d1ea0d275b99591efac0bd1a50392de681` (identity/preflight/CLI)
  and `6ce2f43acf96a463003a22b195f804acbc7edb69` (lifecycle/activation/tray).
- Ordinary ChatGPT Desktop is not targeted; tray-cleanup failure is non-fatal.
  No real account switch or real authorization change was performed. Nothing
  was pushed, merged, tagged or released; existing `v1.0.0` was preserved.

An account can be restarted by an external launcher at any time; this implementation
uses repeated snapshots and retained handles to reject observed races before auth
replacement. Windows offers no atomic lock spanning other launchers and a file
transaction. The real multi-account/tray lifecycle therefore still needs the
following manual check; automated tests do not claim to have performed it.

## Manual checklist: A → B → C → A

Exit the old switcher and run the published manual-test executable. Keep ordinary
ChatGPT Desktop open alongside Codex, and enable "Launch Codex after switching".
For **each** transition A→B, B→C and C→A verify:

1. The intended account is active.
2. Old Codex Desktop and its helpers have fully exited.
3. New Codex launched automatically.
4. Its official window is detected even when the process is `ChatGPT.exe`.
5. Ordinary ChatGPT Desktop stayed open and untouched.
6. The Codex tray icon is present after each repeated switch.
7. Usage limits still refresh through app-server.
8. No duplicate Codex instances remain (normal Electron helper processes are expected).

Also disable automatic relaunch once and confirm the account changes without
launching Codex. A tray-cleanup warning must not prevent a successful switch.
