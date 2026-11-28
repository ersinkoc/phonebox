// The MQTT widget: the connection's state from /api/mqtt/status (a state, the time of the last message and a kind of
// error, nothing else) and the values of the keys it lists, read from the pushed values. The demo serves the status and
// two pushed values that MQTT subscriptions could have written (hall-temp and front-door).
const { test, expect } = require('@playwright/test');

const config = (options = {}, extra = {}) => ({
  night: { enabled: false }, rules: [], shortcuts: [],
  display: { language: 'en' },
  pages: [{ id: 'one', title: 'One', widgets: [{ id: 'mq', type: 'mqtt', x: 0, y: 0, w: 5, h: 4, options }] }],
  ...extra,
});

async function open(page, cfg) {
  await page.goto('/panel/');
  await page.evaluate((c) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify(c));
  }, cfg);
  await page.reload();
}

const widget = (page) => page.locator('.w-mqtt');

test('it shows the state, the last message and the values of its keys from the pushed values', async ({ page }) => {
  await open(page, config({ keys: 'hall-temp, front-door, not-there, hall-temp' }));
  await expect(widget(page).locator('.mq-state')).toHaveClass(/connected/);
  await expect(widget(page).locator('.mq-text')).toHaveText('Connected');
  await expect(widget(page).locator('.w-title')).toContainText('MQTT');
  await expect(widget(page).locator('.mq-last')).toHaveText('Last message just now');
  const rows = widget(page).locator('.mq-row');
  await expect(rows).toHaveCount(3); // a key listed twice is shown once
  await expect(rows.nth(0).locator('.mq-k')).toHaveText('hall-temp');
  await expect(rows.nth(0).locator('.mq-v')).toHaveText('22.4°C'); // the unit a subscription stored with the value
  await expect(rows.nth(1).locator('.mq-v')).toHaveText('closed');
  await expect(rows.nth(1).locator('.mq-age')).toHaveText(/15m ago/); // the time of the value, from the push store
  await expect(rows.nth(2)).toHaveClass(/none/);
  await expect(rows.nth(2).locator('.mq-v')).toHaveText('No data yet');
});

test('a value pushed while it is on screen shows up at once', async ({ page }) => {
  await open(page, config({ keys: 'hall-temp' }));
  await expect(widget(page).locator('.mq-v')).toHaveText('22.4°C');
  // the same path a subscription's message takes: the push store's change event
  await page.evaluate(() => fetch('/api/push/hall-temp', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ value: 23.1, unit: '°C' }) }));
  await expect(widget(page).locator('.mq-v')).toHaveText('23.1°C');
});

test('without keys it shows the state only; turned off, it says where to turn it on', async ({ page }) => {
  await open(page, config({}, { mqtt: { enabled: false } }));
  await expect(widget(page).locator('.mq-state')).toHaveClass(/off/);
  await expect(widget(page).locator('.mq-text')).toHaveText('Turn on MQTT in Settings › General › MQTT.');
  await expect(widget(page).locator('.mq-last')).toHaveText('Last message —');
  await expect(widget(page).locator('.mq-row')).toHaveCount(0);
  await expect(widget(page)).not.toContainText('null');
});

test('an error is shown by its kind, in words', async ({ page }) => {
  await open(page, config({ keys: 'hall-temp' }));
  await expect(widget(page).locator('.mq-state')).toHaveClass(/connected/);
  // the agent's answer for a refused login, then for a broker that cannot be reached (the widget asks every 5 s)
  await page.evaluate(() => {
    const inner = window.fetch;
    window.__mqtt = { state: 'error', lastMessageAt: null, errorKind: 'auth' };
    window.fetch = (input, init) => String(input).endsWith('/api/mqtt/status')
      ? Promise.resolve(new Response(JSON.stringify(window.__mqtt), { headers: { 'Content-Type': 'application/json' } }))
      : inner(input, init);
  });
  await expect(widget(page).locator('.mq-text')).toHaveText('The broker refused the user name or password.', { timeout: 12_000 });
  await expect(widget(page).locator('.mq-state')).toHaveClass(/error/);
  await page.evaluate(() => { window.__mqtt = { state: 'error', lastMessageAt: null, errorKind: 'refused' }; });
  await expect(widget(page).locator('.mq-text')).toContainText('could not be reached', { timeout: 12_000 });
  // the values stay: they come from the push store, not from the connection
  await expect(widget(page).locator('.mq-v')).toHaveText('22.4°C');
});

