const { test, expect } = require('@playwright/test');
const fs = require('fs'), path = require('path'), vm = require('vm');
const root = path.resolve(__dirname, '../..');
const source = fs.readFileSync(path.join(root, 'android/app/src/main/assets/www/i18n.js'), 'utf8');
function dictionary(language = 'en', saved = null) {
  const values = new Map(saved ? [['pb-lang', saved]] : []);
  const ctx = { window: {}, document: { documentElement: {} }, navigator: { languages: [language] },
    localStorage: { getItem: k => values.get(k), setItem: (k, v) => values.set(k, v) } };
  vm.runInNewContext(source, ctx);
  return { i: ctx.window.PhoneBoxI18n, html: ctx.document.documentElement, values };
}
test('Hebrew dictionary covers settings and catalog and preserves substitutions and markup', () => {
  const { i } = dictionary('he-IL');
  expect(Object.keys(i.STRINGS.en).filter(k => !(k in i.STRINGS.he))).toEqual([]);
  expect(Object.keys(i.CATALOG.vi).filter(k => !(k in i.CATALOG.he))).toEqual([]);
  const tokens = text => [...text.matchAll(/\{[A-Za-z][\w]*\}/g)].map(m => m[0]).sort();
  for (const [k, en] of Object.entries(i.STRINGS.en)) {
    expect(tokens(i.STRINGS.he[k]), k).toEqual(tokens(en));
    expect(i.STRINGS.he[k].match(/<[^>]+>/g) || [], k).toEqual(en.match(/<[^>]+>/g) || []);
  }
});
test('Hebrew auto detection and language switching set direction', () => {
  for (const language of ['he-IL', 'iw-IL']) {
    const { i, html, values } = dictionary(language);
    expect(i.getLang()).toBe('he');
    expect(i.getLocale()).toBe('he-IL');
    expect(html.dir).toBe('rtl');
    i.setLang('en');
    expect(html.dir).toBe('ltr');
    expect(values.get('pb-lang')).toBe('en');
    i.setLang('iw');
    expect(i.getPref()).toBe('he');
    expect(html.dir).toBe('rtl');
  }
  expect(dictionary('en', 'he').html.dir).toBe('rtl');
  expect(dictionary('en', 'iw').html.dir).toBe('rtl');
});
test('Hebrew word clock handles midnight, rounding and English fallback', () => {
  const src = fs.readFileSync(path.join(root, 'android/app/src/main/assets/www/widgets/time.js'), 'utf8');
  const body = src.slice(src.indexOf('const NUM_WORDS'), src.indexOf('W.wordclock =')).replace('export function', 'function');
  const ctx = { LOCALE: 'he-IL' }; vm.createContext(ctx); vm.runInContext(body, ctx);
  const at = (h, m, locale) => ctx.timeInWords(new Date(2026, 10, 18, h, m), locale);
  expect(at(0, 0).main).toEqual(['חצות']);
  expect(at(12, 0).main).toEqual(['שתים עשרה']);
  expect(at(10, 15).main).toEqual(['עשר', 'ורבע']);
  expect(at(10, 45).main).toEqual(['רבע', 'לאחת עשרה']);
  expect(at(23, 59).lead).toBe('כמעט');
  expect(at(12, 1).lead).toBe('קצת אחרי');
  expect(at(12, 0, 'en-GB').main).toEqual(['noon']);
});
test('settings saves Hebrew and can revert an LTR language change', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#general');
  await page.locator('#langSel').selectOption('he');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.locator('#displayLangSel')).toHaveValue('he');
  await expect(page.locator('#save')).toContainText(/[א-ת]/);
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  await page.reload();
  await expect(page.locator('#langSel')).toHaveValue('he');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.locator('input[data-bind="adbPath"]')).toHaveAttribute('dir', 'ltr');
  await page.locator('#langSel').selectOption('en');
  await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
  await page.locator('#revert').click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
});
test('Hebrew editor preserves physical drag coordinates', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#advanced');
  await page.locator('#json').fill(JSON.stringify({ display: { language: 'he' }, pages: [
    { id: 'p', title: 'עמוד', widgets: [{ id: 'w1', type: 'clock', x: 0, y: 0, w: 6, h: 4 }] },
  ] }));
  await page.locator('#applyJson').click();
  await page.locator('a[href="#pages"]').click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.locator('#canvas')).toHaveCSS('direction', 'ltr');
  const cell = await page.locator('#canvas .cell').first().boundingBox();
  const box = await page.locator('#canvas .wb').boundingBox();
  await page.mouse.move(box.x + 20, box.y + 20);
  await page.mouse.down();
  await page.mouse.move(box.x + 20 + cell.width, box.y + 20 + cell.height, { steps: 5 });
  await page.mouse.up();
  expect(await page.evaluate(() => { const { x, y } = cfg.pages[0].widgets[0]; return [x, y]; })).toEqual([1, 1]);
});
for (const viewport of [{ width: 780, height: 360 }, { width: 390, height: 844 }]) {
  test(`Hebrew panel navigation preserves page order at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await page.goto('/panel/style.css');
    await page.evaluate(() => {
      localStorage.clear();
      localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [],
        display: { language: 'he' }, pages: ['ראשון', 'שני'].map((title, n) => ({ id: 'p' + n, title,
          widgets: [{ id: 'n' + n, type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: title } }] })) }));
    });
    await page.goto('/panel/');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.locator('#pager')).toHaveCSS('direction', 'ltr');
    await expect(page.locator('#dots i')).toHaveCount(2);
    await page.keyboard.press('ArrowRight');
    await expect(page.locator('#dots i').nth(1)).toHaveClass(/on/);
    await page.keyboard.press('ArrowLeft');
    await expect(page.locator('#dots i').first()).toHaveClass(/on/);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  });
}
