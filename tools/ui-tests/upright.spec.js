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
  // #730: the 12 × 4 shortcuts keep their area, 6 × 8
  expect(await area(page, 'Shortcuts')).toBe('3 / 1 / span 8 / span 6');
});

test('upright, a page marked as having its own layout but with no widget placed is packed automatically, not blank', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 1280 });
  await seed(page, [{ ...AUTO, upright: true }]);
  await page.goto('/panel/');
  await expect(page.locator('.w-note')).toHaveCount(2);
  expect(await area(page, 'Shortcuts')).toBe('3 / 1 / span 8 / span 6');
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
  expect(await saved(page)).toEqual({ upright: true, places: [{ x: 0, y: 0, w: 5, h: 2 }, { x: 0, y: 4, w: 6, h: 8 }] });
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
  // with the tablet rows off, its landscape spot is on the rows a landscape phone shows
  expect(added.y + added.h).toBeLessThanOrEqual(6);
  expect(added.upright).toBeTruthy();
  expect(added.upright.x + added.upright.w).toBeLessThanOrEqual(6);
  expect(added.upright.y + added.upright.h).toBeLessThanOrEqual(12);
});

test('without the tablet screens the editor offers Landscape and Upright', async ({ page }) => {
  await openUpright(page, [AUTO]);
  await expect(page.locator('#screenSeg [data-screen="phone"]')).toHaveText('Landscape');
  await expect(page.locator('#screenSeg [data-screen="upright"]')).toHaveText('Upright');
});

test("the editor's automatic upright view is the panel's packing, and a click only selects", async ({ page }) => {
  await openUpright(page, [AUTO]);
  // the 12 × 4 shortcuts are shown 6 × 8, as the panel draws them (#730)
  expect(await page.locator('#canvas .wb').nth(1).evaluate((el) => el.style.gridArea)).toBe('3 / 1 / span 8 / span 6');
  const keys = await page.locator('#canvas .wb').nth(1).boundingBox();
  await page.mouse.click(keys.x + keys.width / 2, keys.y + keys.height / 2);
  await expect(page.locator('#inspector h3')).toContainText('Note');
  expect(await saved(page)).toEqual({ upright: null, places: [null, null] });
  await expect(page.locator('#canvas')).toHaveClass(/\bauto\b/);

  // the editor's packing and grid.js agree, for a phone and a tablet with More rows
  const widgets = [{ x: 0, y: 0, w: 4, h: 2 }, { x: 4, y: 0, w: 4, h: 2 }, { x: 8, y: 0, w: 4, h: 2 }, { x: 0, y: 2, w: 12, h: 4 }, { x: 0, y: 6, w: 7, h: 3 }];
  const editor = await page.evaluate((ws) => [6, 12].map((c) => { const r = packUpright(ws, c); return { rows: r.rows, pos: ws.map((w) => r.pos.get(w)) }; }), widgets);
  await page.goto('/panel/style.css');
  const panel = await page.evaluate(async (ws) => {
    const g = await import('/panel/grid.js');
    return [6, 12].map((c) => { const r = g.portraitLayout(ws, c); return { rows: r.rows, pos: ws.map((w) => r.pos.get(w)) }; });
  }, widgets);
  expect(editor).toEqual(panel);
});

test("widgets that pack below the 12 upright rows are said in the hint, and listed once the layout is the page's own", async ({ page }) => {
  // three 4 × 2 widgets and a 12 × 4 one pack to 2 + 2 + 8 = 12 rows… plus another 4 × 2: 14 rows
  const FULL = { id: 'full', title: 'Full', enabled: true, widgets: [
    { id: 'a', type: 'note', x: 0, y: 0, w: 4, h: 2, options: { text: 'A' } },
    { id: 'b', type: 'note', x: 4, y: 0, w: 4, h: 2, options: { text: 'B' } },
    { id: 'c', type: 'note', x: 8, y: 0, w: 4, h: 2, options: { text: 'C' } },
    { id: 'd', type: 'note', x: 0, y: 2, w: 12, h: 4, options: { text: 'D' } },
  ] };
  await page.setViewportSize({ width: 1280, height: 900 });
  await seed(page, [FULL]);
  await page.goto('/settings/#pages');
  await expect(page.locator('#canvas .wb')).toHaveCount(4);
  await page.locator('#screenSeg [data-screen="upright"]').click();
  // the automatic view draws what the panel draws: all four, on 14 rows
  await expect(page.locator('#canvas .wb')).toHaveCount(4);
  expect(await page.locator('#canvas .cell').count()).toBe(6 * 14);
  await expect(page.locator('#screenHint')).toContainText('1 of them do not fit');

  // moving A makes the layout the page's own: D, which packs to rows 12 to 14, is left off and listed
  const a = await page.locator('#canvas .wb').first().boundingBox();
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(a.x + a.width / 2 + a.width, a.y + a.height / 2, { steps: 6 });
  await page.mouse.up();
  const places = await page.evaluate(() => cfg.pages[0].widgets.map((w) => w.upright ?? null));
  expect(places[3]).toBeNull();
  expect(places.slice(0, 3).every(Boolean)).toBe(true);
  await expect(page.locator('#uprightOff')).toContainText('Note');
  expect(await page.locator('#canvas .cell').count()).toBe(6 * 12);
});

test("replacing a page's widgets from a template, or clearing them, drops its upright layout", async ({ page }) => {
  await openUpright(page, [OWN]);
  await page.locator('#screenSeg [data-screen="phone"]').click();
  await page.locator('#pageEditor button', { hasText: /^Clear$/ }).click();
  expect(await saved(page)).toEqual({ upright: null, places: [] });

  await seed(page, [OWN]);
  await page.goto('/settings/#pages');
  await page.locator('#pageEditor button', { hasText: 'Replace with template' }).click();
  await page.locator('.tpl-grid .tpl').nth(1).click(); // the first template after Blank page
  const p = await page.evaluate(() => ({ upright: cfg.pages[0].upright ?? null, placed: cfg.pages[0].widgets.some((w) => w.upright) }));
  expect(p).toEqual({ upright: null, placed: false });
});
