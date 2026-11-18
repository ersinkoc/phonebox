const { test, expect } = require('@playwright/test');

async function openPanel(page) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
}

test('a late config response preserves the newer settings and offline cache', async ({ page }) => {
  await openPanel(page);
  const result = await page.evaluate(async () => {
    const panel = await import('/panel/main.js');
    const original = window.fetch;
    const replies = [];
    window.fetch = (url, ...rest) => String(url).endsWith('/config')
      ? new Promise(resolve => replies.push(resolve))
      : original(url, ...rest);
    const complete = (version, theme) => replies.shift()(Response.json({ version, config: { display: { theme } } }));
    try {
      const initial = panel.loadConfig();
      complete(1, 'warm');
      await initial;
      const control = document.body.dataset.theme;
      const old = panel.loadConfig();
      const fresh = panel.loadConfig();
      const finishOld = replies.shift();
      complete(3, 'midnight');
      await fresh;
      finishOld(Response.json({ version: 2, config: { display: { theme: 'graphite' } } }));
      await old;
      return { control, version: panel.cfgVersion, theme: document.body.dataset.theme, cached: panel.store.get('cfg').display.theme };
    } finally {
      window.fetch = original;
    }
  });
  expect(result.control).toBe('warm');
  expect(result, `CONTROL: standalone config rendered warm. EXPECTED: version 3, midnight theme and cache | ACTUAL: ${JSON.stringify(result)}`)
    .toEqual({ control: 'warm', version: 3, theme: 'midnight', cached: 'midnight' });
});

test('a failed newer config request still lets the pending request apply', async ({ page }) => {
  await openPanel(page);
  const version = await page.evaluate(async () => {
    const panel = await import('/panel/main.js');
    const original = window.fetch;
    const replies = [];
    window.fetch = (url, ...rest) => String(url).endsWith('/config')
      ? new Promise(resolve => replies.push(resolve))
      : original(url, ...rest);
    try {
      const pending = panel.loadConfig();
      const failed = panel.loadConfig();
      replies.pop()(new Response(null, { status: 503 }));
      await failed;
      replies.pop()(Response.json({ version: 4, config: {} }));
      await pending;
      return panel.cfgVersion;
    } finally {
      window.fetch = original;
    }
  });
  expect(version).toBe(4);
});

test('a fresh request can load configuration from a restarted agent', async ({ page }) => {
  await openPanel(page);
  const version = await page.evaluate(async () => {
    const panel = await import('/panel/main.js');
    const original = window.fetch;
    let version = 10;
    window.fetch = (url, ...rest) => String(url).endsWith('/config')
      ? Promise.resolve(Response.json({ version, config: {} }))
      : original(url, ...rest);
    try {
      await panel.loadConfig();
      if (panel.cfgVersion !== 10) throw Error('Initial version did not apply');
      version = 1;
      await panel.loadConfig();
      return panel.cfgVersion;
    } finally {
      window.fetch = original;
    }
  });
  expect(version).toBe(1);
});
