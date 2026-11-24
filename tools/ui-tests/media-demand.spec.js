// #523: the agent reads Now Playing every second only while a widget that shows it is on screen. The state frame
// carries the last reading; the Now playing and Synced lyrics widgets ask for fresh ones (GET /api/media) while
// their page is the one showing, and nothing asks from other pages or while the panel is hidden.
const { test, expect } = require('@playwright/test');

const PAGES = [
  { id: 'clock', title: 'Clock', icon: '', enabled: true, widgets: [{ id: 'c', type: 'clock', x: 0, y: 0, w: 12, h: 6 }] },
  { id: 'music', title: 'Music', icon: '', enabled: true, widgets: [{ id: 'm', type: 'media', x: 0, y: 0, w: 12, h: 6 }] },
  { id: 'stack', title: 'Stack', icon: '', enabled: true, widgets: [{ id: 's', type: 'stack', x: 0, y: 0, w: 12, h: 6,
    options: { widgets: [{ type: 'clock', enabled: true }, { type: 'lyrics', enabled: true }], interval: 0 } }] },
];

async function open(page) {
  await page.clock.install();
  await page.goto('/panel/');
  await page.evaluate((pages) => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    // no rule may move the panel to another page during the test
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, rules: [], shortcuts: cfg.shortcuts || [] }));
  }, PAGES);
  await page.reload();
  await expect(page.locator('.w-clock').first()).toBeVisible();
  await page.clock.fastForward(2000);
  // counts the panel's GET /api/media (the demo answers the agent's API inside window.fetch)
  await page.evaluate(() => {
    const inner = window.fetch;
    window.mediaGets = 0;
    window.fetch = (input, init = {}) => {
      const url = new URL(typeof input === 'string' ? input : input.url, location.href);
      if (url.pathname.endsWith('/api/media') && (init.method || 'GET').toUpperCase() === 'GET') window.mediaGets++;
      return inner(input, init);
    };
  });
}

const gets = (page) => page.evaluate(() => window.mediaGets);
const reset = (page) => page.evaluate(() => { window.mediaGets = 0; });
const go = (page, i) => page.evaluate(async (i) => (await import('/panel/main.js')).goPage(i, true), i);

test('only the page on screen with a media widget asks the agent for fresh readings', async ({ page }) => {
  await open(page);
  await go(page, 0);
  await reset(page);
  await page.clock.runFor(16_000);
  expect(await gets(page), 'a page without a media widget never asks').toBe(0);

  await go(page, 1);
  await expect.poll(() => gets(page), { message: 'the Now playing page asks at once' }).toBeGreaterThanOrEqual(1);
  await page.clock.runFor(16_000);
  // every 5 s: well within the agent's 15 s window, so the reader stays at one reading a second
  expect(await gets(page)).toBeGreaterThanOrEqual(3);

  // counted from before the switch: it asks at once, then on the 5 s ticks (one may fall within 4 s of that and wait)
  await reset(page);
  await go(page, 2);
  await page.clock.runFor(11_000);
  expect(await gets(page), 'a Smart Stack with a lyrics child asks too').toBeGreaterThanOrEqual(2);

  await go(page, 0);
  await reset(page);
  await page.clock.runFor(16_000);
  expect(await gets(page), 'back on the clock page nothing asks').toBe(0);
});

test('a hidden panel does not ask, and asks again when it is shown', async ({ page }) => {
  await open(page);
  await go(page, 1);
  await page.evaluate(() => {
    window.panelHidden = true;
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => window.panelHidden });
  });
  await reset(page);
  await page.clock.runFor(16_000);
  expect(await gets(page)).toBe(0);

  await page.evaluate(() => { window.panelHidden = false; document.dispatchEvent(new Event('visibilitychange')); });
  await expect.poll(() => gets(page), { message: 'shown again: it asks at once' }).toBeGreaterThanOrEqual(1);
});

test('the fresh reading reaches the Now playing widget', async ({ page }) => {
  await open(page);
  await go(page, 1);
  const media = page.locator('.w-media');
  await expect(media).toBeVisible();
  const before = await page.evaluate(async () => (await (await fetch('/agent/api/media')).json()).media.title);
  await expect(media).toContainText(before);
  await page.evaluate(() => fetch('/agent/api/media/next', { method: 'POST' }));
  const after = await page.evaluate(async () => (await (await fetch('/agent/api/media')).json()).media.title);
  expect(after).not.toBe(before);
  await page.clock.runFor(6000);
  await expect(media).toContainText(after);
});
