// Big button options: "fill with the shortcut's picture" makes a large picture the key's background while a small one
// (a program's own icon) stays the centred icon, and "show the label" can hide the name. Without them the key looks as
// before.
const { test, expect } = require('@playwright/test');

const svg = (size) => `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 10 10"><rect width="10" height="10" fill="#e11d48"/></svg>`;

const SHORTCUTS = [
  { id: 'art', label: 'Artwork', icon: '🎨', color: '#6b7280', iconRev: 1 },
  { id: 'app', label: 'Program', icon: '🖥️', color: '#6b7280', iconRev: 1 },
  { id: 'blank', label: '', icon: '🎨', color: '#6b7280', iconRev: 1 },
];

/** Opens the panel on one page of Big buttons. Only the demo agent's settings are seeded, so the panel gets iconRev
 *  through the demo's config projection, as it does from the real agent. */
async function open(page, widgets) {
  // the large picture for every shortcut but "app", which gets a program-sized icon
  await page.route((url) => url.pathname.endsWith('/agent/api/shortcuts/icon'), (route) => {
    const id = new URL(route.request().url()).searchParams.get('id');
    route.fulfill({ contentType: 'image/svg+xml', body: svg(id === 'app' ? 32 : 256) });
  });
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS,
    pages: [{ id: 'cover-test', title: 'Buttons', widgets: widgets.map((w, i) => ({ id: 'b' + i, type: 'button', ...w })) }],
  }));
  await page.goto('/panel/');
  return (i) => page.locator('[data-id="cover-test"] .w-button').nth(i).locator('.key');
}

test('a large picture fills the Big button, with the label still shown', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'art', cover: true } }]))(0);
  await expect(key).toHaveClass(/k-cover/);
  const bg = key.locator('img.k-bg');
  await expect(bg).toBeVisible();
  await expect(key.locator('.k-icon')).toBeHidden();
  await expect(key.locator('.k-label')).toHaveText('Artwork');
  await expect(key.locator('.k-label')).toBeVisible();
  // the picture reaches all four inner edges of the key (inside its 1 px border)
  const [k, b] = await Promise.all([key.boundingBox(), bg.boundingBox()]);
  for (const gap of [b.x - k.x, b.y - k.y, (k.x + k.width) - (b.x + b.width), (k.y + k.height) - (b.y + b.height)]) {
    expect(gap).toBeGreaterThanOrEqual(0);
    expect(gap).toBeLessThanOrEqual(1.5);
  }
});

test('a wide, short filled key keeps its label inside the key', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 12, h: 1, options: { id: 'art', cover: true } }]))(0);
  await expect(key).toHaveClass(/k-cover/);
  const label = key.locator('.k-label');
  await expect(label).toBeVisible();
  const [k, l] = await Promise.all([key.boundingBox(), label.boundingBox()]);
  expect(l.y).toBeGreaterThanOrEqual(k.y);
  expect(l.y + l.height).toBeLessThanOrEqual(k.y + k.height);
});

test('a program-sized icon is not stretched across the key', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'app', cover: true } }]))(0);
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key).not.toHaveClass(/k-cover/);
  await expect(key.locator('img.k-bg')).toHaveCount(0);
});

test('without the options the picture stays the centred icon, with its label', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'art' } }]))(0);
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key).not.toHaveClass(/k-cover/);
  await expect(key.locator('.k-label')).toBeVisible();
});

test('a hidden label leaves only the picture or icon, and the button keeps a name', async ({ page }) => {
  const key = await open(page, [
    { x: 0, y: 0, w: 4, h: 6, options: { id: 'art', cover: true, showLabel: false } },
    { x: 4, y: 0, w: 4, h: 6, options: { id: 'app', showLabel: false } },
    { x: 8, y: 0, w: 4, h: 6, options: { id: 'blank', cover: true, showLabel: false } },
  ]);
  await expect(key(0)).toHaveClass(/k-cover/);
  await expect(key(0).locator('img.k-bg')).toBeVisible();
  await expect(key(0).locator('.k-label')).toBeHidden();
  await expect(key(0).locator('.k-shade')).toBeHidden();
  await expect(key(0)).toHaveAttribute('aria-label', 'Artwork');

  await expect(key(1).locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key(1).locator('.k-label')).toBeHidden();
  await expect(key(1)).toHaveAttribute('aria-label', 'Program');

  // a shortcut saved without a label is named by its id
  await expect(key(2)).toHaveAttribute('aria-label', 'blank');
});
