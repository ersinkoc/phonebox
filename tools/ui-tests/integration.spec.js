const { test, expect } = require('@playwright/test');

async function layout(page, widgets, extra = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ widgets, extra }) => {
    localStorage.clear();
    // Widget interaction tests must not depend on the host's wall-clock night schedule.
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, ...extra, pages: [{ id: 'test', title: 'Test', enabled: true, widgets }], shortcuts: [] }));
  }, { widgets, extra });
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/);
}
const widget = (type, options = {}) => ({ id: 'test', type, x: 0, y: 0, w: 12, h: 6, options });

test('fixture initialization preserves its layout when default bootstrap completion is delayed', async ({ page }) => {
  const { gatedLayout } = require('./layout-ordering');
  expect(await gatedLayout(page, layout)).toEqual(['test']);
  await expect(page.locator('.w-note')).toHaveText('owned-layout');
});

for (const viewport of [{ width: 780, height: 360 }, { width: 360, height: 780 }]) {
  test(`stack renders children, changes cards and follows live data at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    const errors = []; page.on('pageerror', e => errors.push(String(e)));
    await layout(page, [widget('stack', { widgets: [{ type: 'clock' }, { type: 'stat', options: { metric: 'cpu' } }, { type: 'weather', enabled: false }] })]);
    await expect(page.locator('.stack-dot')).toHaveCount(2);
    await expect(page.locator('.stack-item.active .w-clock')).toHaveCount(1);
    await page.getByRole('button', { name: 'stat', exact: true }).click();
    await expect(page.locator('.stack-item.active .w-stat')).toHaveCount(1);
    await expect(page.locator('.stack-item.active .t-label')).toHaveText('CPU');
    await expect(page.locator('.stack-item.active .t-val')).not.toContainText('—');
    await page.locator('.stack-item.active').evaluate(el => Promise.all(el.getAnimations().map(a => a.finished)));
    await page.screenshot({ path: `test-results/stack-${viewport.width}.png` });
    expect(errors).toEqual([]);
  });
}

test('a stack with rotation interval 0 changes only when swiped (#171)', async ({ page }) => {
  await page.clock.install();
  await layout(page, [widget('stack', { interval: 0, widgets: [{ type: 'clock' }, { type: 'weather' }] })]);
  await expect(page.locator('.stack-item.active .w-clock')).toHaveCount(1);
  await page.clock.runFor(5 * 60_000);
  await expect(page.locator('.stack-item.active .w-clock')).toHaveCount(1);
  await page.getByRole('button', { name: 'weather', exact: true }).click();
  await expect(page.locator('.stack-item.active .w-weather')).toHaveCount(1);
});

test('stack rotates with the panel clock and an empty stack stays empty', async ({ page }) => {
  await page.clock.install();
  await layout(page, [widget('stack', { interval: 3, widgets: [{ type: 'clock' }, { type: 'weather' }] })]);
  await page.clock.fastForward(4000);
  await expect(page.locator('.stack-item.active .w-weather')).toHaveCount(1);
  await layout(page, [widget('stack', { widgets: [] })]);
  await expect(page.locator('.w-stack')).toContainText('no active widgets');
  await expect(page.locator('.stack-item')).toHaveCount(0);
});

test('a timer in a stack rings when another page is active', async ({ page }) => {
  await page.clock.install();
  await page.addInitScript(() => {
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
      pages: [
        { id: 'timer', title: 'Timer', widgets: [{ id: 'stack', type: 'stack', x: 0, y: 0, w: 12, h: 6, options: { widgets: [{ type: 'timer', options: { presets: '1' } }] } }] },
        { id: 'next', title: 'Next', widgets: [{ id: 'n', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Next' } }] },
        { id: 'away', title: 'Away', widgets: [{ id: 'a', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Away' } }] },
      ],
    }));
  });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).toHaveCount(3);
  await page.locator('.w-stack .w-timer').getByRole('button', { name: '1m' }).click();
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#dots i').nth(2)).toHaveClass('on');
  const later = await page.evaluate(() => Date.now() + 62_000);
  await page.clock.setSystemTime(later);
  await expect(page.locator('#dots i').nth(2)).toHaveClass('on');
  await page.evaluate(async () => (await import('/panel/main.js')).emit('tick'));
  await expect(page.locator('#toast')).toContainText('Time is up', { timeout: 1200 });
});

test('clock status line can hide quota providers', async ({ page }) => {
  await layout(page, [widget('clock', { providers: ['claude'] })]);
  const status = page.locator('.w-clock .status');
  await expect(status).toContainText('Claude');
  await expect(status).not.toContainText('Codex');
  await layout(page, [widget('clock', { providers: [] })]);
  // An early "no Claude" would pass before /api/quotas answers: wait for a rendered status
  // line and the quota payload, then both providers must be absent.
  await expect(page.locator('.w-clock .status')).toContainText('82%');
  await page.waitForFunction(async () => (await import('/panel/main.js')).D.quotas !== null);
  await expect(page.locator('.w-clock .status')).not.toContainText('Claude');
  await expect(page.locator('.w-clock .status')).not.toContainText('Codex');
});

test('quota selection preserves empty selections and explains missing data', async ({ page }) => {
  await layout(page, [widget('quotas', { providers: [] })]);
  await expect(page.locator('.w-quotas')).toContainText('No quota providers enabled');
  await layout(page, [widget('quotas', { providers: ['antigravity'] })]);
  await expect(page.locator('.w-quotas')).toContainText('Quota data is unavailable');
});

test('session sheet shows counters and keeps prompt content out of the panel', async ({ page }) => {
  await layout(page, [widget('sessions')]);
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    D.work = { sessions: [{ id: 'codex:test', tool: 'codex', name: 'Test session', project: 'project', status: 'busy', since: Date.now(), model: 'test-model', effort: 'high', totalTokens: 1234, inputTokens: 1000, outputTokens: 234, lastTool: 'read' }] };
    emit('work');
  });
  await page.getByRole('button', { name: /project/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('test-model');
  await expect(dialog.locator('button.session-close svg.ico')).toBeVisible();
  await expect(dialog).toContainText('1.2k');
  await expect(dialog).toContainText('read');
  await page.getByRole('button', { name: 'Close session details' }).click();
  await expect(dialog).toHaveCount(0);
});

test('uncontrollable volume shows the reading with disabled controls', async ({ page }) => {
  await layout(page, [widget('volume')]);
  await page.evaluate(async () => {
    // every later state poll reports the same, so the check does not race the next poll
    const real = window.fetch;
    window.fetch = async (...args) => {
      const r = await real(...args);
      if (!String(args[0]).includes('/api/state')) return r;
      const state = await r.json(); state.audio = { volume: 42, muted: false, canControl: false };
      return new Response(JSON.stringify(state), { headers: { 'Content-Type': 'application/json' } });
    };
    const { D, emit } = await import('/panel/main.js');
    D.state.audio = { volume: 42, muted: false, canControl: false };
    emit('state');
  });
  await expect(page.locator('.w-volume')).toContainText('42%');
  for (const button of await page.locator('.w-volume button').all()) await expect(button).toBeDisabled();
});

test('stack editor changes child options, enabled state and ordering', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('stack', { widgets: [{ type: 'clock' }, { type: 'weather' }] })]);
  await page.goto('/settings/');
  await page.locator('#canvas [title="Smart Stack"]').click();
  await expect(page.locator('.stack-row')).toHaveCount(2);
  await page.locator('.stack-row').first().getByTitle('Move down').click();
  await expect(page.locator('.stack-row-name').first()).toContainText('Weather');
  await page.locator('.stack-row').first().locator('input[type=checkbox]').uncheck();
  await expect(page.locator('.stack-row').first()).toHaveClass(/off/);
  await page.locator('.stack-row-name').last().click();
  await expect(page.locator('.stack-sub-config')).toBeVisible();
  await expect(page.locator('#stackQuickAdd option[value=command]')).toHaveCount(0);
});

test('two counters in a stack keep separate values after reload', async ({ page }) => {
  await layout(page, [widget('stack', { widgets: [{ type: 'counter' }, { type: 'counter' }] })]);
  await page.locator('.stack-item.active').getByRole('button', { name: '+', exact: true }).click();
  await page.locator('.stack-dot').nth(1).click();
  await page.locator('.stack-item.active').getByRole('button', { name: '+', exact: true }).click({ clickCount: 2 });
  await page.reload();
  await expect(page.locator('.stack-item.active .ct-n')).toHaveText('1');
  await page.locator('.stack-dot').nth(1).click();
  await expect(page.locator('.stack-item.active .ct-n')).toHaveText('2');
});

test('stack pause button, dots hold, heldDots reset, and vertical swipe over quotas child', async ({ page }) => {
  await layout(page, [widget('stack', { interval: 3, widgets: [{ type: 'quotas' }, { type: 'clock' }] })]);
  const pauseBtn = page.getByRole('button', { name: 'Pause stack rotation' });
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');
  await expect(pauseBtn.locator('.i-pause')).toBeVisible();
  await expect(pauseBtn.locator('.i-play')).toBeHidden();

  // Tapping the pause button keeps a constant aria-label and switches to .i-play with aria-pressed="true".
  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'true');
  await expect(pauseBtn).toHaveClass(/\bon\b/);
  await expect(pauseBtn.locator('.i-play')).toBeVisible();
  await expect(pauseBtn.locator('.i-pause')).toBeHidden();
  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');

  // Holding on the card surface does NOT pause rotation; holding on .stack-dots DOES.
  await page.locator('.stack-item.active .q-grid').dispatchEvent('pointerdown', { button: 0, pointerType: 'mouse', clientX: 50, clientY: 50 });
  await page.waitForTimeout(550);
  await page.locator('.stack-item.active .q-grid').dispatchEvent('pointerup', { button: 0, pointerType: 'mouse', clientX: 50, clientY: 50 });
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');

  await page.locator('.stack-dot').first().dispatchEvent('pointerdown', { button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
  await page.waitForTimeout(550);
  await page.locator('.stack-dot').first().dispatchEvent('pointerleave', { button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'true');

  // When a hold ends without a click event, the next tap on .stack-pause still works (heldDots resets on pointerdown).
  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');

  // Vertical swipe inside a .q-grid quotas child switches stack cards.
  await expect(page.locator('.stack-item.active .w-quotas')).toHaveCount(1);
  await page.locator('.stack-item.active .q-grid').evaluate((el) => {
    const mk = (type, x, y) => {
      const e = new Event(type, { bubbles: true, cancelable: true });
      const touch = { clientX: x, clientY: y };
      e.touches = type === 'touchstart' ? [touch] : [];
      e.changedTouches = [touch];
      return e;
    };
    el.dispatchEvent(mk('touchstart', 100, 200));
    el.dispatchEvent(mk('touchend', 100, 100));
  });
  await expect(page.locator('.stack-item.active .w-clock')).toHaveCount(1);
});

for (const hour12 of [false, true]) {
  test(`clock times follow display.hour12 (${hour12})`, async ({ browser, baseURL }) => {
    const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 }, timezoneId: 'UTC' });
    const page = await ctx.newPage();
    await page.clock.setFixedTime(new Date('2026-11-12T14:05:00Z'));
    await layout(page, [
      { id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 3, options: { weather: false, status: false } },
      { id: 'wc', type: 'worldclock', x: 6, y: 0, w: 6, h: 3, options: { zones: ['UTC=Etc/UTC', 'Tokyo=Asia/Tokyo'] } },
      { id: 'cd', type: 'countdown', x: 0, y: 3, w: 6, h: 3, options: { target: '2026-12-24 09:30' } },
    ], { display: { idleReturnSec: 0, hour12 } });
    await expect(page.locator('.clock .hh')).toHaveText(hour12 ? '2' : '14');
    await expect(page.locator('.clock .ap')).toHaveText(hour12 ? 'PM' : '');
    await expect(page.locator('.wc-t')).toHaveText(hour12 ? ['2:05 PM', '11:05 PM'] : ['14:05', '23:05']);
    await expect(page.locator('.cd-s')).toContainText(hour12 ? '9:30 AM' : '09:30');
    await page.screenshot({ path: `test-results/hour12-${hour12}.png` });
    await ctx.close();
  });
}

test('clock times follow display.hour12 ("auto") via Android.is24Hour bridge and react to system changes', async ({ browser, baseURL }) => {
  const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 }, timezoneId: 'UTC' });
  await ctx.addInitScript(() => {
    window.__phoneboxKey = 'pb-key-test';
    window.__phone24h = false;
    window.Android = {
      battery: () => '{}',
      transport: () => 'usb',
      version: () => 'test',
      haptic() {},
      buzz() {},
      setBrightness() {},
      setVolumeKeys() {},
      is24Hour: (key) => (key === 'pb-key-test' ? window.__phone24h : true),
    };
  });
  const page = await ctx.newPage();
  await page.clock.setFixedTime(new Date('2026-11-12T14:05:00Z'));
  await layout(page, [
    { id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 3, options: { weather: false, status: false } },
    { id: 'wc', type: 'worldclock', x: 6, y: 0, w: 6, h: 3, options: { zones: ['UTC=Etc/UTC', 'Tokyo=Asia/Tokyo'] } },
    { id: 'cd', type: 'countdown', x: 0, y: 3, w: 6, h: 3, options: { target: '2026-12-24 09:30' } },
  ], { display: { idleReturnSec: 0, hour12: 'auto' } });

  await expect(page.locator('.clock .hh')).toHaveText('2');
  await expect(page.locator('.clock .ap')).toHaveText('PM');
  await expect(page.locator('.wc-t')).toHaveText(['2:05 PM', '11:05 PM']);
  await expect(page.locator('.cd-s')).toContainText('9:30 AM');

  await page.evaluate(() => {
    window.__phone24h = true;
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(page.locator('.clock .hh')).toHaveText('14');
  await expect(page.locator('.clock .ap')).toHaveText('');
  await expect(page.locator('.wc-t')).toHaveText(['14:05', '23:05']);
  await expect(page.locator('.cd-s')).toContainText('09:30');

  // Calls without a matching panel bridge key fail closed to 24-hour.
  await page.evaluate(() => {
    window.__phone24h = false;
    window.__phoneboxKey = 'wrong-key';
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(page.locator('.clock .hh')).toHaveText('14');
  await expect(page.locator('.clock .ap')).toHaveText('');
  await ctx.close();
});

test('settings Display Time format select saves false, true and auto', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('clock')], { display: { idleReturnSec: 0, hour12: 'auto' } });
  await page.goto('/settings/#general');
  const sel = page.locator('[data-bind="display.hour12"]');
  await expect(sel).toBeVisible();
  await expect(sel).toHaveValue('auto');

  await sel.selectOption('true');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).display.hour12)).toBe(true);

  await sel.selectOption('false');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).display.hour12)).toBe(false);

  await sel.selectOption('auto');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).display.hour12)).toBe('auto');

  await page.reload();
  await expect(sel).toHaveValue('auto');
});

// The feeds, cameras, console, Discord and ping editors (and each control action's "enabled") fill in their defaults
// while they render. The unsaved-changes baseline used to be taken before that, so a page that had just loaded already
// offered Save, and Revert could never get back to a clean state.
test('a freshly loaded settings page has nothing to save, and Revert gets back to that', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#general');
  // the Advanced JSON box is empty in the markup and filled by the changed() at the end of load(): from here on the
  // config is rendered, the form handlers are bound and the baseline is taken (both buttons start disabled anyway)
  await expect(page.locator('#json')).toHaveValue(/"pages"/);
  await expect(page.locator('#save')).toBeDisabled();
  await expect(page.locator('#revert')).toBeDisabled();

  await page.locator('input[data-bind="night.brightness"]').fill('0.1');
  await expect(page.locator('#save')).toBeEnabled();
  await page.locator('#revert').click();
  await expect(page.locator('#save')).toBeDisabled();
  await expect(page.locator('#revert')).toBeDisabled();
  await expect(page.locator('#nbv')).toHaveText('2%');
});

test('multi option preserves saved unlisted quota provider IDs when toggling choices', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('quotas', { providers: ['claude', 'alibaba'] })]);
  await page.goto('/settings/');
  await page.locator('#canvas [title="AI quotas"]').click();
  const codexCheckbox = page.locator('#inspector label.check', { hasText: 'Codex' }).locator('input[type="checkbox"]');
  await expect(codexCheckbox).toBeVisible();
  await codexCheckbox.check();
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.providers);
  expect(saved).toContain('claude');
  expect(saved).toContain('codex');
  expect(saved).toContain('alibaba');
});

// Apply JSON used to assign the paste verbatim: a fragment without sections every renderer reads
// (pages, display, night…) threw inside renderPages and left cfg broken until a reload.
test('Apply JSON fills defaults for a partial config instead of breaking the editor', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#general');
  // move away from the defaults first, so the checks below prove Apply JSON put them back
  await page.locator('input[data-bind="night.brightness"]').fill('0.1');
  await expect(page.locator('#nbv')).toHaveText('10%');
  await page.locator('input[data-bind="display.showSeconds"]').setChecked(false, { force: true });
  await page.locator('a[href="#advanced"]').click();
  await page.locator('#json').fill('{"display": {"theme": "midnight"}}');
  await page.locator('#applyJson').click();
  await expect(page.locator('#jsonErr')).toHaveText('');
  // the fragment has no pages array: defaults keep renderPages working — the empty list still shows its buttons
  await page.locator('a[href="#pages"]').click();
  await expect(page.locator('#pageTabs button', { hasText: 'New page' })).toBeVisible();
  // and the form keeps answering: the night screen slider read its (defaulted) section without throwing
  await page.locator('a[href="#general"]').click();
  await expect(page.locator('#nbv')).toHaveText('2%'); // the agent's NightConfig default, not NaN%
  await expect(page.locator('input[data-bind="display.showSeconds"]')).toBeChecked(); // DisplayConfig.ShowSeconds
});

// Apply JSON and page-file imports can drop a widget without a grid position. A drag then wrote NaN into
// x/y/w/h, JSON.stringify turned that into null and the server refused the whole save as invalid JSON.
test('Apply JSON gives pasted widgets a valid grid position', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#advanced');
  await page.locator('#json').fill('{"pages": [{"id": "p", "title": "P", "widgets": [{"id": "w1", "type": "clock"}]}]}');
  await page.locator('#applyJson').click();
  await expect(page.locator('#jsonErr')).toHaveText('');
  await page.locator('a[href="#pages"]').click();
  const wb = page.locator('#canvas .wb');
  await expect(wb).toHaveCount(1);
  // a positionless clock takes its catalog default size at the top-left cell instead of an invalid gridArea
  expect(await page.evaluate(() => { const { x, y, w, h } = cfg.pages[0].widgets[0]; return [x, y, w, h]; }))
    .toEqual([0, 0, 6, 4]);
  // a drag writes integer coordinates — before the fix it wrote NaN, which JSON.stringify saved as null
  const cell = await page.locator('#canvas .cell').first().boundingBox();
  const box = await wb.boundingBox();
  await page.mouse.move(box.x + 20, box.y + 20);
  await page.mouse.down();
  await page.mouse.move(box.x + 20 + cell.width, box.y + 20 + cell.height, { steps: 5 });
  await page.mouse.up();
  expect(await page.evaluate(() => { const { x, y } = cfg.pages[0].widgets[0]; return [x, y]; })).toEqual([1, 1]);
  expect(await page.evaluate(() => JSON.stringify(cfg))).not.toContain('"x":null');
});

// #159: when the forecast can't be fetched (no route to Open-Meteo, the agent's fallback refused), the weather says so
// instead of "Loading…" for good, and the error answer is not kept as the forecast.
test('a weather fetch that fails shows "Weather unavailable"', async ({ page }) => {
  await page.route('https://api.open-meteo.com/**', (route) => route.abort());
  await layout(page, [widget('clock', { weather: true })]);
  await page.evaluate(() => localStorage.removeItem('wx'));
  await page.reload();
  await expect(page.locator('.wx-desc')).toHaveText('Weather unavailable');
  expect(await page.evaluate(() => localStorage.getItem('wx'))).toBeNull();
});

// After a location change whose fetch fails, the panel must not show the previous city's forecast under the new name.
test('a new weather location never shows the previous place\'s forecast', async ({ page }) => {
  const forecast = { current: { temperature_2m: 31, apparent_temperature: 31, weather_code: 0, is_day: 1 }, daily: {}, hourly: {} };
  let fail = false;
  await page.route('https://api.open-meteo.com/**', (route) => (fail ? route.abort() : route.fulfill({ json: forecast })));
  await layout(page, [widget('clock', { weather: true })], { weather: { city: 'Alpha', lat: 10, lon: 10 } });
  await expect(page.locator('.wx-temp')).toHaveText('31°');

  fail = true;
  await page.evaluate(async () => {
    const c = JSON.parse(localStorage.getItem('pb-demo-config'));
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...c, weather: { city: 'Beta', lat: 20, lon: 20 } }));
    const m = await import('./main.js');
    await m.loadConfig();
  });
  await expect(page.locator('.wx-desc')).toHaveText('Weather unavailable');
  await expect(page.locator('.wx-temp')).not.toHaveText('31°');
});

// weather.unit converts on the phone: the forecast is cached in °C, so the clock line and both forecast ranges show °F
// without a new request, a unit change from the settings redraws at the next config poll (not at the next forecast
// refresh, up to 14 minutes later), and a config that has no unit (saved before the setting existed) stays in °C.
test('the weather shows Fahrenheit when the settings say so', async ({ page }) => {
  const now = new Date();
  const hour = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}T${String(now.getHours()).padStart(2, '0')}:00`;
  const forecast = {
    current: { time: hour, temperature_2m: 31, apparent_temperature: 29.5, weather_code: 0, is_day: 1 },
    daily: { time: [hour.slice(0, 10)], weather_code: [0], temperature_2m_max: [32], temperature_2m_min: [-5], precipitation_probability_max: [0] },
    hourly: { time: [hour], weather_code: [0], temperature_2m: [20], precipitation_probability: [0], is_day: [1] },
  };
  let requests = 0;
  await page.route('https://api.open-meteo.com/**', (route) => { requests++; route.fulfill({ json: forecast }); });
  const widgets = [{ ...widget('clock', { weather: true }), h: 2 }, { ...widget('forecast', { mode: 'daily' }), y: 2, h: 2 }, { ...widget('forecast', { mode: 'hourly' }), y: 4, h: 2 }];
  await layout(page, widgets, { weather: { city: 'Alpha', lat: 10, lon: 10, unit: 'fahrenheit' } });
  await expect(page.locator('.wx-temp')).toHaveText('88°');
  await expect(page.locator('.wx-range')).toHaveText('↑90° ↓23° · Feels like 85°');
  await expect(page.locator('.fc-v').first()).toHaveText('90° 23°');
  await expect(page.locator('.fc-v').nth(1)).toHaveText('68°');
  expect(requests).toBe(1);

  // the settings page saved Celsius: the next config poll redraws from the cached forecast, with no new request
  await page.evaluate(async () => {
    const c = JSON.parse(localStorage.getItem('pb-demo-config'));
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...c, weather: { ...c.weather, unit: 'celsius' } }));
    const m = await import('./main.js');
    await m.loadConfig();
  });
  await expect(page.locator('.wx-temp')).toHaveText('31°');
  await expect(page.locator('.wx-range')).toHaveText('↑32° ↓-5° · Feels like 30°');
  await expect(page.locator('.fc-v').first()).toHaveText('32° -5°');
  await expect(page.locator('.fc-v').nth(1)).toHaveText('20°');
  expect(requests).toBe(1);

  await layout(page, widgets, { weather: { city: 'Alpha', lat: 10, lon: 10 } });
  await expect(page.locator('.wx-temp')).toHaveText('31°');
  await expect(page.locator('.fc-v').first()).toHaveText('32° -5°');
});

