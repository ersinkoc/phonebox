const { test, expect } = require('@playwright/test');

// #613: on Linux and macOS the agent reports only the system output (PortableDeckNative: no sessions, no inputs, one
// output that cannot be switched; the demo answers the same for __demoOs). The deck widgets that need Windows Core
// Audio say so instead of an empty list.
async function show(page, type, os) {
  await page.addInitScript((os) => { window.__demoOs = os; }, os);
  await page.goto('/panel/style.css');
  await page.evaluate((type) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'control', type, x: 0, y: 0, w: 12, h: 6, options: {} }] }] }));
  }, type);
  await page.goto('/panel/');
}

for (const [type, text] of [
  ['audio-mixer', 'Per-app volume is only on Windows for now'],
  ['audio-outputs', 'Switching audio devices is only on Windows for now'],
  ['push-to-talk', 'Microphone control is only on Windows for now'],
]) {
  test(`${type} says it is Windows-only on Linux`, async ({ page }) => {
    await show(page, type, 'linux');
    await expect(page.getByText(text, { exact: true })).toBeVisible();
  });

  test(`${type} keeps its controls on Windows`, async ({ page }) => {
    await show(page, type, 'windows');
    await expect(page.locator('.w-control, .deck-list, button').first()).toBeVisible();
    await expect(page.getByText(text, { exact: true })).toHaveCount(0);
  });
}
