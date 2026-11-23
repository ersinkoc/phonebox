const { test, expect } = require('@playwright/test');

async function openSettings(page, section, language = 'ar') {
  await page.goto('/settings/');
  await page.evaluate(async (language) => {
    const response = await fetch('/api/settings');
    const cfg = await response.json();
    cfg.display = { ...cfg.display, language };
    cfg.shortcuts = [
      { id: 'calc', label: 'Calculator', cmd: 'calc.exe', icon: '🧮' },
      { id: 'custom', label: 'اختصار مخصص', cmd: 'C:\\Demo\\custom.exe', args: '--count 25', icon: '⭐' },
    ];
    await fetch('/api/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(cfg) });
  }, language);
  await page.goto('/settings/');
  await page.evaluate((section) => { location.hash = section; }, section);
  await expect(page.locator('html')).toHaveAttribute('lang', language);
}

test('widget catalog keeps fixed icon spacing in Arabic and preserves the English layout', async ({ page }) => {
  await page.setViewportSize({ width: 1000, height: 740 });
  await openSettings(page, 'pages');
  const rows = page.locator('#palette .pal');
  await expect(rows.first()).toBeVisible();
  await expect(page.locator('#palSearch')).toHaveCSS('text-align', 'right');
  await expect(page.locator('.pal-cat').first()).toHaveCSS('text-align', 'right');
  for (const row of await rows.all().then(items => items.slice(0, 12))) {
    const icon = await row.locator('.pal-ico').boundingBox();
    const text = await row.locator('span:last-child').first().boundingBox();
    expect(icon.width).toBe(28);
    expect(icon.x).toBeGreaterThan(text.x);
    await expect(row.locator('small')).toHaveCSS('text-align', 'right');
  }
  await page.locator('#stageSeg [data-v="live"]').click();
  await expect(page.locator('#stageHint')).toContainText('معاينة فعلية للوحة');
  await page.locator('#langSel').selectOption('en');
  await expect(page.locator('#stageHint')).toContainText('The real panel');
  const row = rows.first();
  expect((await row.locator('.pal-ico').boundingBox()).x).toBeLessThan((await row.locator('small').boundingBox()).x);
});

test('RTL reordering leaves shortcut labels and commands intact', async ({ page }) => {
  await openSettings(page, 'shortcuts');
  const keys = page.locator('#deck .key:not(.add)');
  await expect(keys.locator('.kl')).toHaveText(['Calculator', 'اختصار مخصص']);
  expect((await keys.first().boundingBox()).x).toBeGreaterThan((await keys.last().boundingBox()).x);
  await keys.last().click();
  const command = page.locator('#editor input.technical-input').filter({ visible: true }).first();
  await expect(command).toHaveCSS('direction', 'ltr');
  await expect(command).toHaveValue('C:\\Demo\\custom.exe');
  await page.getByRole('button', { name: 'تقديم', exact: true }).click();
  await expect(keys.locator('.kl')).toHaveText(['اختصار مخصص', 'Calculator']);
  await keys.first().dragTo(keys.last());
  await expect(keys.locator('.kl')).toHaveText(['Calculator', 'اختصار مخصص']);
  await expect(page.locator('#editor input').first()).toHaveValue('اختصار مخصص');
});

test('Arabic rule presets and Webhooks examples preserve their technical values', async ({ page }) => {
  await openSettings(page, 'rules');
  await expect(page.locator('#rules h3').first()).toHaveText('إضافة قاعدة');
  await expect(page.getByRole('button', { name: /بطارية الفأرة/ })).toBeVisible();
  await page.getByRole('button', { name: /تجاوز حد الاستخدام/ }).click();
  await expect(page.locator('#ruleList .rule').last().locator('input[type="number"]').filter({ visible: true }).first()).toHaveValue('80');
  await page.evaluate(() => { location.hash = 'webhooks'; });
  await expect(page.locator('#whExamples code').first()).toHaveCSS('direction', 'ltr');
  await expect(page.locator('#whExamples code').first()).toContainText('/api/push/temperature -d 21.5');
  await expect(page.locator('#whPush')).toContainText('إرسال قيمة تجريبية');
  await expect(page.locator('#webhooks h3').first()).toHaveText('اختبار خطافات الويب');
  await expect(page.locator('#whItems')).toContainText('temperature');
  await expect(page.locator('#whItems bdi').first()).toHaveCSS('direction', 'ltr');
  page.once('dialog', dialog => dialog.accept());
  await page.locator('#whClear').click();
  await expect(page.locator('#whItems')).toContainText('لم تُرسل أي قيم بعد');
  await page.locator('#whPush').click();
  await expect(page.locator('#whItems')).toContainText('demo');
  page.once('dialog', dialog => dialog.accept());
  await page.locator('#whClear').click();
  await expect(page.locator('#whItems')).toContainText('لم تُرسل أي قيم بعد');
});

test('General keeps the pairing prerequisite and uses physical slider direction', async ({ page }) => {
  await openSettings(page, 'general');
  await expect(page.locator('#general h2')).toHaveText('الإعدادات العامة');
  await expect(page.locator('[data-i18n-html="s.wifi.lead"]')).toContainText('بعد تفعيل هذا الخيار وإعادة تشغيل PhoneBox');
  const night = page.locator('[data-bind="night.brightness"]');
  await expect(night).toHaveCSS('direction', 'ltr');
  await night.focus();
  await night.press('Home');
  await expect(page.locator('#nbv')).toHaveText('1%');
  await night.press('ArrowRight');
  await expect(page.locator('#nbv')).toHaveText('2%');
  const appearance = page.locator('#appearanceSettings input[type="range"]').first();
  await expect(appearance).toHaveCSS('direction', 'ltr');
  await appearance.focus();
  await appearance.press('End');
  await expect(page.locator('#appearanceSettings output').first()).toHaveText('200%');
});

test('Advanced JSON has LTR editing, scroll and unchanged Arabic string values', async ({ page }) => {
  await openSettings(page, 'advanced');
  const editor = page.locator('#json');
  await expect(editor).toHaveCSS('direction', 'ltr');
  await expect(editor).toHaveCSS('text-align', 'left');
  await expect(page.locator('#info')).toContainText('الإصدار');
  const cfg = JSON.parse(await editor.inputValue());
  cfg.weather.city = 'القاهرة ' + 'x'.repeat(1200);
  const json = JSON.stringify(cfg, null, 2);
  await editor.fill(json);
  const scroll = await editor.evaluate(el => ({ width: el.clientWidth, content: el.scrollWidth }));
  expect(scroll.content).toBeGreaterThan(scroll.width);
  await editor.press('Control+Home');
  await editor.press('Shift+ArrowRight');
  expect(await editor.evaluate(el => el.value.slice(el.selectionStart, el.selectionEnd))).toBe('{');
  await page.locator('#applyJson').click();
  await expect(page.locator('#save')).toBeEnabled();
  expect(JSON.parse(await editor.inputValue()).weather.city).toBe(cfg.weather.city);
  await editor.fill('{');
  await page.locator('#applyJson').click();
  await expect(page.locator('#jsonErr')).toHaveText('صيغة JSON غير صالحة.');
  const download = page.waitForEvent('download');
  await page.locator('#downloadJson').click();
  expect((await download).suggestedFilename()).toMatch(/^phonebox-settings.*\.json$/);
  cfg.weather.city = 'مدينة تجريبية';
  const chooser = page.waitForEvent('filechooser');
  await page.locator('#uploadJson').click();
  page.once('dialog', dialog => dialog.accept());
  await (await chooser).setFiles({ name: 'demo.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(cfg)) });
  await expect(page.locator('#jsonErr')).toBeEmpty();
  await expect(editor).toHaveValue(/مدينة تجريبية/);
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  expect(await page.evaluate(async () => (await (await fetch('/api/settings')).json()).weather.city)).toBe(cfg.weather.city);
});

test('Arabic panel keeps controls, measurements and signed temperatures in physical order', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 530 });
  await page.addInitScript(() => {
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, display: { language: 'ar', hour12: true }, rules: [], shortcuts: [],
      weather: { city: 'القاهرة', unit: 'celsius' },
      pages: [{ id: 'rtl', title: 'صفحة تجريبية', widgets: [
        { id: 'clock', type: 'clock', x: 0, y: 0, w: 3, h: 2, options: { status: false, weather: false } },
        { id: 'media', type: 'media', x: 3, y: 0, w: 9, h: 2 },
        { id: 'ram', type: 'stat', x: 0, y: 2, w: 4, h: 2, options: { metric: 'ram' } },
        { id: 'net', type: 'stat', x: 4, y: 2, w: 4, h: 2, options: { metric: 'net' } },
        { id: 'gpu', type: 'gauge', x: 8, y: 2, w: 4, h: 2, options: { metric: 'gpu' } },
        { id: 'weather', type: 'weather', x: 0, y: 4, w: 6, h: 2 },
        { id: 'meters', type: 'meters', x: 6, y: 4, w: 3, h: 2 },
        { id: 'calendar', type: 'calendar', x: 9, y: 4, w: 3, h: 2 },
      ] }],
    }));
  });
  await page.clock.setFixedTime(await page.evaluate(() => new Date(2026, 0, 1, 23, 48, 59).getTime()));
  await page.goto('/panel/');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const bounds = await page.locator('.w-clock').evaluate(box => {
    const outer = box.getBoundingClientRect();
    const parts = [...box.querySelectorAll('.clock span')].map(part => part.getBoundingClientRect());
    return [parts[0].left >= outer.left, parts.at(-1).right <= outer.right + 1,
      parts.slice(1).every((part, index) => part.left >= parts[index].right - 1)];
  });
  expect(bounds).toEqual([true, true, true]);
  const ordered = page.locator('.m-controls [data-media], .m-controls .vol');
  const x = await ordered.evaluateAll(elements => elements.map(el => el.getBoundingClientRect().left));
  expect(x).toEqual([...x].sort((a, b) => a - b));
  for (const selector of ['.m-progress .bar', '.m-progress .times', '.m-controls .volr',
    '.w-stat .t-val', '.w-gauge .g-val', '.w-meters .track', '.w-meters .m-row span:last-child']) {
    await expect(page.locator(selector).first()).toHaveCSS('direction', 'ltr');
  }
  await expect(page.locator('.w-gauge .g-label')).toHaveText('معالج الرسومات');
  const weekdays = page.locator('.w-calendar .cal-h');
  await expect(weekdays).toHaveCount(7);
  expect(await weekdays.evaluateAll(cells => cells.every(cell => cell.scrollWidth <= cell.clientWidth))).toBe(true);
  const weather = await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    main.D.weather = { current: { weather_code: 0, is_day: 1, temperature_2m: -5, apparent_temperature: -7 },
      daily: { temperature_2m_max: [2], temperature_2m_min: [-8] } };
    main.emit('weather');
    return document.querySelector('.w-weather .wx-range').textContent;
  });
  expect(weather).toContain('\u2066↑2° ↓-8°\u2069');
  expect(weather).toContain('\u2066-7°\u2069');
  expect(weather.match(/°/g)).toHaveLength(3);
  await expect(page.locator('.w-weather .wx-temp')).toHaveCSS('direction', 'ltr');
  await expect(page.locator('.w-weather .wx-desc')).toHaveCSS('direction', 'rtl');
});
