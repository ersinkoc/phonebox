# Antigravity integration plan

This is an implementation plan, not an implementation. The external Antigravity session and permission protocol is not established by this repository and is a prerequisite.

## Phase 0: Architecture preparation

- **Files:** `SessionMonitor.cs`, `Models.cs`, `Config.cs`, `Lan.cs`, `Program.cs`, `widgets/basic.js`, related tests.
- **Reuse:** `AgentSession`, `QuotaProvider`, `/api/work`, `/api/quotas`, SSE `work`, existing provider config.
- **Changes:** Define a provider adapter boundary and stable `antigravity:` IDs; make the Sessions widget provider-aware; document privacy fields.
- **Dependencies:** Independent Antigravity protocol investigation.
- **Acceptance:** Existing Claude/Codex tests pass; no raw prompt/tool arguments leave the desktop; unknown provider is rendered safely.
- **Risks/tests:** JSON/UI compatibility, provider-label regression, malformed data and cancellation tests.

## Phase 1: Antigravity session discovery

- **Files:** new provider-specific source adjacent to `SessionMonitor.cs`; `SessionMonitor` registration; `SessionDetailsTests`-style fixtures.
- **Reuse:** background monitor cadence, `AgentSession`, per-provider exception boundaries.
- **Changes:** Implement only verified IDE/CLI/session-file discovery; parse PID/process identity and stable session metadata.
- **Dependencies:** Confirm file/event/process schema from an installed Antigravity version.
- **Acceptance:** sessions appear only while live, resumed sessions are handled as designed, no private prompts/tool args are copied.
- **Risks/tests:** OS process differences, stale PIDs, file races, old/new schema fixtures.

## Phase 2: Session lifecycle integration

- **Files:** `SessionMonitor.cs`, `widgets/basic.js`, `main.js` only if polling needs changes, session tests.
- **Reuse:** `AgentSession.Status`, `Outcome`, `Since`, details dialog, `/api/work`.
- **Changes:** Map verified Antigravity lifecycle events to busy/waiting/idle/completed/aborted.
- **Dependencies:** Phase 1 event semantics.
- **Acceptance:** status transitions reach the existing widget and `session-waiting` rules without changing Claude/Codex behavior.
- **Risks/tests:** event ordering, delayed IDE state, widget assumptions; add transition and SSE tests.

## Phase 3: Permission event discovery

- **Files:** provider adapter, configuration model/settings only after protocol confirmation, tests.
- **Reuse:** `Approval` as a normalized display record, `PushStore` event signaling.
- **Changes:** Add a read-only listener/poller only if the external protocol documents prompt authenticity, request identity, expiration, and response semantics.
- **Dependencies:** Independent protocol/security investigation; no assumption that Claude hook protocol applies.
- **Acceptance:** A real prompt can be observed without executing or altering an action; malformed/unknown events are rejected.
- **Risks/tests:** command execution, CSRF/token leakage, duplicate/replayed prompts, disconnect behavior.

## Phase 4: ApprovalStore integration

- **Files:** `ApprovalStore.cs`, `Program.cs`, provider adapter, tests.
- **Reuse:** concurrent pending map, atomic `TrySetResult`, `/api/approvals`, existing panel polling.
- **Changes:** Add provider correlation and explicit completion/cancellation mapping; keep Claude hook output untouched.
- **Dependencies:** Phase 3 semantics.
- **Acceptance:** allow/deny/expiry/cancel/restart outcomes are defined and provider-specific responses are returned correctly.
- **Risks/tests:** ID collision, cross-provider decision, stale phone, concurrent taps, restart; add HTTP and end-to-end tests.

## Phase 5: Phone UI integration

- **Files:** `index.html`, `main.js`, `widgets/basic.js`, `style.css`, `i18n.js`, `widgets.json` only if settings need a new option.
- **Reuse:** approval card, provider quota/session widget, `PANEL_HEADERS`, SSE and polling.
- **Changes:** provider icon/name and any provider-specific detail presentation; preserve Claude question semantics.
- **Dependencies:** stable normalized approval/session contracts.
- **Acceptance:** responsive display, provider identification, offline/reconnect recovery, no duplicate decisions.
- **Risks/tests:** non-Codex currently defaults to Claude label; browser and Android WebView tests.

## Phase 6: Testing and regression validation

- **Files:** existing focused test files plus new provider/route fixtures.
- **Reuse:** xUnit injected process/HTTP functions, Playwright panel tests, build scripts.
- **Changes:** Add protocol fixtures, route tests, concurrency tests, Android proxy tests if available.
- **Dependencies:** all prior phases.
- **Acceptance:** agent tests, `check-release.py`, Android lint/build, and Playwright pass; failures are classified by baseline vs change.
- **Risks/tests:** platform-specific command lines, real IDE absence, network failures, cancellation and restart.

