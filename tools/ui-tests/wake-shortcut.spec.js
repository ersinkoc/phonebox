// Settings › Shortcuts: the Wake-on-LAN preset fills the command with builtin:wake (the MAC goes in the arguments).
const { test, expect } = require('@playwright/test');

test('the shortcut presets offer a Wake-on-LAN key', async ({ page }) => {
  await page.goto('/settings/#shortcuts');
  await page.locator('.key.add').click();
  const preset = page.locator('select', { has: page.locator('option[value="builtin:lock"]') }).first();
  await expect(preset.locator('option[value="builtin:wake"]')).toHaveCount(1);
  await expect(preset.locator('option[value="builtin:wake"]')).toContainText('Wake-on-LAN');
  await preset.selectOption('builtin:wake');
  // the preset sets the input's value property, which an attribute selector cannot see
  await expect.poll(() => page.evaluate(() => [...document.querySelectorAll('input')].some((i) => i.value === 'builtin:wake'))).toBe(true);
});
