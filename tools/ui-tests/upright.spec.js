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
  // #730: the 12 × 4 shortcuts keep their area on 6 columns
  expect(await area(page, 'Shortcuts')).toBe('3 / 1 / span 8 / span 6');
});

test('upright, a page marked upright whose widgets have no place is packed automatically, not left blank', async ({ page }) => {
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

const dirty = (page) => page.locator('#save').isEnabled();

test('a click on a widget in the automatic upright view selects it and changes nothing', async ({ page }) => {
  await openUpright(page, [AUTO]);
  const keys = await page.locator('#canvas .wb').nth(1).boundingBox();
  await page.mouse.click(keys.x + keys.width / 2, keys.y + keys.height / 2);
  await expect(page.locator('#canvas .wb.sel')).toHaveCount(1);
  await expect(page.locator('#canvas')).toHaveClass(/\bauto\b/);
  expect(await saved(page)).toEqual({ upright: null, places: [null, null] });
  expect(await dirty(page)).toBe(false);

  // a column the automatic layout has no room for is refused without starting a layout of the page's own
  // the column, row, width and height fields are the last four number fields
  const fields = page.locator('#inspector input[type="number"]');
  const col = fields.nth((await fields.count()) - 4);
  expect(await col.inputValue()).toBe('0');
  await col.fill('1');
  await col.dispatchEvent('change');
  expect(await saved(page)).toEqual({ upright: null, places: [null, null] });
  expect(await dirty(page)).toBe(false);
});

// Notes keyed by their text, so the editor's boxes and the panel's widgets can be compared.
const note = (text, x, y, w, h) => ({ id: text.toLowerCase(), type: 'note', x, y, w, h, options: { text } });
const LAYOUTS = {
  // the review's case: three 4 × 2 widgets and a 12 × 4 one pack to 14 rows
  fourteen: [note('A', 0, 0, 4, 2), note('B', 4, 0, 4, 2), note('C', 8, 0, 4, 2), note('D', 0, 2, 12, 4)],
  // seven widgets: the last one starts below row 12 of the automatic packing
  sixteen: [note('A', 0, 0, 4, 2), note('B', 4, 0, 4, 2), note('C', 8, 0, 4, 2),
    note('D', 0, 2, 4, 2), note('E', 4, 2, 4, 2), note('F', 8, 2, 4, 2), note('G', 0, 4, 12, 2)],
  // a wide card and two small ones
  mixed: [note('A', 0, 0, 12, 3), note('B', 0, 3, 3, 3), note('C', 3, 3, 9, 3)],
  // a page with tablet rows: E and F sit below row 6, so only a tablet with More rows on a tablet shows them
  tall: [note('A', 0, 0, 6, 3), note('B', 6, 0, 6, 3), note('C', 0, 3, 12, 3), note('E', 0, 6, 6, 2), note('F', 6, 6, 6, 2)],
};

async function seedWith(page, widgets, tabletRows) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ widgets, tabletRows }) => {
    localStorage.clear();
    localStorage.setItem('pb-editor-screen', 'upright');
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      display: { idleReturnSec: 0, tabletRows }, pages: [{ id: 'p', title: 'P', enabled: true, widgets }] }));
  }, { widgets, tabletRows });
}

/** The editor's automatic Upright view and the panel held upright (800 × 1280): each note's grid area, and the grid. */
async function editorAndPanel(page, widgets, tabletRows) {
  await page.setViewportSize({ width: 1280, height: 900 });
  await seedWith(page, widgets, tabletRows);
  await page.goto('/settings/#pages');
  await expect(page.locator('#canvas')).toHaveClass(/\bauto\b/);
  const editor = await page.evaluate(() => {
    const c = document.getElementById('canvas'), areas = {};
    for (const b of c.querySelectorAll('.wb')) areas[cfg.pages[0].widgets[+b.dataset.i].options.text] = b.style.gridArea;
    return { areas, cols: +c.dataset.cols, rows: +c.dataset.rows, off: document.querySelectorAll('#uprightOff button').length };
  });
  await page.setViewportSize({ width: 800, height: 1280 });
  await page.goto('/panel/');
  await expect(page.locator('.w-note').first()).toBeVisible();
  const panel = await page.evaluate(() => {
    const areas = {}, grid = document.querySelector('.grid');
    for (const n of grid.querySelectorAll('.w-note')) areas[n.textContent.trim()] = n.closest('.w').style.gridArea;
    return { areas, cols: getComputedStyle(grid).gridTemplateColumns.split(' ').length, rows: getComputedStyle(grid).gridTemplateRows.split(' ').length };
  });
  return { editor, panel };
}

for (const [name, widgets] of Object.entries(LAYOUTS)) {
  for (const tabletRows of [false, true]) {
    test(`the editor's automatic upright view is what the panel draws: ${name}, tablet rows ${tabletRows ? 'on' : 'off'}`, async ({ page }) => {
      const { editor, panel } = await editorAndPanel(page, widgets, tabletRows);
      expect(editor.areas).toEqual(panel.areas);
      expect([editor.cols, editor.rows]).toEqual([panel.cols, panel.rows]);
      // a phone packs into 6 columns; a tablet held upright with More rows on a tablet into 12
      expect(editor.cols).toBe(tabletRows ? 12 : 6);
      // widgets the panel leaves out (rows this screen does not have) are listed under the canvas
      expect(editor.off).toBe(widgets.length - Object.keys(editor.areas).length);
    });
  }
}

