# Antigravity implementation progress

## Status

The implementation is on `feature/antigravity-integration`. The baseline .NET suite passed before changes:

```text
dotnet test agent/PhoneBox.Agent.Tests --no-restore
Passed: 855, Failed: 0, Skipped: 0
```

## Current diff summary

Only these implementation files were changed or added:

- `android/app/src/main/assets/www/widgets/basic.js`: provider label/icon mapping
  for Claude, Codex, Antigravity, and unknown providers.
- `tools/ui-tests/integration.spec.js`: rendering regression coverage for
  Antigravity and unknown providers.
- `tools/antigravity-diagnostics/collect-antigravity.ps1`: Windows metadata-only
  diagnostic collector.
- `tools/antigravity-diagnostics/README.md`: safe execution and sharing
  instructions.
- This progress document: evidence, adapter boundary, limitations, and
  validation results.

The pre-existing audit documents under this directory remain unmodified.
Claude/Codex session readers, `AgentSession`, `ClaudeHook`, and `ApprovalStore`
were not changed.

## Completed

### Phase 0 - Baseline

- Reviewed the architecture audit documents and existing provider/session code.
- Preserved the pre-existing untracked `docs/architecture-audit/` files.
- Created the feature branch `feature/antigravity-integration`.
- Confirmed that quota monitoring already has an isolated Antigravity implementation.

### Phase 1 - Provider-aware session presentation

- Kept the existing `AgentSession` JSON contract and stable provider identifiers unchanged.
- Updated the Sessions widget to explicitly map `claude`, `codex`, and `antigravity`.
- Added a safe unknown-provider fallback rather than treating every non-Codex session as Claude.
- Changed the empty state to describe all AI coding sessions.
- Added a browser regression test for Antigravity and an unknown provider.

## Investigation findings

### Publicly documented capabilities

- The official [Antigravity permissions guide](https://antigravity.google/docs/permissions)
  documents a permission engine with Deny, Ask, and Allow lists for actions such
  as `command`, `read_file`, `write_file`, `mcp`, and URL access.
- The official [CLI permissions command](https://antigravity.google/docs/cli/commands/permissions)
  documents `/permissions` as an interactive rule manager. These documents
  describe policy configuration, not an external approval-event stream,
  request identifiers, or a decision-submission API.
- The [official Gemini CLI transition announcement](https://github.com/google-gemini/gemini-cli/discussions/27274)
  says Antigravity CLI shares architecture with the desktop app and supports
  hooks and extensions, but does not document local session storage or a
  PhoneBox-compatible session API.

### Repository evidence

- [QuotaMonitor.Antigravity.cs](../../agent/PhoneBox.Agent/QuotaMonitor.Antigravity.cs)
  establishes only bounded quota/plan sources: advertised local language-server
  endpoints and the version-gated `agy -p /usage --output-format json` command.
- [PR-FOLLOWUPS.md](../PR-FOLLOWUPS.md) explicitly records Antigravity session
  monitoring as not implemented and warns that process/database activity
  heuristics require representative fixtures before integration.
- No documented session file, lifecycle event stream, workspace-session schema,
  or supported approval transport was found.

### Diagnostic utility

- Added [collect-antigravity.ps1](../../tools/antigravity-diagnostics/collect-antigravity.ps1)
  and [its README](../../tools/antigravity-diagnostics/README.md).
- The script records installation/version metadata, matching processes and
  parent relationships, candidate data directories, and candidate file
  metadata only. It does not read contents, modify files, transmit data, or
  export credentials.

## Adapter and approval status

### Phase 2 - Antigravity session discovery

No Antigravity installation could be verified in the implementation
environment: `agy` is not on `PATH`, no Antigravity process is running, and no
Antigravity-named files were found under the user home directory. No discovery
adapter was added because fabricating session records would violate the
integration requirements.

The minimum future adapter boundary is a provider-specific source that returns
existing `AgentSession` records to `SessionMonitor`, with cancellation and
bounded I/O supplied by the monitor:

```text
ProviderId
Read(CancellationToken cancellationToken) -> IReadOnlyList<AgentSession>
```

Before implementation, a diagnostic report or documented interface must
establish a stable session identifier, workspace, active/stale signal, and
deduplication key. Model, lifecycle, and token fields remain optional until
their semantics are verified. The source must not return prompts, responses,
tool arguments, credentials, or raw database contents.

### Phases 3-5 - Lifecycle and approvals

The public permission documentation establishes policy configuration, not a
supported event/decision protocol. Lifecycle and approval integration remain
blocked. The existing Claude hook and approval flow remain unchanged; no
undocumented Antigravity RPC or token is used.

## Remaining validation

- `dotnet test agent/PhoneBox.Agent.Tests --no-restore`: passed, 855/855.
- `python tools/check-release.py`: passed.
- `git diff --check`: passed.
- `node --check android/app/src/main/assets/www/widgets/basic.js`: passed.
- The focused Playwright test was prepared but could not launch Chromium because the environment lacks `libatk-1.0.so.0`; this is an environment dependency failure, not a test assertion failure.
- Android lint/build was not run in this environment.
- Revisit session discovery after a Windows diagnostic report or supported
  Antigravity artifact/protocol is available for inspection.
