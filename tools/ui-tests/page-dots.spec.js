// The page dots: a tap on a dot opens its page, a slide along them picks one (named at the top while sliding), the
// current dot and the grid button open the overview of all pages.
const { test, expect } = require('@playwright/test');

const pagesOf = (n) => Array.from({ length: n }, (_, i) => ({ id: 'p' + (i + 1), title: 'Page ' + (i + 1), icon: '',
  widgets: [{ id: 'n' + (i + 1), type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page ' + (i + 1) } }] }));

async function show(page, n = 5) {
  await page.goto('/panel/style.css');
  await page.evaluate((pages) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], pages,
      display: { idleReturnSec: 0, minDwellSec: 0, switchCountdownSec: 0 },
    }));
  }, pagesOf(n));
  await page.goto('/panel/');
  await expect(page.locator('#dots .dot')).toHaveCount(n);
  await expect(page.locator('body')).not.toHaveClass(/night/); // the default night hours apply until the stored settings do
}

const activeDot = (page) => page.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')));
const center = async (locator) => { const b = await locator.boundingBox(); return [b.x + b.width / 2, b.y + b.height / 2]; };

test('a tap on a dot opens its page; the current dot and the grid button open the overview', async ({ page }) => {
  await show(page);
  await page.locator('#dots .dot').nth(3).click();
  await expect.poll(() => activeDot(page)).toBe(3);
  await expect(page.locator('#overview')).toBeHidden();
  await page.locator('#dots .dot').nth(2).click(); // the next one, scrolled to
  await expect.poll(() => activeDot(page)).toBe(2);

  await page.locator('#dots .dot').nth(2).click();
  await expect(page.locator('#overview')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('#overview')).toBeHidden();
  await page.locator('#dots .page-all').click();
  await expect(page.locator('#overview')).toBeVisible();
  await expect(page.locator('#dots .dot').nth(4)).toHaveAttribute('aria-label', '5 · Page 5');
});

test('sliding along the dots names the page under the finger and opens it on release', async ({ page }) => {
  await show(page);
  const [x0, y] = await center(page.locator('#dots .dot').nth(0));
  const [x3] = await center(page.locator('#dots .dot').nth(3));
  await page.mouse.move(x0, y);
  await page.mouse.down();
  await page.mouse.move(x3, y, { steps: 8 });
  await expect(page.locator('#ptitle')).toHaveClass(/\bon\b/);
  await expect(page.locator('#ptitle')).toHaveText('Page 4');
  await expect(page.locator('#dots .dot').nth(3)).toHaveClass(/\baim\b/);
  expect(await activeDot(page)).toBe(0); // nothing changes until the finger lifts
  await page.mouse.up();
  await expect.poll(() => activeDot(page)).toBe(3);
  await expect(page.locator('#overview')).toBeHidden(); // the click after a slide opens nothing
  await expect(page.locator('#dots .dot.aim')).toHaveCount(0);
  await expect(page.locator('#dots')).not.toHaveClass(/\bpaused\b/);
});

test('many pages still fit on a narrow phone', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 640 });
  await show(page, 24);
  const bar = await page.locator('#dots').boundingBox();
  expect(bar.x).toBeGreaterThanOrEqual(0);
  expect(bar.x + bar.width).toBeLessThanOrEqual(360);
  await page.locator('#dots .dot').nth(20).click();
  await expect.poll(() => activeDot(page)).toBe(20);
});

test('a press that leaves the bar without sliding along it names nothing afterwards', async ({ page }) => {
  await show(page);
  const [x0, y] = await center(page.locator('#dots .dot').nth(0));
  const [x3] = await center(page.locator('#dots .dot').nth(3));
  await page.mouse.move(x0, y);
  await page.mouse.down();
  await page.mouse.move(x0, y - 120, { steps: 4 }); // straight up, off the bar
  await page.mouse.move(x3, y - 120, { steps: 4 }); // and sideways up there, as a touch can (it keeps its target)
  await page.mouse.up();
  expect(await activeDot(page)).toBe(0);
  await page.mouse.move(x3, y, { steps: 6 });       // hovering over the dots with no button down
  await expect(page.locator('#dots .dot.aim')).toHaveCount(0);
  expect(await activeDot(page)).toBe(0);
});

test('a slide that wanders far off the bar is called off', async ({ page }) => {
  await show(page);
  const [x0, y] = await center(page.locator('#dots .dot').nth(0));
  const [x1] = await center(page.locator('#dots .dot').nth(1));
  const [x3] = await center(page.locator('#dots .dot').nth(3));
  await page.mouse.move(x0, y);
  await page.mouse.down();
  await page.mouse.move(x1, y, { steps: 4 });         // a slide, aimed at page 2
  await expect(page.locator('#dots .dot').nth(1)).toHaveClass(/\baim\b/);
  await page.mouse.move(x1, y - 150, { steps: 4 });   // far up, off the bar
  await page.mouse.move(x3, y - 150, { steps: 4 });
  await page.mouse.up();
  await expect(page.locator('#dots .dot.aim')).toHaveCount(0);
  await expect(page.locator('#overview')).toBeHidden();
  expect(await activeDot(page)).toBe(0);
});
