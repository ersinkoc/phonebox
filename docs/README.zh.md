<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox 图标"></p>

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
  <a href="README.ru.md">Русский</a> ·
  <a href="README.el.md">Ελληνικά</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <b>中文 (简体)</b> ·
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

<p align="center"><b>将闲置的 Android 手机变成电脑的桌面控制面板。</b><br>
通过 USB 或 Wi-Fi 查看电脑状态、AI 编程会话、音乐与触控操作。<br>
支持 Windows、Linux 和 macOS。无需 Root、无需 PhoneBox 账号、无需云服务。</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>在线演示</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>下载</b></a> ·
  <a href="#快速开始"><b>快速开始</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="构建状态"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="最新版本"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT 许可证"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="三部手机上的 PhoneBox 示例"><br>
<sub>使用演示数据的示例布局。</sub></p>

PhoneBox 将全屏 **Android 应用**与轻量级**桌面代理**配对。通过浏览器可视化编辑器打造专属面板：**123 个小组件**、**30 个模板**、多页面以及实时预览。

## 主要功能

| 用途 | 手机端显示内容 |
|---|---|
| **电脑监控** | CPU、GPU、内存、磁盘、网络与 Ping；图表、GPU 详情与进程管理 |
| **AI 编程** | Claude、Codex 与 Antigravity 配额及消耗预测；本地 Claude Code 与 Codex 会话；GitHub 审查与 CI 状态 |
| **触控操作** | 应用与网址快捷键、宏命令；通过 [Control Deck](CONTROL-DECK.md) 控制应用音量、麦克风、媒体、OBS 与系统 |
| **日常工具** | 正在播放、网络电台、时钟、天气、日历、笔记、清单、番茄钟与计时器 |
| **自动化** | Webhook 推送与通知、Home Assistant 控制以及响应电脑活动的自动化规则 |

## 快速开始

### 1. 安装桌面代理

**Windows 10/11 x64**（PowerShell）：

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux 或 macOS**：

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

安装完成后会自动打开设置页面 <http://127.0.0.1:8765>。详见[安装指南](SETUP.md)。

### 2. 连接手机

| USB | 仅 Wi-Fi |
|---|---|
| 开启 **开发者选项 → USB 调试**并连接数据线。 | 从[最新发布](https://github.com/ersinkoc/phonebox/releases/latest)安装 APK，开启 **Wi-Fi & Tailscale** 并扫描配对二维码。 |

### 3. 自定义面板

打开 <http://127.0.0.1:8765>，在**页面**中选择模板，在 12×6 网格上拖拽调整小组件，然后点击**保存**。

## 文档

| 指南 | 内容 |
|---|---|
| [安装与连接](SETUP.md) | 安装选项、USB、Wi-Fi、Tailscale、更新与故障排查 |
| [用户指南](GUIDE.md) | 模板库、小组件、AI 集成、编辑器、Webhook 与规则 |
| [外观](APPEARANCE.md) | 字体、表面样式、预设与实时预览 |
| [Control Deck](CONTROL-DECK.md) | 音频、媒体、OBS、快捷键与任务 |
| [开发指南](DEVELOPMENT.md) | 架构、本地演示服务器、构建与测试 |
| [隐私政策](../PRIVACY.md) | 外部连接说明与安全性 |

## 许可证

[MIT](../LICENSE)
