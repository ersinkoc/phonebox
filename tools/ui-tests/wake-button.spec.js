// The Wake button on the offline panel: shown only when the app can wake the paired PC, one burst per allowed tap.
const { test, expect } = require('@playwright/test');

// A stub of the app's native bridge: the page may only hand it a computer's id.
async function open(page, { can = true } = {}) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  // remembers which of the panel's 1 s wake timers are still running (the clock's own setInterval is already in place)
  await page.addInitScript(() => {
    const set = window.setInterval, clear = window.clearInterval;
    window.__wakeTimers = new Set();
    window.setInterval = (fn, ms, ...args) => {
      const id = set(fn, ms, ...args);
      if (ms === 1000 && String(fn).includes('wakeRender')) window.__wakeTimers.add(id);
      return id;
    };
    window.clearInterval = (id) => { window.__wakeTimers.delete(id); return clear(id); };
  });
  await page.addInitScript((can) => {
    window.__phoneboxKey = 'k';
    window.__wakes = [];
    window.Android = {
      pcs: () => JSON.stringify([{ id: 'pc1', name: 'Desk', active: true }]),
      canWake: () => { window.__canCalls = (window.__canCalls || 0) + 1; return window.__can ?? can; },
      wakePc: (key, id) => { window.__wakes.push(id); return true; },
    };
  }, can);
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
  await page.evaluate(async () => {
    window.fetch = () => Promise.reject(new TypeError('the agent is not running'));
    const m = await import('/panel/main.js');
    m.setOnline(false);
  });
}

const wakes = (page) => page.evaluate(() => window.__wakes);
const button = (page) => page.locator('#wake button');

test('an unreachable PC shows the Wake button without sending anything', async ({ page }) => {
  await open(page);
  await expect(button(page)).toBeVisible();
  await expect(button(page)).toHaveText('Wake the PC');
  expect(await wakes(page)).toEqual([]);
});

test('a double tap sends one wake for the active computer and shows that it is waking', async ({ page }) => {
  await open(page);
  await button(page).click();
  await button(page).click({ force: true });
  expect(await wakes(page)).toEqual(['pc1']);
  await expect(button(page)).toHaveText('Waking…');
  await expect(button(page)).toBeDisabled();
});

test('the button works again after 10 seconds', async ({ page }) => {
  await open(page);
  await button(page).click();
  await page.clock.runFor(10_500);
  await expect(button(page)).toBeEnabled();
  await button(page).click();
  expect(await wakes(page)).toEqual(['pc1', 'pc1']);
});

test('after 3 minutes without an answer it says the PC did not wake', async ({ page }) => {
  await open(page);
  await button(page).click();
  await page.clock.runFor(181_000);
  await expect(page.locator('#wake .note')).toContainText("Didn't wake");
  await expect(button(page)).toHaveText('Wake the PC');
});

test('the "did not wake" note has an opaque backing so it stays readable over the panel', async ({ page }) => {
  await open(page);
  await button(page).click();
  await page.clock.runFor(181_000);
  const alpha = await page.locator('#wake .note').evaluate((el) => {
    const m = getComputedStyle(el).backgroundColor.match(/rgba?\(([^)]+)\)/);
    const parts = m ? m[1].split(',').map(Number) : [0, 0, 0, 0];
    return parts.length === 4 ? parts[3] : 1;
  });
  expect(alpha).toBeGreaterThanOrEqual(0.9);
});

test('a PC that stays unreachable all night does not poll the app every second', async ({ page }) => {
  await open(page);
  await page.clock.runFor(60_000);
  expect(await page.evaluate(() => window.__canCalls)).toBeLessThan(20);
});

test('the 1 s wake timer stops when the 3 minutes are over, even if the network went away and the button is hidden', async ({ page }) => {
  await open(page);
  await button(page).click();
  expect(await page.evaluate(() => window.__wakeTimers.size)).toBe(1);
  await page.evaluate(() => { window.__can = false; }); // the phone leaves the local network
  await page.clock.runFor(190_000);
  await expect(page.locator('#wake')).toBeHidden();
  expect(await page.evaluate(() => window.__wakeTimers.size)).toBe(0);
});

test('without a stored MAC or a local network there is no Wake button', async ({ page }) => {
  await open(page, { can: false });
  await expect(page.locator('#wake')).toBeHidden();
});

test('when the agent answers again the button goes away and the next outage starts fresh', async ({ page }) => {
  await open(page);
  await button(page).click();
  await expect(button(page)).toHaveText('Waking…');
  await page.evaluate(async () => { (await import('/panel/main.js')).setOnline(true); });
  await expect(page.locator('#wake')).toBeHidden();
  await page.evaluate(async () => { (await import('/panel/main.js')).setOnline(false); });
  await expect(button(page)).toHaveText('Wake the PC');
  await expect(button(page)).toBeEnabled();
});
