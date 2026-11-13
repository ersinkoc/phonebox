// UI tests of the panel and the settings page, against the web demo. They only use what a person sees and does
// (DOM, keys, clicks) plus the demo's PhoneBoxDemo helper, never the panel's internals.
const { test, expect } = require('@playwright/test');

/** Fails the test on any console error or uncaught exception (outside services being down does not count). */
function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const url = m.location()?.url || '';
    if (/Failed to load resource/.test(m.text()) && url && !url.startsWith('http://127.0.0.1:8781')) return;
    errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

const activeDot = (page) => page.locator('#dots i').evaluateAll((dots) => dots.findIndex((d) => d.classList.contains('on')));

test.beforeEach(async ({ page }) => {
  // every test starts from the demo's default layout
  await page.goto('/panel/');
  await page.evaluate(() => { try { localStorage.clear(); } catch (_) {} });
});

test('the panel starts without errors and shows its pages', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  expect(await page.locator('#dots i').count()).toBeGreaterThan(10);
  await page.waitForTimeout(1500); // a few state updates
  expect(errors).toEqual([]);
});

test('no widget fails to build on any page', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // a widget whose create() throws shows "<type>: <message>"
  const broken = await page.locator('.w > .empty').evaluateAll((els) => els.map((e) => e.textContent).filter((t) => /^[a-z]+: /.test(t)));
  expect(broken).toEqual([]);
});

test('every widget in the catalogue builds and runs without errors', async ({ page }) => {
  test.setTimeout(90_000); // Each catalog page runs its render and polling lifecycle; the catalog now exceeds 100 types.
  const errors = watchErrors(page);
  await page.goto('/panel/');
  // one page per widget type, at its default size, through the demo's stored settings
  const count = await page.evaluate(async () => {
    const widgets = await (await fetch('../widgets.json')).json();
    const opts = (w) => Object.fromEntries((w.options || []).map((o) => [o.key, o.default]));
    const pages = widgets.map((w, i) => ({ id: 'w' + i, title: w.name, icon: w.icon, enabled: true,
      widgets: [{ id: w.type, type: w.type, x: 0, y: 0, w: w.w, h: w.h, options: opts(w) }] }));
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null');
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...(cfg || {}), pages, shortcuts: cfg?.shortcuts || [] }));
    return widgets.length;
  });
  await page.reload();
  await expect(page.locator('#dots i')).toHaveCount(count);
  // visit every page so each widget gets data and renders
  for (let i = 0; i < count; i++) { await page.keyboard.press('ArrowRight'); await page.waitForTimeout(60); }
  await page.waitForTimeout(1500);
  const broken = await page.locator('.w > .empty').evaluateAll((els) => els.map((e) => e.textContent).filter((t) => /^[a-z]+: /.test(t)));
  expect(broken).toEqual([]);
  expect(errors).toEqual([]);
});

test('keys flip pages and the overview jumps to a page', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // the demo's settings are loaded (until then the built-in defaults may show the night screen after 00:30)
  await expect.poll(() => page.locator('#dots i').count()).toBeGreaterThan(10);
  await expect(page.locator('body')).not.toHaveClass(/night/);
  expect(await activeDot(page)).toBe(0);
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => activeDot(page)).toBe(1);
  await page.keyboard.press('ArrowLeft');
  await expect.poll(() => activeDot(page)).toBe(0);

  await page.keyboard.press('o');
  await expect(page.locator('#overview')).toBeVisible();
  await page.locator('.ov-tile').nth(3).click();
  await expect(page.locator('#overview')).toBeHidden();
  await expect.poll(() => activeDot(page)).toBe(3);
});

test('a webhook notification shows a toast', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('notify', { title: 'Build #7', text: 'main passed', level: 'ok' }));
  await expect(page.locator('#toast')).toContainText('main passed');
});

test('a webhook switches pages after a countdown', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 3 }));
  await expect(page.locator('#navCard')).toHaveClass(/on/);
  await expect(page.locator('#nvLine')).toContainText('Switching to');
  expect(await activeDot(page)).toBe(0);                     // not yet: it counts down first
  await expect.poll(() => activeDot(page), { timeout: 8000 }).toBe(2);
  await expect(page.locator('#navCard')).not.toHaveClass(/on/);
});

test('Stay cancels a rule page switch', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 2, rule: 'music', name: 'Music', hold: true, countdown: 5 }));
  await expect(page.locator('#nvName')).toHaveText('Music');
  await page.locator('#nvStay').click();
  await expect(page.locator('#navCard')).not.toHaveClass(/on/);
  await page.waitForTimeout(1500);
  expect(await activeDot(page)).toBe(0);
});

