<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="Icône PhoneBox"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <b>Français</b> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.it.md">Italiano</a> ·
  <a href="README.pt.md">Português</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.nl.md">Nederlands</a> ·
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

<p align="center"><b>Transformez un ancien téléphone Android en panneau de bureau pour votre ordinateur.</b><br>
Statistiques PC, sessions de code IA, musique et commandes tactiles via USB ou Wi-Fi.<br>
Windows, Linux et macOS. Sans root, sans compte PhoneBox et sans service cloud.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Essayer la démo</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Télécharger</b></a> ·
  <a href="#démarrage-rapide"><b>Démarrage rapide</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Statut du build"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Dernière version"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="Licence MIT"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox sur trois téléphones"><br>
<sub>Exemples de mises en page avec données de démonstration.</sub></p>

PhoneBox associe une **application Android** plein écran à un léger **agent de bureau**. Concevez votre panneau dans un éditeur visuel web : **124 widgets**, **30 modèles**, plusieurs pages et aperçu en direct.

## Ce que vous pouvez faire

| Usage | Ce qui s'affiche sur le téléphone |
|---|---|
| **Suivi PC** | CPU, GPU, RAM, disques, réseau et ping ; graphiques, détails GPU et gestion des processus |
| **Codage IA** | Quotas Claude, Codex et Antigravity, sessions locales Claude Code et Codex, revues GitHub et statut CI |
| **Commandes tactiles** | Raccourcis d'applications et URL, macros ; audio, micro, média, OBS et système via [Control Deck](CONTROL-DECK.md) |
| **Quotidien** | Lecture en cours, radio internet, horloges, météo, calendriers, notes, listes, pomodoro et minuteurs |
| **Automatisation** | Webhooks, notifications, Home Assistant et règles réactives |

## Démarrage rapide

### 1. Installer l'agent de bureau

**Windows 10/11 x64** (PowerShell) :

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux ou macOS** :

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

L'installation ouvre les paramètres sur <http://127.0.0.1:8765>. Voir le [guide d'installation](SETUP.md).

### 2. Connecter le téléphone

| USB | Wi-Fi uniquement |
|---|---|
| Activez **Options pour les développeurs → Débogage USB** et branchez le câble. | Installez l'APK depuis la [dernière version](https://github.com/ersinkoc/phonebox/releases/latest), activez **Wi-Fi & Tailscale** et scannez le QR code. |

### 3. Personnaliser

Ouvrez <http://127.0.0.1:8765>, choisissez un modèle sous **Pages**, disposez vos widgets sur la grille 12×6 et cliquez sur **Enregistrer**.

## Documentation

| Guide | Contenu |
|---|---|
| [Installation et connexions](SETUP.md) | Options, USB, Wi-Fi, Tailscale, mises à jour et dépannage |
| [Guide utilisateur](GUIDE.md) | Modèles, widgets, IA, éditeur, webhooks et règles |
| [Apparence](APPEARANCE.md) | Polices, surfaces, préréglages et aperçu |
| [Control Deck](CONTROL-DECK.md) | Audio, média, OBS, raccourcis et tâches |
| [Développement](DEVELOPMENT.md) | Architecture, démo locale, compilation et tests |
| [Confidentialité](../PRIVACY.md) | Connexions sortantes et sécurité |

## Licence

[MIT](../LICENSE)
