// PhoneBox interface translations for the "night" rule action. See docs/DEVELOPMENT.md for the locale checks.
// Merged deeply into the other packs: a plain top-level assign would replace whole language entries, and this file
// loads last among the locale-*.js files (check-release.py parses the literal below).
function pbNightMerge(prev, extra) {
  const next = { ...prev };
  for (const [lang, pack] of Object.entries(extra)) {
    const old = next[lang] || {};
    next[lang] = {
      ...old,
      ...(pack.strings && { strings: { ...old.strings, ...pack.strings } }),
      ...(pack.catalog && { catalog: { ...old.catalog, ...pack.catalog } }),
      ...(pack.panel && { panel: { ...old.panel, ...pack.panel } }),
    };
  }
  return next;
}
window.PhoneBoxLocaleData = Object.assign(pbNightMerge(window.PhoneBoxLocaleData || {}, {
  "en": {
    "strings": {
      "s.r.onlyWhileNight": "Only while it lasts (then the night screen turns off)"
    }
  },
  "tr": {
    "strings": {
      "s.r.onlyWhileNight": "Sadece süresi dolana kadar (sonra gece ekranı kapanır)"
    },
    "catalog": {
      "show the night screen": "gece ekranını göster",
      "🌑 Away → night screen": "🌑 Uzakta → gece ekranı"
    }
  },
  "de": {
    "strings": {
      "s.r.onlyWhileNight": "Nur solange es dauert (dann geht der Nachtbildschirm aus)"
    },
    "catalog": {
      "show the night screen": "Den Nachtbildschirm anzeigen",
      "🌑 Away → night screen": "🌑 Abwesend → Nachtbildschirm"
    }
  },
  "fr": {
    "strings": {
      "s.r.onlyWhileNight": "Seulement pendant qu'il dure (puis l'écran de nuit se coupe)"
    },
    "catalog": {
      "show the night screen": "Afficher l'écran de nuit",
      "🌑 Away → night screen": "🌑 Absent → écran de nuit"
    }
  },
  "es": {
    "strings": {
      "s.r.onlyWhileNight": "Solo mientras dure (después se apaga la pantalla nocturna)"
    },
    "catalog": {
      "show the night screen": "Mostrar la pantalla nocturna",
      "🌑 Away → night screen": "🌑 Ausente → pantalla nocturna"
    }
  },
  "it": {
    "strings": {
      "s.r.onlyWhileNight": "Solo finché dura (poi lo schermo notturno si spegne)"
    },
    "catalog": {
      "show the night screen": "Mostra la schermata notturna",
      "🌑 Away → night screen": "🌑 Assente → schermata notturna"
    }
  },
  "pt": {
    "strings": {
      "s.r.onlyWhileNight": "Apenas enquanto dura (depois a tela noturna desliga)"
    },
    "catalog": {
      "show the night screen": "Mostrar a tela noturna",
      "🌑 Away → night screen": "🌑 Ausente → tela noturna"
    }
  },
  "pt-BR": {
    "strings": {
      "s.r.onlyWhileNight": "Apenas enquanto durar (depois a tela noturna desliga)"
    },
    "catalog": {
      "show the night screen": "Mostrar a tela noturna",
      "🌑 Away → night screen": "🌑 Ausente → tela noturna"
    }
  },
  "nl": {
    "strings": {
      "s.r.onlyWhileNight": "Alleen zolang het duurt (dan gaat het nachtscherm uit)"
    },
    "catalog": {
      "show the night screen": "Het nachtscherm tonen",
      "🌑 Away → night screen": "🌑 Afwezig → nachtscherm"
    }
  },
  "pl": {
    "strings": {
      "s.r.onlyWhileNight": "Tylko dopóki trwa (wtedy wyłącza się ekran nocny)"
    },
    "catalog": {
      "show the night screen": "Pokaż ekran nocny",
      "🌑 Away → night screen": "🌑 Nieobecny → ekran nocny"
    }
  },
  "ru": {
    "strings": {
      "s.r.onlyWhileNight": "Только пока оно длится (затем ночной экран отключается)"
    },
    "catalog": {
      "show the night screen": "показать ночной экран",
      "🌑 Away → night screen": "🌑 Отошел → ночной экран"
    }
  },
  "el": {
    "strings": {
      "s.r.onlyWhileNight": "Μόνο όσο διαρκεί (μετά σβήνει η νυχτερινή οθόνη)"
    },
    "catalog": {
      "show the night screen": "εμφάνιση της νυχτερινής οθόνης",
      "🌑 Away → night screen": "🌑 Μακριά → νυχτερινή οθόνη"
    }
  },
  "vi": {
    "strings": {
      "s.r.onlyWhileNight": "Chỉ trong lúc điều kiện đúng (rồi tắt màn hình đêm)"
    },
    "catalog": {
      "show the night screen": "hiện màn hình đêm",
      "🌑 Away → night screen": "🌑 Vắng mặt → màn hình đêm"
    }
  },
  "zh": {
    "strings": {
      "s.r.onlyWhileNight": "只在它持续的时间内(然后夜屏关闭)"
    },
    "catalog": {
      "show the night screen": "显示夜间屏幕",
      "🌑 Away → night screen": "🌑 离开 → 夜间屏幕"
    }
  },
  "ja": {
    "strings": {
      "s.r.onlyWhileNight": "持続する間のみ (その後夜間画面はオフに)"
    },
    "catalog": {
      "show the night screen": "夜間画面を表示",
      "🌑 Away → night screen": "🌑 不在 → 夜間画面"
    }
  },
  "ko": {
    "strings": {
      "s.r.onlyWhileNight": "지속되는 동안만 (그 다음 나이트 화면이 꺼짐)"
    },
    "catalog": {
      "show the night screen": "나이트 화면 보기",
      "🌑 Away → night screen": "🌑 자리를 비움 → 나이트 화면"
    }
  },
  "id": {
    "strings": {
      "s.r.onlyWhileNight": "Hanya selama itu bertahan (lalu layar malam mati)"
    },
    "catalog": {
      "show the night screen": "Tampilkan layar malam",
      "🌑 Away → night screen": "🌑 Tidak ada → layar malam"
    }
  },
  "th": {
    "strings": {
      "s.r.onlyWhileNight": "ใช้ได้เฉพาะในระยะเวลาที่มันอยู่ (แล้วปิดหน้าจอนอน)"
    },
    "catalog": {
      "show the night screen": "แสดงหน้าจอนอน",
      "🌑 Away → night screen": "🌑 ไม่อยู่ → หน้าจอนอน"
    }
  },
  "uk": {
    "strings": {
      "s.r.onlyWhileNight": "Тільки доки триває (потім вимикається нічний екран)"
    },
    "catalog": {
      "show the night screen": "показати нічний екран",
      "🌑 Away → night screen": "🌑 Відійшов → нічний екран"
    }
  },
  "hi": {
    "strings": {
      "s.r.onlyWhileNight": "सिर्फ जब तक यह चलता है (तब रात्रि स्क्रीन बंद हो जाती है)"
    },
    "catalog": {
      "show the night screen": "रात्रि स्क्रीन दिखाएँ",
      "🌑 Away → night screen": "🌑 दूर → रात्रि स्क्रीन"
    }
  },
  "cs": {
    "strings": {
      "s.r.onlyWhileNight": "Jen dokud trvá (pak se vypne noční obrazovka)"
    },
    "catalog": {
      "show the night screen": "Zobrazit noční obrazovku",
      "🌑 Away → night screen": "🌑 Nejsu doma → noční obrazovka"
    }
  },
  "ro": {
    "strings": {
      "s.r.onlyWhileNight": "Doar cât durează (apoi ecranul de noapte se oprește)"
    },
    "catalog": {
      "show the night screen": "arată ecranul de noapte",
      "🌑 Away → night screen": "🌑 Plecat → ecran de noapte"
    }
  },
  "ar": {
    "strings": {
      "s.r.onlyWhileNight": "طوال تحقق الشرط فقط، ثم يُطفأ الشاشة الليلية"
    },
    "catalog": {
      "show the night screen": "أظهر الشاشة الليلية",
      "🌑 Away → night screen": "🌑 بعيداً → الشاشة الليلية"
    }
  },
  "zh-Hant": {
    "strings": {
      "s.r.onlyWhileNight": "只要它持續 (然後夜間螢幕關閉)"
    },
    "catalog": {
      "show the night screen": "顯示夜間螢幕",
      "🌑 Away → night screen": "🌑 離開 → 夜間螢幕"
    }
  }
}));
