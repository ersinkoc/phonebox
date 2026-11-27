<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox icon"></p>

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
  <b>Română</b> ·
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

<p align="center"><b>Transformă un telefon Android vechi într-un panou de birou pentru computerul tău.</b><br>
Statistici PC, sesiuni de programare cu AI, muzică și comenzi tactile prin USB sau Wi-Fi.<br>
Windows, Linux și macOS. Fără root, fără cont PhoneBox și fără serviciu cloud.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Încearcă demo-ul interactiv</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Descarcă</b></a> ·
  <a href="#pornire-rapidă"><b>Pornire rapidă</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Build status"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT license"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox on three phones showing automation, home and planner pages"><br>
<sub>Exemple de layout-uri cu date demo.</sub></p>

PhoneBox combină o **aplicație Android** pe tot ecranul cu un mic **agent desktop**. Îți construiești panoul într-un
editor vizual din browser: **137 de widgeturi**, **30 de șabloane**, mai multe pagini și previzualizare în timp real. Conectează mai multe
telefoane, fiecare cu propriul layout, sau comută un telefon între mai multe computere asociate.

## Ce poți face

| Folosește-l pentru    | Ce apare pe telefon                                                                                                                                                                              |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Monitorizare PC**   | CPU, GPU, RAM, discuri, rețea și ping; grafice, detalii GPU și controlul proceselor                                                                                                              |
| **Programare cu AI**  | Cotele Claude, Codex și Antigravity, momentele de resetare și prognoze ale ritmului de consum; sesiuni locale Claude Code, Codex și OpenCode (native și în WSL); recenzii de cod GitHub și starea CI |
| **Comenzi tactile**   | Scurtături pentru aplicații și URL-uri, combinații de taste și macrocomenzi; audio pe aplicații, microfon, media, OBS și comenzi de sistem prin [Control Deck](CONTROL-DECK.md)            |
| **Utilizare zilnică** | Se redă acum, radio pe internet, ceasuri, vreme, calendare, notițe, liste de verificare, pomodoro și cronometre                                                                                   |
| **Automatizare**      | Valori și notificări prin webhook, comenzi Home Assistant și reguli care reacționează la activitatea de pe computer                                                                              |

