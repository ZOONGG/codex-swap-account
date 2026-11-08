# Codex Desktop sessions on Windows

Desktop Session Capture is experimental and requires an explicit choice and confirmation. Upgrading does not convert existing auth-only profiles.

In **Add profile**, choose **Capture Desktop session** to save the current official Codex Desktop sign-in. Sign in to the intended account before capturing and confirm that Codex may close. Capture requires a local Chromium profile that Windows can associate with the official Codex process, a file-based ChatGPT authorization, and local NTFS or ReFS storage. The overlay reopens Codex after Capture. A profile name can be reused after the old profile has been removed. To refresh a captured profile after signing in to a different account in Codex, capture under a new name.

Selecting a captured profile loads its Chromium snapshot and authorization. Switching from a captured profile to an auth-only profile first saves the active Desktop session and then switches authorization through `auth.json`. The confirmation explains this distinction. Codex chat history, projects, and shared settings continue to live in the shared `.codex` directory. Desktop switching respects the overlay setting for launching Codex after a switch.

## Recover an interrupted auth-only switch

Restart the overlay to let it reconcile the authorization journal. If it reports a recovery error, preserve the switch backups, shared `.codex/auth.json`, and affected profiles for local diagnosis.

## Recover an interrupted Desktop operation

1. Close Codex from its tray menu. In the overlay tray, choose **Open Codex**, then confirm recovery. The overlay closes any remaining verified Desktop processes and reverses the journaled changes.
2. If recovery still fails, keep Codex closed. Preserve `%LOCALAPPDATA%\CodexProfileOverlay\desktop-recovery`, including `pending.json` and its operation directory. Also preserve any corresponding directories whose names start with a space followed by `.desktop-` under the user profile, the profiles root, and beside the Chromium user-data directory. Their suffix matches the operation ID in the journal, without hyphens.
3. Keep the live Chromium directory, shared `.codex\auth.json`, and affected profiles alongside these recovery files. If the error persists after resolving file locks or disk-space issues, use a local backup or obtain help examining the journal before moving anything. The recovery directories contain private session data and should stay on the machine.

The journal records intent before each same-volume rename. Recovery examines both endpoints and reverses the steps in order from newest to oldest. When both endpoints exist or both are missing, recovery stops and preserves the files. Journals and rolled-back data can remain on disk. After a verified restart and a 24-hour grace period, cleanup can remove retired Chromium copies from eligible completed switches. Large snapshots and retained data can still increase disk use. An inactive profile removed through Profile Manager is archived with its Desktop snapshot under the existing removed-profile policy.

## Pending manual verification

- On a disposable Windows account with the official Codex package, capture two test accounts and switch in both directions. Confirm the actual account in Codex after each restart, then check that shared history, projects, and settings are still available.
- Verify graceful close, approved force fallback, and a denied force close. During staging, try launching official Codex from the Start menu and check that it cannot access the session until the operation is ready.
- In a disposable VM, interrupt a switch during the Chromium and auth renames, then use the tray recovery flow after restarting Windows. Exercise a launch failure after Codex has started but before its window becomes available, and verify that the previous account opens again.
- Verify the Capture choice and recovery messages in EN, RU, and ZH, including on a secondary monitor. Exercise an auth-only switch, profile removal, and a new Capture using the removed profile's name.
