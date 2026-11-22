// The page bar (Settings › Display › Page navigation): the dots shown as buttons with each page's icon and name,
// always (with its own strip of the screen, at the bottom or down a side) or while the dots are being used.
const { test, expect } = require('@playwright/test');

const PAGES = ['Home', 'Music', 'Stats', 'Games', 'Agenda'].map((title, i) => ({ id: 'p' + i, title, icon: '',
  widgets: [{ id: 'n' + i, type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: title } }] }));

async function show(page, display, clock = false) {
  await page.setViewportSize({ width: 800, height: 400 });
  if (clock) await page.clock.install();
  await page.goto('/panel/style.css');
  await page.evaluate(({ pages, display }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], pages,
      display: { idleReturnSec: 0, minDwellSec: 0, switchCountdownSec: 0, ...display } }));
  }, { pages: PAGES, display });
  await page.goto('/panel/');
  await expect(page.locator('#dots .dot')).toHaveCount(PAGES.length);
  await expect(page.locator('body')).not.toHaveClass(/night/); // the default night hours apply until the stored settings do
}
const activeDot = (page) => page.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')));
const pagerBox = (page) => page.evaluate(() => { const p = document.getElementById('pager'); return { w: p.clientWidth, h: p.clientHeight, left: p.getBoundingClientRect().left, scroll: p.scrollLeft }; });

test('always shown at the bottom: names to tap, and the pages keep clear of the bar', async ({ page }) => {
  await show(page, { pageBar: 'bar' });
  await expect(page.locator('#dots')).toHaveClass(/\bpagebar\b/);
  await expect(page.locator('#dots .dot-nm').nth(3)).toBeVisible();
  await expect(page.locator('#dots .dot-nm').nth(3)).toHaveText('Games');
  const box = await pagerBox(page);
  expect(box.h).toBe(400 - 52);
  const bar = await page.locator('#dots').boundingBox();
  expect(Math.round(bar.y)).toBeGreaterThanOrEqual(box.h - 1);
  await page.locator('#dots .dot').nth(3).click();
  await expect.poll(() => activeDot(page)).toBe(3);
  await expect(page.locator('#dots .dot').nth(3)).toHaveClass(/\bon\b/);
});

test('always shown on the left: the pages are narrower and still turn one whole page at a time', async ({ page }) => {
  await show(page, { pageBar: 'bar', pageBarSide: 'left' });
  await expect(page.locator('#dots')).toHaveClass(/\bpb-vertical\b/);
  const box = await pagerBox(page);
  expect(box.w).toBe(800 - 76);
  expect(box.left).toBe(76);
  await page.locator('#dots .dot').nth(2).click();
  await expect.poll(() => activeDot(page)).toBe(2);
  await expect.poll(async () => (await pagerBox(page)).scroll).toBe(2 * (800 - 76));
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => activeDot(page)).toBe(3);
});

test('shown when the dots are touched: the first touch opens the bar, it closes again by itself', async ({ page }) => {
  await show(page, { pageBar: 'auto' }, true);
  await expect(page.locator('#dots')).not.toHaveClass(/\bpagebar\b/);
  expect((await pagerBox(page)).h).toBe(400);              // nothing is taken from the pages
  await page.locator('#dots .dot').nth(3).click();        // opens the bar, goes nowhere
  await expect(page.locator('#dots')).toHaveClass(/\bpagebar\b/);
  expect(await activeDot(page)).toBe(0);
  await page.locator('#dots .dot').nth(3).click();        // now the button opens its page
  await expect.poll(() => activeDot(page)).toBe(3);
  for (let i = 0; i < 6; i++) await page.clock.fastForward(1000);
  await expect(page.locator('#dots')).not.toHaveClass(/\bpagebar\b/);
});

test('the touch that opens the bar presses neither the overview nor the pause button', async ({ page }) => {
  await show(page, { pageBar: 'auto' });
  await page.locator('#dots .page-all').click();
  await expect(page.locator('#dots')).toHaveClass(/pagebar/);
  await expect(page.locator('#overview')).toBeHidden();
  await page.locator('#dots .page-all').click();
  await expect(page.locator('#overview')).toBeVisible();
});

test('a side bar shown by a touch stays centred with the controls at the top', async ({ page }) => {
  await show(page, { pageBar: 'auto', pageBarSide: 'left', controlsTop: true });
  await page.locator('#dots .dot').first().click();
  await expect(page.locator('#dots')).toHaveClass(/pb-vertical/);
  const bar = await page.locator('#dots').boundingBox();
  expect(bar.y).toBeGreaterThanOrEqual(0);
  expect(Math.abs(bar.y + bar.height / 2 - 200)).toBeLessThan(2);
});
