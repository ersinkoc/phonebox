// The shared "Tracked AI services" list (#351): a Clock or AI quotas widget without its own Providers list follows it,
// one with its own list keeps that, and with the default every provider shows as before.
const { test, expect } = require('@playwright/test');

const NOW = Date.now();
const limit = (percent) => [{ label: '5 hour', percent, resetsAt: NOW + 3600_000 }];
const QUOTAS = {
  providers: [
    { id: 'claude', name: 'Claude', plan: 'Max', updatedAt: NOW, limits: limit(40) },
    { id: 'codex', name: 'Codex', plan: 'Plus', updatedAt: NOW, limits: limit(0) },
    { id: 'claude@work', name: 'Claude (work)', plan: 'Team', updatedAt: NOW, limits: limit(12) },
  ],
};

/** A panel with one widget, the given shared list (`undefined` = no codexBar key at all) and the demo quotas. */
async function panel(page, widget, shared) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ widget, shared }) => {
    localStorage.clear();
    const cfg = { night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
      pages: [{ id: 'p', title: 'P', widgets: [{ id: 'w', x: 0, y: 0, w: 12, h: 6, ...widget }] }] };
    if (shared !== undefined) cfg.codexBar = { providers: shared, rotate: false, pageSize: 0, rotateInterval: 8 };
    localStorage.setItem('pb-demo-config', JSON.stringify(cfg));
  }, { widget, shared });
  await page.goto('/panel/');
  await expect(page.locator(`.w-${widget.type}`)).toHaveCount(1);
  await page.evaluate(async (q) => {
    const { D, emit } = await import('/panel/main.js');
    D.quotas = q;
    emit('quotas');
  }, QUOTAS);
}

const clock = (options = {}) => ({ type: 'clock', options: { status: true, date: false, weather: false, ...options } });
const quotas = (options = {}) => ({ type: 'quotas', options: { rotate: false, pageSize: 0, ...options } });
const statusQuotas = (page) => page.locator('.w-clock .status .qm');
const cards = (page) => page.locator('.w-quotas .q-card:not(.q-overview-card) .q-name');

test('a clock without its own list follows the shared list', async ({ page }) => {
  await panel(page, clock(), ['claude']);
  await expect(statusQuotas(page)).toHaveCount(2); // Claude and the work Claude account
  await expect(statusQuotas(page).first()).toContainText('Claude');
  await expect(page.locator('.w-clock .status')).not.toContainText('Codex');
});

test('with the default shared list a clock shows every provider, as before', async ({ page }) => {
  await panel(page, clock(), undefined);
  await expect(statusQuotas(page)).toHaveCount(3);
  await expect(page.locator('.w-clock .status')).toContainText('Codex');
});

test("a widget's own list wins over the shared one", async ({ page }) => {
  await panel(page, clock({ providers: ['codex'] }), ['claude']);
  await expect(statusQuotas(page)).toHaveCount(1);
  await expect(statusQuotas(page)).toContainText('Codex');
});

test('the AI quotas widget follows the shared list', async ({ page }) => {
  await panel(page, quotas(), ['codex']);
  await expect(cards(page)).toHaveText(['Codex']);
});

test("the AI quotas widget's own list wins", async ({ page }) => {
  await panel(page, quotas({ providers: ['claude'] }), ['codex']);
  await expect(cards(page)).toHaveText(['Claude', 'Claude (work)']);
});

test('a widget follows a change of the shared list without a reload', async ({ page }) => {
  await panel(page, clock(), ['claude']);
  await expect(statusQuotas(page)).toHaveCount(2);
  await page.evaluate(async () => {
    const { cfg, emit } = await import('/panel/main.js');
    cfg.codexBar.providers = ['codex'];
    emit('quotas');
  });
  await expect(statusQuotas(page)).toHaveCount(1);
  await expect(statusQuotas(page)).toContainText('Codex');
});

// ---- the editor (settings.html)

/** Opens the settings editor with the shared list set to `shared`. */
async function editor(page, shared) {
  await page.goto('/settings/#pages');
  await page.waitForFunction(() => typeof cfg === 'object' && cfg && typeof optionField === 'function' && typeof widgetDef === 'function' && widgetDef('clock'));
  await page.evaluate((shared) => { cfg.codexBar = { ...(cfg.codexBar || {}), providers: shared }; }, shared);
}

/** Draws the inspector field of option `key` for widget `w` into the page; keeps the widget in window.__w. */
async function inspect(page, w, key) {
  await page.evaluate(({ w, key }) => {
    window.__w = w;
    const box = document.createElement('div');
    box.id = 'probe';
    box.append(optionField(w, widgetDef(w.type).options.find((o) => o.key === key)));
    document.body.prepend(box);
  }, { w, key });
  return page.locator('#probe');
}

test('a new Clock, AI quotas or CodexBar widget is saved without a Providers list, so it follows the shared one', async ({ page }) => {
  await editor(page, ['claude']);
  const keys = await page.evaluate(() => ['clock', 'quotas', 'codexbar', 'quotaring'].map((t) => [t, 'providers' in defaultOptions(t), 'provider' in defaultOptions(t)]));
  expect(keys).toEqual([['clock', false, false], ['quotas', false, false], ['codexbar', false, false], ['quotaring', false, true]]);
});

