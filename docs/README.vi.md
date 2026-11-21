<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="Biểu tượng PhoneBox"></p>

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
  <b>Tiếng Việt</b> ·
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

<p align="center"><b>Biến một chiếc điện thoại Android cũ thành bảng điều khiển để bàn cho máy tính của bạn.</b><br>
Thông số PC, các phiên lập trình bằng AI, nhạc và điều khiển cảm ứng qua USB hoặc Wi-Fi.<br>
Windows, Linux và macOS. Không cần root, tài khoản PhoneBox hay dịch vụ đám mây.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Dùng thử bản demo trực tiếp</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Tải xuống</b></a> ·
  <a href="#bắt-đầu-nhanh"><b>Bắt đầu nhanh</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Trạng thái bản dựng"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Bản phát hành mới nhất"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-7%2B-3ddc84" alt="Android 7+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="Giấy phép MIT"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox trên ba chiếc điện thoại hiển thị các trang tự động hóa, trang chủ và lập kế hoạch"><br>
<sub>Các bố cục ví dụ với dữ liệu demo.</sub></p>

PhoneBox ghép nối một **ứng dụng Android** toàn màn hình với một **tác nhân máy tính (agent)** nhỏ gọn. Hãy xây dựng bảng điều khiển của bạn trong trình chỉnh sửa trực quan chạy trên trình duyệt: **124 widget**, **30 mẫu**, nhiều trang và xem trước trực tiếp. Kết nối nhiều điện thoại, mỗi chiếc với bố cục riêng, hoặc chuyển một chiếc điện thoại giữa các máy tính đã ghép nối.

## Bạn có thể làm gì

| Dùng để | Những gì hiển thị trên điện thoại |
|---|---|
| **Giám sát PC** | CPU, GPU, RAM, ổ đĩa, mạng và ping; biểu đồ, chi tiết GPU và điều khiển tiến trình |
| **Lập trình bằng AI** | Hạn mức của Claude, Codex và Antigravity, thời gian đặt lại và dự báo tốc độ tiêu thụ; các phiên Claude Code và Codex cục bộ; trạng thái review và CI trên GitHub |
| **Điều khiển cảm ứng** | Phím tắt ứng dụng và URL, tổ hợp phím và macro; điều khiển âm thanh ứng dụng, micro, đa phương tiện, OBS và hệ thống thông qua [Control Deck](CONTROL-DECK.md) |
| **Dùng hằng ngày** | Đang phát, radio trên internet, đồng hồ, thời tiết, lịch, ghi chú, danh sách kiểm tra, pomodoro và hẹn giờ |
| **Tự động hóa** | Giá trị và thông báo qua webhook, điều khiển Home Assistant và các quy tắc phản ứng theo hoạt động trên máy tính |

