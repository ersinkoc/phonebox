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
  <b>Bahasa Indonesia</b> ·
  <a href="README.th.md">ไทย</a> ·
  <a href="README.uk.md">Українська</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Jadikan ponsel Android lama sebagai panel meja untuk komputer Anda.</b><br>
Lihat statistik PC, sesi pemrograman AI, dan musik serta gunakan kontrol sentuh melalui USB atau Wi-Fi.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Coba demo langsung</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Muat turun</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox menunjukkan otomatisasi, rumah dan halaman perencana pada tiga telepon Android"></p>

## Yang dapat Anda lakukan

- Pantau CPU, GPU, RAM, penyimpanan, jaringan, dan proses yang berjalan.
- Lihat sesi Claude dan Codex, kuota AI, peninjauan GitHub, dan status CI.
- Jalankan pintasan dan makro, atur audio, serta kendalikan OBS melalui Control Deck.
- Tampilkan jam, cuaca, kalender, catatan, media, dan kontrol Home Assistant.
- Susun halaman dan widget di editor visual. Lihat pratinjaunya sebelum menyimpan.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox tur menunjukkan notifikasi, peringatan izin dan penggantian halaman otomatis"></a><br>
<sub>Data demo: pemberitahuan, prompt izin dan penggantian halaman otomatis. <a href="tour-0.6.mp4">Lihat MP4</a></sub></p>

## Mulai cepat

Gunakan telepon Android 5.0 atau lebih baru (Android 6 atau lebih baru direkomendasikan) dan komputer yang didukung. Agen ini termasuk runtime .NET.

### 1. Pasang agen desktop

Di Windows 10/11 x64, jalankan perintah berikut di PowerShell.

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

Di Linux atau macOS, jalankan perintah berikut sebagai pengguna biasa.

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Skrip pemasangan memeriksa checksum rilis dan membuka halaman pengaturan. Skrip juga dapat memasang aplikasi ponsel melalui USB. Jalankan lagi untuk memperbarui atau memperbaiki pemasangan sambil mempertahankan pengaturan dan perangkat yang tersanding.

### 2. Sambungkan telepon

Buka halaman pengaturan dari tray Windows atau kunjungi alamat di bawah ini. <http://127.0.0.1:8765>

- USB. Aktifkan proses debug USB di ponsel, sambungkan kabel data, lalu izinkan koneksi komputer. Pasang aplikasi melalui skrip pemasangan atau tombol pemasangan aplikasi ponsel di editor pengaturan.
- Wi-Fi. Pasang APK dari rilis terbaru. Aktifkan Wi-Fi di pengaturan agen, simpan, lalu mulai ulang. Pilih opsi penyandingan ponsel melalui Wi-Fi, pindai kode QR, dan pastikan kode verifikasi pada kedua perangkat sama.

## Privasi dan keamanan

PhoneBox menghubungkan ponsel dan komputer secara langsung. Anda tidak memerlukan akun PhoneBox dan aplikasi tidak mengirim telemetri. Sebagian widget opsional menghubungi layanan yang Anda atur.

## Dokumentasi

- [Pemasangan, tanda tangan dan penyelesaian masalah](SETUP.md)
- [Panduan pengguna dan galeri widget](GUIDE.md)
- [Tema, fonta dan pratinjau](APPEARANCE.md)
- [panduan Control Deck](CONTROL-DECK.md)
- [Pengembangan dan uji coba](DEVELOPMENT.md)
- [Kebijakan Privasi](../PRIVACY.md)
- [Changelog](../CHANGELOG.md)

## Lisensi

[PhoneBox dirilis di bawah MIT License.](../LICENSE)
