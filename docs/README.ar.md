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
  <b>العربية</b> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center" dir="rtl"><b>حوّل هاتف Android قديمًا إلى لوحة تحكّم مكتبية لحاسوبك.</b><br>
راقب حالة الحاسوب وجلسات البرمجة بالذكاء الاصطناعي، وتحكّم بالموسيقى باللمس عبر USB أو Wi-Fi.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>جرب العرض الحي</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>تنزيل</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="ثلاثة هواتف Android تعرض صفحات الأتمتة والصفحة الرئيسية والمخطّط في PhoneBox"></p>

## ما يمكنك فعله

- راقب المعالج وبطاقة الرسومات والذاكرة والأقراص والشبكة والعمليات الجارية.
- تابع جلسات Claude وCodex وحدود استخدام الذكاء الاصطناعي ومراجعات GitHub وحالة التكامل المستمر.
- شغّل الاختصارات ووحدات الماكرو، واضبط الصوت وتحكّم في OBS عبر Control Deck.
- اعرض الساعات والطقس والتقويمات والملاحظات والوسائط وأدوات تحكّم Home Assistant.
- أنشئ صفحاتك وأدواتك في المحرّر المرئي وعاين التغييرات قبل حفظها.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="جولة في PhoneBox تعرض الإشعارات وطلبات الإذن والتبديل التلقائي بين الصفحات"></a><br>
<sub>جولة ببيانات تجريبية تعرض الإشعارات وطلبات الإذن والتبديل التلقائي بين الصفحات. <a href="tour-0.6.mp4">شاهد فيديو MP4</a></sub></p>

## البداية السريعة

تحتاج إلى هاتف يعمل بنظام Android 5.0 أو أحدث (يُفضّل Android 6 أو أحدث) وحاسوب مدعوم. يتضمّن الوكيل بيئة تشغيل ‎.NET.

### 1. تثبيت وكيل الحاسوب

في Windows 10 أو 11 بمعمارية x64، نفّذ الأمر التالي في PowerShell.

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

في Linux أو macOS، نفّذ الأمر التالي من حسابك العادي.

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

يتحقّق برنامج الإعداد من المجموع الاختباري للإصدار ويفتح الإعدادات. يمكنه أيضًا تثبيت تطبيق الهاتف عبر USB. شغّله مجددًا للتحديث أو الإصلاح مع الاحتفاظ بالإعدادات والأجهزة المقترنة.

### 2. توصيل الهاتف

افتح صفحة الإعدادات من منطقة الإعلام في Windows أو انتقل إلى العنوان التالي. <http://127.0.0.1:8765>

- عبر USB، فعّل تصحيح أخطاء USB على الهاتف ووصّل كابل بيانات واسمح للحاسوب بالاتصال. ثبّت التطبيق من برنامج الإعداد أو من زر تثبيت تطبيق الهاتف في محرّر الإعدادات.
- عبر Wi-Fi، ثبّت ملف APK من أحدث إصدار. فعّل Wi-Fi في إعدادات الوكيل، ثم احفظ الإعدادات وأعد تشغيله. اختر إقران هاتف عبر Wi-Fi، وامسح رمز QR وتحقّق من تطابق رمز التحقّق على الجهازين.

## الخصوصية والأمن

يوصل PhoneBox الهاتف بالحاسوب مباشرة. لا يتطلّب حسابًا ولا يرسل بيانات قياس الاستخدام. تتّصل بعض الأدوات الاختيارية بالخدمات التي تضبطها.

## الوثائق

- [التركيب والتوقيع وحل المشاكل](SETUP.md)
- [دليل المستعمل ومعرض العناصر](GUIDE.md)
- [المواضيع والخطوط والتبصير](APPEARANCE.md)
- [Control Deck دليل](CONTROL-DECK.md)
- [التطوير والاختبار](DEVELOPMENT.md)
- [سياسة الخصوصية](../PRIVACY.md)
- [سجل التغييرات](../CHANGELOG.md)

## الترخيص

[يُوزّع PhoneBox بموجب ترخيص MIT.](../LICENSE)
