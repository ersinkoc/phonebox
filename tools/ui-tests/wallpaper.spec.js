const { test, expect } = require('@playwright/test');

// The panel's wallpaper layer, against the demo (which passes a config's wallpaperRev through; the picture is mocked).
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

async function show(page, config = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate((config) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'p', title: 'P', widgets: [{ id: 'note', type: 'note', x: 0, y: 0, w: 6, h: 6, options: { text: 'Wallpaper sample' } }] }],
      ...config, display: { idleReturnSec: 0, ...(config.display || {}) } }));
  }, config);
  await page.goto('/panel/');
  await expect(page.locator('.w-note').first()).toBeVisible();
}

test('without a wallpaper there is no layer and the album glow stays', async ({ page }) => {
  await show(page);
  await expect(page.locator('#wallpaper')).toHaveCount(0);
  await expect(page.locator('body')).not.toHaveClass(/has-wallpaper/);
});

test('a wallpaper is drawn behind the pages with its revision, darkening and blur', async ({ page }) => {
  const asked = [];
  await page.route('**/agent/api/wallpaper*', (route) => { asked.push(route.request().url()); route.fulfill({ body: PNG, contentType: 'image/png' }); });
  // a revision past 2^53: as a JavaScript number its last digits would be lost and two pictures could share a URL
  await show(page, { wallpaperRev: '9019088123725100162', display: { wallpaper: { dim: 60, blur: 4 } } });
  const layer = page.locator('#wallpaper');
  await expect(layer).toHaveCount(1);
  await expect(page.locator('body')).toHaveClass(/has-wallpaper/);
  expect(await layer.evaluate((el) => el.style.backgroundImage)).toContain('/agent/api/wallpaper?v=9019088123725100162');
  expect(await layer.evaluate((el) => el.style.getPropertyValue('--wp-dim'))).toBe('0.6');
  expect(await layer.evaluate((el) => el.style.getPropertyValue('--wp-blur'))).toBe('4px');
  await expect.poll(() => asked.length).toBeGreaterThan(0);
  // the layer sits under the pages and takes no taps
  expect(await layer.evaluate((el) => getComputedStyle(el).pointerEvents)).toBe('none');
});

test('out-of-range darkening and blur are clamped', async ({ page }) => {
  await page.route('**/agent/api/wallpaper*', (route) => route.fulfill({ body: PNG, contentType: 'image/png' }));
  await show(page, { wallpaperRev: '1', display: { wallpaper: { dim: 500, blur: -3 } } });
  const layer = page.locator('#wallpaper');
  await expect(layer).toHaveCount(1);
  expect(await layer.evaluate((el) => el.style.getPropertyValue('--wp-dim'))).toBe('0.9');
  expect(await layer.evaluate((el) => el.style.getPropertyValue('--wp-blur'))).toBe('0px');
});

test('the settings live preview shows the wallpaper with the darkening being edited', async ({ page }) => {
  // the demo has no picture: the preview's CSS loads it over the network, the settings page through the demo's fetch
  await page.route('**/api/wallpaper*', (route) => route.fulfill({ body: PNG, contentType: 'image/png' }));
  await page.goto('/settings/#general');
  await expect(page.locator('#save')).toBeVisible();
  await page.evaluate((b64) => {
    const original = window.fetch;
    const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
    window.fetch = (url, options) => String(url).startsWith('/api/wallpaper')
      ? Promise.resolve(new Response(bytes, { headers: { 'Content-Type': 'image/png' } })) : original(url, options);
    return refreshWallpaper();
  }, PNG.toString('base64'));
  await expect(page.locator('#wallpaperPreview')).toHaveAttribute('src', /^blob:/);
  await page.locator('#appearanceSettings').getByRole('button', { name: 'Show live appearance preview', exact: true }).click();
  const frame = page.frameLocator('iframe[title="Live appearance preview"]');
  await expect(frame.locator('#wallpaper')).toHaveCount(1);
  await page.locator('[data-bind="display.wallpaper.dim"]').fill('20');
  await page.locator('[data-bind="display.wallpaper.dim"]').dispatchEvent('input');
  await expect.poll(() => frame.locator('#wallpaper').evaluate((el) => el.style.getPropertyValue('--wp-dim'))).toBe('0.2');
});
