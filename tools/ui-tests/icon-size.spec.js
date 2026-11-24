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
  // the .k-icon box is width: 100% with overflow: hidden at "max", so it can never be bigger than the key: measure the emoji itself
  const icon = key.locator('.k-icon svg');
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

// Non-text values (a hand-edited config) are ignored like the agent does, instead of throwing or being coerced ("m" in an array).
for (const [name, value] of [['an array', ['m']], ['an object with a null toString', { toString: null }], ['a number', 42]]) {
  test(`an icon size given as ${name} is ignored`, async ({ page }) => {
    const key = await open(page, value);
    await expect(key.locator('.k-label')).toHaveText('Key');
    await expect(key).not.toHaveClass(/k-isz-/);
  });
}

// On a very short key (about 40px) a picture at "maximum" is not smaller than at "large" either.
test('the maximum picture is not smaller than large on a very short key', async ({ page }) => {
  const sizes = ['l', 'max'];
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [],
    shortcuts: sizes.map((s) => ({ id: 'k' + s, label: 'Key', icon: '🎚️', color: '#6b7280', cmd: 'builtin:keys', iconSize: s, iconRev: 1 })),
    pages: [{ id: 'size-test', title: 'Keys', widgets: sizes.map((s, i) => ({ id: 'b' + i, type: 'button', x: i * 6, y: 0, w: 6, h: 1, options: { id: 'k' + s } })) }],
  }));
  await page.route('**/agent/api/shortcuts/icon**', (r) => r.fulfill({ contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="red"/></svg>' }));
  await page.setViewportSize({ width: 1000, height: 250 });
  await page.goto('/panel/');
  const keys = page.locator('[data-id="size-test"] .w-button .key');
  await expect(keys).toHaveCount(2);
  const [large, max] = await Promise.all([0, 1].map((i) => keys.nth(i).locator('.k-icon img').boundingBox()));
  expect(max.height).toBeGreaterThanOrEqual(large.height - 1);
});

// ---- which pictures the size applies to, and what it leaves alone ----

const PICTURE = '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 10 10"><rect width="10" height="10" fill="#e11d48"/></svg>';

/** Opens a page with the given shortcuts and widgets; every icon request is answered with a large picture. */
async function openPage(page, shortcuts, widgets, viewport = { width: 1000, height: 700 }) {
  await page.route((url) => url.pathname.endsWith('/agent/api/shortcuts/icon'), (r) => r.fulfill({ contentType: 'image/svg+xml', body: PICTURE }));
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts,
    pages: [{ id: 'size-test', title: 'Keys', widgets }],
  }));
  await page.setViewportSize(viewport);
  await page.goto('/panel/');
  return page.locator('[data-id="size-test"] .key');
}
const px = (loc, prop) => loc.evaluate((el, p) => parseFloat(getComputedStyle(el)[p]), prop);

// An uploaded picture is sized by the icon size; a program's own icon (32 px on Windows, 64 px on macOS) keeps its size,
// as it does on a key without a size, instead of being blown up and blurred.
test('the icon size resizes an uploaded picture and leaves a program icon alone', async ({ page }) => {
  const sc = (id, extra) => ({ id, label: id, icon: '🎨', color: '#6b7280', cmd: 'builtin:keys', iconRev: 1, ...extra });
  const keys = await openPage(page,
    [sc('prog'), sc('prog-max', { iconSize: 'max' }), sc('up', { iconUpload: true }), sc('up-max', { iconUpload: true, iconSize: 'max' })],
    ['prog', 'prog-max', 'up', 'up-max'].map((id, i) => ({ id: 'b' + i, type: 'button', x: i * 3, y: 0, w: 3, h: 4, options: { id } })));
  await expect(keys).toHaveCount(4);
  const img = (i) => keys.nth(i).locator('.k-icon img');
  for (let i = 0; i < 4; i++) await expect(img(i)).toBeVisible();
  const [prog, progMax, up, upMax] = await Promise.all([0, 1, 2, 3].map((i) => img(i).boundingBox()));
  expect(Math.abs(progMax.width - prog.width)).toBeLessThanOrEqual(1);
  expect(Math.abs(progMax.height - prog.height)).toBeLessThanOrEqual(1);
  expect(upMax.height).toBeGreaterThan(up.height + 10);
  await expect(keys.nth(1).locator('.k-icon')).not.toHaveClass(/k-icon-up/);
  await expect(keys.nth(3).locator('.k-icon')).toHaveClass(/k-icon-up/);
});