test('a failed status request shows Offline instead of the last answer, and the next answer brings the state back', async ({ page }) => {
  await open(page, config({ keys: 'hall-temp' }));
  await expect(widget(page).locator('.mq-state')).toHaveClass(/connected/);
  await page.evaluate(() => {
    const inner = window.fetch;
    window.__mqttDown = true;
    window.fetch = (input, init) => String(input).endsWith('/api/mqtt/status') && window.__mqttDown ? Promise.reject(new TypeError('Failed to fetch')) : inner(input, init);
  });
  await expect(widget(page).locator('.mq-text')).toHaveText('Offline', { timeout: 12_000 });
  await expect(widget(page).locator('.mq-state')).toHaveClass(/error/);
  await expect(widget(page).locator('.mq-v')).toHaveText('22.4°C'); // the values are the push store's
  await page.evaluate(() => { window.__mqttDown = false; });
  // after a failure the widget asks again within its retry time (15 s)
  await expect(widget(page).locator('.mq-text')).toHaveText('Connected', { timeout: 25_000 });
});

test('rows beyond the widget height can be scrolled to', async ({ page }) => {
  const keys = Array.from({ length: 12 }, (_, i) => 'key' + i).join(',');
  await open(page, { ...config({ keys }), pages: [{ id: 'one', title: 'One', widgets: [{ id: 'mq', type: 'mqtt', x: 0, y: 0, w: 3, h: 2, options: { keys } }] }] });
  const list = widget(page).locator('.mq-list');
  await expect(list.locator('.mq-row')).toHaveCount(12);
  const sizes = await list.evaluate((el) => ({ overflowY: getComputedStyle(el).overflowY, scroll: el.scrollHeight, client: el.clientHeight }));
  expect(sizes.overflowY).toBe('auto');
  expect(sizes.scroll).toBeGreaterThan(sizes.client); // more rows than room: the list scrolls rather than cutting them off
  await list.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await expect(list.locator('.mq-row').last()).toBeInViewport();
  // a new value redraws the widget; the list stays scrolled where it was
  const before = await list.evaluate((el) => el.scrollTop);
  expect(before).toBeGreaterThan(0);
  await page.evaluate(() => fetch('/api/push/key3', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '7' }));
  await expect(list.locator('.mq-row').nth(3).locator('.mq-v')).toHaveText('7');
  expect(await list.evaluate((el) => el.scrollTop)).toBe(before);
  await expect(list.locator('.mq-row').last()).toBeInViewport();
});

test('the widget is in the catalog and the panel speaks the language (German)', async ({ page }) => {
  await open(page, { ...config({}, { mqtt: { enabled: false } }), display: { language: 'de' } });
  await expect(widget(page).locator('.mq-text')).toHaveText('Schalten Sie MQTT in Einstellungen › Allgemein › MQTT ein.');
  await expect(widget(page).locator('.mq-last')).toContainText('Letzte Nachricht');
  const catalog = await page.evaluate(async () => (await (await fetch('/widgets.json')).json()).find((w) => w.type === 'mqtt'));
  expect(catalog.category).toBe('Web & data');
  expect(catalog.options.map((o) => o.key)).toEqual(['title', 'keys']);
  // read-only: nothing in the catalog entry lets the phone send anything
  expect(catalog.options.some((o) => o.key === 'control')).toBe(false);
});
