<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox"></p>

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
  <a href="README.zh.md">中文 (简体)</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.th.md">ไทย</a> ·
  <a href="README.uk.md">Українська</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <b>中文（繁體）</b>
</p>

<p align="center"><b>把用不到的 Android 手機變成電腦的桌上控制面板。</b><br>
透過 USB 或 Wi-Fi 檢視電腦資訊、AI 程式開發工作階段和音樂，並使用觸控操作。</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>試試現場演示</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>下載</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox 顯示自動化、家庭和計劃頁面 Android 電話"></p>

## 可以做什麼

- 監控 CPU、GPU、記憶體、磁碟、網路和執行中的處理程序。
- 查看 Claude 和 Codex 工作階段、AI 使用額度、GitHub 審查與 CI 狀態。
- 透過 Control Deck 執行捷徑和巨集，調整音訊並控制 OBS。
- 顯示時鐘、天氣、日曆、筆記、媒體和 Home Assistant 控件。
- 使用視覺化編輯器配置頁面與小工具，並在儲存前預覽變更。

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox 遊戲展示通知、權限提示和自動頁面切換"></a><br>
<sub>示範資料：通知、權限提示和自動頁面切換。 <a href="tour-0.6.mp4">觀看 MP4</a></sub></p>

## 快速入門

使用 Android 5.0 或更高版本的手機(建議使用 Android 6 或更高版 本)和受支持的電腦。

### 1. 安裝桌面代理

在 Windows 10/11 x64 上，請在 PowerShell 執行以下指令。

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

在 Linux 或 macOS 上，請用一般使用者帳號執行以下指令。

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

安裝指令碼會驗證發行檔的總和檢查碼並開啟設定頁面，也能透過 USB 安裝手機應用程式。再次執行可更新或修復安裝，並保留設定與配對。

### 2. 連接電話

從 Windows 托盤打開設定頁面或訪問下方的地址。 <http://127.0.0.1:8765>

- USB 連線。開啟手機上的 USB 偵錯，接上可傳輸資料的連接線，並允許電腦連線。使用安裝程式或設定編輯器中的手機應用程式安裝按鈕。
- Wi-Fi 連線。安裝最新發行版本的 APK。在電腦端代理程式的設定中開啟 Wi-Fi，儲存後重新啟動。選擇透過 Wi-Fi 配對手機，掃描 QR 碼，並確認兩台裝置上顯示的確認碼相同。

## 隱私和安全

PhoneBox 會直接連接手機與電腦。無須註冊帳號，也不會傳送遙測資料。部分選用的小工具會連線到您設定的服務。

## 文件

- [安裝、簽名和故障排除](SETUP.md)
- [用戶指南和小工具畫廊](GUIDE.md)
- [主題、字體和預覽](APPEARANCE.md)
- [Control Deck 指南](CONTROL-DECK.md)
- [開發和測試](DEVELOPMENT.md)
- [隱私政策](../PRIVACY.md)
- [變更日誌](../CHANGELOG.md)

## 授權

[PhoneBox 是以MIT授權條款發佈的](../LICENSE)
