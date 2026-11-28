const { test, expect } = require('@playwright/test');

async function stubAudio(page) {
  await page.addInitScript(() => {
    window.__tones = [];
    const param = (set) => ({ setValueAtTime: set, linearRampToValueAtTime: set, exponentialRampToValueAtTime() {} });
    window.AudioContext = class {
      constructor() { this.currentTime = 0; this.state = 'running'; this.destination = {}; window.__audio = this; }
      resume() { this.state = 'running'; return Promise.resolve(); }
      suspend() { this.state = 'suspended'; return Promise.resolve(); }
      createGain() { const g = { peak: 0, connect() {} }; g.gain = param((v) => { g.peak = Math.max(g.peak, v); }); return g; }
      createOscillator() {
        const o = { freq: 0, gain: null, stop() {} };
        o.frequency = param((v) => { o.freq = v; });
        o.connect = (g) => { o.gain = g; };
        o.start = () => window.__tones.push({ freq: o.freq, peak: o.gain ? o.gain.peak : 0 });
        return o;
      }
    };
  });
}

const tones = (page) => page.evaluate(() => window.__tones);

// A session that finishes shows a "finished" toast unless Settings › Display turns it off (#275). The "needs input" toast
// always shows: it asks the user to do something.
async function load(page, display, night = { enabled: false }) {
  await stubAudio(page);
  await page.addInitScript(({ d, n }) => {
    const config = JSON.stringify({ night: n, rules: [], shortcuts: [], display: d, pages: [{ id: 'p', title: 'P', widgets: [] }] });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  }, { d: display, n: night });
  await page.goto('/panel/');
  await page.evaluate(() => import('/panel/main.js'));
}
// moves one made-up session through the given states, as the work poll would
const run = (page, ...states) => page.evaluate(async (list) => {
  const { notifyTransitions } = await import('/panel/main.js');
  document.getElementById('toast').className = 'toast';
  for (const status of list) notifyTransitions([{ id: 'claude:toast-test', name: 'Build', status, waitingFor: status === 'waiting' ? 'permission prompt' : null }]);
  const t = document.getElementById('toast');
  return { on: t.classList.contains('on'), text: t.textContent };
}, states);

test('a finished session shows a toast by default', async ({ page }) => {
  await load(page, { vibrate: true });
  const r = await run(page, 'busy', 'idle');
  expect(r.on).toBe(true);
  expect(r.text).toContain('Build finished');
});

test('the finished toast can be turned off, and the needs-input toast still shows', async ({ page }) => {
  await load(page, { vibrate: true, finishToast: false });
  expect((await run(page, 'busy', 'idle')).on, 'EXPECTED: no toast when a session finishes').toBe(false);
  const r = await run(page, 'waiting');
  expect(r.on).toBe(true);
  expect(r.text).toContain('permission prompt');
});

test('a finish plays one chime when finishSound is enabled, and multiple finishes in one update play only one chime', async ({ page }) => {
  await load(page, { finishSound: true, finishSoundVolume: 70 });
  await run(page, 'busy', 'idle');
  const t1 = await tones(page);
  expect(t1.length).toBe(3);
  expect(t1.map((x) => x.freq)).toEqual([660, 880, 1100]);

  // multiple sessions finishing in the same update play only one chime
  await page.evaluate(() => { window.__tones = []; });
  await page.evaluate(async () => {
    const { notifyTransitions } = await import('/panel/main.js');
    notifyTransitions([
      { id: 's1', name: 'One', status: 'busy' },
      { id: 's2', name: 'Two', status: 'busy' }
    ]);
    notifyTransitions([
      { id: 's1', name: 'One', status: 'idle' },
      { id: 's2', name: 'Two', status: 'idle' }
    ]);
  });
  const t2 = await tones(page);
  expect(t2.length).toBe(3);
  expect(t2.map((x) => x.freq)).toEqual([660, 880, 1100]);
});

