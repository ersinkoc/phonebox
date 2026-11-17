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
