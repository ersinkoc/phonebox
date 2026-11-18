// Big button options: "fill with the shortcut's picture" makes an uploaded picture the key's background while a
// program's own icon stays the centred icon, and "show the label" can hide the name. Without them the key looks as
// before.
const { test, expect } = require('@playwright/test');

const svg = (size) => `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 10 10"><rect width="10" height="10" fill="#e11d48"/></svg>`;

// iconUpload is what the agent reports for a picture the user uploaded; "app" is a program's own icon.
const SHORTCUTS = [
  { id: 'art', label: 'Artwork', icon: '🎨', color: '#6b7280', iconRev: 1, iconUpload: true },
  { id: 'app', label: 'Program', icon: '🖥️', color: '#6b7280', iconRev: 1 },
  { id: 'blank', label: '', icon: '🎨', color: '#6b7280', iconRev: 1, iconUpload: true },
  { id: 'iconset', label: 'Icon set', icon: '🎨', color: '#6b7280', iconRev: 1, iconUpload: true },
  { id: 'lamp', label: 'Lamp', icon: '💡', color: '#6b7280', iconRev: 1, iconUpload: true, toggle: true },
  // the config says uploaded, but the upload was removed before the picture was fetched
  { id: 'gone', label: 'Gone', icon: '🗑️', color: '#6b7280', iconRev: 1, iconUpload: true },
];
// "app" is large on purpose: on Linux a program's own icon can be a 256 px theme logo, and it still must not fill.
// "iconset" is a typical icon-set SVG, 24 px by its own attributes, which scales cleanly and so may fill.
const SIZES = { iconset: 24 };

/** Opens the panel on one page of Big buttons. Only the demo agent's settings are seeded, so the panel gets iconRev
 *  and iconUpload through the demo's config projection, as it does from the real agent. */
async function open(page, widgets) {
  // like the agent: upload=1 asks for an upload only; "app" has none, and "gone" no longer has one
  await page.route((url) => url.pathname.endsWith('/agent/api/shortcuts/icon'), (route) => {
    const q = new URL(route.request().url()).searchParams, id = q.get('id');
    if (q.get('upload') === '1' && (id === 'app' || id === 'gone')) return route.fulfill({ status: 404, body: '' });
    route.fulfill({ contentType: 'image/svg+xml', body: svg(SIZES[id] || 256) });
  });
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS,
    pages: [{ id: 'cover-test', title: 'Buttons', widgets: widgets.map((w, i) => ({ id: 'b' + i, type: 'button', ...w })) }],
  }));
  await page.goto('/panel/');
  return (i) => page.locator('[data-id="cover-test"] .w-button').nth(i).locator('.key');
}

test('an uploaded picture fills the Big button, with the label still shown', async ({ page }) => {
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

test('an uploaded icon-set SVG fills the key whatever size it declares', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'iconset', cover: true } }]))(0);
  await expect(key).toHaveClass(/k-cover/);
  await expect(key.locator('img.k-bg')).toBeVisible();
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

test("a program's own icon is not stretched across the key, however large", async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'app', cover: true } }]))(0);
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key).not.toHaveClass(/k-cover/);
  await expect(key.locator('img.k-bg')).toHaveCount(0);
});

test('an upload removed after the config was read leaves the centred icon, not a stretched program icon', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'gone', cover: true } }]))(0);
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key).not.toHaveClass(/k-cover/);
  await expect(key.locator('img.k-bg')).toHaveCount(0);
});

test('a filled key with a blank label is named by its id', async ({ page }) => {
  const key = await open(page, [
    { x: 0, y: 0, w: 4, h: 6, options: { id: 'blank', cover: true } },
    { x: 4, y: 0, w: 4, h: 6, options: { id: 'art', cover: true } },
  ]);
  await expect(key(0)).toHaveClass(/k-cover/);
  await expect(key(0)).toHaveAttribute('aria-label', 'blank');
  // a visible label names the key itself, with no aria-label over it
  await expect(key(1)).toHaveClass(/k-cover/);
  await expect(key(1)).not.toHaveAttribute('aria-label', /.*/);
});

test('without the options the picture stays the centred icon, with its label', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'art' } }]))(0);
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible();
  await expect(key).not.toHaveClass(/k-cover/);
  await expect(key.locator('.k-label')).toBeVisible();
});

test('a filled toggle key keeps its state dot above the picture, with a ring', async ({ page }) => {
  const key = (await open(page, [{ x: 0, y: 0, w: 4, h: 6, options: { id: 'lamp', cover: true } }]))(0);
  await expect(key).toHaveClass(/k-cover/);
  const dot = (prop) => key.evaluate((el, p) => getComputedStyle(el, '::before')[p], prop);
  expect(await dot('content')).not.toBe('none');
  expect(await dot('zIndex')).toBe('1');
  // the dot's box-shadow has a 0.2 s transition, so wait for the ring to settle
  await expect.poll(() => dot('boxShadow')).toContain('rgba(0, 0, 0, 0.55) 0px 0px 0px 2px');
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
