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

test('the landing page embeds the panel', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto('/');
  await expect(page.frameLocator('#panel').locator('.w').first()).toBeVisible();
  await page.getByRole('button', { name: /notification/ }).click();
  await expect(page.frameLocator('#panel').locator('#toast')).toBeVisible();
  expect(errors).toEqual([]);
});

function mockWeatherRoute(page) {
  return page.route('**/api.open-meteo.com/**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      current: { time: '2026-05-15T14:00', temperature_2m: 18, apparent_temperature: 17, weather_code: 1, wind_speed_10m: 9, relative_humidity_2m: 55, is_day: 1 },
      hourly: {
        time: ['2026-05-15T14:00', '2026-05-15T15:00', '2026-05-15T16:00', '2026-05-15T17:00', '2026-05-15T18:00', '2026-05-15T19:00'],
        temperature_2m: [18, 19, 19, 17, 16, 14],
        weather_code: [1, 1, 2, 2, 2, 0],
        precipitation_probability: [0, 0, 10, 10, 0, 0],
        is_day: [1, 1, 1, 1, 1, 1],
      },
      daily: {
        time: ['2026-05-15', '2026-05-16', '2026-05-17'],
        temperature_2m_max: [20, 21, 19],
        temperature_2m_min: [10, 11, 9],
        weather_code: [1, 2, 0],
        precipitation_probability_max: [10, 20, 0],
        sunrise: ['2026-05-15T05:15'],
        sunset: ['2026-05-15T21:45'],
        daylight_duration: [59400],
        uv_index_max: [5],
      },
    }),
  }));
}

test('display.hour12 formats all clock and time widgets and supports per-widget overrides', async ({ browser, baseURL }) => {
  const ctx = await browser.newContext({ baseURL, viewport: { width: 1280, height: 800 }, timezoneId: 'UTC', locale: 'en-GB' });
  const page = await ctx.newPage();
  const errors = watchErrors(page);
  await mockWeatherRoute(page);
  await page.clock.setFixedTime(new Date('2026-05-15T14:30:45Z'));

  const hour12Widgets = [
    { id: 'c12', type: 'clock', x: 0, y: 0, w: 4, h: 2, options: { status: false, weather: false, date: false } },
    { id: 'c24', type: 'clock', x: 4, y: 0, w: 4, h: 2, options: { status: false, weather: false, date: false, hour12: '24h' } },
    { id: 'an12', type: 'analog', x: 8, y: 0, w: 2, h: 2, options: { seconds: true } },
    { id: 'an24', type: 'analog', x: 10, y: 0, w: 2, h: 2, options: { seconds: true, hour12: '24h' } },
    { id: 'wc', type: 'worldclock', x: 0, y: 2, w: 6, h: 1, options: { zones: ['London=UTC', 'East=Etc/GMT-2'] } },
    { id: 'cd', type: 'countdown', x: 6, y: 2, w: 6, h: 1, options: { label: 'Launch', target: '2026-12-31 15:45' } },
    { id: 'ag', type: 'agenda', x: 0, y: 3, w: 4, h: 3, options: { title: 'Agenda' } },
    { id: 'nu', type: 'nextup', x: 4, y: 3, w: 4, h: 1, options: { title: 'Next up' } },
    { id: 'fc', type: 'forecast', x: 4, y: 4, w: 4, h: 2, options: { mode: 'hourly' } },
    { id: 'su', type: 'sun', x: 8, y: 3, w: 4, h: 1, options: {} },
    { id: 'ru', type: 'rules', x: 8, y: 4, w: 4, h: 1, options: { title: 'Rules' } },
    { id: 'qu', type: 'quotas', x: 8, y: 5, w: 4, h: 1, options: { providers: ['claude'], rotate: false, pageSize: 1 } },
  ];

  // First load demo defaults, then switch config to a single test page with display.hour12: true.
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate((widgets) => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    cfg.night = { enabled: false, start: '00:30', end: '07:30', brightness: 0.02 };
    cfg.display = { ...(cfg.display || {}), showSeconds: true, hour12: true };
    cfg.pages = [{ id: 'p1', title: 'All Clocks', icon: '🕒', enabled: true, widgets }];
    localStorage.setItem('pb-demo-config', JSON.stringify(cfg));
  }, hour12Widgets);

  await page.reload();
  const clocks = page.locator('.w-clock');
  await expect(clocks).toHaveCount(2);

  // Global 12-hour clock vs per-widget 24-hour override
  await expect(clocks.nth(0).locator('.hh')).toHaveText('2');
  await expect(clocks.nth(0).locator('.mm')).toHaveText('30');
  await expect(clocks.nth(0).locator('.sec')).toHaveText('45');
  await expect(clocks.nth(0).locator('.ampm')).toHaveText('PM');
  await expect(clocks.nth(1).locator('.hh')).toHaveText('14');
  await expect(clocks.nth(1).locator('.ampm')).toHaveText('');

  // Analog AM/PM indicator respects global and override
  await expect(page.locator('.w-analog .ap').nth(0)).toHaveText('PM');
  await expect(page.locator('.w-analog .ap').nth(1)).toHaveText('');

  // World clocks show 12h AND preserve exact +2h offset arithmetic
  await expect(page.locator('.w-worldclock .wc-t').nth(0)).toHaveText('2:30 PM');
  await expect(page.locator('.w-worldclock .wc-t').nth(1)).toHaveText('4:30 PM');
  await expect(page.locator('.w-worldclock .wc-s').nth(1)).toHaveText('Today, +2h');

  // Countdown target time uses 12h
  await expect(page.locator('.w-countdown .cd-s')).toContainText('3:45 PM');

  // Agenda & Next up use 12h (and Agenda widens column class h12)
  await expect(page.locator('.w-agenda')).toHaveClass(/\bh12\b/);
  await expect(page.locator('.w-agenda .ag-when').first()).toContainText(/(AM|PM)/);
  await expect(page.locator('.w-nextup .nu-meta')).toContainText(/(AM|PM)/);

  // Sun & hourly Forecast use 12h
  await expect(page.locator('.w-sun .sr')).toHaveText('↑ 5:15 AM');
  await expect(page.locator('.w-sun .ss')).toHaveText('9:45 PM ↓');
  await expect(page.locator('.w-forecast .fc-t').nth(1)).toHaveText('4 PM');

  // Quotas reset time uses 12h
  await expect(page.locator('.w-quotas .q-reset').first()).toContainText(/(AM|PM)/);

  // Rules widget & Overview pause button show 12h time when paused
  await expect(page.locator('.w-rules .ru-pause')).toBeVisible();
  await page.locator('.w-rules .ru-pause').click();
  await expect(page.locator('.w-rules .ru-note')).toContainText(/Paused until .* (AM|PM)/);
  await page.keyboard.press('o');
  await expect(page.locator('#ovRules')).toContainText(/paused until .* (AM|PM)/i);
  await page.keyboard.press('Escape');

  // Night screen #nClock formats 2:30 PM at 14:30 when night window covers 14:00-15:00
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    cfg.night = { enabled: true, start: '14:00', end: '15:00', brightness: 0.02 };
    localStorage.setItem('pb-demo-config', JSON.stringify(cfg));
  });
  await page.reload();
  await expect(page.locator('body')).toHaveClass(/night/);
  await expect(page.locator('#nClock')).toHaveText('2:30 PM');

  expect(errors).toEqual([]);
  await ctx.close();
});