// In a Shortcuts grid a sized key is a container of its own, which must not change what its name and a program's icon
// measure: they keep the size its unsized neighbours get from the widget.
test('in a Shortcuts grid a sized key keeps the label and program icon size of its neighbours', async ({ page }) => {
  const sc = (id, extra) => ({ id, label: 'Name ' + id, icon: '🎨', color: '#6b7280', cmd: 'builtin:keys', iconRev: 1, ...extra });
  const ids = ['a', 'b', 'c', 'd'];
  const keys = await openPage(page,
    [sc('a'), sc('b', { iconSize: 'm' }), sc('c', { iconSize: 'l' }), sc('d', { iconSize: 'max' })],
    [{ id: 'g', type: 'shortcuts', x: 0, y: 0, w: 12, h: 3, options: { ids, columns: 4 } }]);
  await expect(keys).toHaveCount(4);
  for (let i = 0; i < 4; i++) await expect(keys.nth(i).locator('.k-icon img')).toBeVisible();
  const label = await px(keys.nth(0).locator('.k-label'), 'fontSize');
  const imgW = (await keys.nth(0).locator('.k-icon img').boundingBox()).width;
  for (let i = 1; i < 4; i++) {
    expect(await px(keys.nth(i).locator('.k-label'), 'fontSize')).toBeCloseTo(label, 1);
    expect((await keys.nth(i).locator('.k-icon img').boundingBox()).width).toBeCloseTo(imgW, 0);
  }
});

// With "fill with the picture" the icon is hidden, so "maximum" does nothing and the label keeps its Big button size.
test('a Big button filled with the picture keeps its label size at the maximum', async ({ page }) => {
  const sc = (id, extra) => ({ id, label: 'Art', icon: '🎨', color: '#6b7280', cmd: 'builtin:keys', iconRev: 1, iconUpload: true, ...extra });
  const keys = await openPage(page, [sc('plain'), sc('big', { iconSize: 'max' })],
    ['plain', 'big'].map((id, i) => ({ id: 'b' + i, type: 'button', x: i * 6, y: 0, w: 6, h: 4, options: { id, cover: true } })));
  await expect(keys).toHaveCount(2);
  await expect(keys.nth(1)).toHaveClass(/k-cover/);
  expect(await px(keys.nth(1).locator('.k-label'), 'fontSize')).toBeCloseTo(await px(keys.nth(0).locator('.k-label'), 'fontSize'), 1);
});

// A cycle key takes the size of the key it shows, not of its own, and a picture that is not an upload takes none.
test('a cycle key follows the icon size of the key it shows', async ({ page }) => {
  const sc = (id, extra) => ({ id, label: id, icon: '🎨', color: '#6b7280', cmd: 'builtin:keys', ...extra });
  const keys = await openPage(page,
    [sc('low', { iconSize: 'l', iconRev: 1, iconUpload: true }), sc('high', { iconRev: 1 }), sc('vol', { cmd: 'builtin:cycle', cycle: ['low', 'high'], iconSize: 'max' })],
    [{ id: 'b0', type: 'button', x: 0, y: 0, w: 4, h: 6, options: { id: 'vol' } }]);
  const key = keys.first();
  await expect(key).toHaveClass(/k-isz-max/);
  await key.click();                               // shows "low": an upload at size large
  await expect(key.locator('.k-label')).toHaveText('low');
  await expect(key).toHaveClass(/k-isz-l/);
  await expect(key).not.toHaveClass(/k-isz-max/);
  await expect(key.locator('.k-icon')).toHaveClass(/k-icon-up/);
  await key.click();                               // shows "high": a program icon, no size
  await expect(key.locator('.k-label')).toHaveText('high');
  await expect(key).not.toHaveClass(/k-isz-/);
  await expect(key.locator('.k-icon')).not.toHaveClass(/k-icon-up/);
});
