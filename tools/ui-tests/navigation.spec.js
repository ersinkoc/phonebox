// Calm page switching (rules, webhooks): what a switch does after it was refused or the user moved on, against the
// web demo with a mocked clock. Every switch here is to a page two or more away, so it fades on the mocked clock
// instead of scrolling smoothly in real time.
const { test, expect } = require('@playwright/test');

const PAGES = ['one', 'two', 'three', 'four', 'five'].map((id) => ({ id, title: id[0].toUpperCase() + id.slice(1),
  widgets: [{ id: 'n-' + id, type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page ' + id } }] }));

async function show(page, display = {}) {
  await page.clock.install();
  await page.goto('/panel/style.css');
  await page.evaluate(({ pages, display }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], pages,
      display: { idleReturnSec: 0, minDwellSec: 0, touchGraceSec: 20, switchCountdownSec: 3, ...display },
    }));
  }, { pages: PAGES, display });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).toHaveCount(PAGES.length);
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

test('navigation fixture configuration survives delayed default initialization', async ({ page }) => {
  const { gatedLayout } = require('./layout-ordering');
  expect(await gatedLayout(page, p => show(p))).toEqual(['one', 'two', 'three', 'four', 'five']);
});

const activeDot = (page) => page.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')));
// fastForward fires each due timer at most once per call, so the panel's 250 ms and 1 s timers are stepped through
async function step(page, ms) { for (let t = 0; t < ms; t += 250) await page.clock.fastForward(250); }
async function stepUntil(page, fn, max = 10_000) {
  for (let t = 0; t < max; t += 250) { if (await fn()) return; await page.clock.fastForward(250); }
  expect(await fn()).toBe(true);
}
const emit = (page, kind, data) => page.evaluate(([k, d]) => window.PhoneBoxDemo.emit(k, d), [kind, data]);
// the demo's agent never lists this rule as active, like a rule that has ended
const holdThree = (page) => emit(page, 'goto', { page: 'three', rule: 'r1', name: 'R1', hold: true, countdown: 0, priority: 5 });

test('Stay on "Back to" after a rule ends keeps the page', async ({ page }) => {
  await show(page);
  await holdThree(page);
  await stepUntil(page, async () => (await activeDot(page)) === 2);
  await emit(page, 'back', { rule: 'r1' });
  await stepUntil(page, async () => /Back to/.test(await page.locator('#nvLine').textContent())
    && /\bon\b/.test(await page.locator('#navCard').getAttribute('class')));
  await page.locator('#nvStay').click();
  await step(page, 30_000);
  expect(await activeDot(page)).toBe(2);
});

test('a page opened later by hand is not undone by a rule that ended while you were elsewhere', async ({ page }) => {
  await show(page);
  await holdThree(page);
  await stepUntil(page, async () => (await activeDot(page)) === 2);
  await page.keyboard.press('ArrowRight');
  await emit(page, 'back', { rule: 'r1' });
  await step(page, 30_000);
  expect(await activeDot(page)).toBe(3);
  await page.keyboard.press('ArrowLeft');
  await step(page, 40_000);
  expect(await activeDot(page)).toBe(2);
});

test('a notification with a malformed countdown switches after the default one and blocks nothing', async ({ page }) => {
  await show(page);
  await emit(page, 'notify', { title: 'Deploy', text: 'done', page: 'three', countdown: 'soon' });
  await step(page, 6000);
  expect(await activeDot(page)).toBe(2);
  await emit(page, 'notify', { title: 'Deploy', text: 'done', page: 'five', countdown: 1e9 }); // at most 30 s
  await step(page, 35_000);
  expect(await activeDot(page)).toBe(4);
});

test('a notification whose countdown is an object is shown, and the next event still arrives', async ({ page }) => {
  await show(page);
  // valid JSON, but Number() of it throws ("Cannot convert object to primitive value")
  await emit(page, 'notify', { title: 'Odd', text: 'object countdown', page: 'three', countdown: { toString: null } });
  await expect(page.locator('#toast')).toContainText('object countdown');
  await step(page, 6000);
  expect(await activeDot(page)).toBe(2);
  await emit(page, 'notify', { title: 'Next', text: 'still delivered' });
  await expect(page.locator('#toast')).toContainText('still delivered');
});

test('page pause button and dots hold block idle return and rule switches with 2s overlay', async ({ page }) => {
  await show(page, { idleReturnSec: 30 });
  await page.keyboard.press('ArrowRight');
  expect(await activeDot(page)).toBe(1);

  const dots = page.locator('#dots');
  const pauseBtn = page.getByRole('button', { name: 'Pause page switching' });
  const overlay = page.locator('#pagePauseOverlay');

  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');
  await expect(pauseBtn.locator('.i-pause')).toBeVisible();
  await expect(pauseBtn.locator('.i-play')).toBeHidden();

  // Long-pressing #dots (outside .page-pause) toggles pause without opening overview and shows the 2s overlay.
  await dots.locator('i').first().dispatchEvent('pointerdown', { button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
  await step(page, 500);
  await dots.locator('i').first().dispatchEvent('pointerleave', { button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });

  await expect(page.locator('#overview')).toBeHidden();
  await expect(dots).toHaveClass(/\bpaused\b/);
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'true');
  await expect(pauseBtn.locator('.i-play')).toBeVisible();
  await expect(pauseBtn.locator('.i-pause')).toBeHidden();
  await expect(overlay).toHaveClass(/\bshow\b/);

  await step(page, 2100);
  await expect(overlay).not.toHaveClass(/\bshow\b/);
  await expect(dots).toHaveClass(/\bpaused\b/);

  // Idle return is blocked while paused.
  await step(page, 60_000);
  expect(await activeDot(page)).toBe(1);

  // Rule switches are also held while paused and appear in #navChip with the pause reason.
  await emit(page, 'goto', { page: 'four', rule: 'r1', name: 'Rule 1', hold: true, countdown: 3, priority: 5 });
  await step(page, 40_000);
  expect(await activeDot(page)).toBe(1);
  await expect(page.locator('#navChip')).toHaveClass(/\bon\b/);
  await expect(page.locator('#ncWhy')).toHaveText('page paused');

  // Tapping .page-pause after a hold that ended with pointerleave still resumes and shows the 2s overlay.
  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');
  await expect(dots).not.toHaveClass(/\bpaused\b/);
  await expect(overlay).toHaveClass(/\bshow\b/);

  await step(page, 2100);
  await expect(overlay).not.toHaveClass(/\bshow\b/);

  // Once resumed and touchGrace + countdown expire, the queued rule switch executes.
  await step(page, 30_000);
  expect(await activeDot(page)).toBe(3);
});

test('switching to another page automatically clears page pause', async ({ page }) => {
  await show(page, { idleReturnSec: 30 });
  await page.keyboard.press('ArrowRight');
  expect(await activeDot(page)).toBe(1);

  const dots = page.locator('#dots');
  const pauseBtn = page.getByRole('button', { name: 'Pause page switching' });

  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'true');
  await expect(dots).toHaveClass(/\bpaused\b/);

  // Manually navigating to another page clears the pause without requiring another tap.
  await page.keyboard.press('ArrowRight');
  expect(await activeDot(page)).toBe(2);
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');
  await expect(dots).not.toHaveClass(/\bpaused\b/);

  // Idle return now operates normally and returns to page one after 30 s + countdown.
  await step(page, 40_000);
  expect(await activeDot(page)).toBe(0);
});
