const { test, expect } = require('@playwright/test');

async function openClock(page, type, options = {}) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.addInitScript(({ type, options }) => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'timers', title: 'Timers', widgets: [{
        id: 'clock', type, x: 0, y: 0, w: 6, h: 4, options: { presets: '1', focus: 1, break: 1, ...options },
      }] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  }, { type, options });
  await page.goto('/panel/');
  return page.locator('.w-' + type);
}

for (const type of ['timer', 'pomodoro']) {
  test(type + ' records completion when Pause is pressed after the deadline', async ({ page }) => {
    const widget = await openClock(page, type);
    const start = widget.locator(type === 'timer' ? '.chip-btn' : '.pb.main');
    await expect(start).toBeVisible();
    await page.clock.pauseAt(new Date(await page.evaluate(() => Date.now() + 2000)));
    await start.evaluate(button => button.click());
    const pause = widget.locator('.pb.main');
    await expect(pause.locator('[data-icon="pause"]')).toHaveCount(1);
    await page.clock.setSystemTime(new Date(await page.evaluate(() => Date.now() + 61_000)));
    await pause.evaluate(button => button.click());
    const actual = await page.evaluate(type => {
      const state = JSON.parse(localStorage.getItem((type === 'timer' ? 'tm:' : 'pomo:') + 'timers/clock'));
      return { completed: type === 'timer' ? state.total === 0 : state.phase === 'break' && state.count === 1,
        paused: state.endsAt === null };
    }, type);
    expect(actual, 'EXPECTED: completed and stopped | ACTUAL: ' + JSON.stringify(actual))
      .toEqual({ completed: true, paused: true });
  });
}

test('timer presets only offer positive durations that can be persisted', async ({ page }) => {
  const widget = await openClock(page, 'timer', { presets: 'Infinity\n1e308\n0\n-1\n0.5\n1' });
  await expect(widget.locator('.chip-btn')).toHaveText(['0.5m', '1m']);
  await widget.getByRole('button', { name: '0.5m' }).click();
  const state = await page.evaluate(() => JSON.parse(localStorage.getItem('tm:timers/clock')));
  expect(state.total).toBe(30_000);
  expect(Number.isFinite(state.endsAt)).toBe(true);
});
