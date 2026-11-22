// Localisation gaps the first i18n pass left behind: literals the dictionary already translates, and one spot
// that compared a localized label against the English word. Against the web demo, in German.
const { test, expect } = require('@playwright/test');

async function show(page, widgets, display = { language: 'de' }) {
  await page.goto('/panel/');
  await page.evaluate(({ widgets, display }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display,
      pages: [{ id: 'one', title: 'One', widgets }],
    }));
  }, { widgets, display });
  await page.reload();
}

test('a small agenda shows relative times in the panel language', async ({ page }) => {
  await page.addInitScript(() => {
    // pin the run to local noon so "today" cannot roll over mid-test, and answer /api/calendar
    // with the fixture before the demo installs its own fetch mock (the setter keeps it as fallback)
    const noon = new Date(); noon.setHours(12, 0, 0, 0);
    const FIXED = noon.getTime();
    const RealDate = Date;
    window.Date = class extends RealDate {
      constructor(...args) { super(...(args.length ? args : [FIXED])); }
      static now() { return FIXED; }
    };
    const cal = JSON.stringify({
      updated: FIXED, errors: [],
      events: [{ title: 'Review', start: FIXED + 35 * 60e3, end: FIXED + 65 * 60e3, allDay: false, calendar: 'Work', color: '#60a5fa' }],
    });
    let inner = window.fetch;
    Object.defineProperty(window, 'fetch', {
      configurable: true,
      // the fallback is fixed when the wrapper is handed out: demo.js keeps the first one as its "real" fetch, and a
      // wrapper that looked up the live mock would send the demo's pass-through requests back to the demo forever
      get: () => {
        const fallback = inner;
        return (input, init) => {
          const url = typeof input === 'string' ? input : input.url;
          if (url.includes('/api/calendar')) {
            return Promise.resolve(new Response(cal, { status: 200, headers: { 'Content-Type': 'application/json' } }));
          }
          return fallback(input, init);
        };
      },
      set: (v) => { inner = v; },
    });
  });
  await show(page, [{ id: 'ag', type: 'agenda', x: 0, y: 0, w: 12, h: 1, options: {} }]);
  await page.evaluate(() => window.PhoneBoxDemo.emit('refresh'));
  const sub = page.locator('.ag-big-s').first();
  await expect(sub).toHaveText(/^in \d+ Min/); // German "in 35 Min · 12:35", not "Heute · 12:35"
  await expect(sub).not.toHaveText(/Heute/);
});

test('the breathe widget greets in the panel language before the first tap', async ({ page }) => {
  await show(page, [{ id: 'br', type: 'breathe', x: 0, y: 0, w: 4, h: 2, options: {} }]);
  await expect(page.locator('.br-w')).toHaveText('Tippen zum Starten');
});

// A copied English label can pass a key-coverage check, so check the translations and their placeholders too.
test('every translated key is really translated, and the rest fall back to English', async ({ page }) => {
  await page.goto('/panel/');
  const result = await page.evaluate(() => {
    const I18n = window.PhoneBoxI18n;
    const S = I18n.STRINGS;
    const en = S.en;
    const gaps = I18n.LANGUAGES.flatMap(({ code: lang }) =>
      lang === 'en' ? [] : Object.keys(en)
        .filter((k) => typeof S[lang]?.[k] !== 'string' || !S[lang][k].trim())
        .map((k) => `${lang}:${k}`));
    // a key whose text is identical to English is either a name that is not translated (Wi-Fi, USB, Client secret) or
    // padding; the Vietnamese dictionary is complete, so check it exactly, and spot-check the rest for copied sentences.
    const SAME_IS_FINE = new Set(['s.discord.clientSecret', 'status.wiFi', 'status.usb', 's.cd.clientId']);
    const copied = Object.keys(S).filter((lang) => lang !== 'en').flatMap((lang) =>
      Object.keys(S[lang])
        .filter((k) => S[lang][k] === en[k] && !SAME_IS_FINE.has(k) && en[k].length > 12 && /[a-z]{4,} [a-z]{3,}/.test(en[k]))
        .map((k) => `${lang}:${k}`));
    const placeholders = (text) => (text.match(/\{[\w.]+\}/g) || []).sort().join();
    const changedPlaceholders = Object.keys(S).filter((lang) => lang !== 'en').flatMap((lang) =>
      Object.keys(en).filter((k) => typeof S[lang][k] === 'string' &&
        placeholders(S[lang][k]) !== placeholders(en[k])).map((k) => `${lang}:${k}`));
    // An English key can ship before its translation.
    I18n.setLang('de');
    S.en['test.englishFallback'] = 'English fallback';
    const fallback = I18n.t('test.englishFallback');
    delete S.en['test.englishFallback'];
    I18n.setLang('vi');
    const translated = I18n.t('s.js.addWidget');
    return { gaps, copied, changedPlaceholders, fallback, translated };
  });
  expect(result.gaps).toEqual([]);
  expect(result.copied).toEqual([]);
  expect(result.changedPlaceholders).toEqual([]);
  expect(result.fallback).toBe('English fallback');
  expect(result.translated).toBe('Thêm widget');
});

