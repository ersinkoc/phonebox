// Focus history (#350): the Pomodoro's finished focus minutes per day, drawn by their own widget, against the web demo
// with a mocked clock.
const { test, expect } = require('@playwright/test');

const MIN = 60_000;
// midday, so the fast-forwarded steps stay on one day unless a test starts near midnight on purpose
const NOON = new Date(2026, 0, 15, 12, 0, 0);

/** The Focus template's layout with Focus history in place of the note: a timer, and the history beside it. */
async function show(page, { history = true, inStack = false, options = { focus: 1, break: 1 }, state = null, hist = null, at = NOON, language = 'en', stackCards = null, pageOn = true, fhH = 4 } = {}) {
  await page.clock.install({ time: at });
  await page.goto('/panel/style.css');
  await page.evaluate(({ history, inStack, options, state, hist, language, stackCards, pageOn, fhH }) => {
    localStorage.clear();
    const widgets = [{ id: 'pomo', type: 'pomodoro', x: 0, y: 0, w: 5, h: 6, options }];
    if (history) {
      widgets.push(inStack
        ? { id: 'st', type: 'stack', x: 5, y: 0, w: 7, h: 4, options: { widgets: stackCards || [{ type: 'clock', enabled: true }, { type: 'focushistory', enabled: true }] } }
        : { id: 'fh', type: 'focushistory', x: 5, y: 0, w: 7, h: fhH });
    }
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0, language },
      pages: pageOn ? [{ id: 'p', title: 'Focus', widgets }]
        // the timer on one page, and the history on a second page that is turned off
        : [{ id: 'p', title: 'Focus', widgets: [widgets[0]] }, { id: 'q', title: 'Off', enabled: false, widgets: widgets.slice(1) }],
    }));
    if (state) localStorage.setItem('pomo:p/pomo', JSON.stringify({ ...state, endsAt: state.endsAt && Date.now() + state.endsAt, day: new Date().toDateString() }));
    if (hist) localStorage.setItem('focus-history', JSON.stringify(hist));
  }, { history, inStack, options, state, hist, language, stackCards, pageOn, fhH });
  await page.goto('/panel/');
  const pomo = page.locator('.w-pomodoro');
  await expect(pomo).toBeVisible();
  const fh = page.locator('.w-focushistory').first();
  return {
    pomo,
    fh,
    label: pomo.locator('.g-label'),
    line: fh.locator('.fh-line'),
    total: fh.locator('.fh-total'),
    play: () => pomo.locator('.pb.main').click(),
    skip: () => pomo.locator('.pb').last().click(),
  };
}

const saved = (page) => page.evaluate(() => JSON.parse(localStorage.getItem('focus-history')));

test('without a Focus history widget the timer keeps no history, as before', async ({ page }) => {
  const p = await show(page, { history: false });
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Break');
  await expect(p.pomo.locator('.sr-only')).toHaveText('Sessions completed today: 1');
  expect(await saved(page)).toBeNull();
});

test('a finished focus step adds its minutes to today, and the break does not', async ({ page }) => {
  const p = await show(page);
  await expect(p.fh.locator('.w-title')).toContainText('Focus · last 7 days');
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,0');
  await expect(p.total).toHaveText('0 min');
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Break');
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,1');
  await expect(p.total).toHaveText('1 min');
  await expect(p.line).toHaveAttribute('role', 'img');
  await expect(p.line).toHaveAttribute('aria-label', /^Focus minutes in the last 7 days: \S+ 0, .+, \S+ 1$/);
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Focus');
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,1');
  expect(await saved(page)).toEqual({ '2026-01-15': 1 });
});

test('the weekdays run from six days ago to today, with today marked', async ({ page }) => {
  const p = await show(page);
  const days = p.fh.locator('.fh-days button');
  await expect(days).toHaveCount(7);
  // 15 January 2026 is a Thursday
  await expect(days.first()).toHaveText('Fri');
  await expect(days.last()).toHaveText('Thu');
  await expect(days.last()).toHaveClass('today');
});

test('a focus step that ends after midnight counts on the new day, and the week moves on', async ({ page }) => {
  const p = await show(page, {
    state: { phase: 'focus', endsAt: null, left: MIN, count: 0 },
    hist: { '2026-01-15': 25 },
    at: new Date(2026, 0, 15, 23, 59, 30),
  });
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,25');
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Break');
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,25,1');
  expect(await saved(page)).toEqual({ '2026-01-15': 25, '2026-01-16': 1 });
});

test('only the last seven days are shown and kept', async ({ page }) => {
  const hist = {};
  for (let d = 7; d <= 15; d++) hist[`2026-01-${String(d).padStart(2, '0')}`] = d - 6; // nine days: 1 .. 9 minutes
  const p = await show(page, { hist });
  await expect(p.line).toHaveAttribute('data-minutes', '3,4,5,6,7,8,9');
  await expect(p.total).toHaveText('42 min');
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.line).toHaveAttribute('data-minutes', '3,4,5,6,7,8,10');
  expect(Object.keys(await saved(page)).sort()).toEqual(
    ['2026-01-09', '2026-01-10', '2026-01-11', '2026-01-12', '2026-01-13', '2026-01-14', '2026-01-15']);
});

