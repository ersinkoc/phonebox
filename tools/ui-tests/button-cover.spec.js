// Big button "fill with the shortcut's picture": a large picture becomes the key's background, a small one (a program's
// own icon) stays the centred icon, and without the option the key looks as before.
const { test, expect } = require('@playwright/test');

const svg = (size) => `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 10 10"><rect width="10" height="10" fill="#e11d48"/></svg>`;

test.beforeEach(async ({ page }) => {
  // The large picture for "art", a program-sized icon for "app".
  await page.route((url) => url.pathname.endsWith('/agent/api/shortcuts/icon'), (route) => {
    const id = new URL(route.request().url()).searchParams.get('id');
    route.fulfill({ contentType: 'image/svg+xml', body: svg(id === 'app' ? 32 : 256) });
  });
  await page.addInitScript(() => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [],
      shortcuts: [
        { id: 'art', label: 'Artwork', icon: '🎨', color: '#6b7280', iconRev: 1 },
        { id: 'app', label: 'Program', icon: '🖥️', color: '#6b7280', iconRev: 1 },
      ],
      pages: [{ id: 'cover-test', title: 'Buttons', widgets: [
        { id: 'b1', type: 'button', x: 0, y: 0, w: 4, h: 6, options: { id: 'art', cover: true } },
        { id: 'b2', type: 'button', x: 4, y: 0, w: 4, h: 6, options: { id: 'app', cover: true } },
        { id: 'b3', type: 'button', x: 8, y: 0, w: 4, h: 3, options: { id: 'art' } },
        { id: 'b4', type: 'button', x: 8, y: 3, w: 2, h: 3, options: { id: 'art', cover: true, showLabel: false } },
        { id: 'b5', type: 'button', x: 10, y: 3, w: 2, h: 3, options: { id: 'app', showLabel: false } },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.goto('/panel/');
});

test('a large picture fills the Big button, with the label still shown', async ({ page }) => {
  const key = page.locator('[data-id="cover-test"] .w-button').nth(0).locator('.key');
  await expect(key).toHaveClass(/k-cover/);
  const bg = key.locator('img.k-bg');
  await expect(bg).toBeVisible();
  await expect(key.locator('.k-icon')).toBeHidden();
  await expect(key.locator('.k-label')).toHaveText('Artwork');
  await expect(key.locator('.k-label')).toBeVisible();
  // the picture covers the whole key inside its 1 px border
  const [k, b] = await Promise.all([key.boundingBox(), bg.boundingBox()]);
  expect(k.width - b.width).toBeLessThanOrEqual(2);
  expect(k.height - b.height).toBeLessThanOrEqual(2);
});

test('a program-sized icon is not stretched across the key', async ({ page }) => {
  const key = page.locator('[data-id="cover-test"] .w-button').nth(1).locator('.key');
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key).not.toHaveClass(/k-cover/);
  await expect(key.locator('img.k-bg')).toHaveCount(0);
});

test('without the option the picture stays the centred icon', async ({ page }) => {
  const key = page.locator('[data-id="cover-test"] .w-button').nth(2).locator('.key');
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key).not.toHaveClass(/k-cover/);
  await expect(key.locator('.k-label')).toBeVisible();
});

test('a hidden label leaves only the picture, and screen readers still get the name', async ({ page }) => {
  const filled = page.locator('[data-id="cover-test"] .w-button').nth(3).locator('.key');
  await expect(filled).toHaveClass(/k-cover/);
  await expect(filled.locator('img.k-bg')).toBeVisible();
  await expect(filled.locator('.k-label')).toBeHidden();
  await expect(filled.locator('.k-shade')).toBeHidden();
  await expect(page.getByRole('button', { name: 'Artwork', exact: true })).toHaveCount(3);

  const icon = page.locator('[data-id="cover-test"] .w-button').nth(4).locator('.key');
  await expect(icon.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(icon.locator('.k-label')).toBeHidden();
  await expect(icon).toHaveAttribute('aria-label', 'Program');
});
