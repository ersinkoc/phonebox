// A Custom HTML page can send a number with a run (phonebox.run(id, value), #677). A dragged slider sends many: the
// panel sends one request per key at most every 300 ms and always the newest value last.
const { test, expect } = require('@playwright/test');

const SHORTCUTS = [
  { id: 'seek', label: 'Seek', icon: '⏩', color: '#6b7280', cmd: 'player', args: '--seek', value: { min: 0, max: 100, step: 1 } },
  { id: 'other', label: 'Other', icon: '⏩', color: '#6b7280', cmd: 'player', value: { min: 0, max: 100, step: 1 } },
];

// what the panel sent, each with the status the demo answered ("seek?value=37 200"), and when each one started and ended
const runs = (page) => page.evaluate(() => window.__runs.map((r) => r.url + ' ' + r.status));
const sent = (page) => page.evaluate(() => window.__runs.map((r) => r.url));

async function open(page) {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS,
    pages: [{ id: 'html-test', title: 'Remote', widgets: [{ id: 'h0', type: 'html', x: 0, y: 0, w: 6, h: 6, options: { html: '<p>remote</p>' } }] }],
  }));
  await page.goto('/panel/');
  const handle = await page.locator('[data-id="html-test"] iframe.html-frame').elementHandle();
  const frame = await handle.contentFrame();
  await expect.poll(() => frame.evaluate(() => typeof window.phonebox?.run)).toBe('function');
  // the demo answers in the page (no network request), so the runs are recorded around its fetch; __delay holds each
  // answer back, and __refuse answers that many runs with 429 (the agent's limit) before the demo sees them
  await page.evaluate(() => {
    const original = window.fetch; window.__runs = []; window.__delay = 0; window.__refuse = 0;
    window.fetch = async (input, init) => {
      const u = new URL(String(input.url || input), location.href);
      if (!u.pathname.includes('/api/run/')) return original(input, init);
      const r = { url: decodeURIComponent(u.pathname.split('/').pop()) + u.search, start: performance.now() };
      window.__runs.push(r);
      if (window.__delay) await new Promise((done) => setTimeout(done, window.__delay));
      const res = window.__refuse > 0 ? (window.__refuse--, new Response('{}', { status: 429 })) : await original(input, init);
      r.status = res.status; r.end = performance.now();
      return res;
    };
  });
  return frame;
}

test('run(id) still sends a plain run, and run(id, value) sends the number', async ({ page }) => {
  const frame = await open(page);
  await frame.evaluate(() => phonebox.run('seek'));
  await expect.poll(() => runs(page)).toEqual(['seek 200']);
  await frame.evaluate(() => phonebox.run('seek', 37));
  await expect.poll(() => runs(page)).toEqual(['seek 200', 'seek?value=37 200']);
});

test('a dragged slider sends the first value at once and the last one when it stops', async ({ page }) => {
  const frame = await open(page);
  await frame.evaluate(() => { for (let v = 0; v <= 40; v++) phonebox.run('seek', v); });
  await expect.poll(() => runs(page), { timeout: 2000 }).toEqual(['seek?value=0 200', 'seek?value=40 200']);
  await page.waitForTimeout(500);
  expect(await runs(page)).toEqual(['seek?value=0 200', 'seek?value=40 200']);
});

test('a long drag sends at most about 4 values a second, each one the newest', async ({ page }) => {
  const frame = await open(page);
  // a value every 20 ms for 1.5 s
  await frame.evaluate(() => new Promise((done) => { let v = 0; const t = setInterval(() => { phonebox.run('seek', v++); if (v > 75) { clearInterval(t); done(); } }, 20); }));
  await page.waitForTimeout(600);
  const list = await page.evaluate(() => window.__runs);
  expect(list.length).toBeGreaterThanOrEqual(4);
  expect(list.length).toBeLessThanOrEqual(7);
  for (let i = 1; i < list.length; i++) expect(list[i].start - list[i - 1].start).toBeGreaterThanOrEqual(290);
  expect(list[list.length - 1].url).toBe('seek?value=75');
});

