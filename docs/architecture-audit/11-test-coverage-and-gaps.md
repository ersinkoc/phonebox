# Test coverage and gaps

## Existing automated coverage

### AI and quota

- [`AntigravityTests.cs`](../../agent/PhoneBox.Agent.Tests/AntigravityTests.cs): CLI version/envelope handling, endpoint parsing/discovery, RPC fallback/cancellation, quota groups/fractions/reset/severity, malformed responses, legacy limits, plan fallback.
- [`SessionDetailsTests.cs`](../../agent/PhoneBox.Agent.Tests/SessionDetailsTests.cs): Claude session/PID filtering, Codex resumed sessions, incremental/partial JSONL, lifecycle, token metadata, prompt/tool privacy.
- [`CodexBarTests.cs`](../../agent/PhoneBox.Agent.Tests/CodexBarTests.cs), [`CodexQuotaTests.cs`](../../agent/PhoneBox.Agent.Tests/CodexQuotaTests.cs): quota parsing, fallback, stale data and cost information.
- Claude quota cache/backoff/schedule tests cover failure behavior, but the external API is not live-tested.

### Approval and transport

- [`QuestionPromptTests.cs`](../../agent/PhoneBox.Agent.Tests/QuestionPromptTests.cs): question text and `skip` terminal fallback.
- [`ConfigTests.cs`](../../agent/PhoneBox.Agent.Tests/ConfigTests.cs): LAN allowlist, token comparison, config/public projection, push keys.
- [`PairingTests.cs`](../../agent/PhoneBox.Agent.Tests/PairingTests.cs), [`AdbReverseTests.cs`](../../agent/PhoneBox.Agent.Tests/AdbReverseTests.cs): pairing and ADB helpers.
- UI tests cover panel navigation, widgets, WebView behavior, installation/demo flows; they do not exercise a live approval route.

## Untested or weakly tested paths

1. Full Claude hook HTTP request → `ApprovalStore` → decision JSON end-to-end.
2. Hook settings installation/removal against real files, including malformed JSON and concurrent edits.
3. Approval timeout, hook cancellation, phone disconnect, agent restart, duplicate IDs, and simultaneous decisions.
4. SSE reconnect after dropped `approval` event and recovery via `/api/approvals`.
5. Antigravity real process enumeration and real `agy` invocation on Windows/Linux/macOS.
6. Antigravity session lifecycle and permission events: no implementation or tests exist.
7. Android `AgentLink`/`AgentStream` integration against a real TLS-pinned agent.
8. Authorization tests proving a phone token cannot access local-only routes and webhook token cannot access phone controls.
9. Provider-aware session widget rendering and unknown-provider resilience.
10. Config migration when adding provider/session/approval settings.

## Recommended regression suite

- Unit: provider ID/ID collision, session lifecycle mapping, malformed/truncated provider records, cancellation and bounded I/O.
- Approval: two concurrent decisions, expiry, stale UI decision, disconnect, restart, unknown provider, provider response failure.
- HTTP: route status/envelope/auth matrix, local-only hook, LAN allowlist, token rotation.
- SSE/UI: missed approval event, polling fallback, reconnect route switch, duplicate sequence, responsive provider labels.
- Android: USB-first selection, Wi-Fi pin mismatch, token failure, no double-submit after unknown control result.
- Cross-platform: injected command-line fixtures and path/argument quoting for Windows, Linux, macOS.

## Validation commands

The repository documents:

```text
python tools/check-release.py
dotnet test agent/PhoneBox.Agent.Tests
cd tools/ui-tests && npm ci && npx playwright install chromium && npx playwright test
cd android && ./gradlew lintDebug assembleDebug
```

These are the baseline checks to run after implementation. This audit does not add dependencies or alter source to make a check pass.

