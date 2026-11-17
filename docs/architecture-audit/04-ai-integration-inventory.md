# AI integration inventory

| Capability | Claude Code | OpenAI Codex | Antigravity |
|---|---|---|---|
| Quota provider ID | `claude` | `codex` | `antigravity` |
| Quota discovery | Verified: Anthropic OAuth usage API, cache/backoff | Verified: rollout `rate_limits`, CodexBar merge | Verified: language-server RPC, then `agy -p /usage --output-format json` |
| Plan detection | Provider response/config-derived | Codex/CodexBar data | Verified: `planStatus.planInfo.planName`, fallback `userTier.name` |
| Quota parsing | Verified parser and reset/error handling | Verified primary/secondary windows, reset, credits | Verified 5-hour/weekly groups, legacy model quotas, remaining fraction |
| Session discovery | Verified `~/.claude/sessions/*.json`, PID/start-time checks | Verified recent/resumed `rollout-*.jsonl` | **Missing** |
| Lifecycle | Verified busy/waiting/shell/idle from Claude status files | Verified task started/completed/aborted and recent age | **Missing** |
| Model/tokens | Session record currently has no token fields | Verified model, effort, total/input/output/cached/reasoning/context | **Missing** |
| Permission origin | Verified Claude `PermissionRequest` command hook | **Missing** | **Missing; external protocol unknown** |
| Approval UI | Verified Allow/Deny; question uses Skip/Got it | Not applicable | Not applicable |
| Decision return | Hook JSON `behavior: allow|deny` | None | None |
| CLI/process discovery | Session files; Claude CLI itself is not spawned | Rollout files/CodexBar | Verified process names/command lines and `agy` PATH/Windows install |
| HTTP/RPC protocol | Local hook HTTP | Local files/CLI | Verified loopback RPC paths and Connect headers; external contract still needs independent validation |
| CSRF | Not applicable | Not applicable | Verified `X-Codeium-Csrf-Token` extracted from process args |
| Existing tests | Session and question/approval store tests; no full hook E2E | Extensive session/quota/CodexBar tests | Broad parser/discovery/HTTP-injection tests; no live process/CLI test |

## Provider-specific evidence

### Claude Code

`ClaudeHook.HandleAsync` checks `PhoneApprovals` and `OnlyWhenAway`, creates an `Approval`, waits through `ApprovalStore`, and emits the Claude hook response. `Configure` edits `~/.claude/settings.json`, removes only PhoneBox entries, and writes a backup. `SessionMonitor.ReadClaude` filters dead/reused PIDs. The hook route is local-only in the middleware.

### Codex

`ReadCodex` discovers files newer than three hours, but retains resumed sessions in older directories through cached candidate data. `ReadCodexDetails` follows complete appended JSONL lines and intentionally excludes prompt/tool arguments from `AgentSession`. Quota reading is a separate concern in `QuotaMonitor.cs`; no permission protocol is implemented.

### Antigravity

`RefreshAntigravity` tries up to eight discovered endpoints with a ten-second budget and three-second per endpoint deadline, then CLI fallback. RPC calls are `RetrieveUserQuotaSummary` and `GetUserStatus`. CLI use is version-gated at `1.1.11` and is limited to structured usage output. Parsing is defensive and avoids converting unknown usage into healthy quota.

The repository establishes quota behavior only. It does **not** establish an Antigravity session file format, event stream, approval prompt transport, or decision RPC. Those are independent investigation blockers.

## Shared extension contract

The safest integration emits existing `QuotaProvider` records for quotas and existing `AgentSession` records for sessions, then lets `/api/quotas`, `/api/work`, SSE `work`, and existing widgets carry them. Provider-specific IDs must remain stable and UI provider labels must stop assuming “non-Codex means Claude” before a third session provider is added.