test('Follow on: the boxes show the shared list and cannot be changed', async ({ page }) => {
  await editor(page, ['claude']);
  const probe = await inspect(page, { type: 'clock', options: { status: true } }, 'providers');
  const follow = probe.getByRole('checkbox', { name: 'Follow my AI providers' });
  await expect(follow).toBeChecked();
  await expect(probe.getByText(/Shows the services ticked under “Tracked AI services”/)).toBeVisible();
  const claude = probe.getByRole('checkbox', { name: 'Claude' }), codex = probe.getByRole('checkbox', { name: 'Codex' });
  await expect(claude).toBeChecked();
  await expect(codex).not.toBeChecked();
  await expect(claude).toBeDisabled();
  await expect(codex).toBeDisabled();
});

test("unticking Follow gives the widget its own list, starting from the shared one; ticking it again removes the list", async ({ page }) => {
  await editor(page, ['claude']);
  const probe = await inspect(page, { type: 'quotas', options: {} }, 'providers');
  const follow = probe.getByRole('checkbox', { name: 'Follow my AI providers' });
  await follow.uncheck();
  expect(await page.evaluate(() => window.__w.options.providers)).toEqual(['claude']);
  const codex = probe.getByRole('checkbox', { name: 'Codex' });
  await expect(codex).toBeEnabled();
  await codex.check();
  expect(await page.evaluate(() => window.__w.options.providers)).toEqual(['claude', 'codex']);
  await follow.check();
  expect(await page.evaluate(() => 'providers' in window.__w.options)).toBe(false);
  await expect(codex).not.toBeChecked();
  await expect(codex).toBeDisabled();
});

test('a widget saved with its own list keeps it: Follow is off', async ({ page }) => {
  await editor(page, ['claude']);
  const probe = await inspect(page, { type: 'clock', options: { providers: ['claude', 'codex', 'antigravity', 'grok'] } }, 'providers');
  await expect(probe.getByRole('checkbox', { name: 'Follow my AI providers' })).not.toBeChecked();
  await expect(probe.getByRole('checkbox', { name: 'Codex' })).toBeChecked();
});

test('a hand-edited "providers": null follows the shared list, as on the phone', async ({ page }) => {
  await editor(page, ['claude']);
  const probe = await inspect(page, { type: 'clock', options: { providers: null } }, 'providers');
  await expect(probe.getByRole('checkbox', { name: 'Follow my AI providers' })).toBeChecked();
  await expect(probe.getByRole('checkbox', { name: 'Claude' })).toBeChecked();
  await expect(probe.getByRole('checkbox', { name: 'Codex' })).not.toBeChecked();
});

test('the Quota ring offers only the providers that are on, plus Cursor and its own choice', async ({ page }) => {
  await editor(page, ['claude']);
  await page.evaluate(() => { // the demo answers /api/quotas inside the page
    const original = window.fetch;
    window.fetch = (url, opts) => String(url).endsWith('/api/quotas')
      ? Promise.resolve(Response.json({ providers: [{ id: 'codex@work', name: 'Codex (work)' }, { id: 'claude@work', name: 'Claude (work)' }] }))
      : original(url, opts);
  });
  const probe = await inspect(page, { type: 'quotaring', options: { provider: 'grok' } }, 'provider');
  // the catalog's order, then what the computer reports; Codex and Codex (work) are off, Grok stays as the ring's choice
  await expect(probe.locator('select option')).toHaveText(['Claude', 'Cursor', 'Grok', 'Claude (work)']);
  await expect(probe.locator('select')).toHaveValue('grok');
});

test('a Smart Stack card: ticking Follow again leaves no Providers list, and the card still shows Follow after a redraw', async ({ page }) => {
  await editor(page, ['claude']);
  const providers = () => page.evaluate(() => window.__w.options.widgets[0].options);
  const draw = () => page.evaluate(() => {
    document.getElementById('probe')?.remove();
    const w = window.__w, box = document.createElement('div');
    box.id = 'probe';
    activeStackSubIdx = 0; // the first card's settings are open
    box.append(renderStackWidgetsOption(w, widgetDef('stack').options.find((o) => o.key === 'widgets')));
    document.body.prepend(box);
  });
  await page.evaluate(() => { window.__w = { type: 'stack', options: { widgets: [{ id: 'card-1', type: 'clock', enabled: true, options: { status: true } }] } }; });
  await draw();
  const follow = () => page.locator('#probe').getByRole('checkbox', { name: 'Follow my AI providers' });
  await expect(follow()).toBeChecked();
  await follow().uncheck();
  expect((await providers()).providers).toEqual(['claude']);
  await follow().check();
  expect('providers' in await providers()).toBe(false);
  await draw(); // the card's settings are drawn again, as after closing and opening it
  await expect(follow()).toBeChecked();
  await expect(page.locator('#probe').getByRole('checkbox', { name: 'Claude' })).toBeChecked();
});

test('the page canvas names the providers a following AI quotas widget shows', async ({ page }) => {
  await editor(page, ['claude']);
  expect(await page.evaluate(() => widgetSummary({ type: 'quotas', options: {} }))).toBe('claude');
  expect(await page.evaluate(() => widgetSummary({ type: 'quotas', options: { providers: ['codex', 'grok'] } }))).toBe('codex + grok');
});
