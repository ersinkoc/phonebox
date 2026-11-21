// The panel's widgets write some text as English literals. Those go through tp() / tpv() (i18n.js PANEL, keyed by the
// English text), so a language can translate them without touching the widgets. These tests keep it that way.
const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

const WWW = path.join(__dirname, '..', '..', 'android', 'app', 'src', 'main', 'assets', 'www');
const FILES = ['main.js', ...fs.readdirSync(path.join(WWW, 'widgets')).filter((f) => f.endsWith('.js')).map((f) => `widgets/${f}`)];
const LIT = String.raw`'((?:[^'\\\n]|\\.)*)'`;
const unquote = (s) => s.replace(/\\'/g, "'");

// Where a widget puts text on screen: h(tag, class, 'text'), toast('text'), an aria-label, or el.textContent / title /
// placeholder = 'text'. A literal there that reads as English words (a capitalised word, or words with a space) is
// text the user sees, and it has to be wrapped. Names, units and symbols ('CPU', 'GB', '—') are not matched.
const SHOWN = [
  new RegExp(String.raw`\bh\(\s*'[\w-]+'\s*,\s*(?:'[^'\n]*'|null)\s*,\s*` + LIT, 'g'),
  new RegExp(String.raw`\btoast\(\s*` + LIT, 'g'),
  new RegExp(String.raw`'aria-label'\s*,\s*` + LIT, 'g'),
  new RegExp(String.raw`\.(?:textContent|title|placeholder)\s*=\s*` + LIT, 'g'),
];
const words = (s) => /[A-Za-z]{3,}/.test(s) && /\s[a-z]|^[A-Z][a-z]+$/.test(s);

test('widget text shown on screen goes through the PANEL table', () => {
  const bare = [];
  for (const f of FILES) {
    const src = fs.readFileSync(path.join(WWW, f), 'utf8');
    for (const re of SHOWN) {
      for (const m of src.matchAll(re)) {
        const s = unquote(m[1]);
        if (words(s)) bare.push(`${f}:${src.slice(0, m.index).split('\n').length} ${JSON.stringify(s)}`);
      }
    }
  }
  expect(bare, 'wrap these in tp() / tpv() and add them to PANEL').toEqual([]);
});

// Every literal handed to tp() / tpv() / en(), for the language tables below.
function panelLiterals() {
  const used = new Set();
  for (const f of FILES) {
    const src = fs.readFileSync(path.join(WWW, f), 'utf8');
    for (const m of src.matchAll(new RegExp(String.raw`\b(?:tp|tpv)\(\s*` + LIT, 'g'))) used.add(unquote(m[1]));
    for (const m of src.matchAll(/\ben\(([^()]*)\)/g)) for (const x of m[1].matchAll(new RegExp(LIT, 'g'))) used.add(unquote(x[1]));
  }
  return [...used].filter((s) => /[A-Za-z]{2,}/.test(s));
}

test('every language in PANEL translates each panel literal and keeps its placeholders', async ({ page }) => {
  const used = panelLiterals();
  expect(used.length).toBeGreaterThan(150); // the parser still finds the call sites
  await page.goto('/panel/');
  const tables = await page.evaluate(() => window.PhoneBoxI18n.PANEL);
  expect(Object.keys(tables).length).toBeGreaterThan(0);
  for (const [lang, table] of Object.entries(tables)) {
    expect(used.filter((s) => !(s in table)), `${lang}: missing`).toEqual([]);
    const holes = Object.keys(table).filter((k) =>
      (k.match(/\{\w+\}/g) || []).sort().join() !== (table[k].match(/\{\w+\}/g) || []).sort().join());
    expect(holes, `${lang}: placeholders`).toEqual([]);
  }
});

test('tp() translates panel labels and preserves unknown text', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const I = window.PhoneBoxI18n;
    const main = await import('/panel/main.js');
    I.setLang('de');
    const de = I.tp('Waiting for GitHub data…');
    I.setLang('pt-BR');
    return {
      de,
      pt: I.tp('No detection'),
      repeat: I.tp('Track'),
      power: main.tpv('{n}% power', { n: 40 }),
      quota: main.quotaText('5-hour session · Opus'),
      unknown: I.tp('A label entered by the user'),
      // an agent label that happens to name an Object member is text, not the inherited member
      inherited: [I.tp('constructor'), I.tc('toString'), main.quotaText('constructor')],
    };
  });
  expect(out.inherited).toEqual(['constructor', 'toString', 'constructor']);
  expect(out.de).toMatch(/GitHub/);
  expect(out.de).not.toBe('Waiting for GitHub data…');
  expect(out.unknown).toBe('A label entered by the user');
  expect(out.pt).toBe('Sem detecção');
  expect(out.repeat).toBe('Faixa');
  expect(out.power).toBe('40% de energia');
  expect(out.quota).toMatch(/ · Opus$/);
  expect(out.quota).not.toMatch(/^5-hour session/);
});
