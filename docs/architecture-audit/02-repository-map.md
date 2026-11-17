# Repository map

## Top-level structure

| Path | Responsibility |
|---|---|
| [`agent/PhoneBox.Agent`](../../agent/PhoneBox.Agent) | .NET desktop agent, HTTP API, monitors, configuration, embedded panel/settings resources |
| [`agent/PhoneBox.Agent.Tests`](../../agent/PhoneBox.Agent.Tests) | xUnit unit and regression tests |
| [`android/app/src/main`](../../android/app/src/main) | Android shell, WebView bridge, transport and panel assets |
| [`tools/ui-tests`](../../tools/ui-tests) | Playwright browser tests and demo server fixtures |
| [`tools`](../../tools) | Release, installer, smoke, and staging checks |
| [`scripts`](../../scripts) | Build/install/uninstall/release scripts for supported platforms |
| [`packaging`](../../packaging) | Winget manifests and packaging helpers |
| [`docs`](../../docs) | User, development, release, and system documentation |
| [`.github/workflows`](../../.github/workflows) | Build/release CI |

## Desktop entry points and core modules

- [`Program.cs`](../../agent/PhoneBox.Agent/Program.cs): creates the web host, configures loopback and optional LAN HTTPS listeners, registers hosted monitors, middleware, all API routes, and the SSE stream.
- [`Config.cs`](../../agent/PhoneBox.Agent/Config.cs): `AgentConfig`, `ClaudeConfig`, `LanConfig`, page/widget/rule models, public-config projection, and normalization.
- [`SessionMonitor.cs`](../../agent/PhoneBox.Agent/SessionMonitor.cs): background two-second refresh; `ReadClaude` reads `~/.claude/sessions`, `ReadCodex` reads recent rollout JSONL.
- [`QuotaMonitor.cs`](../../agent/PhoneBox.Agent/QuotaMonitor.cs): Claude, Codex, CodexBar, cache/backoff, quota snapshots and overview.
- [`QuotaMonitor.Antigravity.cs`](../../agent/PhoneBox.Agent/QuotaMonitor.Antigravity.cs): Antigravity process/CLI discovery, RPC, CSRF, plan and quota parsing.
- [`ClaudeHook.cs`](../../agent/PhoneBox.Agent/ClaudeHook.cs): hook installation and `PermissionRequest` translation.
- [`ApprovalStore.cs`](../../agent/PhoneBox.Agent/ApprovalStore.cs): in-memory concurrent pending approvals and decision completion.
- [`PushStore.cs`](../../agent/PhoneBox.Agent/PushStore.cs): persistent pushed values plus bounded in-memory panel event queue.
- [`Lan.cs`](../../agent/PhoneBox.Agent/Lan.cs), [`Pairing.cs`](../../agent/PhoneBox.Agent/Pairing.cs): TLS certificate, address selection, token checks, QR/one-time-code pairing.
- [`AdbReverse.cs`](../../agent/PhoneBox.Agent/AdbReverse.cs): ADB discovery, device filtering, reverse tunnel, app launch, screen/session synchronization.

## Android entry points

- [`MainActivity.kt`](../../android/app/src/main/java/net/oxog/phonebox/deskpanel/MainActivity.kt): WebView, bridge methods, local proxy and panel lifecycle.
- [`AgentLink.kt`](../../android/app/src/main/java/net/oxog/phonebox/deskpanel/AgentLink.kt): USB-first/Wi-Fi fallback, pairing persistence, HTTPS certificate pinning, request headers and retry policy.
- [`AgentStream.kt`](../../android/app/src/main/java/net/oxog/phonebox/deskpanel/AgentStream.kt): one SSE connection, reconnects, route reporting, Wi-Fi rotation.
- [`assets/www/main.js`](../../android/app/src/main/assets/www/main.js): panel data store, polling/SSE reconciliation, approval prompt, notifications and navigation.
- [`assets/www/widgets/basic.js`](../../android/app/src/main/assets/www/widgets/basic.js): quota and session widgets.
- [`assets/www/widgets/pc.js`](../../android/app/src/main/assets/www/widgets/pc.js): PC/phone widgets, custom HTML bridge, rules and PC switching.

## Configuration and resources

`config.default*.json` provide platform defaults. `settings.html` is the desktop settings/layout editor. `widgets.json` and `templates.json` drive widget palette/templates. Android `assets/www` are embedded into the agent at build time, so panel changes affect both Android and desktop preview.

## Build/test entry points

- Agent: `dotnet test agent/PhoneBox.Agent.Tests`; project targets `net10.0` and Windows additionally `net10.0-windows10.0.19041.0`.
- Android: `cd android && ./gradlew lintDebug assembleDebug`.
- Browser: `cd tools/ui-tests && npm ci && npx playwright test`.
- Release check: `python tools/check-release.py`.

