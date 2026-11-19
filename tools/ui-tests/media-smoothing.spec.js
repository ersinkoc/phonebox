const { test, expect } = require('@playwright/test');

// The media position is counted on the phone between states. A new sample must not make the bar jump every second:
// small differences are ignored, middling ones are eased in, only a real jump (seek, track change, pause) snaps.
async function run(page, steps) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.evaluate(() => window.onAppPause(true)); // the demo's own once-a-second state would replace the media
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z')); // time moves only with runFor, so a loaded machine cannot skew it
  const results = [];
  for (const step of steps) {
    if (step.wait) await page.clock.runFor(step.wait);
    results.push(await page.evaluate(async (sample) => {
      const m = await import('/panel/main.js');
      m.applyState({ ts: Date.now() + 1e12 + (window.__n = (window.__n || 0) + 1), evSeq: 0, media: sample });
      return Math.round(m.media.posMs + (m.media.playing ? performance.now() - m.media.at : 0));
    }, step.media));
  }
  return results;
}
const track = (posMs, extra = {}) => ({ app: 'Spotify', title: 'T', artist: 'A', album: '', playing: true, posMs, durMs: 200000, canSeek: true, ageMs: 0, ...extra });

test('a sample within 0.3 s of where the bar is changes nothing', async ({ page }) => {
  const [first, second] = await run(page, [{ media: track(10000) }, { wait: 1000, media: track(11150) }]);
  expect(first).toBeLessThan(10040);
  expect(second).toBeGreaterThanOrEqual(10990);
  expect(second).toBeLessThanOrEqual(11040); // kept the bar's own 11000, not the sample's 11150
});

test('a middling difference ahead is eased in instead of jumping', async ({ page }) => {
  const [, ahead] = await run(page, [{ media: track(10000) }, { wait: 1000, media: track(11800) }]);
  expect(ahead).toBeGreaterThan(11150);
  expect(ahead).toBeLessThan(11400); // about 30 % of the 800 ms gap
});

test('a middling difference behind is eased back, not all at once', async ({ page }) => {
  const [, behind] = await run(page, [{ media: track(10000) }, { wait: 1000, media: track(10200) }]);
  expect(behind).toBeGreaterThan(10600);
  expect(behind).toBeLessThan(10900); // the bar was at 11000, the sample says 10200: 30 % of the way back
});

test('a real jump, a pause or a new track snaps to the sample', async ({ page }) => {
  const r = await run(page, [
    { media: track(10000) },
    { wait: 1000, media: track(40000) }, // seek
    { wait: 1000, media: track(41000, { playing: false }) }, // pause
    { wait: 1000, media: track(5000, { title: 'Other' }) }, // new track
  ]);
  const near = (got, want) => { expect(got).toBeGreaterThanOrEqual(want); expect(got).toBeLessThan(want + 40); };
  near(r[1], 40000);
  near(r[2], 41000); // paused: nothing is added
  near(r[3], 5000);
});

test('the age the agent reports is counted from the moment of arrival', async ({ page }) => {
  const [pos] = await run(page, [{ media: track(10000, { ageMs: 900 }) }]);
  expect(pos).toBeGreaterThanOrEqual(10890);
  expect(pos).toBeLessThanOrEqual(10910);
});
