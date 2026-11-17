<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox-Symbol"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <b>Deutsch</b> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.it.md">Italiano</a> ·
  <a href="README.pt.md">Português</a> ·
  <a href="README.nl.md">Nederlands</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <a href="README.zh.md">中文 (简体)</a>
</p>

<p align="center"><b>Verwandle ein altes Android-Smartphone in ein Schreibtisch-Panel für deinen Computer.</b><br>
PC-Statistiken, KI-Coding-Sitzungen, Musik und Touch-Steuerung über USB oder WLAN.<br>
Windows, Linux und macOS. Kein Root, kein PhoneBox-Konto und kein Cloud-Dienst nötig.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Live-Demo testen</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Herunterladen</b></a> ·
  <a href="#schnellstart"><b>Schnellstart</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Build-Status"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Neueste Version"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-7%2B-3ddc84" alt="Android 7+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT-Lizenz"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox auf drei Smartphones mit Automatisierungs-, Start- und Planerseiten"><br>
<sub>Beispiellayouts mit Demodaten.</sub></p>

PhoneBox verbindet eine Vollbild-**Android-App** mit einem schlanken **Desktop-Agenten**. Gestalte dein Panel im browserbasierten visuellen Editor: **123 Widgets**, **30 Vorlagen**, mehrere Seiten und Live-Vorschau. Verbinde mehrere Telefone mit eigenen Layouts oder wechsle mit einem Telefon zwischen gekoppelten Computern.

## Funktionen

| Einsatzbereich | Anzeige auf dem Telefon |
|---|---|
| **PC-Überwachung** | CPU, GPU, RAM, Laufwerke, Netzwerk und Ping; Diagramme, GPU-Details und Prozesssteuerung |
| **KI-Coding** | Kontingente für Claude, Codex und Antigravity, Reset-Zeiten und Verbrauchsprognosen; lokale Claude-Code- und Codex-Sitzungen; GitHub-Reviews und CI-Status |
| **Touch-Steuerung** | App- und URL-Verknüpfungen, Tastenkombinationen und Makros; App-Audio, Mikrofon, Medien, OBS und Systemsteuerung über [Control Deck](CONTROL-DECK.md) |
| **Alltag** | Aktuelle Wiedergabe, Internetradio, Uhren, Wetter, Kalender, Notizen, Checklisten, Pomodoro und Timer |
| **Automatisierung** | Webhook-Werte und Benachrichtigungen, Home-Assistant-Steuerung und Regeln, die auf PC-Aktivität reagieren |

## Schnellstart

Du benötigst ein **Android-7+-Smartphone** und einen unterstützten Computer. USB nutzt Datenkabel und USB-Debugging; WLAN-Kopplung funktioniert ohne beides. Release-Agenten enthalten .NET.

### 1. Desktop-Agent installieren

**Windows 10/11 x64** — in ein normales PowerShell-Fenster einfügen:

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux oder macOS** — als eigener Benutzer ausführen:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Das Setup lädt das neueste Release herunter, prüft die SHA-256-Prüfsumme und öffnet die Einstellungen unter <http://127.0.0.1:8765>. Skripte vorab prüfen: [Windows](../setup.ps1) · [Linux/macOS](../setup.sh) · [Installationsanleitung](SETUP.md).

### 2. Telefon verbinden

| USB | Nur WLAN |
|---|---|
| **Entwickleroptionen → USB-Debugging** aktivieren, Datenkabel anschließen und Autorisierung bestätigen. | APK aus dem [neuesten Release](https://github.com/ersinkoc/phonebox/releases/latest) installieren, **Einstellungen → Allgemein → WLAN & Tailscale** aktivieren und QR-Code scannen. |

### 3. Anpassen

Öffne <http://127.0.0.1:8765>, wähle unter **Seiten** eine Vorlage, ordne Widgets im 12×6-Raster an und klicke auf **Speichern**.

## Dokumentation

| Leitfaden | Inhalt |
|---|---|
| [Installation & Verbindungen](SETUP.md) | Setup-Optionen, USB, WLAN, Tailscale, Updates, Deinstallation |
| [Benutzerhandbuch](GUIDE.md) | Vorlagen, Widgets, KI-Integrationen, Editor, Webhooks und Regeln |
| [Erscheinungsbild](APPEARANCE.md) | Schriftarten, Oberflächen, Voreinstellungen und Live-Vorschau |
| [Control Deck](CONTROL-DECK.md) | Audio, Medien, OBS, Verknüpfungen, Aufgaben und Systemlimits |
| [Entwicklung](DEVELOPMENT.md) | Architektur, lokale Demo, Builds und Tests |
| [Mitwirken](../CONTRIBUTING.md) | Beitragsrichtlinien und Datenschutz |
| [Änderungsprotokoll](../CHANGELOG.md) | Versionsverlauf |

## Sicherheit & Datenschutz

Der Einstellungsserver läuft lokal auf `127.0.0.1:8765` (USB über `adb reverse`, WLAN über HTTPS Port 8766 mit gepinntem Zertifikat). PhoneBox hat keine eigene Telemetrie oder Konten. Siehe [Datenschutzrichtlinie](../PRIVACY.md) und [SECURITY.md](../SECURITY.md).

## Lizenz

[MIT](../LICENSE)
