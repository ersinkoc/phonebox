const { test, expect } = require('@playwright/test');

// The checklist tracks done items by their text, so two rows with the same text toggle together.
// Each row is its own task, so they must toggle on their own.
async function layout(page, widgets, extra = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ widgets, extra }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, ...extra, pages: [{ id: 'test', title: 'Test', enabled: true, widgets }], shortcuts: [] }));
  }, { widgets, extra });
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
}

test('two rows with the same text toggle on their own', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, [{ id: 'cl', type: 'checklist', x: 0, y: 0, w: 12, h: 6, options: { items: 'Stretch\nStretch' } }]);
  const rows = page.locator('.cl-i');
  const head = page.locator('.w-sub');
  await expect(rows).toHaveCount(2);
  await expect(head).toHaveText('0 / 2');
  await rows.nth(0).click();
  await expect(head).toHaveText('1 / 2');
  await expect(rows.nth(0)).toHaveClass(/done/);
  await expect(rows.nth(1)).not.toHaveClass(/done/);
  await rows.nth(1).click();
  await expect(head).toHaveText('2 / 2');
  await expect(rows.nth(0)).toHaveClass(/done/);
  await expect(rows.nth(1)).toHaveClass(/done/);
  await rows.nth(0).click();
  await expect(head).toHaveText('1 / 2');
  await expect(rows.nth(0)).not.toHaveClass(/done/);
  await expect(rows.nth(1)).toHaveClass(/done/);
  expect(errors).toEqual([]);
});

test('a list of done texts saved before the fix still means the same rows', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'cl', type: 'checklist', x: 0, y: 0, w: 12, h: 6, options: { items: 'Stretch\nStretch\nWater the plants' } }] }], shortcuts: [] }));
    // saved by the old code, when done held the item texts
    localStorage.setItem('cl:test/cl', JSON.stringify({ day: new Date().toDateString(), done: ['Stretch', 'Water the plants'] }));
  });
  await page.goto('/panel/');
  const rows = page.locator('.cl-i');
  await expect(rows).toHaveCount(3);
  // the old code showed both Stretch rows ticked, so both stay ticked; from then on each row toggles on its own
  await expect(page.locator('.w-sub')).toHaveText('3 / 3');
  await expect(rows.nth(0)).toHaveClass(/done/);
  await expect(rows.nth(1)).toHaveClass(/done/);
  await expect(rows.nth(2)).toHaveClass(/done/);
  await rows.nth(1).click();
  await expect(page.locator('.w-sub')).toHaveText('2 / 3');
  await expect(rows.nth(0)).toHaveClass(/done/);
  await expect(rows.nth(1)).not.toHaveClass(/done/);
});
