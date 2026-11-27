const { test, expect } = require('@playwright/test');

// Custom HTML with a web page address: the page opens in a sandboxed frame of its own origin, and not at all on
// Android 5 and 6, where nothing keeps a redirect from bringing another page to the panel's http address.
const pageConfig = (options) => ({ night: { enabled: false }, shortcuts: [],
  pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'web', type: 'html', x: 0, y: 0, w: 12, h: 6, options }] }] });

async function open(page, options) {
  await page.goto('/panel/');
  await page.evaluate((c) => { localStorage.clear(); localStorage.setItem('pb-demo-config', JSON.stringify(c)); }, pageConfig(options));
  await page.reload();
}

test('a web page address opens in a sandboxed frame instead of the HTML', async ({ page, baseURL }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  const address = new URL(baseURL).origin + '/somewhere';
  await open(page, { html: '<b>own</b>', url: ' ' + address + ' ' });
  const frame = page.locator('.w-html iframe');
  await expect(frame).toHaveAttribute('src', address);
  await expect(frame).toHaveAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms allow-modals allow-downloads');
  await expect(frame).not.toHaveAttribute('srcdoc', /.*/);
  expect(errors).toEqual([]);
});

test('an address that is not http or https keeps the own HTML', async ({ page }) => {
  await open(page, { html: '<b>own</b>', url: 'javascript:alert(1)' });
  await expect(page.locator('.w-html iframe')).toHaveAttribute('sandbox', 'allow-scripts');
  await expect(page.locator('.w-html iframe')).toHaveAttribute('srcdoc', /<b>own<\/b>/);
});

test('on Android 6 the widget says a web page needs Android 7 instead of loading it', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL,
    userAgent: 'Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5 Build/M4B30Z; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/95.0.4638.74 Mobile Safari/537.36' });
  const page = await context.newPage();
  await open(page, { html: '<b>own</b>', url: 'http://192.168.1.10:8123/' });
  await expect(page.locator('.w-html .empty')).toHaveText('A web page needs Android 7 or later');
  await expect(page.locator('.w-html iframe')).toHaveCount(0);
  await context.close();
});
