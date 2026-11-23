const { test, expect } = require('@playwright/test');

// /panel/?device=<id>: the browser panel sends that id like the app does, so the agent gives it that device's pages
// and home page. The demo answers the panel's requests inside the page, so the headers are read from the module.
const headers = (page) => page.evaluate(async () => ({ ...(await import('/panel/main.js')).PANEL_HEADERS }));

async function load(page, url, config = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate((config) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'a', title: 'A', widgets: [] }, { id: 'b', title: 'B', widgets: [] }],
      ...config, display: { idleReturnSec: 0, ...(config.display || {}) } }));
  }, config);
  await page.goto(url);
  await expect(page.locator('#pager .page')).toHaveCount(2);
}

test('a device name in the address is sent with every request', async ({ page }) => {
  await load(page, '/panel/?device=monitor');
  const h = await headers(page);
  expect(h['X-PhoneBox-Device']).toBe('monitor');
  expect(h['X-PhoneBox-Panel']).toBe('1');
});

test('without a device, or with a name the agent would refuse, nothing is sent', async ({ page }) => {
  for (const url of ['/panel/', '/panel/?device=../etc', '/panel/?device=' + 'a'.repeat(33)]) {
    await load(page, url);
    expect(await headers(page)).toEqual({ 'X-PhoneBox-Panel': '1' });
  }
});

test("the device's own home page wins over the global one", async ({ page }) => {
  const homeIndex = () => page.evaluate(async () => (await import('/panel/main.js')).homeIndex());
  await load(page, '/panel/?device=monitor', { display: { homePage: 'a' } });
  expect(await homeIndex()).toBe(0);
  await load(page, '/panel/?device=monitor', { homePage: 'b', display: { homePage: 'a' } });
  expect(await homeIndex()).toBe(1);
});

test('Settings › Phones: a device home page lists its own pages, and empty follows the home page of all devices', async ({ page }) => {
  await page.goto('/settings/#general');
  const card = page.locator('#phoneList .phone-row').first();
  await expect(card).toBeVisible();
  const select = card.locator('select');
  // empty is not "the first page": it is whatever Page switching sets for every device
  await expect(select.locator('option').first()).toHaveText('Same as all devices');
  await expect(select).toHaveValue('');
  // the choices follow the pages the device shows: take one page off and it is no longer offered
  const all = await select.locator('option').count();
  await card.locator('label.check', { hasText: 'Shows all pages' }).locator('input').uncheck();
  const first = card.locator('.phone-pages label.check').first();
  const title = (await first.textContent()).trim();
  await first.locator('input').focus();
  await page.keyboard.press('Space');
  // the list is rebuilt to update the choices; the keyboard stays on the box just changed
  await expect(card.locator('.phone-pages input').first()).toBeFocused();
  await expect(card.locator('.phone-pages input').first()).not.toBeChecked();
  await expect(select.locator('option')).toHaveCount(all - 1);
  expect((await select.locator('option').allTextContents()).map((s) => s.trim())).not.toContain(title);
  await select.selectOption({ index: 1 });
  await expect(page.locator('#save')).toBeEnabled();
});
