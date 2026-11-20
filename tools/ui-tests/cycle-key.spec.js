// A key that cycles through other keys: each tap runs the next one, and the key shows the one that ran last.
const { test, expect } = require('@playwright/test');

const SHORTCUTS = [
  { id: 'low', label: 'Low', icon: '🔈', color: '#6b7280', cmd: 'builtin:keys' },
  { id: 'high', label: 'High', icon: '🔊', color: '#6b7280', cmd: 'builtin:keys' },
  { id: 'vol', label: 'Volume', icon: '🎚️', color: '#6b7280', cmd: 'builtin:cycle', cycle: ['low', 'high'] },
];

async function open(page) {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS,
    pages: [{ id: 'cycle-test', title: 'Keys', widgets: [{ id: 'b0', type: 'button', x: 0, y: 0, w: 4, h: 6, options: { id: 'vol' } }] }],
  }));
  await page.goto('/panel/');
  return page.locator('[data-id="cycle-test"] .w-button .key');
}

test('a cycle key shows its own name until it is tapped, then the key that ran last', async ({ page }) => {
  const key = await open(page);
  await expect(key.locator('.k-label')).toHaveText('Volume');
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('Low');
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('High');
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('Low');
});

test('the key editor can turn a key into one that cycles, and lists the other keys', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#shortcuts');
  const keys = page.locator('#deck .key:not(.add)');
  await expect(keys.first()).toBeVisible();
  const what = page.locator('section.on select').first();
  await what.selectOption({ label: 'Cycle through other keys' });
  await page.getByRole('button', { name: 'Add a key' }).click();
  const rows = page.locator('section.on select').nth(1);
  await expect(rows).toBeVisible();
  const own = (await keys.first().locator('.kl').textContent()).trim();
  expect(await rows.locator('option').allTextContents()).not.toContain(own);
});

test("a cycle key shows the icon of the key that ran, and its own picture goes away", async ({ page }) => {
  const svg = (c) => `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 10 10"><rect width="10" height="10" fill="${c}"/></svg>`;
  // the cycle key has an uploaded picture that fills it; 'low' has a program icon (served for its own id only)
  await page.route((url) => url.pathname.endsWith('/agent/api/shortcuts/icon'), (route) => {
    const q = new URL(route.request().url()).searchParams, id = q.get('id');
    if (id === 'low' && q.get('upload') !== 'true') return route.fulfill({ contentType: 'image/svg+xml', body: svg('#16a34a') });
    if (id === 'vol' && q.get('upload') === 'true') return route.fulfill({ contentType: 'image/svg+xml', body: svg('#e11d48') });
    return route.fulfill({ status: 404, body: '' });
  });
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [],
    shortcuts: [
      { id: 'low', label: 'Low', icon: '🔈', color: '#6b7280', cmd: 'builtin:keys', iconRev: 1 },
      { id: 'high', label: 'High', icon: '🔊', color: '#6b7280', cmd: 'builtin:keys' },
      { id: 'vol', label: 'Volume', icon: '🎚️', color: '#6b7280', cmd: 'builtin:cycle', cycle: ['low', 'high'], iconRev: 1, iconUpload: true },
    ],
    pages: [{ id: 'cycle-test', title: 'Keys', widgets: [{ id: 'b0', type: 'button', x: 0, y: 0, w: 4, h: 6, options: { id: 'vol', cover: true } }] }],
  }));
  await page.goto('/panel/');
  const key = page.locator('[data-id="cycle-test"] .w-button .key');
  await expect(key.locator('img.k-bg')).toBeVisible();      // before the first tap: its own picture
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('Low');
  await expect(key.locator('img.k-bg')).toHaveCount(0);      // the key's own picture is gone
  await expect(key.locator('.k-icon.k-icon-img img')).toBeVisible(); // the program icon of the key that ran
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('High');
  await expect(key.locator('.k-icon.k-icon-img')).toHaveCount(0);    // 'High' has only its emoji
});

test('deleting a key takes it out of the cycles that list it', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#shortcuts');
  const keys = page.locator('#deck .key:not(.add)'), selects = page.locator('section.on select');
  await expect(keys.first()).toBeVisible();
  await keys.nth(0).click();
  await selects.first().selectOption({ label: 'Cycle through other keys' });
  const base = await selects.count();
  await page.getByRole('button', { name: 'Add a key' }).click();
  await expect(selects).toHaveCount(base + 1);                  // one row: the first other key
  await keys.nth(1).click();                                     // that key
  page.once('dialog', (d) => d.accept());
  await page.getByRole('button', { name: 'Delete' }).click();
  await keys.nth(0).click();                                     // back to the cycle key
  await expect(selects).toHaveCount(base);                       // its list no longer holds the deleted key
});
