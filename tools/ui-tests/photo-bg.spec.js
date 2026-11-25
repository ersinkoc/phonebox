const { test, expect } = require('@playwright/test');

// A page's photo background (Settings › Pages): one dimmed picture from the Photo frame's folder behind the
// page's widgets, per page. The demo answers /api/photos like PhotoFrame (a list only for a page with a photo
// background turned on or a photos widget), and /api/config carries the page's options like the agent's ToPublic.
const PAGE = (photoBg, id = 'bg') => ({ id, title: 'Background', enabled: true, photoBg, widgets: [
  { id: 'note', type: 'note', x: 0, y: 0, w: 6, h: 6, options: { text: 'Background sample' } } ] });

async function show(page, { pages } = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate((pages) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 }, pages }));
  }, pages);
  await page.goto('/panel/');
  await expect(page.locator('.w-note').first()).toBeVisible();
}

test('a page with a photo background shows a dimmed picture behind its widgets', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page, { pages: [PAGE({ enabled: true, dim: 60 })] });
  // one background layer of two crossfading pictures and a dim layer, in every page that has the option on
  await expect(page.locator('.page-bg')).toHaveCount(1);
  await expect(page.locator('.page-bg-img')).toHaveCount(2);
  const dim = page.locator('.page-bg-dim');
  expect(await dim.evaluate((n) => getComputedStyle(n).getPropertyValue('--pb-dim').trim())).toBe('0.6');
  expect(await dim.evaluate((n) => getComputedStyle(n).backgroundColor)).toBe('rgba(0, 0, 0, 0.6)');
  // the first picture arrives from /api/photos, a blob, and one layer is showing it
  const img = page.locator('.page-bg-img.on');
  await expect(img).toBeVisible();
  expect(await img.getAttribute('src')).toMatch(/^blob:/);
  expect(errors).toEqual([]);
});

test('a page without a photo background has no background layer', async ({ page }) => {
  await show(page, { pages: [PAGE(null)] });
  await expect(page.locator('.page-bg')).toHaveCount(0);
});

test('settings: the photo background switch and dim are saved with the page', async ({ page }) => {
  await show(page, { pages: [PAGE(null)] });
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.goto('/settings/#pages');
  const toggle = page.locator('#peHead input[type="checkbox"][aria-label="Photo background"]');
  const switchLabel = page.locator('#peHead label.check', { hasText: 'Photo background' });
  await expect(switchLabel).toBeVisible();
  await switchLabel.click();
  await expect(toggle).toBeChecked();
  const slider = page.locator('#peHead input[type="range"][aria-label="Dim"]');
  await expect(slider).toBeVisible();
  await slider.evaluate((el) => { el.value = '75'; el.dispatchEvent(new Event('input', { bubbles: true })); });
  await expect(slider).toHaveValue('75');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const saved = await page.evaluate(async () => (await (await fetch('/api/settings')).json()).pages[0].photoBg);
  expect(saved).toEqual({ enabled: true, dim: 75 });
});
