# Frontend and Android inventory

## Panel UI

The panel is a 12×6 widget grid. [`main.js`](../../android/app/src/main/assets/www/main.js) owns API access, cached config, state/event sequence reconciliation, online status, navigation, notifications, approvals, and polling fallback. Widgets are registered in `registry.js` and implemented under `widgets/`.

The quota widget in [`basic.js`](../../android/app/src/main/assets/www/widgets/basic.js) already defaults to `claude`, `codex`, and `antigravity`, rotates provider cards, displays plan/limits/reset/severity, and shows Codex token details. The Sessions widget displays session status, project, model/effort, token counts, context window, and recent tool, but currently renders only Claude and Codex labels and its empty text names only those providers.

The approval card is in [`index.html`](../../android/app/src/main/assets/www/index.html): source/project/count, tool/description/detail, timer, Allow, Deny, and question-only Got it. `main.js` polls and handles stale/duplicate submissions; `style.css` controls responsive panel layout.

## Extension points

- Add provider session records through the existing `AgentSession` JSON shape.
- Extend the Sessions widget's provider label/icon mapping and empty-state text.
- Reuse approval card only after an Antigravity prompt contract is verified; add provider-specific rendering only if fields cannot fit `Approval`.
- Reuse `PanelEvent` and `/api/stream`; avoid provider-specific WebSocket/native channels.
- Add provider IDs to settings/widget options only through existing config normalization and `widgets.json`.
- Keep sensitive provider credentials and local files server-side; `AgentConfig.ToPublic` is the boundary.

## Android transport and shell

`AgentLink` is USB-first, then pinned HTTPS over paired LAN/Tailscale addresses. It sends `X-PhoneBox-App`, `X-PhoneBox-Device`, model, and WebView headers. Control POSTs use a longer timeout and deliberately do not retry after an unknown result to avoid double execution. `AgentStream` reconnects after errors and periodically reconnects on Wi-Fi to prefer a newly available USB route.

`MainActivity` proxies allowed panel paths and protects bridge calls with a per-frame key. `network_security_config.xml` permits cleartext only to loopback, matching ADB reverse. The manifest declares internet/vibration permissions, deep links for QR pairing, and optional home/QS-tile entry points.

## Desktop settings editor

`settings.html` provides Claude hook controls, AI quota provider selection, phone selection, Wi-Fi/Tailscale pairing, per-phone pages, rules, and webhook examples. The AI quota palette already includes Antigravity. There is no Antigravity session or approval settings surface.

## Responsive and failure behavior

The panel shows offline state, WebView compatibility warnings, boot failure diagnostics, toasts, SSE reconnect state, and polling fallback. A provider integration must return explicit unavailable/error fields rather than fabricated healthy data so these existing paths remain meaningful.

