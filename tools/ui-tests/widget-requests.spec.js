const { test, expect } = require('@playwright/test');

for (const stale of [true, false]) {
  test('a stalled shared widget cache body releases the request with ' + (stale ? 'stale data' : 'no cache'), async ({ page }) => {
    await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
    await page.goto('/panel/');
    await page.evaluate(async stale => {
      const { cached } = await import('/panel/widgets/helpers.js');
      const { store } = await import('/panel/main.js');
      const url = 'https://fixture.invalid/widget-cache';
      const key = 'widget-cache-timeout-fixture';
      if (stale) store.set('nc:' + key, { at: Date.now() - 60_000, data: 'stale' });
      const f = window.widgetCacheFixture = { result: 'pending', calls: 0 };
      const original = window.fetch;
      window.fetch = (input, init) => {
        if (input !== url) return original(input, init);
        if (++f.calls > 1) return Promise.resolve(Response.json('fresh'));
        return Promise.resolve({ ok: true, json: () => new Promise(resolve => { f.release = resolve; }) });
      };
      f.load = () => cached(key, url, 30_000);
      f.stored = () => store.get('nc:' + key)?.data;
      f.pending = f.load().then(result => { f.result = result; }, () => { f.result = 'rejected'; });
    }, stale);
    await page.waitForFunction(() => typeof window.widgetCacheFixture.release === 'function');
    await page.clock.fastForward(10_001);
    const expected = stale ? 'stale' : 'rejected';
    const result = await page.evaluate(() => window.widgetCacheFixture.result);
    expect(result, 'EXPECTED: ' + expected + ' | ACTUAL: ' + result).toBe(expected);
    const recovered = await page.evaluate(async () => {
      const f = window.widgetCacheFixture;
      const data = await f.load();
      f.release('late');
      await f.pending;
      return { data, stored: f.stored(), calls: f.calls };
    });
    expect(recovered).toEqual({ data: 'fresh', stored: 'fresh', calls: 2 });
  });
}

test('normal widget cache responses are reused until expiry', async ({ page }) => {
  await page.goto('/panel/');
  const result = await page.evaluate(async () => {
    const { cached } = await import('/panel/widgets/helpers.js');
    const original = window.fetch;
    const url = 'https://fixture.invalid/widget-cache-control';
    let calls = 0;
    window.fetch = (input, init) => input === url
      ? Promise.resolve(Response.json(++calls))
      : original(input, init);
    const a = await cached('widget-cache-control', url, 30_000);
    const b = await cached('widget-cache-control', url, 30_000);
    return [a, b, calls];
  });
  expect(result).toEqual([1, 1, 1]);
});