test('a skipped focus step does not count', async ({ page }) => {
  const p = await show(page);
  await p.play();
  await page.clock.fastForward(30_000);
  await p.skip();
  await expect(p.label).toHaveText('Break');
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,0');
  expect(await saved(page)).toBeNull();
});

test('a focus step that ended while the panel was closed counts on the day it ended', async ({ page }) => {
  // the panel opens at 00:10 on the 16th; the 25-minute step ended at 23:50 the day before
  const p = await show(page, {
    options: {},
    state: { phase: 'focus', endsAt: -20 * MIN, left: 25 * MIN, count: 0 },
    at: new Date(2026, 0, 16, 0, 10, 0),
  });
  await expect(p.label).toHaveText('Break');
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,25,0');
  expect(await saved(page)).toEqual({ '2026-01-15': 25 });
});

test('the timer also counts when Focus history is in a Smart Stack', async ({ page }) => {
  const p = await show(page, { inStack: true });
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Break');
  expect(await saved(page)).toEqual({ '2026-01-15': 1 });
});

test('tapping a day shows its minutes with a line through it, and the mark goes away again', async ({ page }) => {
  const p = await show(page, { hist: { '2026-01-14': 125, '2026-01-15': 50 } });
  const tip = p.fh.locator('.fh-tip'), guide = p.fh.locator('.fh-guide'), days = p.fh.locator('.fh-days button');
  await expect(tip).toBeHidden();
  const box = await p.line.boundingBox();
  await p.line.click({ position: { x: box.width * (5.5 / 7), y: box.height / 2 } }); // the sixth day: Wednesday the 14th
  await expect(tip).toBeVisible();
  await expect(tip).toHaveText('Wed · 125 min');
  await expect(guide).toBeVisible();
  await expect(days.nth(5)).toHaveClass(/sel/);
  // it stays a moment after the finger lifts, then clears by itself
  await page.clock.fastForward(3000);
  await expect(tip).toBeVisible();
  await page.clock.fastForward(1500);
  await expect(tip).toBeHidden();
  await expect(guide).toBeHidden();
  await expect(days.nth(5)).not.toHaveClass(/sel/);
});

test('a weekday name can be tapped too, another day moves the mark, and the same day again closes it', async ({ page }) => {
  const p = await show(page, { hist: { '2026-01-11': 40 } });
  const tip = p.fh.locator('.fh-tip'), days = p.fh.locator('.fh-days button');
  await days.nth(2).click(); // Sunday the 11th
  await expect(tip).toHaveText('Sun · 40 min');
  await days.nth(6).click(); // today
  await expect(tip).toHaveText('Thu · 0 min');
  await expect(days.nth(2)).not.toHaveClass(/sel/);
  await days.nth(6).click();
  await expect(tip).toBeHidden();
});

test('a marked day shows the new minutes when a step finishes while it is open', async ({ page }) => {
  const p = await show(page);
  await p.fh.locator('.fh-days button').nth(6).click();
  await expect(p.fh.locator('.fh-tip')).toHaveText('Thu · 0 min');
  // another timer on the panel finishes a 5-minute focus step while the mark is shown
  await page.evaluate(() => localStorage.setItem('focus-history', JSON.stringify({ '2026-01-15': 5 })));
  await page.clock.fastForward(1000);
  await expect(p.fh.locator('.fh-tip')).toHaveText('Thu · 5 min');
  await expect(p.total).toHaveText('5 min');
});

test('a swipe that starts on the chart marks nothing, and a hold marks the day while the finger stays', async ({ page }) => {
  const p = await show(page, { hist: { '2026-01-14': 125 } });
  const tip = p.fh.locator('.fh-tip');
  const box = await p.line.boundingBox();
  const y = box.y + box.height / 2, x = (i) => box.x + box.width * ((i + 0.5) / 7);
  // a sideways swipe from Wednesday's column
  await page.mouse.move(x(5), y);
  await page.mouse.down();
  await page.mouse.move(x(2), y, { steps: 5 });
  await page.clock.fastForward(500);
  await page.mouse.up();
  await expect(tip).toBeHidden();
  // a hold: shown after a moment, kept while held, cleared a while after the finger lifts
  await page.mouse.move(x(5), y);
  await page.mouse.down();
  await page.clock.fastForward(300);
  await expect(tip).toHaveText('Wed · 125 min');
  await page.clock.fastForward(10_000);
  await expect(tip).toBeVisible();
  await page.mouse.up();
  await page.clock.fastForward(4500);
  await expect(tip).toBeHidden();
});

