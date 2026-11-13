# PhoneBox Community Plugins & Marketplace

PhoneBox supports installable community plugins distributed via the central marketplace repository (`ersinkoc/phonebox-plugins` by default) or loaded from your local plugins directory.

## Locations

- **Plugins directory**: `%LOCALAPPDATA%\PhoneBox\plugins\<plugin-id>\` on Windows, `~/.local/share/phonebox/plugins/<plugin-id>/` on Linux and macOS.
- **Plugin state & secrets**: `plugins.json` in the agent configuration folder (`%APPDATA%\PhoneBox\plugins.json` on Windows, `~/.config/phonebox/plugins.json` on Linux/macOS).
- **Marketplace repository setting**: configurable in **Settings › Plugins** (`plugins.repo` in `config.json`, default `ersinkoc/phonebox-plugins`).

## Security Model

1. **Permission approval**: Plugins must declare requested permissions (`http`, `exec`, `bridge`) in `plugin.json`. Marketplace and local plugins require explicit approval in Settings before activation.
2. **Archive integrity**: Marketplace downloads require HTTPS, verify SHA-256 digests, block private/link-local SSRF destinations (`ConnectCallback`) and `https -> http` redirect downgrades, reject symbolic links, and prevent Zip-Slip path traversal.
3. **Secrets stay on the PC**: Settings marked `"secret": true` (or `"type": "password"`) are stored only in `plugins.json`, masked as `__UNCHANGED__` across UI saves, and stripped from `/api/config` (including Smart Stack children) alongside secret widget options.
4. **LAN vs. Local access**: Management routes (`/api/plugins*`) and the companion loopback bridge (`/api/plugins/{id}/bridge`) are local-only (`127.0.0.1`). The phone panel only reads widget state and sends explicit widget actions through `/api/plugin/{id}/state` and `/api/plugin/{id}/act`.

## Central Marketplace Index (`index.json`)

The agent fetches `https://{owner}.github.io/{repo}/index.json`, falling back to `https://raw.githubusercontent.com/{owner}/{repo}/main/index.json`:

```json
{
  "schemaVersion": 1,
  "updatedAt": "2026-01-01T00:00:00Z",
  "plugins": [
    {
      "id": "gitea",
      "name": "Gitea",
      "version": "1.0.0",
      "author": "PhoneBox Community",
      "description": "Show open PRs, issues, and build status from Gitea.",
      "category": "Plugins",
      "icon": "🫖",
      "verified": true,
      "minAgentVersion": "0.9.0",
      "os": ["windows", "linux", "macos"],
      "runtime": "none",
      "permissions": ["http"],
      "downloadUrl": "https://github.com/ersinkoc/phonebox-plugins/releases/download/gitea-1.0.0/gitea-1.0.0.zip",
      "sha256": "<64-hex-sha256>"
    }
  ]
}
```

Platform-specific packages may use `"packages": { "win-x64": { "url": "...", "sha256": "..." }, "linux-x64": { ... } }` instead of `downloadUrl` / `sha256`.

## Plugin Manifest (`plugin.json`)

```json
{
  "id": "gitea",
  "name": "Gitea",
  "version": "1.0.0",
  "author": "PhoneBox Community",
  "description": "Show open PRs and CI status from a Gitea instance.",
  "minAgentVersion": "0.9.0",
  "os": ["windows", "linux", "macos"],
  "permissions": ["http"],
  "settings": [
    { "key": "baseUrl", "label": "Server URL", "type": "text", "default": "https://gitea.example.com" },
    { "key": "token", "label": "Access Token", "type": "password", "secret": true }
  ],
  "backend": {
    "kind": "http",
    "runtime": "none",
    "intervalSec": 30,
    "url": "{settings.baseUrl}/api/v1/repos/{options.repo}/pulls?state=open",
    "headers": { "Authorization": "token {settings.token}" },
    "actions": {
      "sync": { "method": "POST", "url": "{settings.baseUrl}/api/v1/repos/{options.repo}/mirror-sync" }
    }
  },
  "widgets": [
    {
      "type": "plugin:gitea.repos",
      "category": "Plugins",
      "name": "Gitea Repo",
      "icon": "🫖",
      "desc": "Shows open pull requests.",
      "w": 4, "h": 3, "minW": 3, "minH": 2,
      "ui": { "kind": "list" },
      "options": [
        { "key": "repo", "label": "Repository", "type": "text", "default": "ersinkoc/phonebox" },
        { "key": "control", "label": "Allow phone actions", "type": "bool", "default": true }
      ]
    }
  ]
}
```

- **Widget `type` naming**: Must be `plugin:<id>` or `plugin:<id>.<subType>`.
- **Allowed `option` types**: `text`, `password`, `number`, `bool`, `select`, `lines`, `color`, `textarea`.

## Backend Modes

| `backend.kind` | Permission | Protocol |
|---|---|---|
| `none` | none (or `bridge`) | Displays pushed companion bridge state |
| `http` | `http` | Polls `backend.url` and executes `backend.actions[op]` with `{settings.*}`, `{options.*}`, `{action.op}`, `{action.arg}` interpolation |
| `command` | `exec` | Runs `backend.entry` via `backend.runtime` (`shell`, `python3`, `node`, `pwsh`, `binary`); passes JSON request on stdin, reads `{ "data": ... }` on stdout |
| `sidecar` | `exec` | Long-running process speaking newline-delimited JSON (`init`, `poll`, `act`, `bridge` -> `reply`, unsolicited `state` pushes) |

### Companion Loopback Bridge (`bridge` permission)

External local tools or browser extensions push live state and drain queued phone actions over loopback HTTP:

- **Push state**: `POST http://127.0.0.1:8765/api/plugins/<id>/bridge` with `Authorization: Bearer <bridgeToken>` and JSON body.
- **Drain queued actions**: `GET http://127.0.0.1:8765/api/plugins/<id>/bridge/actions` with `Authorization: Bearer <bridgeToken>`.

## Widget UI Modes (`ui.kind`)

- **`auto` / `stat` / `gauge` / `list` / `buttons`**: Declarative native PhoneBox cards driven by backend JSON (`title`, `sub`, `badge`, `tone`, `value`, `unit`, `pct`, `metrics`, `items`, `actions`).
- **`html`**: Sandboxed `iframe` (`sandbox="allow-scripts"`) loading `ui.file` from the plugin folder. Inside the HTML widget:
  - `window.phonebox.on('data' | 'state', fn)` receives updates.
  - `window.phonebox.act(op, arg)` dispatches `/api/plugin/<id>/act` when phone controls are enabled.
