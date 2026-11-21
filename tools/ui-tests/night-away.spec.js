// The night screen when the computer is away (Settings › Display): after 2 minutes without the agent the panel shows
// the night screen, and leaves it when the agent answers again. Against the web demo with a mocked clock.
const { test, expect } = require('@playwright/test');

async function show(page, display = {}) {
  await page.clock.install();
  await page.goto('/panel/style.css');
  await page.evaluate((display) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0, ...display },
      pages: [{ id: 'one', title: 'One', widgets: [{ id: 'n1', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page one' } }] }],
    }));
  }, display);
  await page.goto('/panel/');
  await expect(page.locator('.w-note').first()).toBeVisible();
  // the panel's defaults have night hours (00:30-07:30) until the stored config arrives; wait for them to clear
  await expect(page.locator('body')).not.toHaveClass(/night/);
  // the demo answers the API in-page, so page.route doesn't see it: wrap its fetch to take the agent away
  await page.evaluate(() => {
    window.__down = false;
    const demo = window.fetch;
    window.fetch = (url, init) => window.__down && String(url).includes('/api/state')
      ? Promise.reject(new TypeError('Failed to fetch')) : demo(url, init);
  });
}

// fastForward fires each timer at most once per jump, so step 2 s first: the panel polls every second and notices
// the change then, as it would in real time
const away = async (page, down) => {
  await page.evaluate((down) => { window.__down = down; }, down);
  await page.clock.fastForward(2_000);
};
const body = (page) => page.locator('body');

test('the night screen comes on after 2 minutes without the computer', async ({ page }) => {
  await show(page);
  await away(page, true);
  await page.clock.fastForward(100_000);
  await expect(body(page)).toHaveClass(/offline/);
  await expect(body(page)).not.toHaveClass(/night/);
  await page.clock.fastForward(25_000);
  await expect(body(page)).toHaveClass(/night/);
});

test('a short blip does not bring the night screen', async ({ page }) => {
  await show(page);
  await away(page, true);
  await page.clock.fastForward(60_000);
  await away(page, false);
  await page.clock.fastForward(5_000);
  await expect(body(page)).not.toHaveClass(/offline/);
  await away(page, true);
  await page.clock.fastForward(100_000);
  await expect(body(page)).not.toHaveClass(/night/);
});

test('the night screen clears when the computer is back', async ({ page }) => {
  await show(page);
  await away(page, true);
  await page.clock.fastForward(125_000);
  await expect(body(page)).toHaveClass(/night/);
  await away(page, false);
  await page.clock.fastForward(3_000);
  await expect(body(page)).not.toHaveClass(/night/);
});

test('a touch wakes the panel, then the night screen returns while still away', async ({ page }) => {
  await show(page);
  await away(page, true);
  await page.clock.fastForward(125_000);
  await expect(body(page)).toHaveClass(/night/);
  const vp = page.viewportSize();
  await page.mouse.click(vp.width / 2, vp.height / 2);
  await expect(body(page)).not.toHaveClass(/night/);
  await page.clock.fastForward(35_000);
  await expect(body(page)).toHaveClass(/night/);
});

test('turned off, being away never brings the night screen', async ({ page }) => {
  await show(page, { nightWhenAway: false });
  await away(page, true);
  await page.clock.fastForward(300_000);
  await expect(body(page)).toHaveClass(/offline/);
  await expect(body(page)).not.toHaveClass(/night/);
});
