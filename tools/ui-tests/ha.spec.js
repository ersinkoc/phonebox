const { test, expect } = require('@playwright/test');

// Home Assistant widget: per-entity name, icon, visibility and switching (the demo answers /api/ha/* inside the page).
const haConfig = (options, extra = {}) => ({ night: { enabled: false }, shortcuts: [], ...extra, pages: [{ id: 'test', title: 'Test', enabled: true,
  widgets: [{ id: 'ha', type: 'ha', x: 0, y: 0, w: 12, h: 6, options }] }] });

async function open(page, url, options, extra) {
  await page.goto(url);
  await page.evaluate((c) => { localStorage.clear(); localStorage.setItem('pb-demo-config', JSON.stringify(c)); }, haConfig(options, extra));
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
  expect(saved).toEqual([{ id: 'light.desk', icon: '💡', showName: false, control: true }, { id: 'switch.coffee', control: true }]); // the temperature row was moved up, then removed
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
  await expect(page.getByLabel(/default for entities/)).not.toBeChecked(); // the widget's own box says the same
});

test('a list of unusable items does not fall back to the old entity text', async ({ page }) => {
  await open(page, '/panel/', { control: true, entities: 'light.desk', items: [{ id: 'unfinished' }] });
  await expect(page.locator('.ha-tile')).toHaveCount(0);
  await expect(page.locator('.empty')).toContainText('Add entities');
});

test('a row that follows the widget shows its flag when the widget\'s box changes (off to on)', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { control: false, items: [{ id: 'lock.front_door' }] });
  await page.locator('#canvas .wb').first().click();
  const row = page.locator('.ha-row').getByLabel('Allow switching from the phone');
  await expect(row).not.toBeChecked();
  await page.getByLabel(/default for entities/).check();
  await expect(row).toBeChecked(); // the row is drawn again with what the phone will do
  await page.locator('.ha-row input[placeholder="Display name (empty = from Home Assistant)"]').fill('Door');
  await page.locator('#save').click();
  const items = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.items);
  expect(items).toEqual([{ id: 'lock.front_door', name: 'Door', control: true }]);
});

test('every saved row keeps the switching state it showed, whatever the widget\'s box does later', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { control: false, items: [{ id: 'lock.front_door' }] });
  await page.locator('#canvas .wb').first().click();
  await page.locator('.ha-row input[placeholder="Display name (empty = from Home Assistant)"]').fill('Door'); // saves the row: switching off
  await page.getByLabel(/default for entities/).check();
  await expect(page.locator('.ha-row').getByLabel('Allow switching from the phone')).not.toBeChecked();
  await page.locator('#save').click();
  const items = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.items);
  expect(items).toEqual([{ id: 'lock.front_door', name: 'Door', control: false }]);
});

test('a hand-edited numeric id does not break saving', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { control: true, items: [{ id: 42 }, { id: 'light.desk' }] });
  await page.locator('#canvas .wb').first().click();
  await page.locator('.ha-row').nth(1).locator('input[placeholder="Display name (empty = from Home Assistant)"]').fill('Lamp');
  await page.locator('#save').click();
  expect(errors).toEqual([]);
});

test('the editor and the empty panel are translated', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { control: true, items: [{ id: 'light.desk' }] }, { display: { language: 'de' } });
  await page.locator('#canvas .wb').first().click();
  await expect(page.getByRole('button', { name: 'Symbol wählen' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Entität hinzufügen/ })).toBeVisible();
  await expect(page.locator('.ha-row').getByLabel('Namen anzeigen')).toBeChecked();
  await open(page, '/panel/', { control: true, items: [] }, { display: { language: 'de' } });
  await expect(page.locator('.empty')).toContainText('Entitäten in den Widget-Einstellungen hinzufügen');
});

// Wraps window.fetch before the demo installs its own, so a test can see (or fail) the /api/ha/* calls the page makes.
const spyOnFetch = (page, failEntitiesOnce = false) => page.addInitScript((fail) => {
  const native = window.fetch.bind(window);
  let inner = native, failed = !fail, depth = 0;
  window.__calls = [];
  Object.defineProperty(window, 'fetch', { configurable: true, set(f) { inner = f; }, get() {
    return (...a) => {
      if (depth) return native(...a); // the demo's own fetch calls the one it replaced, which is this wrapper
      const url = String(a[0]); window.__calls.push(url);
      if (!failed && url.includes('/api/ha/entities')) { failed = true; return Promise.resolve(new Response(JSON.stringify({ configured: false, error: 'Home Assistant is not reachable', entities: [] }), { headers: { 'content-type': 'application/json' } })); }
      depth++;
      try { return inner(...a); } finally { depth--; }
    };
  } });
}, failEntitiesOnce);

test('a failed entity discovery is retried when the editor opens again', async ({ page }) => {
  await spyOnFetch(page, true);
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { control: true, items: [{ id: 'light.desk' }] });
  await page.locator('#canvas .wb').first().click();
  await expect(page.locator('datalist option')).toHaveCount(0); // the first answer was an error
  await page.getByLabel(/default for entities/).uncheck(); // the inspector draws the editor again
  await expect(page.locator('datalist option')).not.toHaveCount(0); // asked again, and this time it answered
});

test('unusable rows in front of the 200th place do not push usable entities out when saving', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  const junk = Array.from({ length: 200 }, (_, i) => ({ id: 'nodot' + i }));
  await open(page, '/settings/#pages', { control: true, items: [...junk, { id: 'light.desk' }, { id: 'light.desk' }] });
  await page.locator('#canvas .wb').first().click();
  await page.locator('.ha-row').nth(200).locator('input[placeholder="Display name (empty = from Home Assistant)"]').fill('Lamp');
  await page.locator('#save').click();
  const items = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.items);
  expect(items).toEqual([{ id: 'light.desk', control: true, name: 'Lamp' }]);
});

test('many long entity ids are asked for in several smaller requests', async ({ page }) => {
  await spyOnFetch(page);
  const items = Array.from({ length: 200 }, (_, i) => ({ id: 'sensor.' + 'x'.repeat(90) + i }));
  await open(page, '/panel/', { control: false, items });
  await expect(page.locator('.ha-tile')).toHaveCount(200);
  const calls = await page.evaluate(() => window.__calls.filter((u) => u.includes('/api/ha/states')));
  expect(calls.length).toBeGreaterThan(1);
  for (const u of calls) expect(u.length).toBeLessThan(8000);
});
