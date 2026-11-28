const { test, expect } = require('@playwright/test');

const PAGE = { id: 'tall', title: 'Tall', enabled: true, widgets: [
  // Starts on the phone rows and runs onto tablet rows 6 and 7.
  { id: 'tall-note', type: 'note', x: 0, y: 4, w: 4, h: 4, options: { text: 'Runs onto tablet rows' } },
  // Only on a tablet.
  { id: 'tablet-note', type: 'note', x: 6, y: 6, w: 4, h: 2, options: { text: 'Tablet only' } },
] };

async function seed(page, tabletRows) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ PAGE, tabletRows }) => {
    localStorage.clear();
    localStorage.setItem('pb-editor-screen', 'phone');
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      display: { idleReturnSec: 0, tabletRows }, pages: [PAGE] }));
  }, { PAGE, tabletRows });
}

async function openEditor(page, tabletRows = true) {
  await page.setViewportSize({ width: 1280, height: 900 });
  await seed(page, tabletRows);
  await page.goto('/settings/#pages');
  await expect(page.locator('#canvas .wb')).toHaveCount(1); // the tablet-only note is off the Phone view
  const box = await page.locator('#canvas').boundingBox();
  return { cell: box.width / 12, row: box.height / 6, widget: await page.locator('#canvas .wb').boundingBox() };
}

const saved = (page) => page.evaluate(() => {
  const { x, y, w, h } = cfg.pages[0].widgets[0];
  return { x, y, w, h };
});

async function drag(page, from, dx, dy) {
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x + dx, from.y + dy, { steps: 6 });
  await page.mouse.up();
}

const centre = (b) => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 });

test('a sideways drag in Phone view keeps the saved tablet rows', async ({ page }) => {
  const { cell, widget } = await openEditor(page);
  await drag(page, centre(widget), cell * 2, 0);
  expect(await saved(page)).toEqual({ x: 2, y: 4, w: 4, h: 4 });
});

test('a downward drag in Phone view does not move a widget that runs onto tablet rows', async ({ page }) => {
  const { row, widget } = await openEditor(page);
  await drag(page, centre(widget), 0, row);
  expect(await saved(page)).toEqual({ x: 0, y: 4, w: 4, h: 4 });
});

test('a width-only resize in Phone view keeps the saved height', async ({ page }) => {
  const { cell } = await openEditor(page);
  await drag(page, centre(await page.locator('#canvas .wb .rz').boundingBox()), cell * 2, 0);
  expect(await saved(page)).toEqual({ x: 0, y: 4, w: 6, h: 4 });
});

test('dragging the bottom edge in Phone view does not cut a widget that runs onto tablet rows', async ({ page }) => {
  const { row } = await openEditor(page);
  await drag(page, centre(await page.locator('#canvas .wb .rz').boundingBox()), 0, row);
  expect(await saved(page)).toEqual({ x: 0, y: 4, w: 4, h: 4 });
});

test('the inspector changes the column of a widget clipped by the Phone view', async ({ page }) => {
  await openEditor(page);
  await page.locator('#canvas .wb').click();
  const col = page.locator('#inspector .field', { has: page.locator('label', { hasText: /^Column$/ }) }).locator('input');
  await col.fill('1'); // columns 1–4 stay clear of the tablet-only note at columns 6–9
  await col.dispatchEvent('change');
  expect(await saved(page)).toEqual({ x: 1, y: 4, w: 4, h: 4 });
});

test('the screen buttons follow More rows on a tablet', async ({ page }) => {
  await openEditor(page, false);
  // Phone and Upright are always there; the tablet screens come with the setting
  await expect(page.locator('#screenSeg [data-screen="upright"]')).toBeVisible();
  await expect(page.locator('#screenSeg [data-screen="tablet"]')).toBeHidden();
  await page.goto('/settings/#general');
  await page.locator('#tabletRowsOpt').check();
  await page.goto('/settings/#pages');
  await expect(page.locator('#screenSeg [data-screen="tablet"]')).toBeVisible();
});

async function panelRows(page) {
  await page.goto('/panel/');
  await expect(page.locator('.w-note').first()).toBeVisible();
  return page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--pb-rows').trim());
}

test('a 4:3 tablet gets 10 rows with the setting on and 6 with it off', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 768 });
  await seed(page, true);
  expect(await panelRows(page)).toBe('10');
  await expect(page.locator('.w-note')).toHaveCount(2);

  await seed(page, false);
  expect(await panelRows(page)).toBe('6');
  await expect(page.locator('.w-note')).toHaveCount(1); // the tablet-only note waits for the setting
});

test('a phone leaves out the widgets below row 6', async ({ page }) => {
  await page.setViewportSize({ width: 780, height: 360 });
  await seed(page, true);
  expect(await panelRows(page)).toBe('6');
  await expect(page.locator('.w-note')).toHaveCount(1);
});
