const { test, expect } = require('@playwright/test');

test('both previews follow the connected device, its rotation, and the fallback', async ({ page }) => {
  await page.goto('/settings/#pages');
  await expect(page.locator('#pageTabs .ptab').first()).toBeVisible();
  await page.evaluate(async () => {
    setStage('live');
    const toggle = [...document.querySelectorAll('#appearanceSettings button')].find(el =>
      el.textContent === window.PhoneBoxI18n.t('s.appearance.showPreview'));
    toggle.click();
    window.previewFixture = [
      { id: 'tablet', model: 'Tablet', lastSeen: Date.now(), viewport: { width: 1138, height: 711 } },
      { id: 'phone', model: 'Phone', lastSeen: Date.now(), viewport: { width: 800, height: 400 } },
    ];
    const fetchDemo = window.fetch;
    window.fetch = (input, init) => String(input).endsWith('/api/phones')
      ? Promise.resolve(Response.json(window.previewFixture)) : fetchDemo(input, init);
    await loadPhones(false);
  });
  const layout = page.locator('#previewBox iframe');
  const appearance = page.locator('#appearanceSettings iframe');
  for (const frame of [layout, appearance]) await expect(frame).toHaveCSS('width', '1138px');
  await page.locator('#stage .preview-device').selectOption('phone');
  for (const frame of [layout, appearance]) await expect(frame).toHaveCSS('width', '800px');
  await expect(page.locator('#appearanceSettings .preview-device')).toHaveValue('phone');

  await page.evaluate(async () => {
    window.previewFixture[1].viewport = { width: 400, height: 800 };
    await loadPhones(false);
  });
  for (const frame of [layout, appearance]) await expect(frame).toHaveCSS('height', '800px');

  await page.evaluate(async () => {
    window.previewFixture = [];
    await loadPhones(false);
  });
  for (const frame of [layout, appearance]) await expect(frame).toHaveCSS('width', '780px');
});
