// Localisation gaps the first i18n pass left behind: literals the dictionary already translates, and one spot
// that compared a localized label against the English word. Against the web demo, in German.
const { test, expect } = require('@playwright/test');

async function show(page, widgets) {
  await page.goto('/panel/');
  await page.evaluate((widgets) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'de' },
      pages: [{ id: 'one', title: 'One', widgets }],
    }));
  }, widgets);
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

// Panel keys are translated in all twelve languages and are checked strictly. Settings-page strings were added for
// English and Vietnamese only: t() falls back to the English entry for a key a language does not define, so a missing
// s.* key never shows a key name. The languages that do define a key must not carry English text in its place, which is
// the failure mode a plain "every key exists" check cannot see.
test('every translated key is really translated, and the rest fall back to English', async ({ page }) => {
  await page.goto('/panel/');
  const result = await page.evaluate(() => {
    const I18n = window.PhoneBoxI18n;
    const S = I18n.STRINGS;
    const en = S.en;
    // Panel keys (everything outside s.*) are the strings the phone shows, and they are translated in all twelve
    // languages, so a missing one is a regression. Settings-page keys (s.*) were added for English and Vietnamese only
    // and are allowed to fall back; every other gap is a failure.
    const gaps = Object.keys(S).flatMap((lang) =>
      lang === 'en' ? [] : Object.keys(en)
        .filter((k) => !k.startsWith('s.') && !(k in S[lang]))
        .map((k) => `${lang}:${k}`));
    // a key whose text is identical to English is either a name that is not translated (Wi-Fi, USB, Client secret) or
    // padding; the Vietnamese dictionary is complete, so check it exactly, and spot-check the rest for copied sentences.
    const SAME_IS_FINE = new Set(['s.discord.clientSecret', 'status.wiFi', 'status.usb', 's.cd.clientId']);
    const copied = Object.keys(S).filter((lang) => lang !== 'en').flatMap((lang) =>
      Object.keys(S[lang])
        .filter((k) => S[lang][k] === en[k] && !SAME_IS_FINE.has(k) && en[k].length > 12 && /[a-z]{4,} [a-z]{3,}/.test(en[k]))
        .map((k) => `${lang}:${k}`));
    // a missing key must fall back to English, not print the key
    I18n.setLang('de');
    const fallback = I18n.t('s.js.addWidget');
    I18n.setLang('vi');
    const translated = I18n.t('s.js.addWidget');
    return { gaps, copied, fallback, translated };
  });
  expect(result.gaps).toEqual([]); // panel keys must exist in every language; s.* may fall back to English
  expect(result.copied).toEqual([]);
  expect(result.fallback).toBe('Add a widget');
  expect(result.translated).toBe('Thêm widget');
});

test('the Vietnamese panel dictionary is complete', async ({ page }) => {
  await page.goto('/panel/');
  const gaps = await page.evaluate(() => {
    const S = window.PhoneBoxI18n.STRINGS;
    return Object.keys(S.en).filter((k) => !(k in S.vi));
  });
  expect(gaps).toEqual([]);
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
      other: (I18n.setLang('de'), I18n.tc('Clock')),
    };
  });
  expect(out.name).toBe('Đồng hồ');
  expect(out.desc).toContain('Đồng hồ');
  expect(out.category).toBe('Thời tiết & bầu trời');
  expect(out.untouched).toBe('not in the catalog');
  expect(out.other).toBe('Clock');
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
  expect(spaces.size).toBeGreaterThanOrEqual(13); // the parser still finds the language blocks (12 languages + catalog)
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
