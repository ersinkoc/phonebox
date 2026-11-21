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
  <a href="README.nl.md">Nederlands</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.el.md">Ελληνικά</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <a href="README.zh.md">中文 (简体)</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <b>ไทย</b> ·
  <a href="README.uk.md">Українська</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>เปลี่ยนโทรศัพท์ Android เครื่องเก่าให้เป็นแผงควบคุมบนโต๊ะสำหรับคอมพิวเตอร์ของคุณ</b><br>
ดูสถานะพีซี เซสชันเขียนโค้ดด้วย AI และเพลง พร้อมควบคุมด้วยการสัมผัสผ่าน USB หรือ Wi-Fi</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>ลองดูตัวอย่างสด</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>ดาวน์โหลด</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox แสดงหน้าอัตโนมัติ, หน้าบ้าน และหน้าวางแผนบนสาม Android โทรศัพท์"></p>

## สิ่งที่ทำได้

- ติดตาม CPU, GPU, RAM, ดิสก์, เครือข่าย และโปรเซสที่กำลังทำงาน
- ดูเซสชัน Claude และ Codex โควตาการใช้ AI การตรวจโค้ดบน GitHub และสถานะ CI
- ใช้ทางลัดและมาโคร ปรับเสียง และควบคุม OBS ผ่าน Control Deck
- แสดงนาฬิกา อากาศ ปฏิทิน โน๊ต สื่อมวลชน และตัวควบคุม Home Assistant
- จัดหน้าและวิดเจ็ตในตัวแก้ไขแบบภาพ แล้วดูตัวอย่างก่อนบันทึก

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox การแสดงการแจ้งเตือน, คำถามขออนุญาต และเปลี่ยนหน้าอัตโนมัติ"></a><br>
<sub>ข้อมูลการทดลอง: การแจ้งเตือน, คำถามขออนุญาต และเปลี่ยนหน้าอัตโนมัติ <a href="tour-0.6.mp4">ดู MP4</a></sub></p>

## เริ่มเร็ว

ใช้โทรศัพท์ Android 5.0 หรือรุ่นใหม่กว่า (แนะนำให้ใช้ Android 6) และคอมพิวเตอร์ที่รองรับ

### 1. ติดตั้งตัวแทนพื้นที่ทำงาน

สำหรับ Windows 10/11 x64 ให้รันคำสั่งต่อไปนี้ใน PowerShell

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

สำหรับ Linux หรือ macOS ให้รันคำสั่งต่อไปนี้ด้วยบัญชีผู้ใช้ปกติ

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

สคริปต์ติดตั้งตรวจสอบเช็กซัมของรุ่นที่ดาวน์โหลดแล้วเปิดหน้าการตั้งค่า คุณยังติดตั้งแอปบนโทรศัพท์ผ่าน USB ได้ด้วย รันสคริปต์ซ้ำเพื่ออัปเดตหรือซ่อมแซมโดยเก็บการตั้งค่าและการจับคู่ไว้

### 2. เชื่อมโทรศัพท์

เปิดหน้าปรับแต่งจากถาด Windows หรือไปที่ที่อยู่ด้านล่างนี้ <http://127.0.0.1:8765>

- USB เปิดการแก้ไขข้อบกพร่อง USB บนโทรศัพท์ ต่อสายข้อมูล และอนุญาตให้คอมพิวเตอร์เชื่อมต่อ ใช้สคริปต์ติดตั้งหรือปุ่มติดตั้งแอปบนโทรศัพท์ในหน้าการตั้งค่า
- Wi-Fi ติดตั้ง APK จากรุ่นล่าสุด เปิด Wi-Fi ในการตั้งค่าของเอเจนต์ บันทึก แล้วเริ่มเอเจนต์ใหม่ เลือกจับคู่โทรศัพท์ผ่าน Wi-Fi สแกนรหัส QR และตรวจสอบว่ารหัสยืนยันบนอุปกรณ์ทั้งสองตรงกัน

## ความเป็นส่วนตัวและความปลอดภัย

PhoneBox เชื่อมโทรศัพท์กับคอมพิวเตอร์โดยตรง ไม่ต้องมีบัญชีและไม่ส่งข้อมูลการใช้งาน วิดเจ็ตเสริมบางตัวติดต่อเฉพาะบริการที่คุณตั้งค่าไว้

## เอกสาร

- [การติดตั้ง เซ็นชื่อ และแก้ไขปัญหา](SETUP.md)
- [คำแนะนำผู้ใช้และภาพวิดเจ็ต](GUIDE.md)
- [ชุดตกแต่ง, แบบอักษร และตัวอย่าง](APPEARANCE.md)
- [คำแนะนำ Control Deck](CONTROL-DECK.md)
- [การพัฒนาและทดสอบ](DEVELOPMENT.md)
- [นโยบายความเป็นส่วนตัว](../PRIVACY.md)
- [รายการเปลี่ยนแปลง](../CHANGELOG.md)

## ใบอนุญาต

[PhoneBox ถูกปล่อยออกมาภายใต้ MIT License](../LICENSE)
