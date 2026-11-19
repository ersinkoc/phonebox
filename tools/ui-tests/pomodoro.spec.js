// Pomodoro: focus / break, long breaks, an own sequence with rounds, against the web demo with a mocked clock.
const { test, expect } = require('@playwright/test');

const MIN = 60_000;

// Midday, not the real time: the widget counts today's sessions, so a run that started a few minutes before midnight
// crossed into a new day while the tests fast-forward and lost its tomatoes.
const NOON = new Date(2026, 0, 15, 12, 0, 0);

async function show(page, options, state = null) {
  await page.clock.install({ time: NOON });
  await page.goto('/panel/style.css');
  await page.evaluate(({ options, state }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
      pages: [{ id: 'p', title: 'Focus', widgets: [{ id: 'pomo', type: 'pomodoro', x: 0, y: 0, w: 6, h: 6, options }] }],
    }));
    if (state) localStorage.setItem('pomo:p/pomo', JSON.stringify({ ...state, endsAt: state.endsAt && Date.now() + state.endsAt, day: new Date().toDateString() }));
  }, { options, state });
  await page.goto('/panel/');
  const w = page.locator('.w-pomodoro');
  await expect(w).toBeVisible();
  return {
    w,
    label: w.locator('.g-label'),
    sub: w.locator('.g-sub'),
    play: () => w.locator('.pb.main').click(),
    reset: () => w.locator('.pb').first().click(),
    skip: () => w.locator('.pb').last().click(),
  };
}

test('the icon-only buttons have names, and the start button follows the state', async ({ page }) => {
  const p = await show(page, { focus: 1, break: 1 });
  await expect(p.w.getByRole('button', { name: 'Restart', exact: true })).toBeVisible();
  await expect(p.w.getByRole('button', { name: 'Skip to the next step' })).toBeVisible();
  await expect(p.w.getByRole('button', { name: 'Start', exact: true })).toBeVisible();
  await p.play();
  await expect(p.w.getByRole('button', { name: 'Pause', exact: true })).toBeVisible();
});

test('Pomodoro fixture configuration survives delayed default initialization', async ({ page }) => {
  const { gatedLayout } = require('./layout-ordering');
  expect(await gatedLayout(page, p => show(p, { sequence: '25 focus, lunch' }))).toEqual(['p']);
});

test('without new options it alternates focus and break as before', async ({ page }) => {
  const p = await show(page, { focus: 1, break: 1 });
  await expect(p.label).toHaveText('Focus');
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Break');
  await expect(p.sub.locator('.ico')).toHaveCount(1);
  await expect(p.sub.locator('.sr-only')).toHaveText('Sessions completed today: 1'); // the icons are hidden from screen readers
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Focus');
  await expect(p.w).toHaveClass(/run/);
});

test('a long break comes after every N focus sessions', async ({ page }) => {
  const p = await show(page, { focus: 1, break: 1, longEvery: 2, longBreak: 2 });
  await expect(p.label).toHaveText('Focus 1/2');
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Break');
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Focus 2/2');
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Long break');
  await expect(p.w).toHaveClass(/long/);
  await expect(page.locator('#toast')).toContainText('long break');
  await page.clock.fastForward(2 * MIN + 1500);
  await expect(p.label).toHaveText('Focus 1/2');
});

test('an own sequence runs its rounds and then stops', async ({ page }) => {
  const p = await show(page, { sequence: '1 focus, 1 long', rounds: 2 });
  await expect(p.sub).toHaveText('Round 1/2 · No sessions yet today');
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Long break');
  await page.clock.fastForward(MIN + 1500);
  await expect(p.sub).toHaveText(/^Round 2\/2 ·/);
  await expect(p.sub.locator('.ico')).toHaveCount(1);
  await page.clock.fastForward(MIN + 1500);
  await expect(p.label).toHaveText('Long break');
  await page.clock.fastForward(MIN + 1500);
  await expect(page.locator('#toast')).toContainText('Sequence finished');
  await expect(p.label).toHaveText('Focus');
  await expect(p.w).not.toHaveClass(/run/);
  await expect(p.sub).toHaveText(/^Round 1\/2 ·/);
  await expect(p.sub.locator('.ico')).toHaveCount(2);
});

test('reset restarts the step, and pressed again the whole sequence', async ({ page }) => {
  const p = await show(page, { sequence: 'focus 1, break 1, focus 1' });
  await p.skip();
  await p.skip();
  await expect(p.label).toHaveText('Focus 2/2');
  await p.play();
  await page.clock.fastForward(20_000);
  await p.reset();
  await expect(p.label).toHaveText('Focus 2/2'); // this step again
  await p.reset();
  await expect(p.label).toHaveText('Focus 1/2'); // the sequence again
});

test('a sequence it cannot read says so and falls back to focus and break', async ({ page }) => {
  const p = await show(page, { focus: 1, break: 1, sequence: '25 focus, lunch' });
  await expect(p.sub).toHaveText('Sequence not understood: "lunch"');
  await expect(p.label).toHaveText('Focus');
});

test('a timer running before the update keeps running', async ({ page }) => {
  // state saved by the earlier focus / break widget: a break with 30 s to go, two sessions today
  const p = await show(page, { focus: 1, break: 1 }, { phase: 'break', endsAt: 30_000, left: MIN, count: 2 });
  await expect(p.label).toHaveText('Break');
  await expect(p.w).toHaveClass(/run/);
  await expect(p.sub.locator('.ico')).toHaveCount(2);
  await page.clock.fastForward(31_000);
  await expect(p.label).toHaveText('Focus');
});

test('the message names the step that starts next', async ({ page }) => {
  const p = await show(page, { sequence: '1 break, 1 long, 1 focus, 1 focus' });
  await p.play();
  await page.clock.fastForward(MIN + 1500);
  await expect(page.locator('#toast')).toContainText('Time for a long break');
  await page.clock.fastForward(MIN + 1500);
  await expect(page.locator('#toast')).toContainText('Back to focus');
  await page.clock.fastForward(MIN + 1500);
  await expect(page.locator('#toast')).toContainText('Next focus session');
});

test('a sequence longer than 48 steps is refused, not cut short', async ({ page }) => {
  const p = await show(page, { focus: 1, break: 1, sequence: Array(49).fill('1 focus').join(', ') });
  await expect(p.sub).toHaveText('Sequence not understood: "more than 48 steps"');
});
