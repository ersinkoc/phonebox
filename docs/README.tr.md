<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox simgesi"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <b>Türkçe</b> ·
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
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Eski bir Android telefonu bilgisayarınız için bir masaüstü kontrol paneline dönüştürün.</b><br>
USB veya Wi-Fi üzerinden PC istatistikleri, yapay zekâ kodlama oturumları, müzik ve dokunmatik kontroller.<br>
Windows, Linux ve macOS. Root, PhoneBox hesabı veya bulut hizmeti gerekmez.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Canlı demoyu dene</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>İndir</b></a> ·
  <a href="#hızlı-başlangıç"><b>Hızlı başlangıç</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Derleme durumu"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Son sürüm"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT lisansı"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="Otomasyon, ana sayfa ve planlayıcı sayfalarını gösteren üç telefonda PhoneBox"><br>
<sub>Demo verileriyle örnek düzenler.</sub></p>

PhoneBox, tam ekran bir **Android uygulamasını** küçük bir **masaüstü ajanıyla** eşleştirir. Tarayıcı tabanlı görsel düzenleyicide panelinizi oluşturun: **124 widget**, **30 şablon**, çoklu sayfalar ve canlı önizleme. Birden fazla telefonu kendi düzenleriyle bağlayın veya tek bir telefonu eşleştirilmiş bilgisayarlar arasında değiştirin.

## Neler yapabilirsiniz?

| Kullanım alanı | Telefonda görünenler |
|---|---|
| **PC izleme** | CPU, GPU, RAM, diskler, ağ ve ping; grafikler, GPU detayları ve işlem kontrolleri |
| **Yapay zekâ kodlama** | Claude, Codex ve Antigravity kotaları, sıfırlama süreleri ve tüketim tahminleri; yerel Claude Code ve Codex oturumları; GitHub incelemeleri ve CI durumu |
| **Dokunmatik kontroller** | Uygulama ve URL kısayolları, tuş kombinasyonları ve makrolar; [Control Deck](CONTROL-DECK.md) üzerinden uygulama sesi, mikrofon, medya, OBS ve sistem kontrolleri |
| **Günlük kullanım** | Şimdi Oynatılıyor, internet radyosu, saatler, hava durumu, takvimler, notlar, kontrol listeleri, pomodoro ve zamanlayıcılar |
| **Otomasyon** | Webhook değerleri ve bildirimler, Home Assistant kontrolleri ve bilgisayardaki etkinliğe tepki veren kurallar |

