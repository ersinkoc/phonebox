const { test, expect } = require('@playwright/test');
const { queuedSwitch } = require('./rules-pause');

for (const [name, options, expected] of [
  ['pausing cancels an already queued rule switch', { rule: true, mode: 'pause' }, 0],
  ['pausing keeps an independent webhook switch', { rule: false, mode: 'pause' }, 2],
  ['resuming keeps an independent webhook switch', { rule: false, mode: 'resume' }, 2],
  ['a failed pause keeps the queued switch', { rule: true, mode: 'failure' }, 2],
]) {
  test(name, async ({ page }) => {
    expect(await queuedSwitch(page, options)).toBe(expected);
  });
}