// Keep dedicated checks for the two established full dictionaries.
for (const lang of ['vi', 'pt-BR']) {
test(`the ${lang} dictionary is complete`, async ({ page }) => {
  await page.goto('/panel/');
  const gaps = await page.evaluate((lang) => {
    const S = window.PhoneBoxI18n.STRINGS;
    return Object.keys(S.en).filter((k) => !(k in S[lang]));
  }, lang);
  expect(gaps).toEqual([]);
});
}

test('the Greek dictionary and widget catalog are complete', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const I18n = window.PhoneBoxI18n;
    const S = I18n.STRINGS;
    const missing = Object.keys(S.en).filter((k) => !(k in S.el));
    I18n.setLang('el');
    return {
      missing,
      locale: I18n.getLocale(),
      add: I18n.t('s.js.addWidget'),
      name: I18n.tc('Clock'),
      category: I18n.tc('Weather & sky'),
    };
  });
  expect(out.missing).toEqual([]);
  expect(out.locale).toBe('el-GR');
  expect(out.add).not.toBe('Add a widget');
  expect(out.name).toMatch(/[\u0370-\u03ff]/);
  expect(out.category).toMatch(/[\u0370-\u03ff]/);
});

test('the widget catalog translates by its English text', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(() => {
    const I18n = window.PhoneBoxI18n;
    I18n.setLang('vi');
    return {
      name: I18n.tc('Clock'),
      desc: I18n.tc('Clock, weather, now playing and PC tiles.'),
      category: I18n.tc('Weather & sky'),
      untouched: I18n.tc('not in the catalog'),
      // the catalog is keyed by English, so another language keeps the English name rather than a wrong one
      other: (I18n.setLang('en'), I18n.tc('Clock')),
    };
  });
  expect(out.name).toBe('Đồng hồ');
  expect(out.desc).toContain('Đồng hồ');
  expect(out.category).toBe('Thời tiết & bầu trời');
  expect(out.untouched).toBe('not in the catalog');
  expect(out.other).toBe('Clock');
});

test('the widget catalog translates by its English text (pt-BR)', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(() => {
    const I18n = window.PhoneBoxI18n;
    I18n.setLang('pt-BR');
    return {
      name: I18n.tc('Clock'),
      desc: I18n.tc('Clock, weather, now playing and PC tiles.'),
      category: I18n.tc('Weather & sky'),
      untouched: I18n.tc('not in the catalog'),
    };
  });
  expect(out.name).toBe('Relógio');
  expect(out.desc).toContain('Relógio');
  expect(out.category).toBe('Clima & céu');
  expect(out.untouched).toBe('not in the catalog');
});