test('a rule waits while the panel is in use and then goes on request', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.mouse.click(5, 300);                             // somebody is using the panel
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 4, rule: 'claude', name: 'Claude needs me', hold: true }));
  await expect(page.locator('#navChip')).toHaveClass(/on/);
  await expect(page.locator('#ncText')).toContainText('Claude needs me');
  await expect(page.locator('#navCard')).not.toHaveClass(/on/);
  await page.locator('#ncGo').click();
  await expect.poll(() => activeDot(page)).toBe(3);
});

test('rules firing one after another do not bounce the screen', async ({ page }) => {
  test.setTimeout(45_000);
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // a rule that ends before its turn is dropped
  await page.evaluate(() => {
    window.PhoneBoxDemo.emit('goto', { page: 2, rule: 'flappy', hold: true, countdown: 2 });
    window.PhoneBoxDemo.emit('back', { rule: 'flappy' });
  });
  await page.waitForTimeout(2500);
  expect(await activeDot(page)).toBe(0);
  // the higher of two rules goes first; the lower one waits for the minimum time between switches
  await page.evaluate(() => {
    window.PhoneBoxDemo.emit('goto', { page: 2, rule: 'low', name: 'Low', priority: 2, hold: true, countdown: 0 });
    window.PhoneBoxDemo.emit('goto', { page: 5, rule: 'high', name: 'High', priority: 8, hold: true, countdown: 0 });
  });
  await expect.poll(() => activeDot(page)).toBe(4);
  await page.waitForTimeout(1500);
  expect(await activeDot(page)).toBe(4);                      // the lower rule does not take the page over
  // the high rule ends: the low one (still on) gets its turn instead of going back and forth
  await page.evaluate(() => window.PhoneBoxDemo.emit('back', { rule: 'high' }));
  await expect.poll(() => activeDot(page), { timeout: 20000 }).toBe(1);
});

test('a Claude Code permission prompt can be answered', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.ask());
  const prompt = page.locator('#approval');
  await expect(prompt).toBeVisible();
  await prompt.getByRole('button', { name: 'Allow' }).click();
  await expect(prompt).toBeHidden();
});

test('a question from Claude is shown with its choices and left for the terminal', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.ask({ kind: 'question', tool: 'Claude has a question', project: 'api-gateway',
    detail: 'Framework: Which framework?\n  • React — Component library\n  • Vue — Progressive framework' }));
  const prompt = page.locator('#approval');
  await expect(prompt).toBeVisible();
  await expect(prompt).toContainText('React — Component library');
  await expect(prompt.getByRole('button', { name: 'Allow' })).toBeHidden();
  await expect(prompt.getByRole('button', { name: 'Deny' })).toBeHidden();
  await prompt.getByRole('button', { name: 'Got it' }).click();
  await expect(prompt).toBeHidden();
});

test('a GPU without sensors (Apple) shows its memory, not 0 degrees', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const labels = await page.evaluate(async () => {
    const { METRICS } = await import('/panel/main.js');
    const apple = { pc: { gpu: { name: 'Apple M2 Pro', util: 37, temp: null, power: null, vramUsed: 2, vramTotal: 16, fan: null, clock: null }, ramUsedGb: 9.46, ramTotalGb: 16 } };
    const nvidia = { pc: { gpu: { name: 'RTX', util: 5, temp: 61.4, power: 120.2, vramUsed: 2, vramTotal: 16, fan: 30, clock: 1800 } } };
    return { gpu: METRICS.gpu.sub(apple), temp: METRICS.gputemp.sub(apple), ram: METRICS.ram.sub(apple), nvidia: METRICS.gpu.sub(nvidia), nvidiaTemp: METRICS.gputemp.sub(nvidia) };
  });
  expect(labels).toEqual({ gpu: '2 / 16 GB memory', temp: 'No GPU data', ram: '9.5 / 16 GB', nvidia: '61°C · 120 W', nvidiaTemp: 'Fan 30% · 1800 MHz' });
});

test('portrait arranges the widgets in six columns', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const cols = await page.locator('.grid').first().evaluate((g) => getComputedStyle(g).gridTemplateColumns.split(' ').length);
  expect(cols).toBe(6);
  await page.setViewportSize({ width: 780, height: 360 });
  await expect.poll(() => page.locator('.grid').first().evaluate((g) => getComputedStyle(g).gridTemplateColumns.split(' ').length)).toBe(12);
});

test('the settings page loads and its sections open', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#pages');
  await expect(page.locator('#pageTabs .ptab')).not.toHaveCount(0);
  for (const section of ['Shortcuts', 'Rules', 'Webhooks', 'General', 'Advanced', 'Pages']) {
    await page.locator('#nav a', { hasText: section }).click();
    await expect(page.locator('section.on h2')).toHaveText(section);
  }
  expect(errors).toEqual([]);
});

