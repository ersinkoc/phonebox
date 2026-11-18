const { test, expect } = require('@playwright/test');

test('countdown uses the supplied seconds and rejects an invalid seconds field', async ({ page }) => {
  await page.clock.install({ time: new Date(2026, 0, 1, 12, 0, 0) });
  await page.addInitScript(() => {
    const targets = ['2026-01-01 12:00:42', '2026-01-01 12:01', '2026-01-01 12:00:69'];
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'countdowns', title: 'Countdowns', widgets: targets.map((target, i) => ({
        id: 'cd' + i, type: 'countdown', x: i * 4, y: 0, w: 4, h: 6, options: { target },
      })) }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.goto('/panel/');
  const values = page.locator('[data-id="countdowns"] .w-countdown .cd-v');
  await expect(values).toHaveCount(3);
  const actual = await values.evaluateAll(nodes => nodes.map(node => node.textContent));
  expect(actual[1]).not.toBe('—');
  expect(actual[0], 'EXPECTED: at least 30 seconds in the future | ACTUAL: ' + actual[0]).toMatch(/^0:[34]\d$/);
  expect(actual[2]).toBe('—');
});

test('countdown rejects nonexistent calendar dates and trailing text', async ({ page }) => {
  await page.addInitScript(() => {
    const targets = ['2026-02-31 10:00', '2026-12-20 10:00 typo', '2026-12-20 10:00'];
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'countdowns', title: 'Countdowns', widgets: targets.map((target, i) => ({
        id: 'cd' + i, type: 'countdown', x: i * 4, y: 0, w: 4, h: 6, options: { target },
      })) }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.goto('/panel/');
  const widgets = page.locator('[data-id="countdowns"] .w-countdown');
  await expect(widgets).toHaveCount(3);
  await expect(widgets.nth(0).locator('.cd-v')).toHaveText('—');
  await expect(widgets.nth(1).locator('.cd-v')).toHaveText('—');
  await expect(widgets.nth(2).locator('.cd-v')).not.toHaveText('—');
});

// Dates the countdown accepted before the stricter check must keep working: seconds in the time, a time the clocks
// skip when they spring forward, and a plain date on a day whose midnight is skipped.
for (const [zone, target] of [
  ['Europe/Tallinn', '2027-01-01 00:00:00'],
  ['Europe/Tallinn', '2027-03-28 03:30'],
  ['America/Santiago', '2026-09-06'],
]) {
  test.describe(`countdown in ${zone}`, () => {
    test.use({ timezoneId: zone });
    test(`still counts down to ${target}`, async ({ page }) => {
      await page.addInitScript((target) => {
        const config = JSON.stringify({
          night: { enabled: false }, rules: [], shortcuts: [],
          pages: [{ id: 'countdowns', title: 'Countdowns', widgets: [{ id: 'cd', type: 'countdown', x: 0, y: 0, w: 4, h: 6, options: { target } }] }],
        });
        localStorage.setItem('cfg', config);
        localStorage.setItem('pb-demo-config', config);
      }, target);
      await page.goto('/panel/');
      const widget = page.locator('[data-id="countdowns"] .w-countdown');
      await expect(widget).toHaveCount(1);
      await expect(widget.locator('.cd-v')).not.toHaveText('—');
    });
  });
}