// weather.model pins the Open-Meteo model: the forecast URL names it, a model change fetches anew instead of keeping the
// cached forecast for 14 minutes, and Automatic ("") sends no models parameter at all.
test('the weather asks Open-Meteo for the model the settings pick', async ({ page }) => {
  const now = new Date();
  const hour = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}T${String(now.getHours()).padStart(2, '0')}:00`;
  const urls = [];
  await page.route('https://api.open-meteo.com/**', (route) => {
    const model = new URL(route.request().url()).searchParams.get('models');
    urls.push(model);
    const t = model === 'gfs_seamless' ? 19 : 21;
    route.fulfill({ json: {
      current: { time: hour, temperature_2m: t, apparent_temperature: t, weather_code: 3, is_day: 0 },
      daily: { time: [hour.slice(0, 10)], weather_code: [3], temperature_2m_max: [22], temperature_2m_min: [17], precipitation_probability_max: [0] },
      hourly: { time: [hour], weather_code: [3], temperature_2m: [t], precipitation_probability: [0], is_day: [0] },
    } });
  });
  const widgets = [{ ...widget('clock', { weather: true }), h: 2 }];
  await layout(page, widgets, { weather: { city: 'Alpha', lat: 10, lon: 10, model: 'gfs_seamless' } });
  await expect(page.locator('.wx-temp')).toHaveText('19°');
  expect(urls).toEqual(['gfs_seamless']);

  await page.evaluate(async () => {
    const c = JSON.parse(localStorage.getItem('pb-demo-config'));
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...c, weather: { ...c.weather, model: '' } }));
    const m = await import('./main.js');
    await m.loadConfig();
  });
  await expect(page.locator('.wx-temp')).toHaveText('21°');
  expect(urls).toEqual(['gfs_seamless', null]);
});

// Météo-France and CMA forecast fewer than 7 days and send null for the days after: the forecast stops at the last
// real day instead of showing "0° 0°" (Math.round(null)) under a blank icon.
test('a forecast whose last days are null shows only the days it has', async ({ page }) => {
  const now = new Date();
  const day = (k) => { const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() + k, 12); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; };
  const hour = `${day(0)}T${String(now.getHours()).padStart(2, '0')}:00`;
  const days = [0, 1, 2, 3, 4, 5, 6].map(day);
  await page.route('https://api.open-meteo.com/**', (route) => route.fulfill({ json: {
    current: { time: hour, temperature_2m: 12, apparent_temperature: 12, weather_code: 3, is_day: 1 },
    daily: { time: days, weather_code: [3, 3, 3, 3, null, null, null], temperature_2m_max: [14, 15, 16, 17, null, null, null],
      temperature_2m_min: [8, 9, 10, 11, null, null, null], precipitation_probability_max: [null, null, null, null, null, null, null] },
    hourly: { time: [hour], weather_code: [3], temperature_2m: [12], precipitation_probability: [null], is_day: [1] },
  } }));
  await layout(page, [{ ...widget('forecast', { mode: 'daily' }), w: 12, h: 3 }], { weather: { city: 'Alpha', lat: 10, lon: 10, model: 'meteofrance_seamless' } });
  await expect(page.locator('.fc-v').first()).toHaveText('14° 8°');
  await expect(page.locator('.fc-v')).toHaveCount(4);
  await expect(page.locator('.fc-v', { hasText: /^0°/ })).toHaveCount(0);
});

// PRIVACY.md: the phone sends the weather location to Open-Meteo only while a page has a widget that uses the weather.
test('the weather is fetched only while a widget needs it', async ({ page }) => {
  const asked = [];
  await page.route('https://api.open-meteo.com/**', (route) => { asked.push(route.request().url()); route.fulfill({ json: {} }); });
  const settle = () => page.waitForTimeout(1500);

  await layout(page, [widget('note')]);
  asked.length = 0;
  await page.evaluate(() => localStorage.removeItem('wx')); // a fresh fetch would be due
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
  await settle();
  expect(asked).toEqual([]);

  await layout(page, [widget('clock', { weather: false })]);
  asked.length = 0;
  await page.evaluate(() => localStorage.removeItem('wx')); // a fresh fetch would be due
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
  await settle();
  expect(asked).toEqual([]);

  await layout(page, [{ id: 's', type: 'stack', x: 0, y: 0, w: 12, h: 6, options: { widgets: [{ type: 'clock', options: { weather: false } }, { type: 'note' }] } }]);
  asked.length = 0;
  await page.evaluate(() => localStorage.removeItem('wx')); // a fresh fetch would be due
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
  await settle();
  expect(asked).toEqual([]);

  await layout(page, [widget('weather')]);
  await expect.poll(() => asked.length).toBeGreaterThan(0);
});

// the direct fetch and the agent's proxy fallback both failing is a different path from a JSON error body:
// the demo answers /api/proxy inside its own fetch wrapper, so that failure is patched in the page
test('a failed weather fetch and fallback leaves the widget unavailable, not loading (#159)', async ({ page }) => {
  await page.route('https://api.open-meteo.com/**', (route) => route.abort());
  await layout(page, [widget('forecast')]);
  await page.evaluate(async () => {
    const m = await import('./main.js');
    m.D.weather = null;
    localStorage.removeItem('wx');
    const f = window.fetch;
    window.fetch = (input, init) => (String(typeof input === 'string' ? input : input.url).includes('/proxy')
      ? Promise.reject(new TypeError('proxy unreachable')) : f(input, init));
    return m.loadWeather(true);
  });
  await expect(page.locator('.w-forecast .empty')).toHaveText('Weather unavailable');
});

// #58: switch between the computers paired over Wi-Fi with one tap; a USB cable always wins.
test('the Computers widget lists paired computers and switches between them', async ({ browser, baseURL }) => {
  const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 } });
  await ctx.addInitScript(() => {
    window.__phoneboxKey = 'pb-key-test';
    window.__route = 'wifi';
    window.__selected = [];
    window.Android = {
      battery: () => '{}', version: () => 'test', haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {}, is24Hour: () => true,
      transport: () => window.__route,
      pcs: (key) => (key === 'pb-key-test' ? JSON.stringify([{ id: 'a', name: 'Desk PC', active: true }, { id: 'b', name: 'MacBook', active: false }]) : '[]'),
      selectPc: (key, id) => { if (key === 'pb-key-test') window.__selected.push(id); },
    };
  });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, [widget('pcswitch')]);

  const pcs = page.locator('.w-pcswitch .ov-pc');
  await expect(pcs).toHaveText(['Desk PC', 'MacBook']);
  await expect(pcs.locator('svg.ico')).toHaveCount(2); // a stroke icon, not the emoji an old WebView draws however it likes
  await expect(pcs.nth(0)).toHaveClass(/\bon\b/);
  await pcs.nth(0).click();
  await pcs.nth(1).click();
  await expect.poll(() => page.evaluate(() => window.__selected)).toEqual(['b']);
  await page.screenshot({ path: 'test-results/pcswitch-wifi.png' });

  await page.evaluate(() => { window.__route = 'usb'; });
  await expect(pcs.nth(1)).toBeDisabled({ timeout: 10_000 });
  await expect(page.locator('.w-pcswitch .pcs-sub')).toContainText('USB');
  await page.screenshot({ path: 'test-results/pcswitch-usb.png' });
  expect(errors).toEqual([]);
  await ctx.close();
});

test('docker widget clears loading state when filtered or stopped containers yield an empty list', async ({ page }) => {
  await layout(page, [widget('docker', { title: 'Podman', filter: 'redis', stopped: false })]);
  await expect(page.locator('.w-docker .w-title')).toContainText('Podman 0/1');
  await expect(page.locator('.w-docker .empty')).toHaveText('No running containers (1 stopped)');

  await layout(page, [widget('docker', { title: 'Podman', filter: 'missing', stopped: false })]);
  await expect(page.locator('.w-docker .w-title')).toContainText('Podman 0/0');
  await expect(page.locator('.w-docker .empty')).toHaveText('No containers match "missing"');
});

test('the charge guard serves the battery the phone reports', async ({ page }) => {
  await layout(page, [widget('clock')]);
  // the panel posts the demo phone's battery (82 %) a few seconds after boot; /api/charge must serve the report
  await expect.poll(() => page.evaluate(() => fetch('/api/charge').then((r) => r.json()).then((j) => j.phone)), { timeout: 15000 })
    .toMatchObject({ level: 82, plugged: true, temp: 29.5 });
});

// The agent sends headlines without their links; a tap asks the agent to open the article by its id.
test('news feeds list headlines and open one on the PC by its id', async ({ page }) => {
  await layout(page, [widget('feeds', { title: 'News', feed: '', showSource: true, showTime: true, open: true })]);
  // the demo answers inside the page, so the call is recorded there
  await page.evaluate(() => {
    const real = window.fetch;
    window.__opened = [];
    window.fetch = (url, init) => {
      if (String(url).includes('/feeds/open')) window.__opened.push({ id: new URL(String(url), location.href).searchParams.get('id'), method: init?.method });
      return real(url, init);
    };
  });
  const rows = page.locator('.w-feeds .fd-i');
  await expect(rows.first()).toContainText('e-ink weather station');
  await expect(rows.first().locator('.fd-m')).toContainText('Hackaday');
  await rows.first().click();
  await expect.poll(() => page.evaluate(() => window.__opened)).toEqual([{ id: 'f1', method: 'POST' }]);
  await expect(page.locator('#toast')).toContainText('Opening on the PC');

  // opening is off until the widget allows it: a tap asks nothing of the PC
  await layout(page, [widget('feeds', { title: 'News', open: false })]);
  await page.evaluate(() => {
    const real = window.fetch;
    window.__opened = [];
    window.fetch = (url, init) => { if (String(url).includes('/feeds/open')) window.__opened.push(String(url)); return real(url, init); };
  });
  await expect(page.locator('.w-feeds .fd-i.link')).toHaveCount(0);
  await page.locator('.w-feeds .fd-i').first().click();
  await expect(page.locator('#toast')).toContainText('Open articles on the PC');
  expect(await page.evaluate(() => window.__opened)).toEqual([]);

  // one feed only, by its name
  await layout(page, [widget('feeds', { title: 'Tech', feed: 'tech news', showSource: false, showTime: false })]);
  await expect(page.locator('.w-feeds .fd-i').first()).toContainText('Rust 2.0');
  await expect(page.locator('.w-feeds .fd-m')).toHaveCount(0);
  await layout(page, [widget('feeds', { title: 'None', feed: 'nothing like this' })]);
  await expect(page.locator('.w-feeds .empty')).toHaveText('No headlines from "nothing like this"');
});

// #108: the list scrolls (and keeps its place when the ages refresh), an opened article loses its accent bar on
// this phone, and a headline can take two or three lines.
test('news feeds scroll, mark opened articles and wrap headlines', async ({ page }) => {
  await layout(page, [{ ...widget('feeds', { title: 'News', open: true }), h: 2 }]);
  const list = page.locator('.w-feeds .fd-list');
  await expect(page.locator('.w-feeds .fd-i')).toHaveCount(6);
  // more headlines than fit: the list scrolls instead of dropping them
  expect(await list.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  await list.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  const bottom = await list.evaluate((el) => el.scrollTop);
  expect(bottom).toBeGreaterThan(0);

  // every headline starts with the accent bar; opening one takes it away, and the list stays where it was scrolled
  await expect(page.locator('.w-feeds .fd-i.link.read')).toHaveCount(0);
  await page.locator('.w-feeds .fd-i').last().click();
  await expect(page.locator('#toast')).toContainText('Opening on the PC');
  await expect(page.locator('.w-feeds .fd-i').last()).toHaveClass(/\bread\b/);
  await expect(page.locator('.w-feeds .fd-i.read')).toHaveCount(1);
  expect(await page.locator('.w-feeds .fd-i').last().evaluate((el) => getComputedStyle(el).borderLeftColor)).toBe('rgba(0, 0, 0, 0)');
  expect(await list.evaluate((el) => el.scrollTop)).toBe(bottom);

  // it stays read after the panel reloads (kept on the phone)
  await page.reload();
  await expect(page.locator('.w-feeds .fd-i').last()).toHaveClass(/\bread\b/);

  // in a Smart Stack, a vertical swipe over a list that scrolls scrolls it rather than changing the card
  await layout(page, [{ ...widget('stack', { widgets: [{ type: 'feeds', options: { title: 'News' } }, { type: 'clock' }] }), h: 2 }]);
  await expect(page.locator('.stack-item.active .w-feeds .fd-i')).toHaveCount(6);
  await page.locator('.stack-item.active .fd-list').evaluate((el) => {
    const mk = (type, x, y) => {
      const e = new Event(type, { bubbles: true, cancelable: true });
      const touch = { clientX: x, clientY: y };
      e.touches = type === 'touchstart' ? [touch] : [];
      e.changedTouches = [touch];
      return e;
    };
    el.firstElementChild.dispatchEvent(mk('touchstart', 100, 200));
    el.firstElementChild.dispatchEvent(mk('touchend', 100, 100));
  });
  await expect(page.locator('.stack-item.active .w-feeds')).toHaveCount(1);

  // headlines over two lines
  await layout(page, [widget('feeds', { title: 'News', lines: '2' })]);
  const title = page.locator('.w-feeds .fd-t').first();
  await expect(page.locator('.w-feeds .fd-list.wrap')).toHaveCount(1);
  expect(await title.evaluate((el) => [getComputedStyle(el).whiteSpace, getComputedStyle(el).webkitLineClamp])).toEqual(['normal', '2']);
});

// Each News feeds widget follows its own "open" option, and the demo, like the agent, ignores disabled pages.
test('news feeds open only from a widget whose own option allows it', async ({ page }) => {
  await layout(page, [
    { ...widget('feeds', { title: 'Open', open: true }), id: 'a', w: 6 },
    { ...widget('feeds', { title: 'Closed', open: false }), id: 'b', x: 6, w: 6 },
  ]);
  await expect(page.locator('.w-feeds').nth(0).locator('.fd-i.link').first()).toBeVisible();
  await expect(page.locator('.w-feeds').nth(1).locator('.fd-i').first()).toBeVisible();
  await expect(page.locator('.w-feeds').nth(1).locator('.fd-i.link')).toHaveCount(0);

  // the only widget that allows opening sits on a disabled page: the demo refuses like the agent does
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], pages: [
      { id: 'on', title: 'On', enabled: true, widgets: [{ id: 'x', type: 'feeds', x: 0, y: 0, w: 12, h: 6, options: { open: false } }] },
      { id: 'off', title: 'Off', enabled: false, widgets: [{ id: 'y', type: 'feeds', x: 0, y: 0, w: 12, h: 6, options: { open: true } }] },
    ] }));
  });
  await page.reload();
  const canOpen = await page.evaluate(() => fetch('/agent/api/feeds').then((r) => r.json()).then((j) => j.canOpen));
  expect(canOpen).toBe(false);
});

// Photo frame: ids from the agent, one photo at a time fetched by id, crossfading; the clock drives the interval.
test('photo frame shows the folder photos one after another', async ({ page }) => {
  await page.clock.install();
  await layout(page, [widget('photos', { interval: 10, shuffle: false, fit: 'contain' })]);
  const shown = page.locator('.w-photos .pf-img.on');
  await expect(shown).toHaveCount(1);
  const first = await shown.getAttribute('src');
  expect(first).toMatch(/^blob:/);
  await expect(shown).toHaveCSS('object-fit', 'contain');
  await page.clock.fastForward(11_000);
  await expect.poll(() => page.locator('.w-photos .pf-img.on').getAttribute('src')).not.toBe(first);
  await expect(page.locator('.w-photos .pf-img.on')).toHaveCount(1);
  await expect(page.locator('.w-photos .pf-note')).toBeHidden();
});

test('a photo frame on a hidden Smart Stack card fetches nothing', async ({ page }) => {
  await page.clock.install();
  await layout(page, [widget('stack', { interval: 120, widgets: [{ type: 'clock' }, { type: 'photos', options: { interval: 5 } }] })]);
  await page.evaluate(() => {
    window.__photoFetches = 0;
    const demo = window.fetch;
    window.fetch = (url, init) => { if (String(url).includes('/photos')) window.__photoFetches++; return demo(url, init); };
  });
  await page.clock.fastForward(30_000);
  expect(await page.evaluate(() => window.__photoFetches)).toBe(0);
  await page.getByRole('button', { name: 'photos', exact: true }).click();
  await expect(page.locator('.stack-item.active .w-photos .pf-img.on')).toHaveCount(1);
});

// #258: the Trackpad widget used the native bridge without importing it, so with mouse control on it threw while
// building in the phone app; the catalogue test builds it with control off and never got that far.
test('the Trackpad widget sends a tap through the phone bridge when mouse control is on', async ({ browser, baseURL }) => {
  const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 } });
  await ctx.addInitScript(() => {
    window.__phoneboxKey = 'pb-key-test';
    window.__tp = [];
    window.Android = {
      battery: () => '{}', version: () => 'test', haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {}, is24Hour: () => true,
      transport: () => 'usb', pcs: () => '[]',
      trackpad: (key, type) => { if (key === 'pb-key-test') window.__tp.push(type); },
    };
  });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, [widget('trackpad', { control: true, scrollBar: 'none' })]);

  const pad = page.locator('.deck-trackpad');
  await expect(pad).not.toHaveClass(/is-disabled/);
  await pad.click();
  await expect.poll(() => page.evaluate(() => window.__tp)).toContain('click');
  expect(errors).toEqual([]);
  await ctx.close();
});
