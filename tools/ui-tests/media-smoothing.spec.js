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
      return Math.round(m.mediaPosMs(m.media));
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

test('non-1.0x playback rate scales age, live progression and smoothing without jumping on speed changes', async ({ page }) => {
  const r = await run(page, [
    { media: track(10000, { playbackRate: 2, ageMs: 500 }) },       // starts at 11000
    { wait: 1000, media: track(13100, { playbackRate: 2 }) },       // within 0.3 s of 13000: stays smooth
    { wait: 1000, media: track(15000, { playbackRate: 0.5 }) },     // speed change snaps to 15000 at 0.5x
    { wait: 2000, media: track(16050, { playbackRate: 0.5 }) },     // 2 s at 0.5x -> 16000
  ]);
  expect(r[0]).toBeGreaterThanOrEqual(10980);
  expect(r[0]).toBeLessThanOrEqual(11040);
  expect(r[1]).toBeGreaterThanOrEqual(12980);
  expect(r[1]).toBeLessThanOrEqual(13040);
  expect(r[2]).toBeGreaterThanOrEqual(14980);
  expect(r[2]).toBeLessThanOrEqual(15040);
  expect(r[3]).toBeGreaterThanOrEqual(15980);
  expect(r[3]).toBeLessThanOrEqual(16040);
});

test('Now Playing widget renders time scaled by playback rate between agent samples', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('.w-media .m-pos').first()).toBeVisible();
  await page.evaluate(() => window.onAppPause(true));
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));

  const applyAndTick = (sample) => page.evaluate(async (m) => {
    const main = await import('/panel/main.js');
    main.applyState({ ts: Date.now() + 1e12 + (window.__n = (window.__n || 0) + 1), evSeq: 0, media: m });
    main.emit('fast');
  }, sample);

  await applyAndTick(track(10000, { playbackRate: 2 }));
  await page.clock.runFor(2000);
  await page.evaluate(async () => (await import('/panel/main.js')).emit('fast'));
  await expect(page.locator('.w-media .m-pos').first()).toHaveText('0:14');

  await applyAndTick(track(10000, { playbackRate: 0.5 }));
  await page.clock.runFor(2000);
  await page.evaluate(async () => (await import('/panel/main.js')).emit('fast'));
  await expect(page.locator('.w-media .m-pos').first()).toHaveText('0:11');
});
