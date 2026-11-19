const { test, expect } = require('@playwright/test');

// The song position bar is a slider: while it is held, a bubble says where the song will continue, and it seeks only on
// release. The panel's own once-a-second state must not move the thumb under the finger.
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'media-test', title: 'Media', widgets: [{ id: 'media', type: 'media', x: 0, y: 0, w: 12, h: 6 }] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.goto('/panel/');
  await expect(page.locator('[data-id="media-test"] .w-media')).toBeVisible();
  await page.evaluate(() => {
    window.onAppPause(true); // the demo's own state would replace the media
    window.__seeks = [];
    const original = window.fetch;
    window.fetch = (url, opts = {}) => {
      if (String(url).includes('/media/seek')) {
        window.__seeks.push(String(url));
        return Promise.resolve(new Response('{"ok":true}', { headers: { 'Content-Type': 'application/json' } }));
      }
      return original(url, opts);
    };
  });
});

const track = (posMs, extra = {}) => ({ app: 'Spotify', title: 'T', artist: 'A', album: '', playing: true, posMs, durMs: 200000, canSeek: true, ageMs: 0, ...extra });
const apply = (page, media) => page.evaluate(async (m) => {
  const main = await import('/panel/main.js');
  main.applyState({ ts: 9e12 + (window.__n = (window.__n || 0) + 1), evSeq: 0, media: m });
}, media);
const bar = (page) => page.locator('[data-id="media-test"] .w-media .bar');
const at = async (page, fraction) => {
  const box = await bar(page).boundingBox();
  return { x: box.x + box.width * fraction, y: box.y + box.height / 2 };
};
const seeks = (page) => page.evaluate(() => window.__seeks.map((u) => Number(new URL(u, location.href).searchParams.get('ms'))));

test('holding the bar shows where the song will continue and seeks only on release', async ({ page }) => {
  await apply(page, track(30000));
  const tip = page.locator('[data-id="media-test"] .w-media .scrub-tip');
  const p = await at(page, 0.5);
  await page.mouse.move(p.x, p.y);
  await page.mouse.down();
  await expect(bar(page)).toHaveClass(/scrubbing/);
  await expect(tip).toHaveText('1:40'); // half of 3:20
  await expect(page.locator('[data-id="media-test"] .w-media .m-pos')).toHaveText('1:40');
  expect(await seeks(page)).toEqual([]); // nothing yet

  const q = await at(page, 0.25);
  await page.mouse.move(q.x, q.y, { steps: 4 });
  await expect(tip).toHaveText('0:50');
  expect(await seeks(page)).toEqual([]);

  await page.mouse.up();
  await expect(bar(page)).not.toHaveClass(/scrubbing/);
  const sent = await seeks(page);
  expect(sent).toHaveLength(1);
  expect(Math.abs(sent[0] - 50000)).toBeLessThan(1500);
});

test('the once-a-second state does not move the bar while it is held', async ({ page }) => {
  await apply(page, track(30000));
  const p = await at(page, 0.5);
  await page.mouse.move(p.x, p.y);
  await page.mouse.down();
  await apply(page, track(31000));
  await page.evaluate(() => new Promise((r) => setTimeout(r, 600))); // a few of the 250 ms refreshes
  await expect(page.locator('[data-id="media-test"] .w-media .scrub-tip')).toHaveText('1:40');
  await expect(page.locator('[data-id="media-test"] .w-media .m-pos')).toHaveText('1:40');
  await page.mouse.up();
});

test('after releasing, an old position from the agent does not pull the bar back', async ({ page }) => {
  await apply(page, track(30000));
  const p = await at(page, 0.5);
  await page.mouse.move(p.x, p.y);
  await page.mouse.down();
  await page.mouse.up();
  await apply(page, track(31000)); // the agent has not seen the seek yet
  const pos = await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    return main.media.posMs + (performance.now() - main.media.at);
  });
  expect(pos).toBeGreaterThan(95000);
});

test('a cancelled touch seeks nothing and the bar goes back to the song', async ({ page }) => {
  await apply(page, track(30000));
  await page.evaluate(() => {
    const el = document.querySelector('[data-id="media-test"] .w-media .bar');
    const r = el.getBoundingClientRect();
    const init = { bubbles: true, pointerId: 7, pointerType: 'touch', clientX: r.left + r.width * 0.7, clientY: r.top + r.height / 2 };
    el.dispatchEvent(new PointerEvent('pointerdown', init));
    el.dispatchEvent(new PointerEvent('pointercancel', init));
  });
  await expect(bar(page)).not.toHaveClass(/scrubbing/);
  expect(await seeks(page)).toEqual([]);
});

