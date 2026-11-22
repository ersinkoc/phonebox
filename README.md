<p align="center"><img src="agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox icon"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <b>English</b> ·
  <a href="docs/README.tr.md">Türkçe</a> ·
  <a href="docs/README.de.md">Deutsch</a> ·
  <a href="docs/README.fr.md">Français</a> ·
  <a href="docs/README.es.md">Español</a> ·
  <a href="docs/README.it.md">Italiano</a> ·
  <a href="docs/README.pt.md">Português</a> ·
  <a href="docs/README.pt-BR.md">Português (Brasil)</a> ·
  <a href="docs/README.nl.md">Nederlands</a> ·
  <a href="docs/README.pl.md">Polski</a> ·
  <a href="docs/README.ru.md">Русский</a> ·
  <a href="docs/README.el.md">Ελληνικά</a> ·
  <a href="docs/README.vi.md">Tiếng Việt</a> ·
  <a href="docs/README.zh.md">中文 (简体)</a> ·
  <a href="docs/README.ja.md">日本語</a> ·
  <a href="docs/README.ko.md">한국어</a> ·
  <a href="docs/README.id.md">Bahasa Indonesia</a> ·
  <a href="docs/README.th.md">ไทย</a> ·
  <a href="docs/README.uk.md">Українська</a> ·
  <a href="docs/README.hi.md">हिन्दी</a> ·
  <a href="docs/README.cs.md">Čeština</a> ·
  <a href="docs/README.ar.md">العربية</a> ·
  <a href="docs/README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Turn an old Android phone into a desk panel for your computer.</b><br>
PC stats, AI coding sessions, music and touch controls over USB or Wi-Fi.<br>
Windows, Linux and macOS. No root, PhoneBox account or cloud service required.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Try the live demo</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Download</b></a> ·
  <a href="#quick-start"><b>Quick start</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Build status"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT license"></a>
</p>

<p align="center"><img src="docs/hero-0.6.png" width="900" alt="PhoneBox on three phones showing automation, home and planner pages"><br>
<sub>Example layouts with demo data.</sub></p>

PhoneBox pairs a full-screen **Android app** with a small **desktop agent**. Build your panel in a browser-based
visual editor: **128 widgets**, **30 templates**, multiple pages and a live preview. Connect several phones with
their own layouts, or switch one phone between paired computers.

## What you can do

| Use it for | What appears on the phone |
|---|---|
| **PC monitoring** | CPU, GPU, RAM, disks, network and ping; charts, GPU details and process controls |
| **AI coding** | Claude, Codex and Antigravity quotas, reset times and burn-rate forecasts; local Claude Code, Codex and OpenCode sessions (native and inside WSL); GitHub reviews and CI status |
| **Touch controls** | App and URL shortcuts, key combinations and macros; application audio, microphone, media, OBS and system controls through [Control Deck](docs/CONTROL-DECK.md) |
| **Everyday use** | Now Playing, internet radio, clocks, weather, calendars, notes, checklists, pomodoro and timers |
| **Automation** | Webhook values and notifications, Home Assistant controls and rules that react to activity on the computer |

