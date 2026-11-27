const { test, expect } = require('@playwright/test');

// Home Assistant dashboard widget: the phone opens the dashboard itself, from the address the agent sends (never the
// token) or the widget's own, and says so while the server does not answer.
const dashConfig = (options, homeAssistant = { url: null, token: null }) => ({ night: { enabled: false }, shortcuts: [], homeAssistant,
  pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'dash', type: 'hadash', x: 0, y: 0, w: 12, h: 6, options }] }] });

async function open(page, url, options, homeAssistant) {
  await page.goto(url);
  await page.evaluate((c) => { localStorage.clear(); localStorage.setItem('pb-demo-config', JSON.stringify(c)); }, dashConfig(options, homeAssistant));
  await page.reload();
}

test('without an address the widget says where to set it', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await open(page, '/panel/', { dashboard: 'desk-panel/home' });
  await expect(page.locator('.w-hadash .empty')).toHaveText('Set the Home Assistant address in Settings › General, or in this widget');
  await expect(page.locator('.w-hadash iframe')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('the dashboard opens from the address in Settings, in a frame of its own origin', async ({ page, baseURL }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  const server = new URL(baseURL).origin;
  await open(page, '/panel/', { dashboard: '/desk-panel/home/' }, { url: server + '/', token: 'secret-token' });
  const frame = page.locator('.w-hadash iframe');
  await expect(frame).toHaveAttribute('src', server + '/desk-panel/home?kiosk');
  await expect(frame).toHaveAttribute('sandbox', /allow-same-origin/);
  await expect(page.locator('.w-hadash')).toHaveClass(/bare-html/); // no card background by default
  // no card around it, and not clipped to rounded corners (WebView would drop the page's own rounded clips)
  const box = await page.locator('.w-hadash').evaluate((e) => { const c = getComputedStyle(e); return [c.backgroundColor, c.overflow, c.borderRadius]; });
  expect(box).toEqual(['rgba(0, 0, 0, 0)', 'visible', '0px']);
  // the token stays with the agent
  const config = await page.evaluate(() => fetch('/agent/api/config').then((r) => r.text()));
  expect(JSON.parse(config).config.homeAssistant).toEqual({ url: server + '/', alternatives: [] });
  expect(config).not.toContain('secret-token');
  expect(errors).toEqual([]);
});

test('the widget\'s own address wins, and a path it cannot use falls back to the default dashboard', async ({ page, baseURL }) => {
  const server = new URL(baseURL).origin;
  await open(page, '/panel/', { address: server, dashboard: 'bad path"><x', transparent: false, kiosk: false }, { url: 'http://homeassistant.local:8123', token: 't' });
  await expect(page.locator('.w-hadash iframe')).toHaveAttribute('src', server + '/lovelace');
  await expect(page.locator('.w-hadash')).not.toHaveClass(/bare-html/);
});

test('while the server does not answer the widget says so', async ({ page }) => {
  // nothing listens on port 9 (discard) here, so the check fails at once
  await open(page, '/panel/', { address: 'http://127.0.0.1:9' });
  await expect(page.locator('.w-hadash .empty')).toHaveText('Home Assistant does not answer. Trying again…');
  await expect(page.locator('.w-hadash iframe')).toHaveCount(0);
});

test('the settings offer the dashboards Home Assistant lists', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { dashboard: '' });
  await page.locator('#canvas .wb').first().click(); // selects the widget and opens its settings
  // a real list: "Choose a dashboard…", the three views, "Another path…"
  const select = page.locator('.field select').filter({ has: page.locator('option', { hasText: 'Another path…' }) });
  await expect(select.locator('option')).toHaveCount(5);
  await expect(select.locator('option').nth(2)).toHaveText('Desk panel › Home (desk-panel/home)');
  await select.selectOption('desk-panel/home');
  const opens = page.getByText('The phone opens:');
  await expect(opens).toContainText('http://192.168.1.10:8123/desk-panel/home?kiosk');
  // the widget's own address wins, as on the phone, and the line follows it as it is typed
  await page.locator('.field', { hasText: 'Home Assistant address for the phone' }).locator('input').fill('http://10.0.0.5:8123/');
  await expect(opens).toContainText('http://10.0.0.5:8123/desk-panel/home?kiosk');
  // any other path
  await select.selectOption({ label: 'Another path…' });
  const other = page.locator('input[placeholder="dashboard/view"]');
  await expect(other).toBeVisible();
  await other.fill('energy/0');
  await expect(opens).toContainText('http://10.0.0.5:8123/energy/0?kiosk');
  // the kiosk switch changes the line too, both ways
  const kiosk = page.locator('label', { hasText: "Hide Home Assistant's header and sidebar" }).locator('input');
  await kiosk.uncheck();
  await expect(opens).toContainText('http://10.0.0.5:8123/energy/0');
  await expect(opens).not.toContainText('?kiosk');
  await kiosk.check();
  await expect(opens).toContainText('http://10.0.0.5:8123/energy/0?kiosk');
  // what Home Assistant needs, with links to the two add-ons and the guide
  await expect(page.getByRole('link', { name: 'PhoneBox embed' })).toHaveAttribute('href', 'https://github.com/dancho12/ha-phonebox-embed');
  await expect(page.getByRole('link', { name: 'kiosk-mode' })).toHaveAttribute('href', 'https://github.com/NemesisRE/kiosk-mode');
  // valid YAML, on two lines as it is pasted into configuration.yaml
  await expect(page.locator('.hint pre')).toHaveText('http:\n  use_x_frame_options: false');
  await expect(page.getByRole('link', { name: 'Setup guide' })).toHaveAttribute('href', /docs\/HOME-ASSISTANT\.md$/);
  expect(errors).toEqual([]);
});

