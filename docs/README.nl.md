<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox-pictogram"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.it.md">Italiano</a> ·
  <a href="README.pt.md">Português</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <b>Nederlands</b> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.el.md">Ελληνικά</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <a href="README.zh.md">中文 (简体)</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.th.md">ไทย</a> ·
  <a href="README.uk.md">Українська</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Verander een oude Android-telefoon in een bureaupaneel voor je computer.</b><br>
Pc-statistieken, AI-codeersessies, muziek en aanraakbediening via USB of Wi-Fi.<br>
Windows, Linux en macOS. Geen root, geen PhoneBox-account en geen clouddienst nodig.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Live demo proberen</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Downloaden</b></a> ·
  <a href="#snelstart"><b>Snelstart</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Build-status"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Nieuwste release"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT-licentie"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox op drie telefoons"><br>
<sub>Voorbeeldindelingen met demogegevens.</sub></p>

PhoneBox koppelt een schermvullende **Android-app** aan een lichte **desktop-agent**. Ontwerp je paneel in de visuele editor in je browser: **124 widgets**, **30 sjablonen**, meerdere pagina's en een live voorbeeld.

## Wat je kunt doen

| Gebruik | Wat er op de telefoon verschijnt |
|---|---|
| **Pc-monitoring** | CPU, GPU, RAM, schijven, netwerk en ping; grafieken, GPU-details en procesbeheer |
| **AI-coderen** | Claude-, Codex- en Antigravity-quota; lokale Claude Code- en Codex-sessies; GitHub-reviews en CI-status |
| **Aanraakbediening** | App- en URL-snelkoppelingen, macro's; audio, microfoon, media, OBS en systeem via [Control Deck](CONTROL-DECK.md) |
| **Dagelijks gebruik** | Nu aan het spelen, internetradio, klokken, weer, agenda's, notities, checklijsten, pomodoro en timers |
| **Automatisering** | Webhooks, meldingen, Home Assistant en reactieve regels |

## Snelstart

### 1. Desktop-agent installeren

**Windows 10/11 x64** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux of macOS**:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

De instellingen openen op <http://127.0.0.1:8765>. Zie de [installatiehandleiding](SETUP.md).

### 2. Telefoon verbinden

| USB | Alleen Wi-Fi |
|---|---|
| Schakel **Opties voor ontwikkelaars → USB-foutopsporing** in en sluit de kabel aan. | Installeer de APK van de [nieuwste release](https://github.com/ersinkoc/phonebox/releases/latest), schakel **Wi-Fi & Tailscale** in en scan de QR-code. |

### 3. Aanpassen

Open <http://127.0.0.1:8765>, kies een sjabloon onder **Pagina's**, plaats widgets op het 12×6-raster en klik op **Opslaan**.

## Documentatie

| Gids | Inhoud |
|---|---|
| [Installatie en verbindingen](SETUP.md) | Opties, USB, Wi-Fi, Tailscale, updates en probleemoplossing |
| [Gebruikershandleiding](GUIDE.md) | Sjablonen, widgets, AI-integraties, editor, webhooks en regels |
| [Weergave](APPEARANCE.md) | Lettertypen, oppervlakken, voorinstellingen en voorbeeld |
| [Control Deck](CONTROL-DECK.md) | Audio, media, OBS, snelkoppelingen en taken |
| [Ontwikkeling](DEVELOPMENT.md) | Architectuur, lokale demo, builds en tests |
| [Privacy](../PRIVACY.md) | Uitgaande verbindingen en beveiliging |

## Licentie

[MIT](../LICENSE)
