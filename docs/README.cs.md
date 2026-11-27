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
  <b>Čeština</b> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Proměňte starý telefon s Androidem v ovládací panel pro počítač.</b><br>
Stav počítače, relace programování s AI, hudba a dotykové ovládání přes USB nebo Wi-Fi.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Vyzkoušejte živé demo</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Stáhnout</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox zobrazující stránky automatizace, domova a plánovače na třech telefonech Android"></p>

## Co můžete dělat

- Sledovat využití procesoru, grafické karty a paměti, disky, síť a běžící procesy.
- Sledovat relace Claude a Codex, limity AI, revize na GitHubu a stav CI.
- Spouštět zkratky a makra, ovládat zvuk a OBS pomocí Control Decku.
- Zobrazit hodiny, počasí, kalendáře, poznámky, média a ovládací prvky Home Assistant.
- Vytvářet stránky a widgety ve vizuálním editoru a prohlížet si změny před uložením.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox prohlídka zobrazující oznámení, výzvy k povolení a automatické přepínání stránek"></a><br>
<sub>Demo data: oznámení, výzvy k oprávnění a automatické přepínání stránek. <a href="tour-0.6.mp4">Podívejte se na MP4</a></sub></p>

## Rychlý start

Používejte telefon Android 5.0 nebo novější (doporučeno je Android 6 nebo novější) a podporovaný počítač.Agent obsahuje runtime .NET.

### 1. Instalace desktopového agenta

Ve Windows 10/11 x64 spusťte následující příkaz v PowerShellu.

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

V Linuxu nebo macOS spusťte příkaz pod svým běžným uživatelským účtem.

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Instalační skript ověří kontrolní součet vydání a otevře nastavení. Umí také nainstalovat mobilní aplikaci přes USB. Opětovným spuštěním aktualizujete nebo opravíte instalaci a zachováte nastavení i spárování.

### 2. Připojení telefonu

Otevřete stránku nastavení z panelu Windows nebo navštivte níže uvedenou adresu. <http://127.0.0.1:8765>

- USB. V telefonu zapněte ladění USB, připojte datový kabel a povolte připojení počítače. Aplikaci nainstalujte skriptem nebo příslušným tlačítkem v nastavení.
- Wi-Fi. Nainstalujte APK z nejnovějšího vydání. V nastavení agenta zapněte Wi-Fi, změnu uložte a agenta restartujte. Vyberte párování přes Wi-Fi, naskenujte QR kód a zkontrolujte, že obě zařízení zobrazují stejný potvrzovací kód.

## Ochrana soukromí a bezpečnost

PhoneBox propojuje telefon a počítač přímo. Nevyžaduje účet a neodesílá telemetrii. Některé volitelné widgety se připojují ke službám, které nastavíte.

## Dokumentace

- [Instalace, podepisování a řešení problémů](SETUP.md)
- [Uživatelská příručka a galerie widgetů](GUIDE.md)
- [Motivy, písma a náhled](APPEARANCE.md)
- [Průvodce Control Deck](CONTROL-DECK.md)
- [Vývoj a testy](DEVELOPMENT.md)
- [Ochrana osobních údajů |](../PRIVACY.md)
- [Changelog](../CHANGELOG.md)

## Licence

[PhoneBox je vydán pod licencí MIT.](../LICENSE)