test('a saved path Home Assistant does not list shows as another path', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { dashboard: 'old-panel/0' });
  await page.locator('#canvas .wb').first().click();
  await expect(page.locator('input[placeholder="dashboard/view"]')).toHaveValue('old-panel/0');
  const select = page.locator('.field select').filter({ has: page.locator('option', { hasText: 'Another path…' }) });
  await expect(select.locator('option:checked')).toHaveText('Another path… old-panel/0');
});

test('without an address of its own the widget uses the first address that answers', async ({ page, baseURL }) => {
  // the name from Settings does not answer on this network; the address the computer found for it does
  const server = new URL(baseURL).origin;
  await open(page, '/panel/', { dashboard: 'desk-panel/home' }, { url: 'http://127.0.0.1:9', token: 't', alternatives: [server] });
  await expect(page.locator('.w-hadash iframe')).toHaveAttribute('src', server + '/desk-panel/home?kiosk');
});

// Home Assistant's own sign-in: no external_auth in the frame's address, and nothing asks the agent for the token
test('the dashboard signs in on its own: no external auth, no token request', async ({ page, baseURL }) => {
  const asked = []; page.on('request', (r) => { if (/sign-in|token/i.test(r.url())) asked.push(r.url()); });
  const server = new URL(baseURL).origin;
  await open(page, '/panel/', { dashboard: 'desk-panel/home' }, { url: server, token: 'secret-token' });
  await expect(page.locator('.w-hadash iframe')).toHaveAttribute('src', server + '/desk-panel/home?kiosk');
  expect(asked).toEqual([]);
});

test('on Android 6 the dashboard is not opened', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL,
    userAgent: 'Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5 Build/M4B30Z; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/95.0.4638.74 Mobile Safari/537.36' });
  const page = await context.newPage();
  await open(page, '/panel/', { address: new URL(baseURL).origin });
  await expect(page.locator('.w-hadash .empty')).toHaveText('A web page needs Android 7 or later');
  await expect(page.locator('.w-hadash iframe')).toHaveCount(0);
  await context.close();
});

test('a dashboard on a neighbouring page does not ask Home Assistant until its page is shown', async ({ page, baseURL }) => {
  const server = new URL(baseURL).origin;
  const asked = []; page.on('request', (r) => { if (r.url().endsWith('/manifest.json')) asked.push(r.url()); });
  await page.goto('/panel/');
  await page.evaluate((c) => { localStorage.clear(); localStorage.setItem('pb-demo-config', JSON.stringify(c)); }, {
    night: { enabled: false }, shortcuts: [], homeAssistant: { url: server, token: null }, pages: [
      { id: 'home', title: 'Home', enabled: true, widgets: [{ id: 'n', type: 'note', x: 0, y: 0, w: 6, h: 5, options: { text: 'Home' } }] },
      { id: 'ha', title: 'HA', enabled: true, widgets: [{ id: 'dash', type: 'hadash', x: 0, y: 0, w: 12, h: 6, options: { dashboard: 'desk-panel/home' } }] },
    ] });
  await page.reload();
  await expect(page.locator('#dots i')).toHaveCount(2);
  await page.waitForTimeout(2500);
  expect(asked).toEqual([]);
  await expect(page.locator('.w-hadash iframe')).toHaveCount(0);
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#dots i').nth(1)).toHaveClass('on');
  await expect(page.locator('.w-hadash iframe')).toHaveAttribute('src', server + '/desk-panel/home?kiosk');
  expect(asked.length).toBe(1);
});

test('the settings say so when the dashboard list cannot be refreshed', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { dashboard: '' });
  await page.locator('#canvas .wb').first().click();
  const select = page.locator('.field select').filter({ has: page.locator('option', { hasText: 'Another path…' }) });
  await expect(select.locator('option')).toHaveCount(5);
  const failWith = (how) => page.evaluate((how) => {
    const inner = window.__haFetch || (window.__haFetch = window.fetch);
    window.fetch = (input, init) => String(input).includes('/api/ha/dashboards')
      ? (how === 'reject' ? Promise.reject(new TypeError('Failed to fetch')) : Promise.resolve(new Response('no', { status: 500 })))
      : inner(input, init);
  }, how);
  for (const how of ['reject', 'status']) {
    await failWith(how);
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect(page.getByText('Could not reach the agent.')).toBeVisible();
    await expect(select.locator('option')).toHaveCount(5); // the list from before stays
    await page.evaluate(() => { window.fetch = window.__haFetch; });
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect(page.getByText('Could not reach the agent.')).toHaveCount(0);
  }
});
