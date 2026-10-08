# Official account token statistics: safety gate

Settings → Status and limits contains a separate, localized token statistics card.
Authenticated integration is deliberately disabled, as permitted by the task's safety fallback.
No profile is selectable, no query is sent, and no current-account response is assigned to saved profiles.
The placeholders mean unavailable, never zero. No credentials are read by this feature.

## Compatibility evidence

On 2026-10-08 the installed `codex-cli 0.162.0-alpha.2` generated a non-experimental
JSON schema containing `account/usage/read` and `GetAccountTokenUsageResponse`.
Generation used a new empty CODEX_HOME under ignored artifacts, without credentials.
This proves protocol support for this installed binary, not server entitlement or authentication safety.
No minimum supported version or compatibility with other binaries has been established.

Official reference: https://learn.chatgpt.com/docs/app-server (Token usage and authentication).
It documents nullable summary metrics, optional daily buckets, and automatic refresh
for managed ChatGPT OAuth credentials. The usage method has no documented switch to
forbid credential refresh. The existing isolated profile runtime changes CODEX_HOME
and forces file storage, but a copied refresh token can still share a remote session.
Local file isolation alone therefore does not prove that rotation cannot affect an existing session.
Current-account-only access has the same refresh concern. No authenticated smoke test was run.

## Prepared parsing

The pure parser accepts only official lifetimeTokens, peakDailyTokens, startDate and tokens.
Missing, null, negative, malformed or overflowing counts remain unknown. Dates are ordered;
ambiguous duplicate dates are excluded. Seven-day aggregation requires all seven complete
calendar dates before the supplied today date; callers must establish the service date basis
before displaying an aggregate. Missing buckets never supply implicit zeroes.
No rate-limit conversion, thread estimates, costs, database or telemetry was added.

## Deferred until safe access is established

Live provider, selector, refresh, memory cache, request deduplication, timeout/backoff,
network/auth/loading/stale states and chart are intentionally not wired to a credential context.
Tests for those live behaviors would not validate an absent integration and are deferred.
Before enabling: establish a non-refreshing authenticated transport and verified account identity,
then test cancellation, cache isolation, duplicate requests and all failure states with fakes.
Manually inspect this card in RU/EN and dark/light themes at narrow supported window sizes.
