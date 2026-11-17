# PhoneBox architecture audit

## Scope and evidence standard

This audit is based on the checked-out source, tests, build files, and documentation. Claims are labelled **verified** when the implementation and a caller/consumer were traced, **partial** when only one side of a contract exists, and **requires runtime/independent investigation** when the repository cannot establish the external protocol.

## Overall assessment

PhoneBox is a mature, cross-platform .NET 10 desktop agent paired with an Android WebView panel. The agent owns configuration, monitoring, local integrations, HTTP routes, SSE, and approval state. Android contributes the transport selection, certificate pinning, WebView bridge, and panel shell. The same panel assets are embedded into the agent for the settings-page preview (`PhoneBox.Agent.csproj`, `Program.cs`).

The architecture is suitable for extending an existing provider, provided provider-specific discovery/parsing remains isolated from the shared `AgentSession`, `QuotaProvider`, `Approval`, SSE, and widget contracts. There is no evidence that Antigravity exposes Claude Code's `PermissionRequest` hook protocol; it must not be assumed.

## Major findings

- **Verified:** Claude Code session files are monitored, the Claude `PermissionRequest` hook is installable, approvals are held in `ApprovalStore`, and the Android panel can allow, deny, or skip them (`SessionMonitor.cs`, `ClaudeHook.cs`, `ApprovalStore.cs`, `Program.cs`, `main.js`).
- **Verified:** Codex rollout JSONL files provide session lifecycle, model, effort, token, context-window, and last-tool data. Codex quota data is separately parsed from rollout files/CodexBar.
- **Verified:** Antigravity quota support is substantial: process command-line discovery, loopback RPC with CSRF and Connect headers, structured `agy` CLI `/usage` fallback, plan extraction, legacy quota parsing, bounded timeouts, and parser tests (`QuotaMonitor.Antigravity.cs`, `AntigravityTests.cs`).
- **Missing:** Antigravity session discovery, session lifecycle, permission-event discovery, approval-origin correlation, and decision submission.
- **Verified:** USB uses `adb reverse` to the loopback agent; Wi-Fi/Tailscale uses pinned self-signed HTTPS plus a paired bearer token. A single SSE stream carries state, work, and panel events with polling fallback.
- **Critical risk:** approval IDs are random but not authenticated as originating from a particular AI process; the Wi-Fi bearer token authorizes `/api/approvals`, so token compromise permits arbitrary decisions.
- **Critical risk:** approval state is in memory only. Agent restart loses pending approvals while the originating Claude hook may remain blocked until its own timeout.
- **Important risk:** SSE events are bounded and non-durable; a Wi-Fi reconnect starts at the current sequence. The UI compensates by polling approvals, but new provider event delivery must preserve this behavior.

## Current maturity

The repository has broad unit coverage for parsers, configuration normalization, transport/pairing helpers, session parsing, and quota failure paths. Browser/UI tests exist for the panel and installation checks exist for release tooling. The main gaps are provider end-to-end tests, approval HTTP integration tests, process discovery against real installations, restart/disconnect scenarios, and Android-to-agent integration tests.

## Antigravity readiness

**Quota display: ready and reusable.** The quota API and `quotas` widget already recognize `antigravity`.

**Session display: not ready.** `SessionMonitor` only emits Claude and Codex sessions, and the Sessions widget labels every non-Codex provider as Claude.

**Approval: not ready.** Only Claude's hook can originate an `Approval`; no Antigravity protocol or event source is present.

**Recommended posture:** first establish the independent Antigravity session and permission protocols, then add narrow provider adapters that emit existing shared records. Do not alter Claude hook semantics or invent an Antigravity permission protocol based on filenames or quota RPCs.

