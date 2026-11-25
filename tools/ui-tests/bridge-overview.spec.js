// The page overview (long-press the page dots) is where the phone's own Phone bridge screen is reached from the panel.
// The button exists only inside the Android app (window.Android with openBridge) AND only when the agent says it has a
// bridge: macOS does, Windows and Linux do not (the screen could only say "unsupported"), and an agent from before the
// bridge says nothing. The agent's answer is bridge.supported in the public config.
const { test, expect } = require('@playwright/test');

/** The panel with a one-note page; [bridge] is what the agent's public config says about its bridge (undefined: no such key). */
async function openPanel(page, { app = true, bridge } = {}) {
  if (app) {
    await page.addInitScript(() => {
      window.__opened = [];
      window.Android = { openBridge: (key) => { window.__opened.push(key); } };
    });
  }
  await page.goto('/panel/style.css');
  await page.evaluate((b) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { language: 'en' },
      ...(b === undefined ? {} : { bridge: b }),
      pages: [{ id: 'one', title: 'One', enabled: true, widgets: [
        { id: 'n1', type: 'note', x: 0, y: 0, w: 6, h: 3, options: { text: 'loaded', color: '#fbbf24' } },
      ] }],
    }));
  }, bridge);
  await page.goto('/panel/');
  await expect(page.locator('.note-t')).toHaveText('loaded'); // the config has arrived
  await page.waitForFunction(() => typeof window.openOverview === 'function');
  await page.evaluate(() => window.openOverview());
  await expect(page.locator('#overview')).toBeVisible();
}

test('the overview offers Phone bridge inside the app when the agent has a bridge, and a tap opens the native screen', async ({ page }) => {
  await openPanel(page, { bridge: { clipboard: false, supported: true } });
  const button = page.locator('#ovBridge');
  await expect(button).toBeVisible();
  await expect(button).toContainText('Phone bridge');
  await button.click();
  expect(await page.evaluate(() => window.__opened.length)).toBe(1);
  await expect(page.locator('#overview')).toBeHidden();
});

test('a Windows or Linux agent has no bridge, so there is no Phone bridge button', async ({ page }) => {
  await openPanel(page, { bridge: { clipboard: false, supported: false } });
  await expect(page.locator('#ovBridge')).toBeHidden();
});

test('an agent from before the bridge says nothing about it, so there is no Phone bridge button', async ({ page }) => {
  await openPanel(page);
  await expect(page.locator('#ovBridge')).toBeHidden();
});

test('without the app there is no Phone bridge button, whatever the agent says', async ({ page }) => {
  await openPanel(page, { app: false, bridge: { clipboard: false, supported: true } });
  await expect(page.locator('#ovBridge')).toBeHidden();
});
