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

test('"__proto__" is an ordinary pushed key: it shows, goes away when deleted and leaves the other values alone', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { language: 'en', idleReturnSec: 0 },
      pages: [{ id: 'one', title: 'One', enabled: true, widgets: [
        { id: 'p1', type: 'push', x: 0, y: 0, w: 6, h: 4, options: { key: '__proto__', title: 'Proto' } },
        { id: 'p2', type: 'push', x: 6, y: 0, w: 6, h: 4, options: { key: 'temperature', title: 'Temp' } },
      ] }],
    }));
  });
  await page.goto('/panel/');
  const proto = page.locator('.w-push', { hasText: 'Proto' });
  const temp = page.locator('.w-push', { hasText: 'Temp' });
  await expect(proto.locator('.push-empty')).toBeVisible();
  await expect(temp.locator('.pv')).toContainText('21.5');
  await expect.poll(async () => page.evaluate(async () => (await import('/panel/main.js')).pushVersion)).not.toBeNull();
  await instrumentPushGets(page);
  const store = () => page.evaluate(async () => {
    const { D } = await import('/panel/main.js');
    return { own: Object.prototype.hasOwnProperty.call(D.push, '__proto__'), plain: Object.getPrototypeOf(D.push) === Object.prototype,
      inherited: D.push.data === undefined, temperature: D.push.temperature?.data };
  });

  await page.evaluate(async () => { await fetch('/api/push/__proto__', { method: 'POST', body: '42' }); });
  await expect(proto.locator('.pv')).toHaveText('42');
  expect(await store()).toEqual({ own: true, plain: true, inherited: true, temperature: 21.5 });

  await page.evaluate(async () => { await fetch('/api/push/__proto__', { method: 'DELETE' }); });
  await expect(proto.locator('.push-empty')).toBeVisible();
  expect(await store()).toEqual({ own: false, plain: true, inherited: true, temperature: 21.5 });
  await expect(temp.locator('.pv')).toContainText('21.5');
  expect(await page.evaluate(() => window.__pushGets)).toBe(0); // both changes came from events, not a refetch
});

test('an older /push answer does not undo an event applied while it was on its way, and a restart still applies', async ({ page }) => {
  const out = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    const original = window.fetch;
    const gates = [];
    window.fetch = (input, init = {}) => {
      const url = new URL(typeof input === 'string' ? input : input.url, location.href);
      if (url.pathname.endsWith('/api/push') && (init.method || 'GET').toUpperCase() === 'GET') return new Promise((r) => gates.push(r));
      return original(input, init);
    };
    try {
      const v = m.pushVersion, before = { ...m.D.push };
      const stale = m.loadPush(); // asks at v
      m.applyPushChange({ baseVersion: v, version: v + 1, key: 'fps', item: { data: 99, at: Date.now(), expiresAt: null } });
      gates[0](Response.json({ version: v, items: before }));
      await stale;
      const kept = { fps: m.D.push.fps?.data, step: m.pushVersion - v };

      // a lower version with no event during the request is the agent starting over: it replaces the list
      const restart = m.loadPush();
      gates[gates.length - 1](Response.json({ version: 1, items: { fresh: { data: 'r', at: Date.now(), expiresAt: null } } }));
      await restart;
      return { kept, restarted: { fresh: m.D.push.fresh?.data, fps: m.D.push.fps?.data ?? null, version: m.pushVersion } };
    } finally {
      window.fetch = original;
    }
  });
  expect(out).toEqual({ kept: { fps: 99, step: 1 }, restarted: { fresh: 'r', fps: null, version: 1 } });
});
