const { test, expect } = require('@playwright/test');

// Spectrum bars: the demo answers /api/spectrum like the agent (bands only for a widget on an enabled page).
async function show(page, options = {}, size = { w: 12, h: 3 }) {
  await page.goto('/panel/');
  await page.evaluate(({ options, size }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'sp', type: 'spectrum', x: 0, y: 0, ...size, options }] }] }));
  }, { options, size });
  await page.reload();
  await expect(page.locator('.w-spectrum canvas')).toBeVisible();
}

// how many pixels of the canvas are lit (bright enough to be a bar, not an unlit LED)
const lit = (page) => page.locator('.w-spectrum canvas').evaluate((c) => {
  const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
  let n = 0;
  for (let i = 3; i < d.length; i += 4) if (d[i] > 200) n++;
  return n;
});

for (const options of [{}, { style: 'fire', segments: false, peaks: false }, { style: 'single', color: '#ff00aa', bars: 8 }]) {
  test(`spectrum bars draw the PC's sound (${JSON.stringify(options)})`, async ({ page }) => {
    const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
    await show(page, options);
    await expect.poll(() => lit(page)).toBeGreaterThan(500);
    await expect(page.locator('.w-spectrum .spec-msg')).toBeHidden();
    // the bars move between frames
    const a = await lit(page);
    await expect.poll(async () => Math.abs((await lit(page)) - a)).toBeGreaterThan(0);
    expect(errors).toEqual([]);
  });
}

test('spectrum bars fit a 2 x 1 widget', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page, { title: 'Now' }, { w: 2, h: 1 });
  await expect.poll(() => lit(page)).toBeGreaterThan(20);
  expect(errors).toEqual([]);
});

test('spectrum bars say when the computer cannot capture its sound', async ({ page }) => {
  await page.addInitScript(() => {
    // an agent that is not on Windows: the widget is shown, but there is nothing to capture
    let inner = window.fetch;
    Object.defineProperty(window, 'fetch', {
      configurable: true,
      get: () => {
        const fallback = inner;
        return (input, init) => {
          const url = typeof input === 'string' ? input : input.url;
          if (url.includes('/api/spectrum')) {
            return Promise.resolve(new Response(JSON.stringify({ shown: true, active: false, fps: 30, b: null }),
              { status: 200, headers: { 'Content-Type': 'application/json' } }));
          }
          return fallback(input, init);
        };
      },
      set: (v) => { inner = v; },
    });
  });
  await show(page);
  await expect(page.locator('.w-spectrum .spec-msg')).toHaveText('Can’t capture PC audio (Windows only)', { timeout: 6000 });
});

// The agent captures audio only while a panel asks; a widget that is mounted but not showing must not ask.
async function asks(page, pages) {
  await page.goto('/panel/');
  await page.evaluate((pages) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 }, pages }));
  }, pages);
  await page.reload();
  await page.evaluate(() => {
    window.__asks = 0;
    const demo = window.fetch;
    window.fetch = (url, init) => { if (String(url).includes('/api/spectrum')) window.__asks++; return demo(url, init); };
  });
  await page.waitForTimeout(1500);
  return page.evaluate(() => window.__asks);
}
const spec = { id: 'sp', type: 'spectrum', x: 0, y: 0, w: 12, h: 3, options: {} };
const clock = { id: 'c', type: 'clock', x: 0, y: 0, w: 12, h: 3, options: {} };

test('spectrum bars on another page do not keep the agent capturing', async ({ page }) => {
  expect(await asks(page, [{ id: 'one', title: 'One', widgets: [clock] }, { id: 'two', title: 'Two', widgets: [spec] }])).toBe(0);
});

test('spectrum bars in a hidden Smart Stack card do not keep the agent capturing', async ({ page }) => {
  const stack = { id: 's', type: 'stack', x: 0, y: 0, w: 12, h: 6, options: { interval: 0, widgets: [{ type: 'clock' }, { type: 'spectrum' }] } };
  expect(await asks(page, [{ id: 'one', title: 'One', widgets: [stack] }])).toBe(0);
  // shown once its card is chosen
  await page.getByRole('button', { name: 'spectrum', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.__asks)).toBeGreaterThan(0);
});

test('spectrum bars under the night screen stop asking', async ({ page }) => {
  expect(await asks(page, [{ id: 'one', title: 'One', widgets: [spec] }])).toBeGreaterThan(0);
  await page.evaluate(() => document.body.classList.add('night'));
  await page.waitForTimeout(300); // a request already in flight may still finish
  const before = await page.evaluate(() => window.__asks);
  await page.waitForTimeout(1500);
  expect(await page.evaluate(() => window.__asks)).toBe(before);
});

test('spectrum bars on the next page do not animate the frames meant for this one', async ({ page }) => {
  await asks(page, [{ id: 'one', title: 'One', widgets: [spec] }, { id: 'two', title: 'Two', widgets: [{ ...spec, id: 'sp2' }] }]);
  const canvases = page.locator('.w-spectrum canvas');
  await expect(canvases).toHaveCount(2); // the neighbouring page is mounted too
  const litOf = (i) => canvases.nth(i).evaluate((c) => {
    const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
    let n = 0;
    for (let k = 3; k < d.length; k += 4) if (d[k] > 200) n++;
    return n;
  });
  await expect.poll(() => litOf(0)).toBeGreaterThan(500);
  expect(await litOf(1)).toBe(0);
});