test('midnight 12-hour formatting, default 24h mode, and Settings Display selector', async ({ browser, baseURL }) => {
  const ctx = await browser.newContext({ baseURL, viewport: { width: 1280, height: 860 }, timezoneId: 'UTC', locale: 'en-US' });
  const page = await ctx.newPage();
  const errors = watchErrors(page);
  await page.clock.setFixedTime(new Date('2026-05-15T00:05:09Z'));

  // 1) Settings page selector defaults to 24h (false), can switch to 12h (true) and auto ('auto')
  await page.goto('/settings/#general');
  const sel = page.locator('[data-bind="display.hour12"]');
  await expect(sel).toBeVisible();
  await expect(sel).toHaveValue('false');

  await sel.selectOption('true');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const savedTrue = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config') || '{}').display?.hour12);
  expect(savedTrue).toBe(true);

  // 2) Verify midnight 00:05:09 renders as 12:05 09 AM on panel
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    cfg.night = { enabled: false, start: '01:00', end: '02:00', brightness: 0.02 };
    cfg.pages = [{ id: 'p1', title: 'Midnight', icon: '🕒', enabled: true, widgets: [
      { id: 'c12', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: {} },
      { id: 'c24', type: 'clock', x: 6, y: 0, w: 6, h: 4, options: { hour12: '24h' } },
    ] }];
    localStorage.setItem('pb-demo-config', JSON.stringify(cfg));
  });
  await page.goto('/panel/');
  await expect(page.locator('.w-clock').nth(0).locator('.hh')).toHaveText('12');
  await expect(page.locator('.w-clock').nth(0).locator('.mm')).toHaveText('05');
  await expect(page.locator('.w-clock').nth(0).locator('.ampm')).toHaveText('AM');
  await expect(page.locator('.w-clock').nth(1).locator('.hh')).toHaveText('00');
  await expect(page.locator('.w-clock').nth(1).locator('.ampm')).toHaveText('');

  // 3) Check "auto" mode saves string 'auto' and follows en-US browser Intl (12h -> 12:05 AM)
  await page.goto('/settings/#general');
  await expect(page.locator('[data-bind="display.hour12"]')).toHaveValue('true');
  await page.locator('[data-bind="display.hour12"]').selectOption('auto');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const savedAuto = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config') || '{}').display?.hour12);
  expect(savedAuto).toBe('auto');

  await page.goto('/panel/');
  await expect(page.locator('.w-clock').nth(0).locator('.hh')).toHaveText('12');
  await expect(page.locator('.w-clock').nth(0).locator('.ampm')).toHaveText('AM');

  expect(errors).toEqual([]);
  await ctx.close();
});
