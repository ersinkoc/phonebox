const { test, expect } = require('@playwright/test');

test('refreshing an empty photo folder clears the last displayed photo', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/style.css');
  await page.evaluate(() => localStorage.setItem('pb-demo-config', JSON.stringify({
    night: { enabled: false },
    pages: [{ id: 'p', title: 'Photos', enabled: true, widgets: [
      { id: 'photo', type: 'photos', x: 0, y: 0, w: 6, h: 4, options: { interval: 5, shuffle: false } },
    ] }],
  })));
  await page.goto('/panel/');
  const live = page.locator('.pf-img.on[src]');
  await expect(live).toBeVisible();
  await page.evaluate(() => {
    const original = window.fetch;
    const fixture = window.emptyPhotoFixture = { empty: true, listRequests: 0 };
    window.fetch = (input, init) => {
      const url = typeof input === 'string' ? input : input.url;
      if (!fixture.empty) return original(input, init);
      if (url.includes('/api/photos/')) return Promise.resolve(new Response(null, { status: 404 }));
      if (!url.endsWith('/api/photos')) return original(input, init);
      fixture.listRequests++;
      return Promise.resolve(Response.json({ ids: [], error: 'No photos remain' }));
    };
  });
  await page.clock.fastForward(10 * 60_000 + 2_000);
  await expect(page.locator('.pf-note')).toHaveText('No photos remain');
  const actual = await live.count();
  expect(actual, `EXPECTED: 0 displayed photos | ACTUAL: ${actual} displayed photos`).toBe(0);
  expect(await page.evaluate(() => window.emptyPhotoFixture.listRequests)).toBeGreaterThan(0);

  await page.evaluate(() => { window.emptyPhotoFixture.empty = false; });
  await page.clock.fastForward(10 * 60_000 + 2_000);
  await expect(live).toBeVisible();
  await expect(page.locator('.pf-note')).toBeHidden();
});
