const { test, expect } = require('@playwright/test');

// A games page drawn on the 12 × 6 landscape grid: a library card above a shortcuts grid across the page.
const AUTO = { id: 'games', title: 'Games', enabled: true, widgets: [
  { id: 'lib', type: 'note', x: 0, y: 0, w: 5, h: 2, options: { text: 'Library' } },
  { id: 'keys', type: 'note', x: 0, y: 2, w: 12, h: 4, options: { text: 'Shortcuts' } },
] };
// The same page with its own upright layout: the shortcuts take the lower two thirds, the library is left off.
const OWN = { ...AUTO, upright: true, widgets: [
  { ...AUTO.widgets[0] },
  { ...AUTO.widgets[1], upright: { x: 0, y: 4, w: 6, h: 8 } },
] };

async function seed(page, pages) {
  await page.goto('/panel/style.css');
  await page.evaluate((pages) => {
    localStorage.clear();
    localStorage.setItem('pb-editor-screen', 'phone');
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      display: { idleReturnSec: 0 }, pages }));
  }, pages);
}

const area = (page, text) => page.locator('.w-note', { hasText: text }).evaluate((el) => el.closest('.w').style.gridArea);

test('upright, a page with its own layout puts each widget at its upright place and leaves out the others', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 1280 });
  await seed(page, [OWN]);
  await page.goto('/panel/');
  await expect(page.locator('.w-note', { hasText: 'Shortcuts' })).toBeVisible();
  await expect(page.locator('.w-note', { hasText: 'Library' })).toHaveCount(0);
  expect(await area(page, 'Shortcuts')).toBe('5 / 1 / span 8 / span 6');
  const grid = await page.locator('.grid').first().evaluate((g) => ({
    cols: getComputedStyle(g).gridTemplateColumns.split(' ').length, rows: getComputedStyle(g).gridTemplateRows.split(' ').length }));
  expect(grid).toEqual({ cols: 6, rows: 12 });

  // landscape, the same page keeps its landscape places
  await page.setViewportSize({ width: 1280, height: 800 });
  await expect(page.locator('.w-note', { hasText: 'Library' })).toBeVisible();
  expect(await area(page, 'Shortcuts')).toBe('3 / 1 / span 4 / span 12');
});

test('upright, a page without its own layout is still packed automatically', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 1280 });
  await seed(page, [AUTO]);
  await page.goto('/panel/');
  await expect(page.locator('.w-note')).toHaveCount(2);
  expect(await area(page, 'Shortcuts')).toBe('3 / 1 / span 4 / span 6');
});

const saved = (page) => page.evaluate(() => {
  const p = cfg.pages[0];
  return { upright: p.upright ?? null, places: p.widgets.map((w) => w.upright ?? null) };
});

async function openUpright(page, pages) {
  await page.setViewportSize({ width: 1280, height: 900 });
  await seed(page, pages);
  await page.goto('/settings/#pages');
  await expect(page.locator('#canvas .wb')).toHaveCount(2);
  await page.locator('#screenSeg [data-screen="upright"]').click();
}

test('the editor shows the automatic upright layout, and the first move makes it the page\'s own', async ({ page }) => {
  await openUpright(page, [AUTO]);
  await expect(page.locator('#canvas')).toHaveClass(/\bauto\b/);
  await expect(page.locator('#screenHint')).toContainText('arranged automatically');
  await expect(page.locator('#uprightReset')).toBeHidden();
  expect(await saved(page)).toEqual({ upright: null, places: [null, null] });

  const box = await page.locator('#canvas').boundingBox();
  const row = box.height / 12;
  const keys = await page.locator('#canvas .wb').nth(1).boundingBox();
  await page.mouse.move(keys.x + keys.width / 2, keys.y + keys.height / 2);
  await page.mouse.down();
  await page.mouse.move(keys.x + keys.width / 2, keys.y + keys.height / 2 + row * 2, { steps: 6 });
  await page.mouse.up();

  // the automatic places became the page's own, with the shortcuts two rows lower; landscape is untouched
  expect(await saved(page)).toEqual({ upright: true, places: [{ x: 0, y: 0, w: 5, h: 2 }, { x: 0, y: 4, w: 6, h: 4 }] });
  expect(await page.evaluate(() => cfg.pages[0].widgets.map(({ x, y, w, h }) => ({ x, y, w, h })))).toEqual([
    { x: 0, y: 0, w: 5, h: 2 }, { x: 0, y: 2, w: 12, h: 4 }]);
  await expect(page.locator('#canvas')).not.toHaveClass(/\bauto\b/);
  await expect(page.locator('#uprightReset')).toBeVisible();
});

test('a widget can be left off the upright layout, put back, and the page set back to automatic', async ({ page }) => {
  await openUpright(page, [OWN]);
  await expect(page.locator('#canvas .wb')).toHaveCount(1);
  await expect(page.locator('#uprightOff')).toContainText('Note');

  // put the library back: it lands in the first free spot of the upright grid
  await page.locator('#uprightOff button').click();
  await expect(page.locator('#canvas .wb')).toHaveCount(2);
  expect(await saved(page)).toEqual({ upright: true, places: [{ x: 0, y: 0, w: 5, h: 2 }, { x: 0, y: 4, w: 6, h: 8 }] });

  // leave it off again from the inspector
  await page.locator('#inspector button', { hasText: 'Leave off upright' }).click();
  expect(await saved(page)).toEqual({ upright: true, places: [null, { x: 0, y: 4, w: 6, h: 8 }] });

  await page.locator('#uprightReset').click();
  expect(await saved(page)).toEqual({ upright: null, places: [null, null] });
  await expect(page.locator('#canvas')).toHaveClass(/\bauto\b/);
});

test('a widget added while editing upright gets a place on both layouts', async ({ page }) => {
  await openUpright(page, [OWN]);
  await page.locator('#palSearch').fill('sticky note');
  await page.locator('#palette .pal').first().click();
  const added = await page.evaluate(() => { const w = cfg.pages[0].widgets.at(-1); return { x: w.x, y: w.y, w: w.w, h: w.h, upright: w.upright }; });
  expect(added.y + added.h).toBeLessThanOrEqual(18);
  expect(added.upright).toBeTruthy();
  expect(added.upright.x + added.upright.w).toBeLessThanOrEqual(6);
  expect(added.upright.y + added.upright.h).toBeLessThanOrEqual(12);
});

test('without the tablet screens the editor offers Landscape and Upright', async ({ page }) => {
  await openUpright(page, [AUTO]);
  await expect(page.locator('#screenSeg [data-screen="phone"]')).toHaveText('Landscape');
  await expect(page.locator('#screenSeg [data-screen="upright"]')).toHaveText('Upright');
});
