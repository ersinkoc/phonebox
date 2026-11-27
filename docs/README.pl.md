<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="Ikona PhoneBox"></p>

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
  <a href="README.ro.md">Română</a> ·
  <a href="README.nl.md">Nederlands</a> ·
  <b>Polski</b> ·
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

<p align="center"><b>Zamień stary telefon z Androidem w biurkowy panel dla swojego komputera.</b><br>
Statystyki PC, sesje kodowania AI, muzyka i sterowanie dotykowe przez USB lub Wi-Fi.<br>
Windows, Linux i macOS. Bez roota, bez konta PhoneBox i bez chmury.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Wypróbuj demo</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Pobierz</b></a> ·
  <a href="#szybki-start"><b>Szybki start</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Status budowania"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Najnowsze wydanie"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="Licencja MIT"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox na trzech telefonach"><br>
<sub>Przykładowe układy z danymi demonstracyjnymi.</sub></p>

PhoneBox łączy pełnoekranową **aplikację Android** z lekkim **agentem na komputer**. Zaprojektuj panel w edytorze wizualnym w przeglądarce: **123 widżetów**, **30 szablonów**, wiele stron i podgląd na żywo.

## Możliwości

| Zastosowanie | Co wyświetla telefon |
|---|---|
| **Monitoring PC** | CPU, GPU, RAM, dyski, sieć i ping; wykresy, szczegóły GPU i zarządzanie procesami |
| **Kodowanie AI** | Limity Claude, Codex i Antigravity; lokalne sesje Claude Code i Codex; przeglądy GitHub i status CI |
| **Sterowanie dotykowe** | Skróty aplikacji i URL, makra; dźwięk, mikrofon, multimedia, OBS i system przez [Control Deck](CONTROL-DECK.md) |
| **Na co dzień** | Odtwarzacz, radio internetowe, zegary, pogoda, kalendarze, notatki, listy, pomodoro i minutniki |
| **Automatyzacja** | Webhooki, powiadomienia, Home Assistant i reguły reaktywne |

## Szybki start

### 1. Zainstaluj agenta na komputerze

**Windows 10/11 x64** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux lub macOS**:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Ustawienia otworzą się pod adresem <http://127.0.0.1:8765>. Zobacz [przewodnik instalacji](SETUP.md).

### 2. Połącz telefon

| USB | Tylko Wi-Fi |
|---|---|
| Włącz **Opcje programistyczne → Debugowanie USB** i podłącz kabel. | Zainstaluj APK z [najnowszego wydania](https://github.com/ersinkoc/phonebox/releases/latest), włącz **Wi-Fi & Tailscale** i zeskanuj kod QR. |

### 3. Dostosuj

Otwórz <http://127.0.0.1:8765>, wybierz szablon w sekcji **Strony**, ułóż widżety na siatce 12×6 i kliknij **Zapisz**.

## Dokumentacja

| Przewodnik | Zawartość |
|---|---|
| [Instalacja i połączenia](SETUP.md) | Opcje, USB, Wi-Fi, Tailscale, aktualizacje i rozwiązywanie problemów |
| [Podręcznik użytkownika](GUIDE.md) | Szablony, widżety, integracje AI, edytor, webhooki i reguły |
| [Wygląd](APPEARANCE.md) | Czcionki, powierzchnie, presety i podgląd |
| [Control Deck](CONTROL-DECK.md) | Dźwięk, multimedia, OBS, skróty i zadania |
| [Rozwój](DEVELOPMENT.md) | Architektura, lokalne demo, budowanie i testy |
| [Prywatność](../PRIVACY.md) | Połączenia wychodzące i bezpieczeństwo |

## Licencja

[MIT](../LICENSE)