test('a source that cannot seek does not scrub', async ({ page }) => {
  await apply(page, track(30000, { canSeek: false }));
  const p = await at(page, 0.5);
  await page.mouse.move(p.x, p.y);
  await page.mouse.down();
  await expect(bar(page)).not.toHaveClass(/scrubbing/);
  await page.mouse.up();
  expect(await seeks(page)).toEqual([]);
});

test('the bar works from the keyboard: arrows move by five seconds and show the bubble', async ({ page }) => {
  await apply(page, track(30000));
  await bar(page).focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('[data-id="media-test"] .w-media .scrub-tip')).toHaveText('0:35');
  await expect.poll(async () => (await seeks(page)).length).toBe(1);
  expect((await seeks(page))[0]).toBeGreaterThan(34000);
  expect((await seeks(page))[0]).toBeLessThan(36500);
});

// The panel runs on Chromium 80+, but :focus-visible is 86+. A browser that does not know a pseudo-class drops the whole
// rule it is in, so the bubble's rules must not share a selector list with it, or the bubble never shows on those phones.
test('the bubble rules do not share a selector list with :focus-visible', async ({ page }) => {
  const shared = await page.evaluate(() => {
    const found = [];
    for (const sheet of document.styleSheets) {
      let rules; try { rules = sheet.cssRules; } catch (_) { continue; }
      for (const r of rules) {
        const s = r.selectorText || '';
        if (s.includes(':focus-visible') && s.includes('.scrubbing')) found.push(s);
      }
    }
    return found;
  });
  expect(shared).toEqual([]);
});

test('changing track while the bar is held cancels the scrub, and releasing then seeks nothing', async ({ page }) => {
  await apply(page, track(30000));
  const p = await at(page, 0.9); // 3:00 of a 3:20 track
  await page.mouse.move(p.x, p.y);
  await page.mouse.down();
  await expect(bar(page)).toHaveClass(/scrubbing/);
  await apply(page, track(5000, { title: 'Next', durMs: 60000 })); // a 1:00 track: 3:00 is past its end
  await expect(bar(page)).not.toHaveClass(/scrubbing/);
  await page.mouse.up();
  expect(await seeks(page)).toEqual([]);
});

test('the bar tells assistive technology the range and the current time, also during normal playback', async ({ page }) => {
  await apply(page, track(90000));
  await expect(bar(page)).toHaveAttribute('aria-valuemax', '200');
  const now = Number(await bar(page).getAttribute('aria-valuenow'));
  expect(now).toBeGreaterThanOrEqual(90);
  expect(now).toBeLessThanOrEqual(91);
  await expect(bar(page)).toHaveAttribute('aria-valuetext', /1:3\d/);

  await page.waitForTimeout(1300); // playback moves on; the panel's own refresh is paused in these tests, so ask for it
  await page.evaluate(async () => (await import('/panel/main.js')).emit('fast'));
  expect(Number(await bar(page).getAttribute('aria-valuenow'))).toBeGreaterThanOrEqual(now + 1);
});

test('a track with no length (a live stream) leaves no stale range on the bar, and the next song brings it back', async ({ page }) => {
  await apply(page, track(90000));
  await expect(bar(page)).toHaveAttribute('aria-valuemax', '200');
  await expect(bar(page)).toHaveAttribute('aria-disabled', 'false');

  await apply(page, { app: 'Internet Radio', title: 'Live', artist: '', album: '', playing: true, posMs: 5000, durMs: 0, canSeek: false, ageMs: 0 });
  await expect(bar(page)).toHaveAttribute('aria-valuenow', '0'); // not 90 with a range of 1
  await expect(bar(page)).toHaveAttribute('aria-valuemax', '0');
  await expect(bar(page)).toHaveAttribute('aria-disabled', 'true');

  await apply(page, track(40000, { title: 'Back' }));
  await expect(bar(page)).toHaveAttribute('aria-valuemax', '200');
  await expect(bar(page)).toHaveAttribute('aria-disabled', 'false');
  expect(Number(await bar(page).getAttribute('aria-valuenow'))).toBeGreaterThanOrEqual(40);
});
