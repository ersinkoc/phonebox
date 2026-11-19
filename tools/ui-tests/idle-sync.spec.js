const { test, expect } = require('@playwright/test');

// The Android shell tells the panel when nobody can see it (WebView.onPause() does not stop JavaScript). While paused
// the panel must not poll the agent or refresh; on resume it fetches again at once.
test('a paused panel stops polling the agent and a resumed one fetches again', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.evaluate(() => {
    window.__calls = [];
    const original = window.fetch;
    window.fetch = (url, ...rest) => {
      window.__calls.push(String(url));
      return original(url, ...rest);
    };
  });
  const calls = () => page.evaluate(() => window.__calls.splice(0).filter(u => /\/(state|approvals|work|quotas|calendar|config|push|procs)/.test(u)));

  await page.clock.runFor(4000);
  expect((await calls()).length, 'EXPECTED: the visible panel polls | ACTUAL: no request').toBeGreaterThan(0);

  await page.evaluate(() => window.onAppPause(true));
  await calls();
  await page.clock.runFor(70_000);
  expect(await calls(), 'EXPECTED: nothing while paused').toEqual([]);

  await page.evaluate(() => window.onAppPause(false));
  await page.clock.runFor(3000);
  expect((await calls()).length, 'EXPECTED: polling again after resume').toBeGreaterThan(0);
});