// A JavaScript object literal keeps the last of two equal keys without a warning, so a key defined twice in one
// language silently replaces the first text (s.ha.hint once showed the token setup paragraph in the entity picker).
test('i18n.js defines every key once per language', () => {
  const fs = require('fs');
  const path = require('path');
  const lines = fs.readFileSync(path.join(__dirname, '..', '..', 'android', 'app', 'src', 'main', 'assets', 'www', 'i18n.js'), 'utf8').split('\n');
  // EXTRA is merged into STRINGS with Object.assign, so the two share one key space per language; CATALOG is its own.
  const TABLE = /^ {2}const ([A-Z_]+) = \{\s*$/;
  // A property name as source text: 'single', "double" or bare. name() turns it into the name JavaScript uses, so
  // Time, 'Time' and "Time" (or vi and 'vi') count as the same key.
  const NAME = String.raw`'(?:[^'\\]|\\.)*'|"(?:[^"\\]|\\.)*"|[A-Za-z_$][\w$]*`;
  const BLOCK = new RegExp(String.raw`^ {4}(${NAME}):\s*\{\s*$`);
  const KEY = new RegExp(String.raw`^ {6}(${NAME}):`);
  const name = (token) => (/^['"]/.test(token) ? Function(`"use strict"; return (${token});`)() : token);
  const dupes = [];
  const spaces = new Map();
  let table = null, block = null;
  lines.forEach((line, i) => {
    const tb = TABLE.exec(line);
    if (tb) { table = tb[1] === 'EXTRA' ? 'STRINGS' : tb[1]; block = null; return; }
    const b = BLOCK.exec(line);
    if (b) { block = `${table} ${name(b[1])}`; if (!spaces.has(block)) spaces.set(block, new Map()); return; }
    const k = block && KEY.exec(line);
    if (!k) return;
    const key = name(k[1]);
    const seen = spaces.get(block);
    if (seen.has(key)) dupes.push(`${block} ${k[1]} (lines ${seen.get(key)} and ${i + 1})`);
    else seen.set(key, i + 1);
  });
  expect(spaces.size).toBeGreaterThanOrEqual(14); // the parser still finds the language blocks (13 languages + catalog)
  expect(dupes).toEqual([]);
});

test('translations preserve literal dollar tokens in session names', async ({ page }) => {
  await page.goto('/panel/i18n.js');
  await page.addScriptTag({ url: '/panel/i18n.js' });
  const result = await page.evaluate(() => {
    const i = window.PhoneBoxI18n;
    i.setLang('en');
    return ['Build', 'Budget $&', '$$', '$`', "$'", '', 0, null].map(name => i.t('panel.sessionFinished', { name }));
  });
  expect(result).toEqual(['Build finished', 'Budget $& finished', '$$ finished', '$` finished', "$' finished", ' finished', '0 finished', ' finished']);
});

test('both settings language selectors stay in sync and Revert restores the saved language', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(async () => {
    localStorage.clear();
    const config = await (await fetch('/api/settings')).json();
    config.display.language = 'en';
    config.controls.actions = [];
    config.controls.profiles = [];
    localStorage.setItem('pb-demo-config', JSON.stringify(config));
  });
  await page.goto('/settings/#general');
  await expect(page.locator('#langSel')).toHaveValue('en');
  await page.locator('#displayLangSel').selectOption('tr');
  await expect(page.locator('#langSel')).toHaveValue('tr');
  await expect(page.locator('#save')).toBeEnabled();
  await expect(page.locator('[data-bind="display.theme"]')).toBeVisible();
  await page.locator('#revert').click();
  await expect(page.locator('#langSel')).toHaveValue('en');
  await expect(page.locator('#displayLangSel')).toHaveValue('en');
  await expect(page.locator('#save')).toBeDisabled();
});

test('a widget title typed in Turkish uses Turkish capitals', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'tr' },
      pages: [{ id: 'one', title: 'One', widgets: [
        { id: 'inbox', type: 'inbox', x: 0, y: 0, w: 6, h: 4 },
        { id: 'sw', type: 'stopwatch', x: 6, y: 0, w: 3, h: 3 },
        { id: 'tm', type: 'timer', x: 9, y: 0, w: 3, h: 3, options: { label: 'içecek' } },
      ] }],
    }));
  });
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('lang', 'tr');
  // innerText is the rendered text, so it shows what text-transform made of it; the widgets are laid out in order
  const title = (i) => page.locator('.w-title').nth(i).evaluate((e) => e.innerText.split('\n')[0].trim());
  await expect.poll(() => title(2)).toBe('İÇECEK');
});


test('the saved Japanese preference reaches the settings and the phone panel', async ({ page }) => {
  await page.goto('/settings/#general');
  await expect(page.locator('#save')).toBeDisabled();
  await page.locator('#langSel').selectOption('ja');
  await expect(page.locator('#save')).toHaveText('保存');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  await page.reload();
  await expect(page.locator('#displayLangSel')).toHaveValue('ja');
  await page.goto('/panel/');
  await expect(page.locator('html')).toHaveAttribute('lang', 'ja');
  await expect(page.locator('#dots')).toHaveAttribute('title', /[^\x00-\x7f]/);
});

test('Arabic pages still turn: the pager keeps scrolling left to right', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'ar' },
      pages: [
        { id: 'one', title: 'One', widgets: [{ id: 'a', type: 'clock', x: 0, y: 0, w: 3, h: 2 }] },
        { id: 'two', title: 'Two', widgets: [{ id: 'b', type: 'clock', x: 0, y: 0, w: 3, h: 2 }] },
      ],
    }));
  });
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.locator('#pager')).toHaveCSS('direction', 'ltr');
  await expect(page.locator('#pager > .page').first()).toHaveCSS('direction', 'rtl');
  await page.evaluate(async () => { const { goPage } = await import('/panel/main.js'); goPage(1, true); });
  const second = await page.locator('#pager > .page').nth(1).boundingBox();
  expect(Math.abs(second.x)).toBeLessThan(2); // the second page is the one on screen
});

