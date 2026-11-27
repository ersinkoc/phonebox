const { test, expect } = require('@playwright/test');

async function instrumentPushGets(page) {
  await page.evaluate(() => {
    window.__pushGets = 0;
    const original = window.fetch;
    window.fetch = (input, init = {}) => {
      const url = new URL(typeof input === 'string' ? input : input.url, location.href);
      if (url.pathname.endsWith('/api/push') && (init.method || 'GET').toUpperCase() === 'GET') window.__pushGets++;
      return original(input, init);
    };
  });
}

test.beforeEach(async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect.poll(async () => page.evaluate(async () => (await import('/panel/main.js')).pushVersion)).not.toBeNull();
});

test('a pushed value applies from its change event without refetching every value', async ({ page }) => {
  await instrumentPushGets(page);
  await page.evaluate(async () => { await fetch('/api/push/fps', { method: 'POST', body: '61' }); });
  await expect.poll(async () => page.evaluate(async () => (await import('/panel/main.js')).D.push.fps?.data)).toBe(61);
  await page.waitForTimeout(1200);
  expect(await page.evaluate(() => window.__pushGets)).toBe(0);
});

test('a break in the pushed-value version chain refetches the list', async ({ page }) => {
  await instrumentPushGets(page);
  await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    window.PhoneBoxDemo.emit('push', { baseVersion: main.pushVersion + 5, version: main.pushVersion + 6, key: 'lost', item: { data: 1, at: Date.now(), expiresAt: null } });
  });
  await expect.poll(async () => page.evaluate(() => window.__pushGets)).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(async () => (await import('/panel/main.js')).D.push.lost)).toBeUndefined();
});
