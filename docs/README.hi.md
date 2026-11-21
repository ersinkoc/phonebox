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
  <a href="README.uk.md">Українська</a> ·
  <b>हिन्दी</b> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>पुराने Android फ़ोन को अपने कंप्यूटर के लिए डेस्क पैनल बनाएँ।</b><br>
USB या Wi-Fi से जुड़कर PC की स्थिति, AI कोडिंग सेशन और संगीत देखें, और टच से नियंत्रण करें।</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>लाइव डेमो का प्रयास करें</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>डाउनलोड</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox तीन Android फोनों पर स्वचालन, घर और प्लानर पृष्ठ दिखा रहा है"></p>

## आप क्या कर सकते हैं

- CPU, GPU, RAM, डिस्क, नेटवर्क और चल रही प्रक्रियाओं पर नज़र रखें।
- Claude और Codex के सेशन, AI उपयोग की सीमा, GitHub समीक्षाएँ और CI की स्थिति देखें।
- Control Deck से शॉर्टकट और मैक्रो चलाएँ, आवाज़ बदलें और OBS को नियंत्रित करें।
- घड़ी, मौसम, पंचांग, नोट्स, मीडिया तथा Home Assistant नियंत्रण दिखाएँ.
- विज़ुअल एडिटर में पेज और विजेट बनाएँ और सहेजने से पहले प्रीव्यू देखें।

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox दौरा सूचनाएँ, अनुमति प्रोम्प्ट तथा स्वचालित पृष्ठ स्विचिंग दिखाता है"></a><br>
<sub>डेमो डाटा: अधिसूचनाएं, अनुमति प्रोम्प्ट और स्वचालित पृष्ठ स्विचिंग। <a href="tour-0.6.mp4">MP4 देखें</a></sub></p>

## त्वरित प्रारंभ

एक प्रयोग करें Android 5.0या बाद में phone (Android 6 या बाद में अनुशंसित) और एक समर्थित कंप्यूटर. एजेंट में .NET रनटाइम शामिल है.

### 1. डेस्कटॉप एजेंट संस्थापित करें

Windows 10/11 x64 पर यह कमांड PowerShell में चलाएँ।

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

Linux या macOS पर यह कमांड अपने सामान्य उपयोगकर्ता खाते से चलाएँ।

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

सेटअप रिलीज़ का चेकसम जाँचता है और सेटिंग पेज खोलता है। यह फ़ोन ऐप को USB से इंस्टॉल भी कर सकता है। सेटिंग और पेयरिंग बनाए रखते हुए अपडेट या मरम्मत के लिए इसे फिर चलाएँ।

### 2. फोन को जोड़ें

Windows ट्रे से विन्यास पृष्ठ खोलें या नीचे दिए गए पता पर जाएँ. <http://127.0.0.1:8765>

- USB से जोड़ने के लिए फ़ोन पर USB डीबगिंग चालू करें, डेटा केबल लगाएँ और कंप्यूटर को अनुमति दें। फ़ोन ऐप सेटअप से या सेटिंग एडिटर के इंस्टॉल बटन से इंस्टॉल करें।
- Wi-Fi से जोड़ने के लिए नई रिलीज़ का APK इंस्टॉल करें। एजेंट की सेटिंग में Wi-Fi चालू करें, सहेजें और एजेंट रीस्टार्ट करें। Wi-Fi पेयरिंग चुनें, QR कोड स्कैन करें और जाँचें कि दोनों डिवाइस पर एक ही पुष्टि कोड दिख रहा है।

## गोपनीयता तथा सुरक्षा

PhoneBox फ़ोन और कंप्यूटर को सीधे जोड़ता है। इसके लिए खाते की ज़रूरत नहीं है और यह टेलीमेट्री नहीं भेजता। कुछ वैकल्पिक विजेट आपकी कॉन्फ़िगर की हुई सेवाओं से जुड़ते हैं।

## दस्तावेज़ीकरण

- [संस्थापना, हस्ताक्षर तथा समस्या निवारण](SETUP.md)
- [उपयोक्ता मार्गदर्शक तथा विजेट गैलरी](GUIDE.md)
- [प्रसंग, फ़ॉन्ट तथा पूर्वावलोकन](APPEARANCE.md)
- [Control Deck मार्गदर्शक](CONTROL-DECK.md)
- [विकास तथा परीक्षण](DEVELOPMENT.md)
- [गोपनीयता नीति](../PRIVACY.md)
- [चेंज लॉग](../CHANGELOG.md)

## लाइसेंस

[PhoneBox MIT लाइसेंस के तहत जारी किया गया है।](../LICENSE)
