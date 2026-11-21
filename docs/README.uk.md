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
  <a href="README.th.md">ไทย</a> ·
  <b>Українська</b> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Перетворіть старий телефон Android на настільну панель для комп’ютера.</b><br>
Статистика ПК, сеанси програмування з ШІ, музика й сенсорне керування через USB або Wi-Fi.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Спробувати демонстрацію</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Звантажити</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox показує автоматизацію, домашню сторінку та сторінки планувальника на трьох телефонах Android"></p>

## Що можна робити

- Стежити за процесором, графічним процесором, пам’яттю, дисками, мережею та запущеними процесами.
- Переглядати сеанси Claude та Codex, квоти ШІ, рецензії GitHub і стан CI.
- Запускати комбінації клавіш і макроси, налаштовувати звук та керувати OBS через Control Deck.
- Показувати годинники, погоду, календарі, нотатки, мультимедійні файли та елементи керування Home Assistant.
- Створювати сторінки й віджети у візуальному редакторі та переглядати зміни перед збереженням.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox екскурсія, що показує сповіщення, запит на дозвіл і автоматичне перемикання сторінок"></a><br>
<sub>Демонстраційні дані: сповіщення, запит на дозвіл і автоматичне перемикання сторінок. <a href="tour-0.6.mp4">Переглянути MP4</a></sub></p>

## Швидкий запуск

Використовуйте телефон Android 5. 0 або пізнішої версії (рекомендується Android 6 або пізнішою) і комп’ ютер, який підтримує цю версію. Агент включає середовище виконання . NET.

### 1. Встановити агента стільниці

У Windows 10/11 x64 виконайте цю команду в PowerShell.

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

У Linux або macOS виконайте команду від імені свого користувача.

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

Сценарій встановлення перевіряє контрольну суму випуску та відкриває налаштування. Він також може встановити застосунок на телефон через USB. Запустіть його ще раз для оновлення або відновлення зі збереженням налаштувань і сполучень.

### 2. З' єднати телефон

Відкрийте сторінку параметрів з лотка Windows або відвідайте адресу, наведену нижче. <http://127.0.0.1:8765>

- USB. Увімкніть налагодження USB на телефоні, під’єднайте кабель з передаванням даних і дозвольте підключення комп’ютера. Встановіть застосунок через сценарій встановлення або кнопку в налаштуваннях.
- Wi-Fi. Встановіть APK з останнього випуску. Увімкніть Wi-Fi у налаштуваннях агента, збережіть зміни й перезапустіть агент. Виберіть створення пари через Wi-Fi, відскануйте QR-код і перевірте, чи збігається код підтвердження на обох пристроях.

## Конфіденційність і безпека

PhoneBox підключає телефон до комп’ютера напряму. Обліковий запис не потрібен, телеметрія не надсилається. Деякі необов’язкові віджети підключаються до налаштованих вами сервісів.

## Документація

- [Встановлення, підписування та виправлення помилок](SETUP.md)
- [Підручник користувача і галерея віджетів](GUIDE.md)
- [Теми, шрифти і перегляд](APPEARANCE.md)
- [Посібник по Control Deck](CONTROL-DECK.md)
- [Розробка і тестування](DEVELOPMENT.md)
- [Правила конфіденційності](../PRIVACY.md)
- [Журнал змін](../CHANGELOG.md)

## Ліцензія

[PhoneBox випущений під ліцензією MIT.](../LICENSE)
