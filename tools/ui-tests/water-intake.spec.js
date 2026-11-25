const { test, expect } = require('@playwright/test');

test.use({ timezoneId: 'UTC' });

async function openWidget(page) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.addInitScript(() => {
    const config = JSON.stringify({
      display: { language: 'en' }, night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'wellness', title: 'Wellness', widgets: [{
        id: 'glass', type: 'water-intake', x: 0, y: 0, w: 4, h: 3,
        options: { goalMl: 600, reminderMinutes: 1 },
      }] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
    window.waterBuzzes = [];
    window.Android = { buzz: pattern => window.waterBuzzes.push(pattern), haptic() {} };
  });
  await page.goto('/panel/');
  const widget = page.locator('.w-water-intake');
  await expect(widget.getByRole('progressbar')).toBeVisible();
  return widget;
}

test('quick and custom amounts persist locally and reset at midnight', async ({ page }) => {
  const widget = await openWidget(page);
  await expect(widget.locator('.water-total')).toHaveText('0 ml / 600 ml');
  await widget.getByRole('button', { name: '+250 ml' }).click();
  await widget.getByRole('spinbutton', { name: 'Custom amount (ml)' }).fill('125');
  await widget.getByRole('button', { name: 'Log water' }).click();
  await expect(widget.getByRole('progressbar')).toHaveJSProperty('value', 375);
  await expect(widget.locator('.water-remaining')).toHaveText('225 ml remaining');
  await page.reload();
  await expect(widget.locator('.water-total')).toHaveText('375 ml / 600 ml');
  await page.clock.setSystemTime(new Date('2026-01-02T00:00:00Z'));
  await page.clock.runFor(1100);
  await expect(widget.locator('.water-total')).toHaveText('0 ml / 600 ml');
});

test('a reminder fires on schedule and stops after reaching the goal', async ({ page }) => {
  const widget = await openWidget(page);
  await page.clock.fastForward(61_000);
  await expect.poll(() => page.evaluate(() => window.waterBuzzes.length)).toBe(1);
  await widget.getByRole('button', { name: '+500 ml' }).click();
  await widget.getByRole('button', { name: '+250 ml' }).click();
  await expect(widget.getByRole('progressbar')).toHaveJSProperty('value', 600);
  await expect(widget.locator('.water-remaining')).toHaveText('0 ml remaining');
  await page.clock.fastForward(61_000);
  expect(await page.evaluate(() => window.waterBuzzes.length)).toBe(1);
});