Tap an AI session for its model, timing and token details. Opt in to Codex, Claude Code or OpenCode permission prompts
to answer **Allow**, **Deny** (and **Always allow** for OpenCode) from the phone. Optional
[CodexBar integration](docs/GUIDE.md#widgets) adds quota providers and a cost overview.

<p align="center"><a href="docs/tour-0.6.mp4"><img src="docs/tour-0.6.gif" width="780" alt="PhoneBox tour showing notifications, a Claude Code permission prompt and automatic page switching"></a><br>
<sub>A tour with demo data: notifications, permission prompts and automatic page switching. <a href="docs/tour-0.6.mp4">Watch the MP4</a></sub></p>

## Quick start

You need an **Android 5+ phone** (Android 6 or later recommended) and a supported computer. USB uses a data cable and USB debugging; Wi-Fi pairing
works without either. Release agents include .NET, so you do not need to install the runtime.

### 1. Install the desktop agent

**Windows 10/11 x64** — paste into a normal PowerShell window:

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux or macOS** — run as your own user:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Setup downloads the latest release, checks its SHA-256 checksum, offers missing dependencies and start-at-login,
and opens the settings at <http://127.0.0.1:8765>. It can also install the Android app over USB.
Run setup again to update or repair the installation while keeping settings and pairings.

> **Windows signing:** release agents are still unsigned: SignPath Foundation accepted PhoneBox and test signing works, and releases are signed once its release certificate is issued.
> SmartScreen or antivirus software may warn or block them. See the [code signing policy](#code-signing-policy)
> and [installation warnings](docs/SETUP.md#quick-start). USB debugging should be authorized only on computers you trust.

Read the scripts before running them: [Windows](setup.ps1) · [Linux/macOS](setup.sh).
The [installation guide](docs/SETUP.md) covers options, dependencies, install paths, troubleshooting and uninstalling.

### 2. Connect the phone

| USB | Wi-Fi only |
|---|---|
| Enable **Developer options → USB debugging**, connect a data cable and accept the phone's authorization prompt. Let setup install the app, or use **Settings → General → Install the phone app**. | Install the APK from the [latest release](https://github.com/ersinkoc/phonebox/releases/latest). Wi-Fi is off by default: open the settings page (tray icon, or `http://127.0.0.1:8765`), turn on **General → Wi-Fi & Tailscale → Let the phone connect without the USB cable**, click **Save**, then **Restart to apply** at the top. Choose **Pair a phone over Wi-Fi**, scan the pairing QR code and confirm the matching check code. |
| Wakes and opens the panel on connection; supports app updates over adb. | No adb or developer options needed; install app updates manually unless you later configure USB. |

The phone app is not in any app store; it comes only from this repository's releases. It runs on Android 5.0 and
newer; there is no iOS version.

For automatic USB/Wi-Fi fallback, enable Wi-Fi and connect the phone over USB once.
[Connection details and Tailscale setup →](docs/SETUP.md#wi-fi--tailscale)

### 3. Make it yours

Open <http://127.0.0.1:8765> (or double-click the Windows tray icon). Under **Pages**, choose a template, drag and
resize widgets, then **Save**. Use **Live preview** to see unsaved edits with live data.
Swipe, tap the page dots or use the phone's volume keys to move between pages.

<details>
<summary><b>Other installation methods</b></summary>

- **Manual download:** unzip the agent for your system from the [release page](https://github.com/ersinkoc/phonebox/releases/latest)
  and run it. Install the APK on the phone. Checksums and build provenance accompany the release assets.
- **Scoop (Windows):**

  ```powershell
  scoop bucket add phonebox https://github.com/ersinkoc/phonebox
  scoop install main/adb phonebox/phonebox
  ```

- **From source:** see the [installation guide](docs/SETUP.md#other-ways-to-install) and [development guide](docs/DEVELOPMENT.md).
- **winget:** manifests are prepared in [packaging/winget](packaging/winget); see the installation guide for details.

</details>

## Pages & templates

Start with Home, System, AI quotas, Sessions, Music, Planner, Automation or a Control Deck template, then adapt the
12 × 6 grid to your phone. Portrait and landscape layouts are supported. Smart Stack puts up to 12 widgets in one slot.

| Home | AI sessions |
|---|---|
| ![Clock, weather, media and PC metrics](docs/screenshots/home.png) | ![Coding sessions and GitHub activity](docs/screenshots/work.png) |
| **System** | **Layout editor** |
| ![CPU, GPU, memory and network charts](docs/screenshots/system.png) | ![Pages and widget settings in the visual editor](docs/screenshots/settings-editor.png) |

Screenshots use demo data. [Browse the template gallery and widgets](docs/GUIDE.md#pages--templates).
Change fonts, size, spacing, corners and surfaces globally or for one page or widget with the [appearance guide](docs/APPEARANCE.md).

## Platform support

| | Windows | Linux | macOS |
|---|---|---|---|
| Release builds | x64 | x64 / ARM64, glibc | Apple silicon / Intel |
| CPU, RAM, disks, network, processes | ✓ | ✓ | ✓ |
| GPU data | Windows counters; NVIDIA via `nvidia-smi`; extra sensors via [LibreHardwareMonitor](docs/SETUP.md#librehardwaremonitor-on-windows) | NVIDIA / AMD; Intel clock | Apple silicon, when exposed by macOS |
| Media | System media sessions | `playerctl` | `nowplaying-cli`; Spotify / Music fallback |
| Volume | Native | `wpctl` / `pactl` | System output; availability varies by device |
| Agent interface | Tray icon + browser settings | Browser settings | Browser settings |

Widgets depend on the host's hardware, installed tools and permissions. Advanced Control Deck features have
[their own platform requirements](docs/CONTROL-DECK.md). Windows ARM and musl Linux (such as Alpine) do not have
release builds. See the [full compatibility table](docs/SETUP.md#what-works-where).

## Documentation

| Guide | Contents |
|---|---|
| [Installation and connections](docs/SETUP.md) | Setup options, warnings, manual and source installs, USB, Wi-Fi, Tailscale, updates, uninstall and troubleshooting |
| [User guide](docs/GUIDE.md) | Template gallery, widgets, AI integrations, editor, Claude Code prompts, webhooks and automation rules |
| [Appearance](docs/APPEARANCE.md) | Fonts, surfaces, presets, inheritance and live preview |
| [Control Deck](docs/CONTROL-DECK.md) | Audio, media, OBS, shortcuts, tasks, protocols, dependencies and platform limits |
| [Discord widget](docs/DISCORD.md) | Creating the Discord application, connecting it, controls and troubleshooting |
| [Development](docs/DEVELOPMENT.md) | Architecture, local demo, builds, tests, widget sources and isolated agent runs |
| [Contributing](CONTRIBUTING.md) | Contribution scope, review expectations, privacy and required checks |
| [Changelog](CHANGELOG.md) | Release history and unreleased changes |
| [Release checklist](docs/RELEASING.md) | Maintainer build, signing and publication workflow |

The [UMIDIGI BISON extras](extras/umidigi-bison) contain the reversible, no-root tune-up used on the original development phone.

## Community add-ons

Add-ons are written and maintained by their authors. PhoneBox doesn't review or ship them. They run with your own
rights on the computer and can change PhoneBox settings through `PUT /api/settings`, so read what an add-on does
before you install it.

| Add-on | What it does | What it sends outside the PC |
|---|---|---|
| [PhoneBox Games Tab](https://github.com/lucasaz/phonebox-games-tab) (Windows) | A Games page with live FPS, cover art and play time, through the push API and a Custom HTML widget | Store / Xbox apps' package names, with the Windows display language and region, to the Microsoft Store catalog (`displaycatalog.mp.microsoft.com`); the phone loads cover images from Microsoft's image servers and from Steam's, whose URL contains the game's Steam app ID |

## Security & privacy

The desktop settings server listens on `127.0.0.1:8765`. USB reaches the agent through `adb reverse` on a loopback
port of its own, over TLS pinned like Wi-Fi, with a key the phone app makes and the agent reads over adb.
Optional Wi-Fi uses HTTPS on port 8766 with a pinned certificate and a paired key. Settings and pairing endpoints
stay local. The phone requests configured actions by ID; commands and integration credentials stay on the computer.

PhoneBox has no telemetry, analytics or accounts of its own. Optional widgets and integrations contact external
services for weather, quotas, GitHub and other data, and the agent checks GitHub for updates.
Supported public feeds can fall back through the agent over USB when the phone has no internet.
The [privacy policy](PRIVACY.md) lists outbound connections and how to disable them.
Read the [security details](docs/GUIDE.md#security--privacy). To report a vulnerability, follow [SECURITY.md](SECURITY.md).

## Development

The agent uses **.NET 10**; the Android app uses **Kotlin and a WebView** with plain JavaScript widgets.
For Android builds, install **JDK 17** and **Android SDK Platform 35** (the SDK the app is built with; the app itself
runs on Android 5.0, API 21, and later).
Preview the panel and editor from source with Python 3, using simulated data:

```sh
python tools/demo-server.py
```

Open <http://127.0.0.1:8790/panel/> or <http://127.0.0.1:8790/settings/>.
See [development commands](docs/DEVELOPMENT.md#development) for running the agent, building the APK and testing changes.
Contributions are welcome; start with [CONTRIBUTING.md](CONTRIBUTING.md).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

- **Windows agent:** SignPath Foundation accepted the project and a test-signed build passed; releases stay unsigned
  until the Foundation's release certificate is issued.
  The intended signing target is `PhoneBoxAgent.exe` in the `win-x64` release zip, built by the
  [release workflow](.github/workflows/release.yml). Every signing request requires manual approval.
  Bundled runtime and library notices are in [THIRD-PARTY-NOTICES.txt](agent/PhoneBox.Agent/THIRD-PARTY-NOTICES.txt).
- **Android app:** release APKs use the project's own signing key, independently of SignPath.
- **Team roles:** committer, reviewer and signing approver: [ersinkoc](https://github.com/ersinkoc).
  Pull requests from other contributors are reviewed before merging.
- **Privacy:** see the [privacy policy](PRIVACY.md).

## Thanks

People who tested PhoneBox on their own hardware:

- [anandyakshit](https://github.com/anandyakshit): Android 7 support, on an Android 7.1.1 phone ([#73](https://github.com/ersinkoc/phonebox/issues/73)).

## Sponsor

<!-- sponsor: WrongStack -->
<p align="center">
  <a href="https://wrongstack.com">
    <img src="docs/sponsors/wrongstack.svg" width="900" alt="Sponsored by WrongStack — open-source AI coding with durable memory and multi-agent workflows. Explore wrongstack.com.">
  </a>
</p>
<p align="center">
  <strong>Supported by <a href="https://wrongstack.com">WrongStack</a></strong> ·
  An open-source AI coding agent that remembers your codebase.<br>
  <a href="https://wrongstack.com"><strong>Explore WrongStack →</strong></a> ·
  <a href="https://github.com/wrongstack/wrongstack"><strong>View on GitHub &amp; give it a star ★</strong></a>
</p>
<!-- /sponsor: WrongStack -->

## Star History

<a href="https://www.star-history.com/#ersinkoc/phonebox&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date&theme=dark">
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
    <img alt="Star history of ersinkoc/phonebox" src="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
  </picture>
</a>

## License

[MIT](LICENSE)
