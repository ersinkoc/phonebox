# Regression risk assessment

| Area | Risk | Isolation/mitigation |
|---|---|---|
| `AgentSession` JSON shape | Adding provider fields or changing IDs can break widget sorting, rules, session details, and tests | Preserve optional fields; use stable `antigravity:` IDs; add provider-aware UI mapping |
| `SessionMonitor` loop | A new scan can slow or throw inside the two-second loop and hide existing sessions | Separate adapter, bounded I/O, per-provider exception boundary, fixture tests |
| Claude PID/status parsing | Refactor could reintroduce stale/reused PID sessions | Leave `ReadClaude` unchanged; add Antigravity beside it |
| Codex incremental parser | Shared state/cache changes can lose appended rollout records | Keep Codex cache and offsets isolated; run `SessionDetailsTests` |
| `ApprovalStore` | New origins can collide IDs or change timeout/cancellation semantics | Add provider namespace/correlation and atomic tests; do not alter Claude behavior without regression tests |
| `/api/approvals` contract | New fields or authorization may break panel and Android proxy | Add optional fields only; preserve `items` and status codes |
| SSE `work` event | More sessions change serialized `work` and UI assumptions | Keep `{sessions,github}` shape; test unknown provider rendering and event deduplication |
| SSE approval events | Missed bounded events can leave prompts invisible | Retain polling recovery; test reconnect and event loss |
| Quota provider config | Provider filtering/normalization can hide current providers | Preserve `claude/codex/antigravity`; test unknown-provider cleanup |
| Quota widget | It currently treats non-Codex session tools as Claude and has provider styling defaults | Add explicit provider mapping and empty-state text |
| Settings migration | New provider settings can invalidate old config or change defaults | Prefer existing `CodexBarConfig.Providers` pattern; test `{}` and old configs |
| LAN allowlist | Adding provider routes may expose a new action over Wi-Fi | Keep discovery/approval routes local until authorization is proven; update `LanTests` |
| Android proxy | New route/header/path handling can bypass panel restrictions | Reuse existing allowed path and `PANEL_HEADERS`; add proxy tests |
| Windows/Linux/macOS | Process names, command-line APIs, paths, and executable discovery differ | Use existing OS branches; test injected command lines and platform-specific path fixtures |
| Build embedding | Panel edits affect Android and embedded desktop preview | Run Android lint/build and agent tests after UI changes |
| Restart/disconnect | Provider background tasks may outlive route/agent lifecycle | Honor cancellation; define explicit stale-session/approval outcomes |

## Components that must remain untouched in the first integration

Claude hook JSON semantics, Claude settings backup/removal behavior, Codex rollout parsing, Android certificate pinning, ADB reverse setup, and existing public API envelopes should be treated as compatibility contracts.