Chạm vào một phiên AI để xem chi tiết về mô hình, thời gian và token của nó. Bật tùy chọn nhắc cấp quyền của Claude Code để trả lời **Cho phép (Allow)** hoặc **Từ chối (Deny)** ngay từ điện thoại. Tích hợp tùy chọn [CodexBar](GUIDE.md#widgets) bổ sung các nhà cung cấp hạn mức và tổng quan chi phí.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="Chuyến tham quan PhoneBox hiển thị thông báo, lời nhắc cấp quyền của Claude Code và chuyển trang tự động"></a><br>
<sub>Một chuyến tham quan với dữ liệu demo: thông báo, lời nhắc cấp quyền và chuyển trang tự động. <a href="tour-0.6.mp4">Xem bản MP4</a></sub></p>

## Bắt đầu nhanh

Bạn cần một chiếc **điện thoại Android 7+** và một máy tính được hỗ trợ. USB dùng cáp dữ liệu và gỡ lỗi USB; ghép nối qua Wi-Fi thì không cần cả hai thứ đó. Các agent bản phát hành đã kèm sẵn .NET, nên bạn không cần cài thêm runtime.

### 1. Cài đặt agent trên máy tính

**Windows 10/11 x64** — dán vào một cửa sổ PowerShell thông thường:

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux hoặc macOS** — chạy bằng tài khoản người dùng của chính bạn:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Trình cài đặt sẽ tải bản phát hành mới nhất, kiểm tra mã băm SHA-256 của nó, đề xuất các thành phần phụ thuộc còn thiếu và tùy chọn khởi động cùng lúc đăng nhập, rồi mở trang cài đặt tại <http://127.0.0.1:8765>. Nó cũng có thể cài ứng dụng Android qua USB.
Chạy lại trình cài đặt để cập nhật hoặc sửa chữa bản cài đặt mà vẫn giữ nguyên cài đặt và các ghép nối.

> **Ký số trên Windows:** các agent bản phát hành hiện chưa được ký số trong lúc chờ hồ sơ tại SignPath Foundation được duyệt.
> SmartScreen hoặc phần mềm diệt virus có thể cảnh báo hoặc chặn chúng. Xem [chính sách ký mã](#chính-sách-ký-mã)
> và [cảnh báo khi cài đặt](SETUP.md#quick-start). Chỉ nên cấp quyền gỡ lỗi USB trên những máy tính mà bạn tin tưởng.

Hãy đọc các script trước khi chạy chúng: [Windows](../setup.ps1) · [Linux/macOS](../setup.sh).
[Hướng dẫn cài đặt](SETUP.md) bao gồm các tùy chọn, thành phần phụ thuộc, đường dẫn cài đặt, khắc phục sự cố và gỡ cài đặt.

### 2. Kết nối điện thoại

| USB | Chỉ Wi-Fi |
|---|---|
| Bật **Tùy chọn nhà phát triển → Gỡ lỗi USB**, cắm cáp dữ liệu và chấp nhận lời nhắc cấp quyền trên điện thoại. Để trình cài đặt cài ứng dụng, hoặc dùng **Cài đặt → Chung → Cài ứng dụng điện thoại**. | Cài APK từ [bản phát hành mới nhất](https://github.com/ersinkoc/phonebox/releases/latest). Bật **Cài đặt → Chung → Wi-Fi & Tailscale**, khởi động lại agent khi được yêu cầu, rồi chọn **Ghép nối điện thoại qua Wi-Fi**. Quét mã QR ghép nối và xác nhận mã kiểm tra trùng khớp. |
| Đánh thức và mở bảng điều khiển khi kết nối; hỗ trợ cập nhật ứng dụng qua adb. | Không cần adb hay tùy chọn nhà phát triển; cài bản cập nhật ứng dụng thủ công trừ khi bạn cấu hình USB về sau. |

Để tự động dự phòng USB/Wi-Fi, hãy bật Wi-Fi và kết nối điện thoại qua USB một lần.
[Chi tiết kết nối và thiết lập Tailscale →](SETUP.md#wi-fi--tailscale)

### 3. Tùy biến theo ý bạn

Mở <http://127.0.0.1:8765> (hoặc nhấp đúp vào biểu tượng PhoneBox trên khay hệ thống Windows). Trong mục **Trang (Pages)**, chọn một mẫu, kéo và thay đổi kích thước các widget, rồi bấm **Lưu (Save)**. Dùng **Xem trước trực tiếp (Live preview)** để xem các chỉnh sửa chưa lưu với dữ liệu thật.
Vuốt, chạm vào các chấm chỉ trang hoặc dùng phím âm lượng của điện thoại để di chuyển giữa các trang.

<details>
<summary><b>Các cách cài đặt khác</b></summary>

- **Tải thủ công:** giải nén agent dành cho hệ điều hành của bạn từ [trang phát hành](https://github.com/ersinkoc/phonebox/releases/latest)
  và chạy nó. Cài APK lên điện thoại. Mã kiểm tra và thông tin nguồn gốc bản dựng đi kèm với các tệp của bản phát hành.
- **Scoop (Windows):**

  ```powershell
  scoop bucket add phonebox https://github.com/ersinkoc/phonebox
  scoop install main/adb phonebox/phonebox
  ```

- **Từ mã nguồn:** xem [hướng dẫn cài đặt](SETUP.md#other-ways-to-install) và [hướng dẫn phát triển](DEVELOPMENT.md).
- **winget:** các manifest đã được chuẩn bị sẵn trong [packaging/winget](../packaging/winget); xem hướng dẫn cài đặt để biết chi tiết.

</details>

## Trang & mẫu

Bắt đầu với mẫu Trang chủ, Hệ thống, Hạn mức AI, Phiên, Nhạc, Lập kế hoạch, Tự động hóa hoặc Control Deck, rồi tùy chỉnh lưới 12 × 6 cho vừa điện thoại của bạn. Hỗ trợ cả bố cục dọc và ngang. Smart Stack đặt tối đa 12 widget vào một ô duy nhất.

| Trang chủ | Phiên AI |
|---|---|
| ![Đồng hồ, thời tiết, đa phương tiện và thông số PC](screenshots/home.png) | ![Các phiên lập trình và hoạt động GitHub](screenshots/work.png) |
| **Hệ thống** | **Trình chỉnh sửa bố cục** |
| ![Biểu đồ CPU, GPU, bộ nhớ và mạng](screenshots/system.png) | ![Trang và cài đặt widget trong trình chỉnh sửa trực quan](screenshots/settings-editor.png) |

Ảnh chụp màn hình sử dụng dữ liệu demo. [Duyệt thư viện mẫu và widget](GUIDE.md#pages--templates).
Thay đổi phông chữ, kích thước, khoảng cách, góc bo và bề mặt trên toàn cục hoặc cho riêng một trang hay widget bằng [hướng dẫn giao diện](APPEARANCE.md).

## Hỗ trợ nền tảng

| | Windows | Linux | macOS |
|---|---|---|---|
| Bản dựng phát hành | x64 | x64 / ARM64, glibc | Apple silicon / Intel |
| CPU, RAM, ổ đĩa, mạng, tiến trình | ✓ | ✓ | ✓ |
| Dữ liệu GPU | Bộ đếm của Windows; NVIDIA qua `nvidia-smi`; thêm cảm biến qua [LibreHardwareMonitor](SETUP.md#librehardwaremonitor-on-windows) | NVIDIA / AMD; xung nhịp Intel | Apple silicon, khi macOS cung cấp dữ liệu đó |
| Đa phương tiện | Các phiên media của hệ thống | `playerctl` | `nowplaying-cli`; dự phòng Spotify / Music |
| Âm lượng | Nguyên bản | `wpctl` / `pactl` | Đầu ra hệ thống; tùy thuộc vào thiết bị |
| Giao diện agent | Biểu tượng khay hệ thống + cài đặt trên trình duyệt | Cài đặt trên trình duyệt | Cài đặt trên trình duyệt |

Widget phụ thuộc vào phần cứng, công cụ đã cài và quyền của máy chủ. Các tính năng nâng cao của Control Deck có
[yêu cầu nền tảng riêng](CONTROL-DECK.md). Windows ARM và Linux musl (như Alpine) không có
bản dựng phát hành. Xem [bảng tương thích đầy đủ](SETUP.md#what-works-where).

## Tài liệu

| Hướng dẫn | Nội dung |
|---|---|
| [Cài đặt và kết nối](SETUP.md) | Tùy chọn cài đặt, cảnh báo, cài thủ công và từ mã nguồn, USB, Wi-Fi, Tailscale, cập nhật, gỡ cài đặt và khắc phục sự cố |
| [Hướng dẫn sử dụng](GUIDE.md) | Thư viện mẫu, widget, tích hợp AI, trình chỉnh sửa, lời nhắc của Claude Code, webhook và quy tắc tự động hóa |
| [Giao diện](APPEARANCE.md) | Phông chữ, bề mặt, preset, kế thừa và xem trước trực tiếp |
| [Control Deck](CONTROL-DECK.md) | Âm thanh, đa phương tiện, OBS, phím tắt, tác vụ, giao thức, thành phần phụ thuộc và giới hạn nền tảng |
| [Widget Discord](DISCORD.md) | Tạo ứng dụng Discord, kết nối, điều khiển và khắc phục sự cố |
| [Phát triển](DEVELOPMENT.md) | Kiến trúc, demo cục bộ, bản dựng, kiểm thử, nguồn widget và chạy agent tách biệt |
| [Đóng góp](../CONTRIBUTING.md) | Phạm vi đóng góp, kỳ vọng khi review, quyền riêng tư và các kiểm tra bắt buộc |
| [Nhật ký thay đổi](../CHANGELOG.md) | Lịch sử phát hành và các thay đổi chưa phát hành |
| [Danh sách kiểm tra phát hành](RELEASING.md) | Quy trình dựng, ký số và công bố của người bảo trì |

Các [tiện ích bổ sung cho UMIDIGI BISON](../extras/umidigi-bison) chứa bản tinh chỉnh có thể hoàn tác, không cần root, từng được dùng trên chiếc điện thoại phát triển gốc.

## Bảo mật & quyền riêng tư

Máy chủ cài đặt trên máy tính lắng nghe tại `127.0.0.1:8765`; USB truy cập tới nó qua `adb reverse`.
Wi-Fi tùy chọn dùng HTTPS trên cổng 8766 với chứng chỉ được ghim cố định và khóa đã ghép nối. Các endpoint cài đặt và ghép nối
luôn ở cục bộ. Điện thoại yêu cầu các hành động đã cấu hình theo ID; lệnh và thông tin đăng nhập của các tích hợp luôn nằm trên máy tính.

PhoneBox không có hệ thống đo lường từ xa (telemetry), phân tích hay tài khoản riêng. Các widget và tích hợp tùy chọn sẽ liên hệ với
các dịch vụ bên ngoài để lấy dữ liệu thời tiết, hạn mức, GitHub và các dữ liệu khác, và agent sẽ kiểm tra bản cập nhật trên GitHub.
Các nguồn cấp công khai được hỗ trợ có thể dự phòng qua agent bằng USB khi điện thoại không có internet.
[Chính sách quyền riêng tư](../PRIVACY.md) liệt kê các kết nối ra ngoài và cách tắt chúng.
Đọc [chi tiết bảo mật](GUIDE.md#security--privacy). Để báo cáo một lỗ hổng bảo mật, hãy làm theo [SECURITY.md](../SECURITY.md).

## Phát triển

Agent dùng **.NET 10**; ứng dụng Android dùng **Kotlin và WebView** với các widget viết bằng JavaScript thuần.
Để dựng bản Android, cài **JDK 17** và **Android SDK Platform 35** (SDK dùng để dựng ứng dụng; bản thân ứng dụng
chạy trên Android 7.0, API 24, trở lên).
Xem trước bảng điều khiển và trình chỉnh sửa từ mã nguồn bằng Python 3, với dữ liệu mô phỏng:

```sh
python tools/demo-server.py
```

Mở <http://127.0.0.1:8790/panel/> hoặc <http://127.0.0.1:8790/settings/>.
Xem [các lệnh phát triển](DEVELOPMENT.md#development) để chạy agent, dựng APK và kiểm thử thay đổi.
Rất hoan nghênh đóng góp; hãy bắt đầu với [CONTRIBUTING.md](../CONTRIBUTING.md).

## Chính sách ký mã

Ký mã miễn phí do [SignPath.io](https://about.signpath.io/) cung cấp, chứng chỉ do
[SignPath Foundation](https://signpath.org/) cấp.

- **Agent Windows:** hồ sơ tại SignPath Foundation đang chờ duyệt; các bản phát hành chưa được ký cho đến khi được phê duyệt.
  Đối tượng ký dự kiến là `PhoneBoxAgent.exe` trong tệp zip bản phát hành `win-x64`, được dựng bởi
  [quy trình phát hành](../.github/workflows/release.yml). Mọi yêu cầu ký đều cần phê duyệt thủ công.
  Thông báo về runtime và thư viện đi kèm nằm trong [THIRD-PARTY-NOTICES.txt](../agent/PhoneBox.Agent/THIRD-PARTY-NOTICES.txt).
- **Ứng dụng Android:** các APK bản phát hành dùng khóa ký riêng của dự án, độc lập với SignPath.
- **Vai trò trong nhóm:** người commit, người review và người phê duyệt ký số: [ersinkoc](https://github.com/ersinkoc).
  Pull request từ các cộng tác viên khác sẽ được review trước khi hợp nhất.
- **Quyền riêng tư:** xem [chính sách quyền riêng tư](../PRIVACY.md).

## Lời cảm ơn

Những người đã thử nghiệm PhoneBox trên phần cứng của chính họ:

- [anandyakshit](https://github.com/anandyakshit): hỗ trợ Android 7, trên một chiếc điện thoại Android 7.1.1 ([#73](https://github.com/ersinkoc/phonebox/issues/73)).

## Nhà tài trợ

<!-- sponsor: WrongStack -->
<p align="center">
  <a href="https://wrongstack.com">
    <img src="sponsors/wrongstack.svg" width="900" alt="Được tài trợ bởi WrongStack — lập trình bằng AI mã nguồn mở với bộ nhớ bền vững và quy trình đa agent. Khám phá tại wrongstack.com.">
  </a>
</p>
<p align="center">
  <strong>Được hỗ trợ bởi <a href="https://wrongstack.com">WrongStack</a></strong> ·
  Một agent lập trình bằng AI mã nguồn mở có khả năng ghi nhớ codebase của bạn.<br>
  <a href="https://wrongstack.com"><strong>Khám phá WrongStack →</strong></a> ·
  <a href="https://github.com/wrongstack/wrongstack"><strong>Xem trên GitHub &amp; tặng một ngôi sao ★</strong></a>
</p>
<!-- /sponsor: WrongStack -->

## Lịch sử ngôi sao

<a href="https://www.star-history.com/#ersinkoc/phonebox&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date&theme=dark">
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
    <img alt="Lịch sử ngôi sao của ersinkoc/phonebox" src="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
  </picture>
</a>

## Giấy phép

[MIT](../LICENSE)