test('a value waits for the one on its way, so they run in order', async ({ page }) => {
  const frame = await open(page);
  await page.evaluate(() => { window.__delay = 700; });
  await frame.evaluate(() => phonebox.run('seek', 1));
  await page.waitForTimeout(350);
  await frame.evaluate(() => { phonebox.run('seek', 2); phonebox.run('seek', 3); });
  await expect.poll(() => runs(page), { timeout: 4000 }).toEqual(['seek?value=1 200', 'seek?value=3 200']);
  const [a, b] = await page.evaluate(() => window.__runs);
  expect(b.start).toBeGreaterThanOrEqual(a.end);
});

test('a value refused as too fast is sent once more', async ({ page }) => {
  const frame = await open(page);
  await page.evaluate(() => { window.__refuse = 1; });
  await frame.evaluate(() => phonebox.run('seek', 5));
  await expect.poll(() => runs(page), { timeout: 3000 }).toEqual(['seek?value=5 429', 'seek?value=5 200']);
  await expect(page.locator('#toast')).not.toHaveClass(/\bon\b/);
});

test('a value refused as too fast twice is not dropped unseen', async ({ page }) => {
  const frame = await open(page);
  await page.evaluate(() => { window.__refuse = 2; });
  await frame.evaluate(() => phonebox.run('seek', 5));
  await expect.poll(() => runs(page), { timeout: 3000 }).toEqual(['seek?value=5 429', 'seek?value=5 429']);
  await expect(page.locator('#toast')).toContainText('The PC refused this number');
  const [a, b] = await page.evaluate(() => window.__runs);
  expect(b.start - a.end).toBeGreaterThanOrEqual(950); // the retry waits for the agent's one-second window
});

async function openTwo(page, refuse = 0) {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS,
    pages: [{ id: 'html-two', title: 'Two', widgets: [
      { id: 'h1', type: 'html', x: 0, y: 0, w: 4, h: 6, options: { html: '<p>a</p>' } },
      { id: 'h2', type: 'html', x: 4, y: 0, w: 4, h: 6, options: { html: '<p>b</p>' } }] }],
  }));
  await page.goto('/panel/');
  const frames = [];
  for (const i of [0, 1]) frames.push(await (await page.locator('[data-id="html-two"] iframe.html-frame').nth(i).elementHandle()).contentFrame());
  for (const f of frames) await expect.poll(() => f.evaluate(() => typeof window.phonebox?.run)).toBe('function');
  await page.evaluate((refuse) => {
    const original = window.fetch; window.__runs = []; window.__refuse = refuse;
    window.fetch = async (input, init) => {
      const u = new URL(String(input.url || input), location.href);
      if (!u.pathname.includes('/api/run/')) return original(input, init);
      const r = { url: decodeURIComponent(u.pathname.split('/').pop()) + u.search, start: performance.now() };
      window.__runs.push(r);
      const res = window.__refuse > 0 ? (window.__refuse--, new Response('{}', { status: 429 })) : await original(input, init);
      r.status = res.status;
      return res;
    };
  }, refuse);
  return frames;
}

test('two widgets on one key share its queue: a retry does not undo a newer value from the other widget', async ({ page }) => {
  const frames = await openTwo(page, 1);
  await frames[0].evaluate(() => phonebox.run('seek', 10));
  await expect.poll(() => runs(page)).toEqual(['seek?value=10 429']);
  await frames[1].evaluate(() => phonebox.run('seek', 90));
  await expect.poll(() => runs(page)).toEqual(['seek?value=10 429', 'seek?value=90 200']);
  await page.waitForTimeout(1500); // past the retry
  expect(await runs(page)).toEqual(['seek?value=10 429', 'seek?value=90 200']);
});

test('a value one widget still holds back is dropped once another widget sent a newer one, and the pace is shared', async ({ page }) => {
  const frames = await openTwo(page);
  // widget A: 10 at once, 20 a moment later (held back by its pace); widget B: 90 after that, before A's 20 goes out
  await Promise.all([
    frames[0].evaluate(() => { phonebox.run('seek', 10); setTimeout(() => phonebox.run('seek', 20), 100); }),
    frames[1].evaluate(() => { setTimeout(() => phonebox.run('seek', 90), 200); }),
  ]);
  await expect.poll(() => sent(page), { timeout: 3000 }).toEqual(['seek?value=10', 'seek?value=90']);
  await page.waitForTimeout(800);
  expect(await sent(page)).toEqual(['seek?value=10', 'seek?value=90']);
  const [a, b] = await page.evaluate(() => window.__runs);
  expect(b.start - a.start).toBeGreaterThanOrEqual(290); // B's value waited for the key's pace, though B sent nothing before
});

