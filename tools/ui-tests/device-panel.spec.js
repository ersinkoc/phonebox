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