Model, zamanlama ve token ayrıntıları için bir yapay zekâ oturumuna dokunun. Telefondan **İzin ver** veya **Reddet** yanıtı vermek için Claude Code izin istemlerini etkinleştirin. İsteğe bağlı [CodexBar entegrasyonu](GUIDE.md#widgets) kota sağlayıcıları ve maliyet özeti ekler.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="Bildirimler, Claude Code izin istemi ve otomatik sayfa geçişini gösteren PhoneBox turu"></a><br>
<sub>Demo verileriyle tur: bildirimler, izin istemleri ve otomatik sayfa geçişi. <a href="tour-0.6.mp4">MP4 izle</a></sub></p>

## Hızlı başlangıç

**Android 5+ telefon** (Android 6 veya üstü önerilir) ve desteklenen bir bilgisayara ihtiyacınız vardır. USB bağlantısı veri kablosu ve USB hata ayıklama kullanır; Wi-Fi eşleştirme ikisi olmadan da çalışır. Sürüm ajanları .NET içerdiğinden ayrıca runtime kurmanız gerekmez.

### 1. Masaüstü ajanını kurun

**Windows 10/11 x64** — normal bir PowerShell penceresine yapıştırın:

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux veya macOS** — kendi kullanıcınızla çalıştırın:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Kurulum en son sürümü indirir, SHA-256 sağlama toplamını doğrular, eksik bağımlılıkları ve oturum açılışında başlatmayı sunar ve <http://127.0.0.1:8765> adresinde ayarları açar. Android uygulamasını USB üzerinden de yükleyebilir. Ayarları ve eşleştirmeleri koruyarak güncellemek veya onarmak için kurulumu tekrar çalıştırabilirsiniz.

> **Windows imzalama:** SignPath Foundation başvurusu beklemedeyken sürüm ajanları şu anda imzasızdır. SmartScreen veya antivirüs yazılımları uyarı verebilir. Bkz. [kod imzalama politikası](#kod-imzalama-politikası) ve [kurulum uyarıları](SETUP.md#quick-start). USB hata ayıklamaya yalnızca güvendiğiniz bilgisayarlarda izin verin.

Çalıştırmadan önce betikleri inceleyin: [Windows](../setup.ps1) · [Linux/macOS](../setup.sh).
[Kurulum kılavuzu](SETUP.md) seçenekleri, bağımlılıkları, kurulum yollarını, sorun gidermeyi ve kaldırmayı kapsar.

### 2. Telefonu bağlayın

| USB | Yalnızca Wi-Fi |
|---|---|
| **Geliştirici seçenekleri → USB hata ayıklama**'yı açın, veri kablosunu bağlayın ve telefondaki yetkilendirme istemini onaylayın. Kurulumun uygulamayı yüklemesine izin verin veya **Ayarlar → Genel → Telefon uygulamasını yükle**'yi kullanın. | [Son sürümden](https://github.com/ersinkoc/phonebox/releases/latest) APK'yı yükleyin. **Ayarlar → Genel → Wi-Fi & Tailscale**'i etkinleştirin, istendiğinde ajanı yeniden başlatın ve **Telefonu Wi-Fi ile eşleştir**'i seçin. Eşleştirme QR kodunu tarayın ve doğrulama kodunu onaylayın. |
| Bağlandığında ekranı uyandırır ve paneli açar; adb üzerinden uygulama güncellemelerini destekler. | adb veya geliştirici seçenekleri gerekmez; daha sonra USB yapılandırmadıkça uygulama güncellemelerini elle yükleyin. |

Otomatik USB/Wi-Fi geçişi için Wi-Fi'yi etkinleştirin ve telefonu bir kez USB üzerinden bağlayın.
[Bağlantı ayrıntıları ve Tailscale kurulumu →](SETUP.md#wi-fi--tailscale)

### 3. Kendinize göre düzenleyin

<http://127.0.0.1:8765> adresini açın (veya Windows sistem tepsisi simgesine çift tıklayın). **Sayfalar** altında bir şablon seçin, widget'ları sürükleyip boyutlandırın ve **Kaydet**'e basın. Kaydedilmemiş değişiklikleri canlı verilerle görmek için **Canlı önizleme**'yi kullanın.
Sayfalar arasında gezinmek için kaydırın, sayfa noktalarına dokunun veya telefonun ses tuşlarını kullanın.

## Sayfalar ve şablonlar

Home, System, AI quotas, Sessions, Music, Planner, Automation veya Control Deck şablonlarıyla başlayın, ardından 12 × 6 ızgarayı telefonunuza göre uyarlayın. Dikey ve yatay düzenler desteklenir. Smart Stack tek bir yuvaya 12 adede kadar widget yerleştirir.

| Ana Sayfa | Yapay zekâ oturumları |
|---|---|
| ![Saat, hava durumu, medya ve PC metrikleri](screenshots/home.png) | ![Kodlama oturumları ve GitHub etkinliği](screenshots/work.png) |
| **Sistem** | **Düzen düzenleyici** |
| ![CPU, GPU, bellek ve ağ grafikleri](screenshots/system.png) | ![Görsel düzenleyicide sayfalar ve widget ayarları](screenshots/settings-editor.png) |

Ekran görüntüleri demo verileri kullanır. [Şablon galerisi ve widget'lara göz atın](GUIDE.md#pages--templates).
[Görünüm kılavuzu](APPEARANCE.md) ile yazı tiplerini, boyutları, boşlukları, köşeleri ve yüzeyleri genel veya sayfa/widget bazında değiştirin.

## Platform desteği

| | Windows | Linux | macOS |
|---|---|---|---|
| Sürüm derlemeleri | x64 | x64 / ARM64, glibc | Apple Silicon / Intel |
| CPU, RAM, diskler, ağ, işlemler | ✓ | ✓ | ✓ |
| GPU verisi | Windows sayaçları; `nvidia-smi` ile NVIDIA; LibreHardwareMonitor ile ek sensörler | NVIDIA / AMD; Intel saat hızı | macOS tarafından sunulduğunda Apple Silicon |
| Medya | Sistem medya oturumları | `playerctl` | `nowplaying-cli`; Spotify / Music yedeği |
| Ses düzeyi | Yerel | `wpctl` / `pactl` | Sistem çıkışı; cihaza göre değişir |
| Ajan arayüzü | Tepsi simgesi + tarayıcı ayarları | Tarayıcı ayarları | Tarayıcı ayarları |

## Belgeler

| Kılavuz | İçerik |
|---|---|
| [Kurulum ve bağlantılar](SETUP.md) | Kurulum seçenekleri, uyarılar, elle ve kaynaktan kurulum, USB, Wi-Fi, Tailscale, güncellemeler, kaldırma |
| [Kullanıcı kılavuzu](GUIDE.md) | Şablon galerisi, widget'lar, yapay zekâ entegrasyonları, düzenleyici, Claude Code istemleri, webhook'lar ve kurallar |
| [Görünüm](APPEARANCE.md) | Yazı tipleri, yüzeyler, ön ayarlar, kalıtım ve canlı önizleme |
| [Control Deck](CONTROL-DECK.md) | Ses, medya, OBS, kısayollar, görevler, protokoller ve platform sınırları |
| [Geliştirme](DEVELOPMENT.md) | Mimari, yerel demo, derlemeler, testler ve izole ajan çalıştırma |
| [Katkıda bulunma](../CONTRIBUTING.md) | Katkı kapsamı, inceleme beklentileri, gizlilik ve zorunlu kontroller |
| [Değişiklik günlüğü](../CHANGELOG.md) | Sürüm geçmişi ve yayınlanmamış değişiklikler |
| [Sürüm kontrol listesi](RELEASING.md) | Bakımcı derleme, imzalama ve yayınlama iş akışı |

## Güvenlik ve gizlilik

Masaüstü ayar sunucusu `127.0.0.1:8765` adresini dinler; USB bağlantısı `adb reverse` üzerinden ulaşır. İsteğe bağlı Wi-Fi bağlantısı 8766 portunda sabitlenmiş sertifika ve eşleştirilmiş anahtarla HTTPS kullanır. Ayarlar ve eşleştirme uç noktaları yerel kalır. PhoneBox kendi telemetri, analiz veya hesap sistemine sahip değildir. Ayrıntılar için [gizlilik politikasına](../PRIVACY.md) ve [SECURITY.md](../SECURITY.md) dosyasına bakın.

## Geliştirme

Ajan **.NET 10**; Android uygulaması ise düz JavaScript widget'ları ile **Kotlin ve WebView** kullanır.
Python 3 ile simüle edilmiş veriler kullanarak paneli ve düzenleyiciyi kaynaktan önizleyebilirsiniz:

```sh
python tools/demo-server.py
```

<http://127.0.0.1:8790/panel/> veya <http://127.0.0.1:8790/settings/> adresini açın.

## Kod imzalama politikası

Ücretsiz kod imzalama [SignPath.io](https://about.signpath.io/) tarafından, sertifika ise [SignPath Foundation](https://signpath.org/) tarafından sağlanmaktadır.

- **Windows ajanı:** SignPath Foundation başvurusu beklemededir; onaylanana kadar sürümler imzasızdır.
- **Android uygulaması:** Sürüm APK'ları SignPath'ten bağımsız olarak projenin kendi imzalama anahtarını kullanır.
- **Ekip rolleri:** Commit, inceleme ve imzalama onaylayıcısı: [ersinkoc](https://github.com/ersinkoc).
- **Gizlilik:** Bkz. [gizlilik politikası](../PRIVACY.md).

## Sponsor

<!-- sponsor: WrongStack -->
<p align="center">
  <a href="https://wrongstack.com">
    <img src="sponsors/wrongstack.svg" width="900" alt="WrongStack tarafından desteklenmektedir">
  </a>
</p>
<p align="center">
  <strong><a href="https://wrongstack.com">WrongStack</a> tarafından desteklenmektedir</strong> ·
  Kod tabanınızı hatırlayan açık kaynaklı yapay zekâ kodlama ajanı.<br>
  <a href="https://wrongstack.com"><strong>WrongStack'i Keşfet →</strong></a> ·
  <a href="https://github.com/wrongstack/wrongstack"><strong>GitHub'da görüntüle ve yıldız ver ★</strong></a>
</p>
<!-- /sponsor: WrongStack -->

## Star History

<a href="https://www.star-history.com/#ersinkoc/phonebox&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date&theme=dark">
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
    <img alt="ersinkoc/phonebox yıldız geçmişi" src="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
  </picture>
</a>

## Lisans

[MIT](../LICENSE)
