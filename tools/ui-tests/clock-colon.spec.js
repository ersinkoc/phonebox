const { test, expect } = require('@playwright/test');

test.use({ timezoneId: 'UTC' });

// The clock's colon used to blink with an infinite CSS animation. A running animation makes the browser produce a frame on
// every display refresh (120 a second on a 120 Hz phone) even though the colon changes only once a second, which kept the
// panel at several times the CPU it needs. The clock now dims the colon itself on odd seconds, from its own tick.
async function mountClock(page, iso) {
  await page.clock.install({ time: new Date(iso) });
  await page.goto('/panel/');
  await page.clock.setFixedTime(new Date(iso));
  await page.evaluate(async () => {
    await import('/panel/main.js');
    const { W } = await import('/panel/registry.js');
    const box = document.createElement('section');
    box.id = 'clock-fixture';
    box.style.cssText = 'position: fixed; left: 0; top: 0; width: 400px; height: 200px';
    document.body.append(box);
    const widget = W.clock.create(box, { o: { status: false, date: false, weather: false }, page: { id: 'fixture' }, w: { id: 'clock' } });
    window.tickClock = () => widget.update('tick');
    window.tickClock();
  });
  return page.locator('#clock-fixture .clock .colon');
}

test('the clock colon has no running animation', async ({ page }) => {
  const colon = await mountClock(page, '2026-01-01T12:15:00Z');
  await expect(colon).toBeVisible();
  const running = await page.evaluate(() => document.getAnimations().filter((a) => a.effect?.target?.matches?.('.clock .colon')).length);
  expect(running, 'EXPECTED: no CSS animation on the colon | ACTUAL: ' + running).toBe(0);
  expect(await colon.evaluate((el) => getComputedStyle(el).animationName)).toBe('none');
});

test('the colon is dimmed on odd seconds and bright on even ones', async ({ page }) => {
  const colon = await mountClock(page, '2026-01-01T12:15:00Z'); // second 0
  const opacity = () => colon.evaluate((el) => Number(getComputedStyle(el).opacity));
  const at = async (iso) => { await page.clock.setFixedTime(new Date(iso)); await page.evaluate(() => window.tickClock()); };
  expect(await opacity()).toBeCloseTo(0.55, 2);
  await at('2026-01-01T12:15:01Z');
  expect(await opacity()).toBeCloseTo(0.2, 2);
  await at('2026-01-01T12:15:02Z');
  expect(await opacity()).toBeCloseTo(0.55, 2);
  await at('2026-01-01T12:15:59Z');
  expect(await opacity()).toBeCloseTo(0.2, 2);
});