test('upright, a wide widget keeps its area in the editor too, and the rows grow instead of dropping widgets', async ({ page }) => {
  const { editor } = await editorAndPanel(page, LAYOUTS.fourteen, false);
  expect(editor.areas.D).toBe('7 / 1 / span 8 / span 6');
  expect(editor.rows).toBe(14);
  expect(Object.keys(editor.areas).sort()).toEqual(['A', 'B', 'C', 'D']);
});

test('starting the page\'s own layout keeps what fits on 6 × 12 and lists the rest as left off', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await seedWith(page, LAYOUTS.sixteen, false);
  await page.goto('/settings/#pages');
  await expect(page.locator('#canvas .wb')).toHaveCount(7);
  expect(await page.locator('#canvas').evaluate((c) => +c.dataset.rows)).toBe(16);

  // move A one column to the right
  const box = await page.locator('#canvas').boundingBox();
  const a = await page.locator('#canvas .wb').first().boundingBox();
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(a.x + a.width / 2 + box.width / 6, a.y + a.height / 2, { steps: 6 });
  await page.mouse.up();

  expect(await saved(page)).toEqual({ upright: true, places: [
    { x: 1, y: 0, w: 4, h: 2 }, { x: 0, y: 2, w: 4, h: 2 }, { x: 0, y: 4, w: 4, h: 2 },
    { x: 0, y: 6, w: 4, h: 2 }, { x: 0, y: 8, w: 4, h: 2 }, { x: 0, y: 10, w: 4, h: 2 }, null] });
  // G did not fit: it is not lost but listed under the canvas
  await expect(page.locator('#canvas .wb')).toHaveCount(6);
  await expect(page.locator('#uprightOff button')).toHaveCount(1);
  expect(await dirty(page)).toBe(true);
});

test('a widget that runs past row 12 when the page\'s own layout starts is shortened to the rows left', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await seedWith(page, LAYOUTS.fourteen, false);
  await page.goto('/settings/#pages');
  await expect(page.locator('#canvas .wb')).toHaveCount(4);
  const box = await page.locator('#canvas').boundingBox();
  const a = await page.locator('#canvas .wb').first().boundingBox();
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(a.x + a.width / 2 + box.width / 6, a.y + a.height / 2, { steps: 6 });
  await page.mouse.up();
  // D packs to 6 × 8 at row 6 (rows 6-13); on the 6 × 12 layout it keeps its place with the 6 rows left
  expect(await saved(page)).toEqual({ upright: true, places: [
    { x: 1, y: 0, w: 4, h: 2 }, { x: 0, y: 2, w: 4, h: 2 }, { x: 0, y: 4, w: 4, h: 2 }, { x: 0, y: 6, w: 6, h: 6 }] });
  await expect(page.locator('#uprightOff')).toBeHidden();
});

test('Replace widgets from a template and Clear drop the page\'s own upright layout', async ({ page }) => {
  await openUpright(page, [OWN]);
  await expect(page.locator('#uprightReset')).toBeVisible();
  await page.locator('#peHead button', { hasText: 'Replace' }).click();
  await page.locator('#modalBody .tpl').nth(1).click();
  const replaced = await saved(page);
  expect(replaced.upright).toBeNull();
  expect(replaced.places.length).toBeGreaterThan(0);
  expect(replaced.places.every((p) => p === null)).toBe(true);
  // the upright view shows the template's widgets packed automatically, not an empty page
  await expect(page.locator('#canvas')).toHaveClass(/\bauto\b/);
  expect(await page.locator('#canvas .wb').count()).toBeGreaterThan(0);
  await expect(page.locator('#uprightReset')).toBeHidden();

  // give it a layout of its own again, then clear the page
  await page.evaluate(() => { const p = cfg.pages[0]; p.upright = true; p.widgets[0].upright = { x: 0, y: 0, w: 2, h: 2 }; });
  await page.locator('#peHead button', { hasText: 'Clear' }).click();
  expect(await saved(page)).toEqual({ upright: null, places: [] });
});

test('a widget added in the automatic upright view with tablet rows off stays on the landscape rows', async ({ page }) => {
  // a full landscape page: no room left on rows 0-5
  await page.setViewportSize({ width: 1280, height: 900 });
  await seedWith(page, [note('A', 0, 0, 12, 3), note('B', 0, 3, 12, 3)], false);
  await page.goto('/settings/#pages');
  await page.locator('#palSearch').fill('sticky note');
  await page.locator('#palette .pal').first().click();
  // refused rather than put on row 6 or below, which a phone in landscape never shows
  expect(await page.evaluate(() => cfg.pages[0].widgets.length)).toBe(2);

  await seedWith(page, [note('A', 0, 0, 12, 3)], false);
  await page.goto('/settings/#pages');
  await page.locator('#palSearch').fill('sticky note');
  await page.locator('#palette .pal').first().click();
  const added = await page.evaluate(() => cfg.pages[0].widgets.at(-1));
  expect(added.y + added.h).toBeLessThanOrEqual(6);
  // the page keeps its automatic upright layout, which packs the new widget by itself
  expect(added.upright).toBeUndefined();
  expect((await saved(page)).upright).toBeNull();
  await expect(page.locator('#canvas .wb')).toHaveCount(2);
});
