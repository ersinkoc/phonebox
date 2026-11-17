const { test, expect } = require('@playwright/test');

// Home Assistant widget: per-entity name, icon, visibility and switching (the demo answers /api/ha/* inside the page).
const haConfig = (options) => ({ night: { enabled: false }, shortcuts: [], pages: [{ id: 'test', title: 'Test', enabled: true,
  widgets: [{ id: 'ha', type: 'ha', x: 0, y: 0, w: 12, h: 6, options }] }] });

async function open(page, url, options) {
  await page.goto(url);
  await page.evaluate((c) => { localStorage.clear(); localStorage.setItem('pb-demo-config', JSON.stringify(c)); }, haConfig(options));
  await page.reload();
}

test('the panel uses each entity\'s own name, icon and visibility', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await open(page, '/panel/', { control: true, items: [
    { id: 'light.desk', name: 'Reading light', icon: '🪔' },
    { id: 'switch.coffee', showName: false },
    { id: 'lock.front_door', showIcon: false, control: false },
    { id: 'light.desk', name: 'duplicate' },
  ] });
  const tiles = page.locator('.ha-tile');
  await expect(tiles).toHaveCount(3); // the duplicate is dropped
  await expect(tiles.nth(0).locator('.ha-name')).toHaveText('Reading light');
  await expect(tiles.nth(0).locator('.ha-ico')).toHaveText('🪔');
  await expect(tiles.nth(1).locator('.ha-name')).toHaveCount(0);
  await expect(tiles.nth(1).locator('.ha-ico')).toHaveCount(1);
  await expect(tiles.nth(2).locator('.ha-ico')).toHaveCount(0);
  await expect(tiles.nth(2).locator('.ha-name')).toHaveText('Door lock');
  // the widget allows switching, the lock opted out
  await expect(tiles.nth(0)).toHaveClass(/can/);
  await expect(tiles.nth(2)).not.toHaveClass(/can/);
  await tiles.nth(0).click();
  await expect(tiles.nth(0)).not.toHaveClass(/\bon\b/); // the demo's lamp was on
  expect(errors).toEqual([]);
});

test('widgets saved with the entity text still work', async ({ page }) => {
  await open(page, '/panel/', { control: true, entities: 'light.desk, sensor.office_temperature' });
  await expect(page.locator('.ha-tile')).toHaveCount(2);
  await expect(page.locator('.ha-tile').first()).toHaveClass(/can/);
});

test('a widget\'s switching flag is the default for entities that do not set their own', async ({ page }) => {
  await open(page, '/panel/', { control: false, items: [{ id: 'light.desk' }, { id: 'light.ceiling', control: true }] });
  await expect(page.locator('.ha-tile').nth(0)).not.toHaveClass(/can/);
  await expect(page.locator('.ha-tile').nth(1)).toHaveClass(/can/);
});

test('the settings editor lists, edits, reorders and removes entities', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { control: true, entities: 'light.desk, sensor.office_temperature' });
  await page.locator('#canvas .wb').first().click(); // selects the widget and opens its settings
  const rows = page.locator('.ha-row');
  await expect(rows).toHaveCount(2); // started from the entity text
  await expect(page.locator('datalist option')).not.toHaveCount(0);
  await rows.nth(0).getByRole('button', { name: 'Pick icon' }).click();
  await page.locator('.ha-row .emojis button', { hasText: '💡' }).click();
  await expect(rows.nth(0).locator('input[placeholder="Icon"]')).toHaveValue('💡');
  await rows.nth(1).locator('input[placeholder="Icon"]').fill('🌡️');
  await expect(page.locator('#inspector')).not.toContainText('null');
  await rows.nth(1).getByRole('button', { name: 'Pick icon' }).click();
  await page.locator('#inspector').screenshot({ path: 'test-results/ha-editor.png' });
  await rows.nth(1).getByRole('button', { name: 'Close' }).click();
  await rows.nth(0).getByLabel('Show name').uncheck();
  await rows.nth(1).getByRole('button', { name: '▲' }).click();
  await page.getByRole('button', { name: '＋ Add entity' }).click();
  await page.locator('.ha-row').nth(2).locator('input[placeholder="light.desk"]').fill('switch.coffee');
  await page.locator('.ha-row').nth(0).getByRole('button', { name: '✕' }).click();
  await expect(page.locator('.ha-row')).toHaveCount(2);
  await page.locator('#save').click();
  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.items);
  expect(saved).toEqual([{ id: 'light.desk', icon: '💡', showName: false }, { id: 'switch.coffee' }]); // the temperature row was moved up, then removed
  expect(errors).toEqual([]);
});

test('removing every row of an old widget does not bring the entity text back', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { control: true, entities: 'light.desk, sensor.office_temperature' });
  await page.locator('#canvas .wb').first().click();
  await page.locator('.ha-row').nth(0).getByRole('button', { name: '✕' }).click();
  await page.locator('.ha-row').nth(0).getByRole('button', { name: '✕' }).click();
  await expect(page.locator('.ha-row')).toHaveCount(0);
  await page.locator('#save').click();
  const o = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options);
  expect(o.items).toEqual([]);
  expect(o.entities).toBe('');
});

test('the switching checkbox shows what the agent applies when the widget has no flag', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { items: [{ id: 'light.desk' }] }); // no "control" key at all
  await page.locator('#canvas .wb').first().click();
  await expect(page.locator('.ha-row').getByLabel('Allow switching from the phone')).not.toBeChecked();
});

test('a list of unusable items does not fall back to the old entity text', async ({ page }) => {
  await open(page, '/panel/', { control: true, entities: 'light.desk', items: [{ id: 'unfinished' }] });
  await expect(page.locator('.ha-tile')).toHaveCount(0);
  await expect(page.locator('.empty')).toContainText('Add entities');
});
