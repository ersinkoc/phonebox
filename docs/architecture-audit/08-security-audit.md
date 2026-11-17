# Security audit

## Positive controls verified

- Default agent listener is loopback-only; middleware validates host and browser origin.
- Optional LAN access is HTTPS with a persisted self-signed certificate and Android certificate pinning.
- Phone and webhook tokens are separate and compared with fixed-time comparisons.
- Pairing uses a one-time random code, five-minute expiry, ten-failure limit, certificate fingerprint confirmation, and no secret in the QR fragment's HTTP request.
- LAN routes are allowlisted; settings, hook administration, and diagnostic routes remain local.
- Public config excludes commands, script arguments, feed/calendar URLs, and integration tokens.
- Widget/process actions are gated by configuration and widget permissions.
- HTTP bodies and pushed values have size/key/count limits.

## Findings and recommended mitigations

### Approval authorization and authenticity — high

Any holder of the paired phone bearer token can call `POST /api/approvals/{id}`. IDs are unguessable, but the route does not bind a decision to a device identity, provider process, or displayed approval version. A compromised phone/token can deny or allow any still-pending Claude request. Mitigate with device-scoped authorization, an approval nonce/version, and audit logging of device/request/provider; preserve the existing user experience.

### In-memory approval loss — medium/high

Restart, crash, or process replacement drops pending requests without notifying the hook. The hook eventually times out, but the panel and originating process can disagree during the interval. Add explicit restart cancellation semantics or durable pending metadata only after defining privacy and replay rules.

### Replay/stale decision risk — medium

The UI suppresses locally answered IDs and the store uses `TrySetResult`, but IDs are not persisted and there is no issued-at/version check in the decision route. A future adapter must ensure an old UI cannot decide a newly reused ID. Never reuse IDs; reject expired and provider-mismatched decisions.

### Webhook token in query strings — medium

`Lan.MatchesQuery` intentionally supports senders that cannot set headers, but URLs may be recorded by proxies, shell history, browser history, or logs. Prefer Authorization headers, document query-token risk, and support rotation/revocation.

### Sensitive local discovery — medium

Antigravity command-line parsing reads process command lines containing CSRF tokens; Codex/Claude read local session/rollout files. Current code does not send these raw values to the phone, but logs/errors and future diagnostics must not expose them. Redact tokens and never include raw prompt/tool arguments in session records.

### Loopback trust boundary — medium

Local processes can call the Claude hook and local API routes. Host/origin checks protect browsers, not malicious local processes. Treat local machine compromise as outside the current model; do not broaden hook/LAN exposure.

### Broad control routes — medium

The paired token can reach process killing, shortcuts, Docker, Home Assistant, OBS, apps, and media controls where configured. Keep provider integrations read-only until their authorization and user confirmation semantics are proven.

### External Antigravity protocol — unknown

The repository proves CSRF-protected local quota RPC calls, not that those endpoints authorize actions or expose permission prompts. Do not send commands to undocumented RPC methods. Independently validate endpoint provenance, token scope, origin checks, and replay behavior.

## Security test priorities

Test stale/duplicate IDs, simultaneous decisions, expired approvals, restart behavior, token rotation, LAN allowlist boundaries, query-token leakage documentation, malformed Antigravity command lines, and redaction of CSRF/session content.