test('an edit in the settings page shows up on the panel right away', async ({ context }) => {
  const panel = await context.newPage();
  await panel.goto('/panel/');
  await expect(panel.locator('.w').first()).toBeVisible();

  const settings = await context.newPage();
  await settings.setViewportSize({ width: 1280, height: 860 });
  await settings.goto('/settings/#pages');
  await expect(settings.locator('#pageTabs .ptab')).not.toHaveCount(0);
  const title = settings.locator('#peHead input[placeholder="Page name"]');
  await title.fill('Edited in a test');
  await settings.locator('#save').click();

  // the overview lists the new name once the panel has the new settings (it is built when it opens)
  await expect.poll(async () => {
    await panel.keyboard.press('o');
    const text = await panel.locator('.ov-tile').first().textContent();
    await panel.keyboard.press('Escape');
    return text;
  }, { timeout: 10_000 }).toContain('Edited in a test');
});

test('the live preview in the layout editor runs the real panel', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#pages');
  await expect(page.locator('#pageTabs .ptab')).not.toHaveCount(0);
  await page.locator('#stageSeg button', { hasText: 'Live preview' }).click();
  const preview = page.frameLocator('#previewBox iframe');
  await expect(preview.locator('.w').first()).toBeVisible();
  // picking another page in the editor moves the preview there
  await page.locator('#pageTabs .ptab').nth(2).click();
  await expect.poll(() => preview.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')))).toBe(2);
});

test('rule options and page switching settings are there', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#rules');
  await page.locator('#rulePresets button', { hasText: 'Review requested' }).click();
  const rule = page.locator('#ruleList .rule').last();
  await expect(rule.locator('.ropts')).toHaveAttribute('open', '');
  await expect(rule.locator('.ropts summary')).toContainText('09:00-18:00');
  await expect(rule.locator('.day.on')).toHaveCount(5);
  await rule.locator('.day', { hasText: 'Sa' }).click();
  await expect(rule.locator('.day.on')).toHaveCount(6);
  await page.goto('/settings/#general');
  await expect(page.locator('#homePageSel option')).not.toHaveCount(0);
  await expect(page.locator('[data-bind="display.switchCountdownSec"]')).toHaveValue(/\d/);
});

test('rules can be added from the examples', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#rules');
  await expect(page.locator('#rulePresets button').first()).toBeVisible();
  const before = await page.locator('#ruleList .rule').count();
  await page.locator('#rulePresets button').first().click();
  await expect(page.locator('#ruleList .rule')).toHaveCount(before + 1);
  await expect(page.locator('#save')).toBeEnabled();
});

// The agent sends all-day events as the UTC midnight of their date; the panel has to show them on that date wherever
// the phone is (audit F9: west of UTC they showed a day early, east of UTC they ran into the next day).
for (const [zone, noon] of [['America/New_York', '2026-11-09T12:00:00-05:00'], ['Pacific/Auckland', '2026-11-09T12:00:00+13:00']]) {
  test(`all-day events land on their own date (${zone})`, async ({ browser, baseURL }) => {
    const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 }, timezoneId: zone });
    const page = await ctx.newPage();
    await page.clock.setFixedTime(new Date(noon));
    await page.goto('/panel/');
    await expect(page.locator('.w').first()).toBeVisible();
    // The first widgets belong to built-in defaults; wait for the demo's full page list before navigating.
    await expect.poll(() => page.locator('#dots i').count()).toBeGreaterThan(10);
    await expect(page.locator('body')).not.toHaveClass(/night/);
    const planner = await page.locator('#dots i').count() - 4; // Planner is the 5th page from the end of the demo
    for (let i = 0; i < planner; i++) await page.keyboard.press('ArrowRight');
    const heading = page.locator('.ag-day, .ag-row').filter({ hasText: /Release day|^(Today|Tomorrow)$/ });
    await expect(page.locator('.ag-row', { hasText: 'Release day' })).toBeVisible();
    const order = await heading.evaluateAll((els) => els.map((e) => e.textContent.trim()));
    expect(order[order.findIndex((t) => t.includes('Release day')) - 1]).toBe('Tomorrow');
    await ctx.close();
  });
}

test('clicking a RAM widget navigates to the Processes page sorted by RAM', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect.poll(() => page.locator('#dots i').count()).toBeGreaterThan(10);
  const ramLabel = page.locator('.w .t-label', { hasText: /^RAM/ }).first();
  await expect(ramLabel).toBeVisible();
  await expect(ramLabel).toContainText('%');
  await ramLabel.locator('..').click();
  const procHead = page.locator('.w .p-head');
  await expect(procHead).toBeVisible();
  await expect(procHead.locator('.seg button.on')).toHaveText('RAM');
  await expect(procHead.locator('.p-sum')).toContainText('Total RAM');
});

test('the landing page embeds the panel', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto('/');
  await expect(page.frameLocator('#panel').locator('.w').first()).toBeVisible();
  await page.getByRole('button', { name: /notification/ }).click();
  await expect(page.frameLocator('#panel').locator('#toast')).toBeVisible();
  expect(errors).toEqual([]);
});