test('Arabic reads right to left without moving saved widget coordinates', async ({ page }) => {
  await show(page, [
    { id: 'left', type: 'chart', x: 0, y: 0, w: 3, h: 2, options: { metrics: ['cpu'] } },
    { id: 'right', type: 'clock', x: 9, y: 0, w: 3, h: 2 },
  ]);
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config'));
    cfg.display.language = 'ar';
    localStorage.setItem('pb-demo-config', JSON.stringify(cfg));
  });
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const widgets = page.locator('.grid .w');
  await expect(widgets).toHaveCount(2);
  await expect(widgets.first()).toHaveCSS('direction', 'rtl');
  const left = await widgets.first().boundingBox();
  const right = await widgets.last().boundingBox();
  expect(left.x).toBeLessThan(right.x);
  await expect(widgets.first().locator('canvas').first()).toHaveCSS('direction', 'ltr');

  await page.keyboard.press('o');
  const preview = page.locator('.ov-map').first().locator('span');
  await expect(preview).toHaveCount(2);
  const previewLeft = await preview.first().boundingBox();
  const previewRight = await preview.last().boundingBox();
  expect(previewLeft.x).toBeLessThan(previewRight.x);
});

test('Arabic settings wraps a long adb path and localizes the appearance defaults', async ({ page }) => {
  await page.setViewportSize({ width: 1120, height: 780 });
  await page.goto('/settings/#general');
  await page.locator('#langSel').selectOption('ar');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.locator('#appearanceSettings output').first()).toHaveText('تلقائي');
  const fits = await page.locator('#adbFound').evaluate((hint) => {
    hint.textContent = 'C:\\platform-tools\\' + 'long-directory\\'.repeat(18) + 'adb.exe';
    return hint.scrollWidth <= hint.clientWidth &&
      document.documentElement.scrollWidth <= window.innerWidth;
  });
  expect(fits).toBe(true);
});

test('follow system distinguishes Traditional Chinese and the legacy Indonesian tag', async ({ page }) => {
  await page.goto('/panel/');
  const resolved = await page.evaluate(() => {
    const i18n = window.PhoneBoxI18n;
    return ['zh-Hant-TW', 'zh-HK', 'zh_CN', 'in-ID', 'xx', 'constructor'].map((tag) => {
      Object.defineProperty(navigator, 'languages', { value: [tag], configurable: true });
      return i18n.resolveLang('auto');
    });
  });
  expect(resolved).toEqual(['zh-Hant', 'zh-Hant', 'zh', 'id', 'en', 'en']);
});


test('the idle media widget uses the Arabic no-audio label', async ({ page }) => {
  await show(page, [{ id: 'media', type: 'media', x: 0, y: 0, w: 8, h: 4, options: {} }], { language: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
  await expect(page.locator('.w-media')).toBeVisible();
  const label = await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    main.D.state = { ...main.D.state, media: null };
    main.emit('state');
    return document.querySelector('.w-media .m-title').textContent;
  });
  expect(label).toBe('لا يوجد صوت');
});

test('the Arabic template dialog inserts the saved page title literally', async ({ page }) => {
  const title = 'Budget $& <2026> "Q4"';
  await page.addInitScript((title) => {
    localStorage.setItem('pb-demo-config', JSON.stringify({
      display: { language: 'ar' },
      pages: [{ id: 'home', title, widgets: [] }],
    }));
  }, title);
  await page.goto('/settings/#pages');
  await expect(page.locator('#peHead input[type="text"]').nth(1)).toHaveValue(title);
  await page.locator('#peHead').getByRole('button', { name: 'استبدال بقالب…' }).click();
  await expect(page.locator('#modalBody h3')).toHaveText(`استبدال عناصر صفحة «${title}»`);
});

test('the shortcut delete button is localized in Arabic without changing its English label', async ({ page }) => {
  await page.goto('/settings/#shortcuts');
  await page.locator('#deck .key:not(.add)').first().click();
  const deleteButton = page.locator('#editor button.danger');
  await expect(deleteButton).toHaveAccessibleName('Delete');
  const shortcut = page.locator('#deck .key:not(.add) .kl').first();
  const label = await shortcut.textContent();
  await page.locator('#langSel').selectOption('ar');
  await expect(deleteButton).toHaveAccessibleName('حذف');
  await expect(shortcut).toHaveText(label);
  await page.evaluate(() => { location.hash = '#webhooks'; });
  const pushedDelete = page.locator('#whItems button').first();
  await expect(pushedDelete).toHaveAccessibleName('حذف');
  await page.locator('#langSel').selectOption('en');
  await expect(pushedDelete).toHaveAccessibleName('Delete');
});

for (const language of ['ar', 'de']) {
  test(`the calendar uses localized Monday-first weekday headings (${language})`, async ({ page }) => {
    await show(page, [{ id: 'calendar', type: 'calendar', x: 0, y: 0, w: 4, h: 4 }], { language });
    const headings = await page.evaluate(() => {
      const formatter = new Intl.DateTimeFormat(window.PhoneBoxI18n.getLocale(), { weekday: 'narrow' });
      return Array.from({ length: 7 }, (_, i) => formatter.format(new Date(2024, 0, i + 1)));
    });
    await expect(page.locator('.w-calendar .cal-h')).toHaveText(headings);
  });
}
