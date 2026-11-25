// The Phone bridge card in Settings: macOS only, off by default, the code switch needs the notification switch, and the
// test button asks the agent for a test notification (only for settings that are saved, because the agent reads the file).
const { test, expect } = require('@playwright/test');

const config = (bridge) => ({
  display: { language: 'en', theme: 'default' },
  pages: [],
  bridge: { clipboard: false, notifications: false, copyCodes: false, ...bridge },
});

async function open(page, cfg, os) {
  await page.goto('/panel/style.css');
  await page.evaluate((c) => localStorage.setItem('pb-demo-config', JSON.stringify(c)), cfg);
  await page.goto('/settings/#general');
  await page.evaluate(async (osName) => {
    const real = window.fetch;
    window.__bridgeCalls = [];
    window.fetch = async (input, init) => {
      const url = String(input);
      if (url.endsWith('/api/bridge/notification/test')) {
        window.__bridgeCalls.push(init?.method || 'GET');
        return new Response(JSON.stringify({ ok: true }), { headers: { 'Content-Type': 'application/json' } });
      }
      const r = await real(input, init);
      if (url.endsWith('/api/info')) {
        const d = await r.json();
        d.os = osName;
        return new Response(JSON.stringify(d), { headers: { 'Content-Type': 'application/json' } });
      }
      return r;
    };
    await loadInfo();
  }, os);
}

test('the card is shown on macOS and hidden elsewhere', async ({ page }) => {
  await open(page, config({}), 'windows');
  await expect(page.locator('#bridgeCard')).toBeHidden();
  await open(page, config({}), 'macos');
  await expect(page.locator('#bridgeCard')).toBeVisible();
  await expect(page.locator('[data-bind="bridge.clipboard"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="bridge.notifications"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="bridge.copyCodes"]')).not.toBeChecked();
});

test('the code switch and the test button wait for the notification switch', async ({ page }) => {
  await open(page, config({}), 'macos');
  await expect(page.locator('#bridgeCopyCodes')).toBeDisabled();
  await expect(page.locator('#bridgeTest')).toBeDisabled();
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.notifications"]') }).click();
  await expect(page.locator('[data-bind="bridge.notifications"]')).toBeChecked();
  await expect(page.locator('#bridgeCopyCodes')).toBeEnabled();
  await expect(page.locator('#bridgeTest')).toBeEnabled();
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.notifications"]') }).click();
  await expect(page.locator('#bridgeCopyCodes')).toBeDisabled();
});

test('saved notification settings come back on and enable the code switch', async ({ page }) => {
  await open(page, config({ notifications: true, copyCodes: true }), 'macos');
  await expect(page.locator('[data-bind="bridge.notifications"]')).toBeChecked();
  await expect(page.locator('[data-bind="bridge.copyCodes"]')).toBeChecked();
  await expect(page.locator('#bridgeCopyCodes')).toBeEnabled();
});

test('the test button posts to the agent once the settings are saved, and asks to save first otherwise', async ({ page }) => {
  await open(page, config({ notifications: true }), 'macos');
  await page.locator('#bridgeTest').click();
  await expect.poll(() => page.evaluate(() => window.__bridgeCalls)).toEqual(['POST']);
  // an unsaved change: the agent would test the old settings, so the page says to save first and sends nothing
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.clipboard"]') }).click();
  await page.locator('#bridgeTest').click();
  await expect(page.locator('#toast')).toContainText(/save/i);
  expect(await page.evaluate(() => window.__bridgeCalls)).toEqual(['POST']);
});
