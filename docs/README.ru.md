<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="Значок PhoneBox"></p>

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
  <a href="README.pl.md">Polski</a> ·
  <b>Русский</b> ·
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

<p align="center"><b>Превратите старый Android-смартфон в настольную панель управления для вашего компьютера.</b><br>
Статистика ПК, сессии ИИ-программирования, музыка и сенсорное управление по USB или Wi-Fi.<br>
Windows, Linux и macOS. Без root, без аккаунта PhoneBox и без облачных сервисов.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Живое демо</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Скачать</b></a> ·
  <a href="#быстрый-старт"><b>Быстрый старт</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Статус сборки"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Последний релиз"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="Лицензия MIT"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox на трёх телефонах"><br>
<sub>Примеры макетов с демо-данными.</sub></p>

PhoneBox объединяет полноэкранное **Android-приложение** с компактным **десктопным агентом**. Настройте панель в визуальном редакторе в браузере: **123 виджетов**, **30 шаблонов**, несколько страниц и предпросмотр в реальном времени.

## Возможности

| Назначение | Что отображается на телефоне |
|---|---|
| **Мониторинг ПК** | CPU, GPU, RAM, диски, сеть и пинг; графики, детали GPU и управление процессами |
| **ИИ-кодинг** | Квоты Claude, Codex и Antigravity; локальные сессии Claude Code и Codex; ревью GitHub и статус CI |
| **Сенсорное управление** | Ярлыки приложений и URL, макросы; звук, микрофон, медиа, OBS и система через [Control Deck](CONTROL-DECK.md) |
| **На каждый день** | Сейчас играет, интернет-радио, часы, погода, календари, заметки, списки, помодоро и таймеры |
| **Автоматизация** | Вебхуки, уведомления, Home Assistant и реактивные правила |

## Быстрый старт

### 1. Установите десктопный агент

**Windows 10/11 x64** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux или macOS**:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Настройки откроются по адресу <http://127.0.0.1:8765>. Подробнее в [руководстве по установке](SETUP.md).

### 2. Подключите телефон

| USB | Только Wi-Fi |
|---|---|
| Включите **Для разработчиков → Отладка по USB** и подключите кабель. | Установите APK из [последнего релиза](https://github.com/ersinkoc/phonebox/releases/latest), включите **Wi-Fi & Tailscale** и отсканируйте QR-код. |

### 3. Настройте под себя

Откройте <http://127.0.0.1:8765>, выберите шаблон во вкладке **Страницы**, разместите виджеты на сетке 12×6 и нажмите **Сохранить**.

## Документация

| Руководство | Содержание |
|---|---|
| [Установка и подключения](SETUP.md) | Параметры, USB, Wi-Fi, Tailscale, обновления и устранение неполадок |
| [Руководство пользователя](GUIDE.md) | Шаблоны, виджеты, ИИ-интеграции, редактор, вебхуки и правила |
| [Внешний вид](APPEARANCE.md) | Шрифты, поверхности, пресеты и предпросмотр |
| [Control Deck](CONTROL-DECK.md) | Звук, медиа, OBS, ярлыки и задачи |
| [Разработка](DEVELOPMENT.md) | Архитектура, локальное демо, сборка и тесты |
| [Конфиденциальность](../PRIVACY.md) | Исходящие соединения и безопасность |

## Лицензия

[MIT](../LICENSE)
