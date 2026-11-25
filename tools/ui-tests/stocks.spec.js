const { test, expect } = require('@playwright/test');

test('the stock watchlist and charts render and refresh', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.addInitScript(() => {
    const config = JSON.stringify({
      display: { language: 'en' }, night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'markets', title: 'Markets', widgets: [
        { id: 'list', type: 'stocks', x: 0, y: 0, w: 4, h: 3 },
        { id: 'charts', type: 'stocks', x: 4, y: 0, w: 4, h: 3, options: { mode: 'chart', colours: 'green-up' } },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/panel/');
  const charts = page.locator('.sc-grid');
  await expect(charts.locator('canvas')).toHaveCount(2);
  await expect(charts.locator('.sc-pct').first()).toHaveText('+0.51%');
  await expect(charts.locator('.sc-pct').first()).toHaveCSS('color', 'rgb(34, 197, 94)');
  await expect(page.locator('.fx-list')).toContainText('Shanghai Composite');
  await expect(page.locator('.fx-list .fx-v').first()).toHaveText('3,375.21');
  const minutePrices = await page.evaluate(async () => {
    const demo = code => fetch('/api/stocks/minutes?code=' + code).then(r => r.json());
    const [shanghai, tencent] = await Promise.all([demo('sh000001'), demo('hk00700')]);
    return [shanghai.points.at(-1)[1], tencent.points.at(-1)[1]];
  });
  expect(minutePrices).toEqual([3375.21, 410.8]);
  await page.evaluate(() => {
    const fetchDemo = window.fetch;
    window.minuteRequests = 0;
    window.fetch = (input, ...rest) => {
      if (String(input).includes('/api/stocks/minutes?')) {
        window.minuteRequests++;
        if (window.minuteFailure) return Promise.resolve(Response.json({ error: 'unavailable' }, { status: 404 }));
      }
      if (String(input).endsWith('/api/stocks')) {
        if (window.stockFailure === 'offline') return Promise.reject(new TypeError('offline'));
        return Promise.resolve(Response.json({ quotes: [
          { code: 'sh000001', name: 'Shanghai Composite', price: 3377, changePct: 0.8 },
          { code: 'hk00700', name: 'Tencent', price: 410.8, changePct: -0.29 },
        ], error: window.stockFailure === 'partial' ? 'incomplete quotes' : null }));
      }
      return fetchDemo(input, ...rest);
    };
  });
  await page.clock.fastForward(61_000);
  await expect(charts.locator('.sc-pct').first()).toHaveText('+0.80%');
  await expect.poll(() => page.evaluate(() => window.minuteRequests)).toBe(2);

  const listStatus = page.locator('.w-stocks .w-title .w-sub');
  const chartStatus = page.locator('.sc-status');
  const stale = 'Some data may be stale';
  await page.evaluate(() => { window.stockFailure = 'partial'; });
  await page.clock.fastForward(61_000);
  await expect(listStatus).toHaveText(stale);
  await expect(chartStatus).toHaveText(stale);
  await expect(charts.locator('canvas')).toHaveCount(2);
  await page.evaluate(() => { window.stockFailure = null; window.minuteFailure = true; });
  await page.clock.fastForward(61_000);
  await expect(listStatus).not.toHaveText(stale);
  await expect(chartStatus).toHaveText(stale);
  await page.evaluate(() => { window.minuteFailure = false; });
  await page.clock.fastForward(61_000);
  await expect(chartStatus).toBeHidden();
  await page.evaluate(() => { window.stockFailure = 'offline'; });
  await page.clock.fastForward(61_000);
  await expect(listStatus).toHaveText(stale);
  await expect(chartStatus).toHaveText(stale);
  await expect(page.locator('.fx-list')).toContainText('Shanghai Composite');
  expect(errors).toEqual([]);
});