test('a Smart Stack whose Focus history card is turned off, or a page that is turned off, keeps no history', async ({ page }) => {
  for (const setup of [
    { inStack: true, stackCards: [{ type: 'clock', enabled: true }, { type: 'focushistory', enabled: false }] },
    { inStack: true, stackCards: [{ type: 'clock' }, { type: 'focushistory', active: false }] },
    { pageOn: false },
  ]) {
    const p = await show(page, setup);
    await p.play();
    await page.clock.fastForward(MIN + 1500);
    await expect(p.label).toHaveText('Break');
    expect(await saved(page), JSON.stringify(setup)).toBeNull();
  }
});

test('days older than the last seven are dropped from storage when the widget opens and when the date changes', async ({ page }) => {
  const p = await show(page, { hist: { '2026-01-08': 30, '2026-01-09': 20, '2026-01-15': 10 }, at: new Date(2026, 0, 15, 23, 59, 0) });
  await expect(p.line).toHaveAttribute('data-minutes', '20,0,0,0,0,0,10');
  expect(await saved(page)).toEqual({ '2026-01-09': 20, '2026-01-15': 10 });
  await page.clock.fastForward(2 * MIN);
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,10,0');
  expect(await saved(page)).toEqual({ '2026-01-15': 10 });
});

test('the weekdays are buttons a keyboard can use: Enter shows a day, Enter again closes it', async ({ page }) => {
  const p = await show(page, { hist: { '2026-01-11': 40 } });
  const tip = p.fh.locator('.fh-tip'), sun = p.fh.locator('.fh-days button').nth(2);
  await expect(sun).toHaveAttribute('aria-label', 'Sun · 40 min');
  await sun.focus();
  await page.keyboard.press('Enter');
  await expect(tip).toHaveText('Sun · 40 min');
  await expect(sun).toHaveAttribute('aria-pressed', 'true');
  await page.keyboard.press('Enter');
  await expect(tip).toBeHidden();
  await expect(sun).toHaveAttribute('aria-pressed', 'false');
  await page.keyboard.press('Space');
  await expect(tip).toHaveText('Sun · 40 min');
});

test('in Arabic the weekdays stay under their points, oldest on the left, and a tap marks the day under the finger', async ({ page }) => {
  const p = await show(page, { language: 'ar', hist: { '2026-01-09': 7 } });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const days = p.fh.locator('.fh-days button');
  const first = await days.first().boundingBox(), last = await days.last().boundingBox();
  expect(first.x).toBeLessThan(last.x); // the oldest day on the left, like the line
  await expect(days.last()).toHaveClass('today');
  await days.first().click();
  await expect(days.first()).toHaveClass(/sel/);
  await expect(p.fh.locator('.fh-tip')).toContainText('7');
  const guide = await p.fh.locator('.fh-guide').boundingBox();
  expect(guide.x).toBeGreaterThan(first.x);
  expect(guide.x).toBeLessThan(first.x + first.width);
});

test('on a short widget the weekdays are out of sight and out of the way, but a keyboard can still pick a day', async ({ page }) => {
  await page.setViewportSize({ width: 640, height: 300 }); // a smaller landscape phone, where two rows are a short widget
  const p = await show(page, { fhH: 2, hist: { '2026-01-14': 125 } });
  expect((await p.fh.boundingBox()).height).toBeLessThanOrEqual(90);
  const days = p.fh.locator('.fh-days'), wed = days.locator('button').nth(5), tip = p.fh.locator('.fh-tip');
  await expect(days).toHaveCSS('opacity', '0');
  await expect(days).toHaveCSS('pointer-events', 'none');
  // a tap on the chart still reaches the line, not the hidden row
  const box = await p.line.boundingBox();
  await page.mouse.click(box.x + box.width * (5.5 / 7), box.y + box.height - 3);
  await expect(tip).toHaveText('Wed · 125 min');
  await page.mouse.click(box.x + box.width * (5.5 / 7), box.y + box.height - 3);
  await expect(tip).toBeHidden();
  await wed.focus();
  await expect(days).toHaveCSS('opacity', '1');
  await page.keyboard.press('Enter');
  await expect(tip).toHaveText('Wed · 125 min');
});

test('a focus step shows its minutes as it goes, and a restarted or skipped step takes them back (#741)', async ({ page }) => {
  const p = await show(page, { options: { focus: 25, break: 5 } });
  await p.play();
  await page.clock.fastForward(10 * MIN);
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,10');
  await p.pomo.locator('.pb.main').click(); // paused: the minutes stay
  await page.clock.fastForward(5 * MIN);
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,10');
  await p.pomo.locator('.pb').first().click(); // restarted: the step is discarded
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,0');
  await p.play();
  await page.clock.fastForward(7 * MIN);
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,7');
  await p.skip();
  await expect(p.label).toHaveText('Break');
  await expect(p.line).toHaveAttribute('data-minutes', '0,0,0,0,0,0,0');
  expect(await saved(page)).toBeNull();
});
