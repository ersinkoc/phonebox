const { test, expect } = require('@playwright/test');

// The deck's level meters read in dB (-60 dBFS empty, 0 dBFS full), and the bar is clipped rather than scaled so its
// colours stay at the same dB marks (#793, step 1).
async function show(page) {
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'mixer', type: 'audio-mixer', x: 0, y: 0, w: 12, h: 6, options: {} }] }] }));
  });
  await page.goto('/panel/');
}

const inset = (peak) => `inset(0px ${((1 - Math.max(0, Math.min(1, 1 + 20 * Math.log10(peak) / 60))) * 100).toFixed(1)}% 0px 0px)`;

test('the meter maps a peak to dB', async ({ page }) => {
  await show(page);
  const levels = await page.evaluate(async () => {
    const { meterLevel } = await import('/panel/widgets/controls.js');
    return [1, 0.1, 0.001, 0.0001, 0, -1, 2, NaN, undefined].map(meterLevel);
  });
  expect(levels[0]).toBe(1);                  // 0 dBFS: full
  expect(levels[1]).toBeCloseTo(2 / 3, 10);   // -20 dBFS: two thirds, not a tenth
  expect(levels[2]).toBeCloseTo(0, 10);       // -60 dBFS: empty
  expect(levels.slice(3)).toEqual([0, 0, 0, 1, 0, 0]); // below -60 dBFS, nothing, invalid and over full scale
});

test('each channel bar is clipped to its level and keeps its colours in place', async ({ page }) => {
  await show(page);
  // the demo's Spotify and Browser sessions peak at 0.55 and 0.25
  for (const [name, peak] of [['Spotify', 0.55], ['Browser', 0.25]]) {
    const bar = page.locator('.deck-channel', { hasText: name }).locator('.deck-meter i');
    await expect(bar).toBeVisible();
    await expect.poll(() => bar.evaluate((e) => getComputedStyle(e).clipPath)).toBe(inset(peak));
    expect(await bar.evaluate((e) => getComputedStyle(e).transform)).toBe('none');
  }
});
