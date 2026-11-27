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
  <b>日本語</b> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.th.md">ไทย</a> ·
  <a href="README.uk.md">Українська</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>使わなくなった Android スマートフォンを、パソコン用のデスクパネルに。</b><br>
USB または Wi-Fi で接続し、PC の状態や AI コーディングセッション、音楽を表示。タッチ操作にも対応します。</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>ライブデモを試してみる</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>ダウンロード</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="3 台の Android スマートフォンに自動化、ホーム、プランナーを表示した PhoneBox"></p>

## できること

- CPU、GPU、メモリ、ディスク、ネットワーク、実行中のプロセスを監視する。
- Claude や Codex のセッション、AI の利用枠、GitHub のレビューや CI の状態を確認する。
- Control Deck でショートカットやマクロを実行し、音量や OBS を操作する。
- 時計、天気、予定、メモ、メディア、Home Assistant の操作ボタンを表示する。
- ビジュアルエディターでページやウィジェットを配置し、保存前にプレビューする。

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="通知、許可の確認、ページの自動切り替えを紹介する PhoneBox のデモ"></a><br>
<sub>デモデータを使用。通知、許可の確認、ページの自動切り替えを紹介します。 <a href="tour-0.6.mp4">MP4 を見る</a></sub></p>

## クイックスタート

Android 5.0 以降のスマートフォン（Android 6 以降を推奨）と、対応するパソコンが必要です。.NET ランタイムはエージェントに含まれています。

### 1. デスクトップエージェントをインストールする

Windows 10/11 x64 では、PowerShell で次のコマンドを実行します。

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

Linux または macOS では、自分のユーザーアカウントで次のコマンドを実行します。

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

セットアップは配布ファイルのチェックサムを検証して設定画面を開きます。USB 経由でスマートフォンアプリをインストールすることもできます。再実行すると、設定やペアリング情報を保持したまま更新や修復ができます。

### 2. スマートフォンを接続する

Windows のシステムトレイから設定を開くか、次のアドレスにアクセスします。 <http://127.0.0.1:8765>

- USB 接続では、スマートフォンで USB デバッグを有効にしてデータ通信対応のケーブルをつなぎ、パソコンを許可します。アプリのインストールにはセットアップか、設定画面のスマートフォンアプリのインストールボタンを使います。
- Wi-Fi 接続では、最新版の APK を入れます。エージェントの設定で Wi-Fi を有効にし、保存して再起動します。Wi-Fi ペアリングを開き、QR コードを読み取って、両方の画面で確認コードが一致することを確認します。

## プライバシーとセキュリティ

PhoneBox はスマートフォンとパソコンを直接接続します。PhoneBox アカウントは不要で、利用状況データは送信しません。一部のオプションウィジェットは、設定した外部サービスに接続します。

## ドキュメント

- [インストール、署名、トラブルシューティング](SETUP.md)
- [ユーザーガイドとウィジェット一覧](GUIDE.md)
- [テーマ、フォント、プレビュー](APPEARANCE.md)
- [Control Deck ガイド](CONTROL-DECK.md)
- [開発・テスト](DEVELOPMENT.md)
- [プライバシーポリシー](../PRIVACY.md)
- [変更履歴](../CHANGELOG.md)

## ライセンス

[PhoneBox は MIT ライセンスで公開されています。](../LICENSE)
