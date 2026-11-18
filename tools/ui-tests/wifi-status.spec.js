const { test, expect } = require('@playwright/test');

// #239: the line next to "Pair a phone over Wi-Fi" said "Off" right after the switch was turned on, so it looked as if
// turning it on had not worked; it needs Save and a restart, and the line says so as soon as the switch moves.
test('the Wi-Fi status line follows the switch before it is saved', async ({ page }) => {
  await page.goto('/settings/#general');
  const status = page.locator('#lanStatus');
  const toggle = page.locator('[data-bind="lan.enabled"]');
  const flip = () => page.locator('label.switch', { has: toggle }).click(); // the input itself is hidden behind the switch
  await expect(status).toHaveText('Listening on port 8766 (HTTPS)');

  await flip();
  await expect(status).toHaveText('Click Save to turn it off');
  // saved as off: Wi-Fi requests are refused at once, while the port is released only by the restart
  await page.evaluate(() => { lanInfo = { ...lanInfo, configured: false, restartNeeded: true }; renderLanStatus(); });
  await expect(status).toHaveText('Off');

  // an agent with Wi-Fi off, as on a fresh install
  await page.evaluate(() => { lanInfo = { ...lanInfo, configured: false, active: false }; renderLanStatus(); });
  await expect(status).toHaveText('Off');
  await flip();
  await expect(status).toHaveText('Click Save, then Restart to apply at the top to start listening');
  await flip();
  await expect(status).toHaveText('Off');
});
