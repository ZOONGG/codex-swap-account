# Changelog

## 1.1.0 - 2026-10-12

### Added
- Automatic background downloads of verified Windows updates, with installation and restart only after confirmation, and a per-user Windows installer.
- Official browser login and account reauthorization with cancellation and timeout handling.
- Detailed cached 5-hour and weekly account limits on hover and optional tray indicators.

### Changed
- Improved Codex Desktop shutdown, relaunch, process identity and tray compatibility.
- Clearer account availability and status indicators.
- Redesigned Support UI embedded in Settings, with improved amount inputs and artwork.
- More compact Settings actions and improved overlay focus and window behavior.

### Fixed
- Profile creation and auxiliary window lifecycle issues.
- Support amount input behavior and Settings window lifecycle.

## 1.0.0 - 2026-09-20

- Fixed a critical storage bug caused by the former per-account state backup implementation recursively copying `sessions`, attachments, rollout JSONL files, and local databases into every `state-*` rollback directory.
- Replaced full-state rollback copies with minimal atomic authentication transactions containing only the previous authorization, active-profile metadata, and a sanitized manifest.
- Added 10 MB per-file and 25 MB per-switch backup guards, plus retention limits of five completed backups, seven days, and 100 MB total.
- Added safe legacy `state-*` inspection and confirmed cleanup that retains the two newest copies, along with Advanced settings storage statistics and completed-backup cleanup.
- Added regression tests proving shared sessions, rollouts, attachments, settings, and SQLite data are never copied or changed by switching and rollback.
- Added profile status metadata for manual tracking of profile limits and availability.
- Added validated per-profile manual status editing in Settings; metadata remains separate from credentials.
- Added an emoji-only overlay formatter and a deterministic recommendation algorithm for future real provider data.
- Added schema migration, generalized usage windows, UTC/local-time handling, stale-cache preservation, timeout/cancellation, and notification deduplication primitives.
- Replaced the fragile interactive `/status` capture with profile-isolated `codex app-server` requests to `account/rateLimits/read`.
- Kept chats, sessions, and local Codex databases shared across account profiles; only `auth.json` changes during a switch.
- Added a one-time migration that merges legacy per-profile session files and thread database rows into the shared Codex state.
- Wait for every Codex process to exit after forced termination before replacing authorization, preventing restart races and locked database errors.
- Fixed build, test, and publish scripts to prefer the repository-local .NET SDK when the system installation contains only a runtime.
- Added provider support testing, manual refresh, last successful refresh, last safe error, CLI version display, and full master-toggle behavior for automatic limit indicators.
- Added privacy regression coverage for ANSI/terminal parsing, low-window selection, malformed output, stale cache, unavailable providers, timeouts, and redaction of email/session identifiers.
- Fixed severe overlay overhead by reusing the attached window, skipping unchanged WPF placement work, containing tracking errors, and avoiding duplicate Codex launches.
- Expanded English/Russian localization and regression coverage for settings, thresholds, recommendations, stale data, and unavailable-provider behavior.
- Removed the obsolete interactive `/status` terminal compatibility path so usage data now has one deterministic, structured app-server implementation.
- Updated the bundled SQLite native runtime to a non-vulnerable release for safe shared-state migration.

## Previous Releases

Added tray lifecycle, single-instance activation, startup registration, compact/expanded/auto display modes, global hotkeys, settings, profile management, notifications, docs, CI, packaging scripts, and repository safety checks.