test('a value still held back when the widget goes away is sent as it unloads', async ({ page }) => {
  const frame = await open(page);
  await frame.evaluate(() => { phonebox.run('seek', 10); phonebox.run('seek', 20); }); // 20 waits for the pace
  await page.evaluate(() => document.querySelector('[data-id="html-test"] iframe.html-frame').remove());
  // the prelude's timer went with the frame: without the flush on unload, 20 never leaves
  await expect.poll(() => sent(page), { timeout: 2000 }).toEqual(['seek?value=10', 'seek?value=20']);
});

test('a value the computer refuses says so', async ({ page }) => {
  const frame = await open(page);
  await frame.evaluate(() => phonebox.run('seek', 500)); // above the range
  await expect.poll(() => runs(page)).toEqual(['seek?value=500 400']);
  await expect(page.locator('#toast')).toContainText('The PC refused this number');
});

test('each key has its own pace', async ({ page }) => {
  const frame = await open(page);
  await frame.evaluate(() => { phonebox.run('seek', 1); phonebox.run('other', 2); });
  await expect.poll(async () => (await runs(page)).sort()).toEqual(['other?value=2 200', 'seek?value=1 200']);
});

test('what is not a finite number, or not a key, is not sent', async ({ page }) => {
  const frame = await open(page);
  await frame.evaluate(() => { phonebox.run('seek', 'abc'); phonebox.run('seek', Infinity); phonebox.run('nope', 5); phonebox.run('seek', '12'); });
  // a numeric string counts as its number; the others are dropped before they leave the page
  await expect.poll(() => sent(page)).toEqual(['seek?value=12']);
  await page.waitForTimeout(500);
  expect(await sent(page)).toEqual(['seek?value=12']);
});

test("the page's arithmetic noise is rounded off before the number leaves the panel", async ({ page }) => {
  const frame = await open(page);
  await frame.evaluate(() => phonebox.run('other', 0.1 + 0.2));
  await expect.poll(() => sent(page)).toEqual(['other?value=0.3']);
});

test('the key editor gives a key that runs a program a number range, and saves it', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.addInitScript((config) => { if (!sessionStorage.getItem('seeded')) { sessionStorage.setItem('seeded', '1'); localStorage.setItem('pb-demo-config', config); } }, JSON.stringify({
    night: { enabled: false }, rules: [], pages: [],
    shortcuts: [
      { id: 'seek', label: 'Seek', icon: '⏩', color: '#6b7280', cmd: 'player', args: '--seek {value}' },
      { id: 'lamp', label: 'Lamp', icon: '💡', color: '#6b7280', cmd: 'lamp', toggle: true },
      { id: 'copy', label: 'Copy', icon: '📋', color: '#6b7280', cmd: 'builtin:keys', args: 'ctrl+c' },
    ],
  }));
  await page.goto('/settings/#shortcuts');
  const keys = page.locator('#deck .key:not(.add)');
  const takes = page.getByLabel('Takes a number from a Custom HTML page or a script');
  await keys.nth(1).click();
  await expect(takes).toHaveCount(0);        // an on / off key
  await keys.nth(2).click();
  await expect(takes).toHaveCount(0);        // a built-in key
  await keys.nth(0).click();
  await expect(page.getByLabel('Lowest', { exact: true })).toHaveCount(0);
  await takes.check();
  await expect(page.getByLabel('Lowest', { exact: true })).toHaveValue('0');
  await expect(page.getByLabel('Highest', { exact: true })).toHaveValue('100');
  await expect(page.getByLabel('Step', { exact: true })).toHaveValue('1');
  await page.getByLabel('Highest', { exact: true }).fill('250');
  await page.getByLabel('Step', { exact: true }).fill('0.5');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).shortcuts.find((s) => s.id === 'seek').value))
    .toEqual({ min: 0, max: 250, step: 0.5 });
  await takes.uncheck();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => page.evaluate(() => 'value' in JSON.parse(localStorage.getItem('pb-demo-config')).shortcuts.find((s) => s.id === 'seek'))).toBe(false);
});
