# Account limits UI

The account row retains its switch action. The adjacent information button opens a 312-DIP WPF Popup anchored to the selected account button, preferring its left side. In Compact mode, the account menu stays open while details are visible so the anchor remains valid. Closing details also closes the account menu. Escape, outside click, hiding/minimizing Codex, switching profiles, or a tracked geometry change closes details. WPF Popup placement handles screen-edge correction without an independent permanent window.

Settings → Status and limits shows the same reusable details view for each saved profile. The profile editor retains automatic refresh and recommendation configuration; retired manual status fields are ignored when loading older JSON. The tray mode defaults to the application icon; optional modes show the active account's 5-hour or weekly remaining percentage. Unknown, stale, and expired tray values use the application icon. A local 30-second timer expires tray values without network or CLI requests.

Both views use ProfileStatusService snapshots. Opening details never refreshes. Explicit refresh works independently of automatic monitoring; the existing automatic scheduler and provider remain unchanged. Concurrent refreshes for the same case-insensitive profile ID are skipped, and the existing isolation gate, timeout and cancellation remain in use. Cached values remain visible with a stale warning after failed refreshes. Percentages are remaining capacity; missing windows are omitted. Reset countdowns and exact local timestamps only appear when supplied by the provider.

## Validation

Targeted unit coverage includes window selection, missing/stale/expired values, countdown localization, default tray mode, overlapping refreshes, existing cancellation/timeouts, recommendation preservation, settings and protocol parsing. Release x64 build and local publish are required.

An isolated render harness in ignored `artifacts/usage-render` renders the actual shared view with application resources for both themes, RU/EN and 100/125/150/175/200% scales. Representative light/Russian and dark/English images were visually inspected. Synthetic data is used only by the harness, never by the product.

Manual verification still required with Codex Desktop: account switching versus information-button clicks in Compact/Expanded/Auto; pointer and keyboard activation, Escape/outside click; resize/move/minimize and transitions across monitors with mixed DPI; placement near all screen edges; refresh while automatic polling is active; actual tray readability at Windows taskbar sizes. Static rendering does not verify native popup placement or focus behavior.
