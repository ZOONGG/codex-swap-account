# Browser login and reauthorization (1.0.0)

Add account asks for a profile name and offers **Sign in through browser**. The
existing CLI locator finds fixed and versioned official Codex Desktop bundled
runtimes before PATH. The app invokes official `codex login`; no terminal, custom
OAuth implementation or manual credential editing is needed. If the runtime cannot
start, a localized message asks the user to install/update official Codex Desktop,
open it once, and retry.

Login uses a unique temporary CODEX_HOME outside the discoverable profile root,
with file storage forced and inherited automation credentials removed. The progress
window supports cancellation and a ten-minute timeout. Output is drained without
displaying or logging it. Only successful login with a generated auth file publishes
an account. Runtime logs and other generated files are removed.

**Reauthorize** in each account menu retains name, metadata, order and color. Old
credentials stay in place during browser login. Only auth files are atomically
replaced; auth-only backups use the existing maintenance service. Failures restore
old files. Recovery copies remain if rollback itself fails. Shared chats, sessions,
projects and databases are never copied or reset by login.

For an active profile, after successful browser login the app always requests
explicit confirmation to safely close Codex. It prepares a verified launch target,
runs the existing Windows shutdown/tray coordinator, verifies shutdown, then updates
both saved and shared auth. Declining retains old credentials. Codex is reopened;
a launch failure is reported separately. Account switching, removal and usage
refresh cannot start during login.

## Conservative status

- **Available**: official browser login succeeded in this app within 15 minutes.
- **Sign-in required**: official App Server `account/read` explicitly returns a null
  account with `requiresOpenaiAuth: true`.
- **Unknown**: unverified cached credentials, unsupported runtime, timeout, network
  failure or an unrecognized response. File existence is not proof of validity.

Checks are manual via the account menu, use an isolated credential copy, and request
`account/read` with `refreshToken: false`. They do not force token rotation while a
live Codex session might hold the old refresh token. An existing signed-in account
can therefore display Unknown; this is not a remote entitlement/expiry check.
No polling, error-message heuristics or new dependencies were added.

Official references: [CLI login](https://learn.chatgpt.com/docs/developer-commands?surface=cli),
[App Server](https://learn.chatgpt.com/docs/app-server),
[credential storage](https://learn.chatgpt.com/docs/auth).

## Integration and validation

Initial HEAD `0418622` already included `bce724d`, `6ce2f43`, `93763a5`, `1c93bfe`,
`989d950`, `a7d51ed`, `c154b37`, `0418622` and Support integration `4000849`.
The integration branch starts there; no cherry-picks, merges or conflicts were needed.
Support UI/icons, compact Settings actions, disappearing Applied message, release
checker, installer/updater, Windows lifecycle and tray code are preserved.
Support/payment routes and contracts are unchanged. Version remains 1.0.0.

`./build.ps1` builds Release x64 and runs tests. `./publish.ps1` produces the ZIP and
Inno Setup EXE from current source. Both contain `build-info.json` with source HEAD,
clean-tree flag and executable SHA-256. Both consume the same fresh publish output.
`SHA256SUMS.txt` covers the executable and both distribution artifacts.

`./test-installer-smoke.ps1 -PublishedPayload ./artifacts/publish` compiles the
production installer script under a unique test identity, installs and upgrades the
current payload, compares hashes, verifies fake-data preservation, and uninstalls.
It creates no production app registration, launches no app, uses no real accounts
and makes no payments.

Manual checks: real browser callback on Windows; cancel/retry; active profile
shutdown/relaunch and tray behavior; RU/EN and dark/light appearance; a future
official release's one-click update. Automated tests use only temporary fake data.
