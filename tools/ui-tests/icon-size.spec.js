// A shortcut's icon size (settings › Shortcuts › Icon size): the key gets a size class, and "max" keeps the icon inside the key.
const { test, expect } = require('@playwright/test');

async function open(page, iconSize) {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [],
    shortcuts: [{ id: 'k', label: 'Key', icon: '🎚️', color: '#6b7280', cmd: 'builtin:keys', ...(iconSize ? { iconSize } : {}) }],
    pages: [{ id: 'size-test', title: 'Keys', widgets: [{ id: 'b0', type: 'button', x: 0, y: 0, w: 4, h: 6, options: { id: 'k' } }] }],
  }));
  await page.goto('/panel/');
  return page.locator('[data-id="size-test"] .w-button .key');
}

for (const size of ['m', 'l', 'max']) {
  test(`a key with icon size "${size}" gets its size class`, async ({ page }) => {
    const key = await open(page, size);
    await expect(key).toHaveClass(new RegExp(`\\bk-isz-${size}\\b`));
  });
}

test('a key without an icon size keeps the default look', async ({ page }) => {
  const key = await open(page);
  await expect(key).not.toHaveClass(/k-isz-/);
});

test('the maximum size keeps an emoji icon inside its key', async ({ page }) => {
  const key = await open(page, 'max');
  const icon = key.locator('.k-icon');
  await expect(icon).toBeVisible();
  const [k, i] = await Promise.all([key.boundingBox(), icon.boundingBox()]);
  expect(i.width).toBeLessThanOrEqual(k.width + 1);
  expect(i.height).toBeLessThanOrEqual(k.height + 1);
});

// The sizes are measured against the key, so they grow in order even on a short, wide key, where the maximum
// used to come out smaller than "large" (its padding followed the key's width) and large ran into the label.
test('the sizes grow in order and stay inside a short, wide key', async ({ page }) => {
  const sizes = ['', 'm', 'l', 'max'];
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [],
    shortcuts: sizes.map((s) => ({ id: 'k' + s, label: 'Key', icon: '🎚️', color: '#6b7280', cmd: 'builtin:keys', ...(s ? { iconSize: s } : {}) })),
    pages: [{ id: 'size-test', title: 'Keys', widgets: sizes.map((s, i) => ({ id: 'b' + i, type: 'button', x: i * 3, y: 0, w: 3, h: 2, options: { id: 'k' + s } })) }],
  }));
  await page.setViewportSize({ width: 1000, height: 330 });
  await page.goto('/panel/');
  const keys = page.locator('[data-id="size-test"] .w-button .key');
  await expect(keys).toHaveCount(4);
  const heights = [];
  for (let i = 0; i < 4; i++) {
    const [k, icon, label] = await Promise.all([keys.nth(i).boundingBox(), keys.nth(i).locator('.k-icon svg').boundingBox(), keys.nth(i).locator('.k-label').boundingBox()]);
    heights.push(icon.height);
    expect(icon.y).toBeGreaterThanOrEqual(k.y - 1);
    expect(icon.y + icon.height).toBeLessThanOrEqual(label.y + 1); // the icon never runs into the label
  }
  for (let i = 1; i < 4; i++) expect(heights[i]).toBeGreaterThan(heights[i - 1]);
});

// Even on a very short key (about 50px) "maximum" is never smaller than "large".
test('the maximum is not smaller than large on a very short key', async ({ page }) => {
  const sizes = ['l', 'max'];
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [],
    shortcuts: sizes.map((s) => ({ id: 'k' + s, label: 'Key', icon: '🎚️', color: '#6b7280', cmd: 'builtin:keys', iconSize: s })),
    pages: [{ id: 'size-test', title: 'Keys', widgets: sizes.map((s, i) => ({ id: 'b' + i, type: 'button', x: i * 6, y: 0, w: 6, h: 1, options: { id: 'k' + s } })) }],
  }));
  await page.setViewportSize({ width: 1000, height: 330 });
  await page.goto('/panel/');
  const keys = page.locator('[data-id="size-test"] .w-button .key');
  await expect(keys).toHaveCount(2);
  const [large, max] = await Promise.all([0, 1].map((i) => keys.nth(i).locator('.k-icon svg').boundingBox()));
  expect(max.height).toBeGreaterThanOrEqual(large.height - 1);
});

// A size the panel doesn't know (a hand-edited or older config) is ignored instead of breaking the key.
test('an unknown icon size is ignored', async ({ page }) => {
  const key = await open(page, 'huge');
  await expect(key.locator('.k-label')).toHaveText('Key');
  await expect(key).not.toHaveClass(/k-isz-/);
});