Atinge o sesiune AI pentru detalii despre model, durată și tokeni. Activează, dacă vrei, solicitările de permisiune din Codex,
Claude Code sau OpenCode, ca să răspunzi cu **Allow**, **Deny** (și **Always allow** pentru OpenCode) direct de pe telefon.
[Integrarea opțională CodexBar](GUIDE.md#widgets) adaugă furnizori de cote și o privire de ansamblu asupra costurilor.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox tour showing notifications, a Claude Code permission prompt and automatic page switching"></a><br>
<sub>Un tur cu date demo: notificări, solicitări de permisiune și comutare automată a paginilor. <a href="tour-0.6.mp4">Vezi MP4-ul</a></sub></p>

## Pornire rapidă

Ai nevoie de un **telefon cu Android 5+** (recomandat Android 6 sau mai nou) și de un computer compatibil. Conexiunea USB necesită un
cablu de date și depanarea USB; asocierea prin Wi-Fi funcționează fără ele. Agenții din versiunile publicate includ .NET, deci nu trebuie
să instalezi mediul de rulare.

### 1. Instalează agentul desktop

**Windows 10/11 x64 sau Windows 11 ARM64** — lipește într-o fereastră PowerShell obișnuită:

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux sau macOS** — rulează cu propriul utilizator:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Instalarea descarcă ultima versiune, îi verifică suma de control SHA-256, oferă dependențele lipsă și pornirea la autentificare,
apoi deschide setările la <http://127.0.0.1:8765>. Poate instala și aplicația Android prin USB.
Rulează din nou instalarea pentru a actualiza sau repara instalarea; setările și asocierile se păstrează.

> **Semnare pe Windows:** agenții publicați sunt încă nesemnați: SignPath Foundation a acceptat PhoneBox și semnarea de test funcționează, iar versiunile vor fi semnate după emiterea certificatului de lansare.
> SmartScreen sau antivirusul te pot avertiza sau îi pot bloca. Vezi [politica de semnare a codului](#politica-de-semnare-a-codului)
> și [avertismentele de instalare](SETUP.md#quick-start). Autorizează depanarea USB doar pe computere în care ai încredere.

Citește scripturile înainte să le rulezi: [Windows](../setup.ps1) · [Linux/macOS](../setup.sh).
[Ghidul de instalare](SETUP.md) acoperă opțiunile, dependențele, căile de instalare, depanarea și dezinstalarea.

### 2. Conectează telefonul

| USB                                                                                                                                                                                                                                | Doar Wi-Fi                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Activează **Opțiuni pentru dezvoltatori → Depanare prin USB** (Developer options → USB debugging), conectează un cablu de date și acceptă solicitarea de autorizare de pe telefon. Lasă instalarea să pună aplicația sau folosește **Settings → General → Install the phone app**. | Instalează APK-ul din [ultima versiune](https://github.com/ersinkoc/phonebox/releases/latest). Wi-Fi este dezactivat implicit: deschide pagina de setări (pictograma din bara de sistem sau `http://127.0.0.1:8765`), activează **General → Wi-Fi & Tailscale → Let the phone connect without the USB cable**, apasă **Save**, apoi **Restart to apply** din partea de sus. Alege **Pair a phone over Wi-Fi**, scanează codul QR de asociere și confirmă codul de verificare identic. |
| Trezește telefonul și deschide panoul la conectare; permite actualizarea aplicației prin adb.                                                                                                                                     | Nu necesită adb sau opțiuni pentru dezvoltatori; actualizările aplicației se instalează manual, dacă nu configurezi ulterior USB.                                                                                                                                                                                                                                                                                                                                                         |

Aplicația pentru telefon nu se află în niciun magazin de aplicații; este disponibilă doar în versiunile publicate din acest depozit. Rulează pe
Android 5.0 și mai nou. O aplicație însoțitoare experimentală pentru iOS (SwiftUI, compilată din sursă cu Xcode) este documentată în
[ios/PhoneBox](../ios/PhoneBox/README.md); nu are versiuni publicate și nu implementează funcțiile de panou disponibile doar pe Android.

Pentru trecerea automată între USB și Wi-Fi, activează Wi-Fi și conectează telefonul o dată prin USB.
[Detalii despre conexiune și configurarea Tailscale →](SETUP.md#wi-fi--tailscale)

### 3. Personalizează-l

Deschide <http://127.0.0.1:8765> (sau dă dublu clic pe pictograma din bara de sistem Windows). În **Pages**, alege un șablon, trage și
redimensionează widgeturile, apoi apasă **Save**. Folosește **Live preview** ca să vezi modificările nesalvate cu date în timp real.
Glisează, atinge punctele de pagină sau folosește tastele de volum ale telefonului ca să treci de la o pagină la alta.

<details>
<summary><b>Alte metode de instalare</b></summary>

- **Descărcare manuală:** dezarhivează agentul pentru sistemul tău de pe [pagina versiunilor](https://github.com/ersinkoc/phonebox/releases/latest)
  și rulează-l. Instalează APK-ul pe telefon. Sumele de control și proveniența build-ului însoțesc fișierele versiunii.
- **Scoop (Windows):**

  ```powershell
  scoop bucket add phonebox https://github.com/ersinkoc/phonebox
  scoop install main/adb phonebox/phonebox
  ```

- **Din sursă:** vezi [ghidul de instalare](SETUP.md#other-ways-to-install) și [ghidul de dezvoltare](DEVELOPMENT.md).
- **winget:** manifestele sunt pregătite în [packaging/winget](../packaging/winget); vezi ghidul de instalare pentru detalii.

</details>

## Pagini și șabloane

Pornește de la un șablon Home, System, AI quotas, Sessions, Music, Planner, Automation sau Control Deck, apoi adaptează
grila de 12 × 6 la telefonul tău. Sunt acceptate layout-uri portret și peisaj. Smart Stack pune până la 12 widgeturi într-un singur loc.

| Acasă                                                               | Sesiuni AI                                                                              |
| ------------------------------------------------------------------- | --------------------------------------------------------------------------------------- |
| ![Clock, weather, media and PC metrics](screenshots/home.png)  | ![Coding sessions and GitHub activity](screenshots/work.png)                       |
| **Sistem**                                                          | **Editor de layout**                                                                    |
| ![CPU, GPU, memory and network charts](screenshots/system.png) | ![Pages and widget settings in the visual editor](screenshots/settings-editor.png) |

Capturile de ecran folosesc date demo. [Răsfoiește galeria de șabloane și widgeturile](GUIDE.md#pages--templates).
Schimbă fonturile, dimensiunea, spațierea, colțurile și suprafețele global sau pentru o singură pagină ori un singur widget cu [ghidul de aspect](APPEARANCE.md).

## Platforme acceptate

|                                   | Windows                                                                                                                                | Linux                         | macOS                                                        |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------- | ------------------------------------------------------------ |
| Build-uri publicate               | x64                                                                                                                                    | x64 / ARM64, glibc            | Apple silicon / Intel                                        |
| CPU, RAM, discuri, rețea, procese | ✓                                                                                                                                      | ✓                             | ✓                                                            |
| Date GPU                          | Contoare Windows; NVIDIA prin `nvidia-smi`; senzori suplimentari prin [LibreHardwareMonitor](SETUP.md#librehardwaremonitor-on-windows) | NVIDIA / AMD; frecvență Intel | Apple silicon, când macOS le expune                          |
| Media                             | Sesiunile media ale sistemului                                                                                                         | `playerctl`                   | `nowplaying-cli`; rezervă Spotify / Music                    |
| Volum                             | Nativ                                                                                                                                  | `wpctl` / `pactl`             | Ieșirea sistemului; disponibilitatea variază după dispozitiv |
| Interfața agentului               | Pictogramă în bara de sistem + setări în browser                                                                                       | Setări în browser             | Setări în browser                                            |

Widgeturile depind de hardware-ul gazdei, de instrumentele instalate și de permisiuni. Funcțiile avansate Control Deck au
[propriile cerințe de platformă](CONTROL-DECK.md). Windows pe ARM necesită Windows 11.
Linux cu musl (de exemplu Alpine) nu are build publicat. Vezi [tabelul complet de compatibilitate](SETUP.md#what-works-where).

## Documentație

| Ghid                                          | Conținut                                                                                                                         |
| --------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| [Instalare și conexiuni](SETUP.md)       | Opțiuni de instalare, avertismente, instalare manuală și din sursă, USB, Wi-Fi, Tailscale, actualizări, dezinstalare și depanare |
| [Ghidul utilizatorului](GUIDE.md)        | Galeria de șabloane, widgeturi, integrări AI, editor, solicitări Claude Code, webhookuri și reguli de automatizare               |
| [Aspect](APPEARANCE.md)                  | Fonturi, suprafețe, presetări, moștenire și previzualizare în timp real                                                                  |
| [Control Deck](CONTROL-DECK.md)          | Audio, media, OBS, scurtături, sarcini, protocoale, dependențe și limitări de platformă                                          |
| [Widgetul Discord](DISCORD.md)           | Crearea aplicației Discord, conectarea ei, comenzi și depanare                                                                   |
| [Dezvoltare](DEVELOPMENT.md)             | Arhitectură, demo local, build-uri, teste, sursele widgeturilor și rulări izolate ale agentului                                  |
| [Contribuții](../CONTRIBUTING.md)                | Domeniul contribuțiilor, așteptări privind recenzia codului, confidențialitate și verificări obligatorii                                        |
| [Jurnal de modificări](../CHANGELOG.md)          | Istoricul versiunilor și modificările nepublicate                                                                                |
| [Lista de verificare pentru lansare](RELEASING.md)         | Fluxul de build, semnare și publicare pentru cei care întrețin proiectul                                                                          |

[Suplimentele pentru UMIDIGI BISON](../extras/umidigi-bison) conțin optimizarea reversibilă, fără root, folosită pe telefonul inițial de dezvoltare.

## Extensii din comunitate

Extensiile sunt scrise și întreținute de autorii lor. PhoneBox nu le verifică și nu le distribuie. Ele rulează cu propriile tale
drepturi pe computer și pot modifica setările PhoneBox prin `PUT /api/settings`, așa că citește ce face o extensie
înainte să o instalezi.

| Extensie                                                                                       | Ce face                                                                                                                                           | Ce trimite în afara PC-ului                                                                                                                                                                                                                                                                                                                                                                       |
| ---------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [PhoneBox Games Tab](https://github.com/lucasaz/phonebox-games-tab) (Windows)                  | O pagină Games cu FPS în timp real, coperți și timp de joc, prin API-ul push și un widget Custom HTML                                                     | Numele pachetelor aplicațiilor Store / Xbox, împreună cu limba de afișare și regiunea Windows, către catalogul Microsoft Store (`displaycatalog.mp.microsoft.com`); ID-urile aplicațiilor jocurilor Steam către magazinul Steam (`store.steampowered.com`), pentru adresa copertei; telefonul încarcă imaginile copertelor de pe serverele de imagini Microsoft și Steam, al căror URL conține ID-ul aplicației Steam a jocului |
| [PhoneBox for GNOME](https://github.com/Gabrielmororo180/phonebox-gnome) (Linux, GNOME)        | CPU și cota sesiunii Claude în bara de sus GNOME; un clic deschide panoul, cu file de pagini, chiar sub ea. Funcționează și ca widget de desktop  | Nimic propriu; panoul pe care îl deschide face aceleași cereri ca pe telefon, de exemplu vremea de la `api.open-meteo.com` (vezi [PRIVACY.md](../PRIVACY.md))                                                                                                                                                                                                                                        |
| [Phonebox Keyboard Addon](https://github.com/Ultraalmir/Keyboard-Addon-for-PhoneBox) (Windows) | Scurtături globale de tastatură pentru navigarea directă între pagini, plus comenzi Următoarea / Anterioara și un panou de setări în bara de sistem | Nimic; apelează doar agentul local pe `127.0.0.1`                                                                                                                                                                                                                                                                                                                                                 |

## Securitate și confidențialitate

Serverul de setări de pe desktop ascultă pe `127.0.0.1:8765`. Prin USB, telefonul ajunge la agent prin `adb reverse` pe un port
loopback separat, peste TLS fixat (pinned) la fel ca la Wi-Fi, cu o cheie pe care o generează aplicația de pe telefon și pe care agentul o citește prin adb.
Wi-Fi-ul opțional folosește HTTPS pe portul 8766, cu un certificat fixat și o cheie asociată. Punctele de acces (endpoints) de setări și de asociere
rămân locale. Telefonul cere acțiunile configurate după ID; comenzile și credențialele integrărilor rămân pe computer.

PhoneBox nu are telemetrie, analiză sau conturi proprii. Widgeturile și integrările opționale contactează servicii externe
pentru vreme, cote, GitHub și alte date, iar agentul verifică GitHub pentru actualizări.
Fluxurile publice acceptate pot trece prin agent, prin USB, când telefonul nu are internet.
[Politica de confidențialitate](../PRIVACY.md) enumeră conexiunile de ieșire și modul în care le poți dezactiva.
Citește [detaliile de securitate](GUIDE.md#security--privacy). Pentru a raporta o vulnerabilitate, urmează [SECURITY.md](../SECURITY.md).

## Dezvoltare

Agentul folosește **.NET 10**; aplicația Android folosește **Kotlin și un WebView** cu widgeturi în JavaScript simplu.
Aplicația iOS opțională este SwiftUI + `WKWebView` și refolosește resursele panoului Android; vezi
[ios/PhoneBox/README.md](../ios/PhoneBox/README.md).
Pentru build-uri Android, instalează **JDK 17** și **Android SDK Platform 35** (SDK-ul cu care este compilată aplicația; aplicația
în sine rulează pe Android 5.0, API 21, și mai nou).
Previzualizează panoul și editorul din sursă cu Python 3, folosind date simulate:

```sh
python tools/demo-server.py
```

Deschide <http://127.0.0.1:8790/panel/> sau <http://127.0.0.1:8790/settings/>.
Vezi [comenzile de dezvoltare](DEVELOPMENT.md#development) pentru rularea agentului, compilarea APK-ului și testarea modificărilor.
Contribuțiile sunt binevenite; începe cu [CONTRIBUTING.md](../CONTRIBUTING.md).

## Politica de semnare a codului

Semnare gratuită a codului oferită de [SignPath.io](https://about.signpath.io/), certificat oferit de
[SignPath Foundation](https://signpath.org/).

- **Agentul Windows:** SignPath Foundation a acceptat proiectul și un build semnat de test a trecut verificarea; versiunile rămân nesemnate
  până la emiterea certificatului de lansare al Fundației.
  Ținta de semnare prevăzută este `PhoneBoxAgent.exe` din arhivele zip `win-x64` și `win-arm64`, compilate de
  [fluxul de lansare](../.github/workflows/release.yml). Fiecare cerere de semnare necesită aprobare manuală.
  Notificările pentru mediul de rulare și bibliotecile incluse se află în [THIRD-PARTY-NOTICES.txt](../agent/PhoneBox.Agent/THIRD-PARTY-NOTICES.txt).
- **Aplicația Android:** APK-urile publicate folosesc cheia de semnare proprie a proiectului, independent de SignPath.
- **Roluri în echipă:** committer, reviewer și aprobator de semnare: [ersinkoc](https://github.com/ersinkoc).
  Pull request-urile altor contribuitori sunt verificate înainte de îmbinare.
- **Confidențialitate:** vezi [politica de confidențialitate](../PRIVACY.md).

## Mulțumiri

Persoane care au testat PhoneBox pe propriul hardware:

- [anandyakshit](https://github.com/anandyakshit): suport pentru Android 7, pe un telefon cu Android 7.1.1 ([#73](https://github.com/ersinkoc/phonebox/issues/73)).

## Sponsor

<!-- sponsor: WrongStack -->
<p align="center">
  <a href="https://wrongstack.com">
    <img src="sponsors/wrongstack.svg" width="900" alt="Sponsored by WrongStack — open-source AI coding with durable memory and multi-agent workflows. Explore wrongstack.com.">
  </a>
</p>
<p align="center">
  <strong>Susținut de <a href="https://wrongstack.com">WrongStack</a></strong> ·
  Un agent de programare AI open-source care îți ține minte codul.<br>
  <a href="https://wrongstack.com"><strong>Descoperă WrongStack →</strong></a> ·
  <a href="https://github.com/wrongstack/wrongstack"><strong>Vezi pe GitHub și dă-i o stea ★</strong></a>
</p>
<!-- /sponsor: WrongStack -->

## Istoricul stelelor

<a href="https://www.star-history.com/#ersinkoc/phonebox&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date&theme=dark">
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
    <img alt="Star history of ersinkoc/phonebox" src="https://api.star-history.com/svg?repos=ersinkoc/phonebox&type=Date">
  </picture>
</a>

## Licență

[MIT](../LICENSE)