test('without finishSound a finished session stays silent', async ({ page }) => {
  await load(page, {});
  await run(page, 'busy', 'idle');
  expect(await tones(page)).toEqual([]);
});

test('the night screen keeps the chime silent', async ({ page }) => {
  const NOON = new Date(2026, 0, 1, 12, 0, 0).getTime();
  await page.clock.install({ time: NOON });
  await load(page, { finishSound: true }, { enabled: true, start: '11:00', end: '13:00' });
  await expect(page.locator('body')).toHaveClass(/night/);
  await run(page, 'busy', 'idle');
  expect(await tones(page)).toEqual([]);
});

// A permission prompt can chime too (#497): two beeps, so it sounds different from the finish tune, once per prompt,
// at the finish sound's volume.
const ask = (page) => page.evaluate(() => window.PhoneBoxDemo.ask());

test('a permission prompt plays two beeps once when approvalSound is enabled', async ({ page }) => {
  await load(page, { approvalSound: true, finishSoundVolume: 50 });
  await ask(page);
  await expect(page.locator('#approval')).toBeVisible();
  const t = await tones(page);
  expect(t.map((x) => x.freq)).toEqual([880, 880]);
  expect(t[0].peak).toBeCloseTo(0.2);
  // the same prompt drawn again (as the next poll does) doesn't chime again
  await page.evaluate(async () => (await import('/panel/main.js')).renderApproval());
  expect(await tones(page)).toHaveLength(2);
});

test('without approvalSound a permission prompt stays silent', async ({ page }) => {
  await load(page, {});
  await ask(page);
  await expect(page.locator('#approval')).toBeVisible();
  expect(await tones(page)).toEqual([]);
});

test('a permission prompt that wakes the night screen stays silent', async ({ page }) => {
  const NOON = new Date(2026, 0, 1, 12, 0, 0).getTime();
  await page.clock.install({ time: NOON });
  await load(page, { approvalSound: true }, { enabled: true, start: '11:00', end: '13:00' });
  await expect(page.locator('body')).toHaveClass(/night/);
  await ask(page);
  await expect(page.locator('#approval')).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/); // the prompt still wakes the screen
  expect(await tones(page)).toEqual([]);
});

// WorkBuddy turns end as completed, stopped or failed (#701): a failure reads as one (an error toast, shown even with the
// finish toast off), and a feed that went quiet (unknown) is not a finish, so it says nothing.
const runWorkBuddy = (page, ...states) => page.evaluate(async (list) => {
  const { notifyTransitions } = await import('/panel/main.js');
  document.getElementById('toast').className = 'toast';
  for (const status of list) notifyTransitions([{ id: 'workbuddy:toast-test', tool: 'workbuddy', name: 'Night build', status, recent: status !== 'unknown' }]);
  const t = document.getElementById('toast');
  return { on: t.classList.contains('on'), err: t.classList.contains('err'), ok: t.classList.contains('ok'), text: t.textContent };
}, states);

test('a WorkBuddy turn that completes shows the finished toast and chimes', async ({ page }) => {
  await load(page, { finishSound: true });
  const r = await runWorkBuddy(page, 'busy', 'completed');
  expect(r.on && r.ok).toBe(true);
  expect(r.text).toContain('Night build finished');
  expect(await tones(page)).toHaveLength(3);
});

test('a failed WorkBuddy turn reads as a failure, not as finished', async ({ page }) => {
  await load(page, { finishSound: true, finishToast: false });
  const r = await runWorkBuddy(page, 'busy', 'failed');
  expect(r.on && r.err).toBe(true);
  expect(r.text).toContain('Night build');
  expect(r.text).toContain('Failed');
  expect(r.text).not.toContain('finished');
  expect(await tones(page)).toEqual([]);
});

test('a WorkBuddy feed that goes quiet stays quiet', async ({ page }) => {
  await load(page, { finishSound: true });
  expect((await runWorkBuddy(page, 'busy', 'unknown')).on).toBe(false);
  expect(await tones(page)).toEqual([]);
});
