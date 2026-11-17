<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="Icona PhoneBox"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.es.md">Español</a> ·
  <b>Italiano</b> ·
  <a href="README.pt.md">Português</a> ·
  <a href="README.nl.md">Nederlands</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <a href="README.zh.md">中文 (简体)</a>
</p>

<p align="center"><b>Trasforma un vecchio smartphone Android in un pannello da scrivania per il tuo computer.</b><br>
Statistiche del PC, sessioni di coding IA, musica e controlli touch via USB o Wi-Fi.<br>
Windows, Linux e macOS. Nessun root, nessun account PhoneBox e nessun servizio cloud.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Prova la demo live</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Scarica</b></a> ·
  <a href="#avvio-rapido"><b>Avvio rapido</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Stato build"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Ultima versione"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-7%2B-3ddc84" alt="Android 7+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="Licenza MIT"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox su tre telefoni"><br>
<sub>Layout di esempio con dati dimostrativi.</sub></p>

PhoneBox abbina un'**app Android** a schermo intero a un piccolo **agente desktop**. Progetta il tuo pannello nell'editor visivo browser: **123 widget**, **30 modelli**, più pagine e anteprima live.

## Cosa puoi fare

| Utilizzo | Cosa appare sul telefono |
|---|---|
| **Monitoraggio PC** | CPU, GPU, RAM, dischi, rete e ping; grafici, dettagli GPU e gestione processi |
| **Coding IA** | Quote Claude, Codex e Antigravity; sessioni locali Claude Code e Codex; review GitHub e stato CI |
| **Controlli touch** | Scorciatoie app e URL, macro; audio, microfono, media, OBS e sistema tramite [Control Deck](CONTROL-DECK.md) |
| **Uso quotidiano** | In riproduzione, radio internet, orologi, meteo, calendari, note, liste, pomodoro e timer |
| **Automazione** | Webhook, notifiche, Home Assistant e regole reattive |

## Avvio rapido

### 1. Installa l'agente desktop

**Windows 10/11 x64** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux o macOS**:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Le impostazioni si aprono su <http://127.0.0.1:8765>. Vedi la [guida all'installazione](SETUP.md).

### 2. Collega il telefono

| USB | Solo Wi-Fi |
|---|---|
| Abilita **Opzioni sviluppatore → Debug USB** e collega il cavo. | Installa l'APK dall'[ultima versione](https://github.com/ersinkoc/phonebox/releases/latest), attiva **Wi-Fi & Tailscale** e scansiona il codice QR. |

### 3. Personalizza

Apri <http://127.0.0.1:8765>, scegli un modello in **Pagine**, disponi i widget sulla griglia 12×6 e clicca su **Salva**.

## Documentazione

| Guida | Contenuto |
|---|---|
| [Installazione e connessioni](SETUP.md) | Opzioni, USB, Wi-Fi, Tailscale, aggiornamenti e risoluzione problemi |
| [Guida utente](GUIDE.md) | Modelli, widget, integrazioni IA, editor, webhook e regole |
| [Aspetto](APPEARANCE.md) | Font, superfici, preimpostazioni e anteprima |
| [Control Deck](CONTROL-DECK.md) | Audio, media, OBS, scorciatoie e attività |
| [Sviluppo](DEVELOPMENT.md) | Architettura, demo locale, build e test |
| [Privacy](../PRIVACY.md) | Connessioni in uscita e sicurezza |

## Licenza

[MIT](../LICENSE)
