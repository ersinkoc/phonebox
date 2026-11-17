# API and event inventory

## AI/work routes

| Method/path | Response/behavior | Consumer/auth |
|---|---|---|
| `GET /api/work` | `{ sessions, github }` | Panel/work widget; paired phone token over Wi-Fi, local origin/USB |
| `GET /api/quotas` | `{ providers, overview }` | Quota widgets/settings; paired phone token over Wi-Fi |
| `POST /api/hooks/claude/permission` | Claude hook JSON or empty text | Local-only Claude hook; no LAN |
| `GET /api/claude/hook` | `{ installed, path }` | Desktop settings |
| `POST /api/claude/hook` / `DELETE` | Install/remove hook, `{ error }` on failure | Desktop settings |
| `GET /api/approvals` | `{ items: Approval[] }` | Panel; paired phone token over Wi-Fi |
| `POST /api/approvals/{id}?decision=...` | `200` or `404` | Panel; paired phone token over Wi-Fi |

## State and streaming

| Method/path | Structure |
|---|---|
| `GET /api/state[?ev=n]` | PC/media/audio/ping/rules/privacy/OBS/toggles, config/push versions, event backlog |
| `GET /api/stream` | SSE events `event: state`, `event: work`, `event: event`; JSON data |
| `GET /api/config` | `{ version, config }`, public phone projection |
| `GET /api/push`, `/api/push/{key}` | Persistent pushed values |
| `POST/PUT /api/push/{key}` | JSON/plain value, optional TTL |
| `POST /api/notify` | emits `notify` panel event |
| `POST /api/goto/{page}` | emits `goto` event |
| `POST /api/refresh` | wakes monitors and emits `refresh`/`reload` |

## Event types

`PanelEvent` is `{ seq, kind, data, at }`. Verified kinds include `notify`, `goto`, `back`, `refresh`, `reload`, `approval`, and `approval-done`. The SSE stream sends:

- `event`: each `PanelEvent` after the client sequence.
- `state`: one-second state snapshot; phone updates `PhoneLastSeen`.
- `work`: changed serialized `{ sessions, github }`, sent every two seconds when changed.

Events are bounded in `PushStore` and are not persisted across restarts. The UI deduplicates sequence numbers and polls as a recovery path.

## Authentication and exposure

- Loopback requests require host `127.0.0.1`/`localhost`, same-origin browser requests, or the USB proxy behavior without an Origin header.
- LAN HTTPS requires `Authorization: Bearer <phone token>` for phone routes.
- Webhooks use a separate webhook token in `Authorization` or `?token=`; query tokens can appear in logs.
- `/api/pair/claim` is the sole keyless LAN route and accepts a short-lived one-time code.
- Settings, Claude hook, pairing administration, and tests are not LAN-allowed.

## Existing consumers

`main.js` loads `/config`, `/state`, `/quotas`, `/work`, `/push`, and `/approvals`; `AgentStream.kt` consumes `/stream`; `basic.js` renders quotas and sessions; `settings.html` consumes settings, info, quotas, hook, phone, pairing, and webhook routes. `Lan.AllowedOverLan` is the authoritative allowlist.

No Antigravity-specific endpoint exists. Integration should use `/api/work`, `/api/quotas`, and existing SSE events unless an external protocol requires a new desktop-only route.

