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
  <b>한국어</b> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.th.md">ไทย</a> ·
  <a href="README.uk.md">Українська</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>쓰지 않는 Android 휴대전화를 컴퓨터용 데스크 패널로 바꿔 보세요.</b><br>
USB 또는 Wi-Fi로 연결해 PC 상태, AI 코딩 세션, 음악을 확인하고 터치로 제어할 수 있습니다.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>라이브 데모를 시도해 보세요</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>다운로드</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox 3개의 Android 전화기에서 자동화, 홈 및 플래너 페이지를 보여주는 모습"></p>

## 할 수 있는 일

- CPU, GPU, 메모리, 디스크, 네트워크 및 실행 중인 프로세스를 모니터링합니다.
- Claude 및 Codex 세션, AI 사용 한도, GitHub 리뷰, CI 상태를 확인합니다.
- Control Deck에서 바로가기와 매크로를 실행하고 오디오와 OBS를 제어합니다.
- 시계, 날씨, 일정, 노트, 미디어 및 Home Assistant 컨트롤을 보여줍니다.
- 시각적 편집기에서 페이지와 위젯을 배치하고 저장하기 전에 미리 봅니다.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="알림, 권한 프롬프트 및 자동 페이지 전환을 보여주는 PhoneBox 투어"></a><br>
<sub>데모 데이터: 알림, 권한 프롬프트 및 자동 페이지 전환. <a href="tour-0.6.mp4">MP4를 보세요</a></sub></p>

## 빠른 시작

Android 5.0 이상의 전화(Android 6 이상이 권장됨)와 지원되는 컴퓨터를 사용합니다. 에이전트에는 .NET 런타임이 포함됩니다.

### 1. 데스크톱 에이전트 설치

Windows 10/11 x64에서는 PowerShell에서 다음 명령을 실행합니다.

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

Linux 또는 macOS에서는 일반 사용자 계정에서 다음 명령을 실행합니다.

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

설치 스크립트는 릴리스 체크섬을 확인하고 설정 페이지를 엽니다. USB로 휴대전화 앱을 설치할 수도 있습니다. 설정과 페어링을 유지하면서 업데이트하거나 복구하려면 스크립트를 다시 실행하세요.

### 2. 휴대폰 연결

Windows 트레이에서 설정 페이지를 열거나 아래 주소를 방문하십시오. <http://127.0.0.1:8765>

- USB로 연결하려면 휴대전화에서 USB 디버깅을 켜고 데이터 케이블을 연결한 뒤 컴퓨터 연결을 허용하세요. 설치 스크립트나 설정 편집기의 휴대전화 앱 설치 버튼으로 앱을 설치할 수 있습니다.
- Wi-Fi로 연결하려면 최신 릴리스의 APK를 설치하세요. 에이전트 설정에서 Wi-Fi를 켜고 저장한 뒤 다시 시작합니다. Wi-Fi 페어링 메뉴에서 QR 코드를 스캔하고 두 기기의 확인 코드가 같은지 확인하세요.

## 개인정보 보호 및 보안

PhoneBox는 휴대전화와 컴퓨터를 직접 연결합니다. 계정이 필요 없고 원격 측정 데이터를 보내지 않습니다. 일부 선택 위젯은 사용자가 설정한 서비스에 연결합니다.

## 문서화

- [설치, 서명 및 문제 해결](SETUP.md)
- [사용자 가이드 및 위젯 갤러리](GUIDE.md)
- [테마, 서체 및 미리보기](APPEARANCE.md)
- [Control Deck 가이드](CONTROL-DECK.md)
- [개발 및 테스트](DEVELOPMENT.md)
- [개인정보 보호 정책](../PRIVACY.md)
- [변경 사항](../CHANGELOG.md)

## 라이선스

[PhoneBox MIT 라이선스로 배포됩니다.](../LICENSE)
