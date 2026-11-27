// UI tests of the panel and the settings page, against the web demo. They only use what a person sees and does
// (DOM, keys, clicks) plus the demo's PhoneBoxDemo helper, never the panel's internals.
const { test, expect } = require('@playwright/test');
const { staleState } = require('./state-ordering');

test('a late poll preserves the newer state and metric history', async ({ page }) => {
  const actual = await staleState(page, { edges: true });
  expect(actual).toEqual({ host: 'newest', cpu: 22, hist: 22, equal: 'equal', withoutTimestamp: 'without timestamp' });
});

/** Fails the test on any console error or uncaught exception (outside services being down does not count). */
function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const url = m.location()?.url || '';
    if (/Failed to load resource/.test(m.text()) && url && !url.startsWith('http://127.0.0.1:8781')) return;
    errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

const activeDot = (page) => page.locator('#dots i').evaluateAll((dots) => dots.findIndex((d) => d.classList.contains('on')));

test.beforeEach(async ({ page }) => {
  // every test starts from the demo's default layout
  await page.goto('/panel/');
  await page.evaluate(() => { try { localStorage.clear(); } catch (_) {} });
});

test('the panel starts without errors and shows its pages', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  expect(await page.locator('#dots i').count()).toBeGreaterThan(10);
  await page.waitForTimeout(1500); // a few state updates
  expect(errors).toEqual([]);
});

test('no widget fails to build on any page', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // a widget whose create() throws shows "<type>: <message>"
  const broken = await page.locator('.w > .empty').evaluateAll((els) => els.map((e) => e.textContent).filter((t) => /^[a-z]+: /.test(t)));
  expect(broken).toEqual([]);
});

test('every widget in the catalogue builds and runs without errors', async ({ page }) => {
  test.setTimeout(90_000); // Each catalog page runs its render and polling lifecycle; the catalog now exceeds 100 types.
  const errors = watchErrors(page);
  // Widgets that read a public service straight from the phone (crypto prices, exchange rates, Hacker News) must not
  // make this test depend on the internet: a CORS refusal from CI once failed it. They get an error answer that allows
  // the panel, so they show their own "unavailable" state, which is what this test checks they survive.
  await page.route((url) => !['127.0.0.1', 'localhost'].includes(url.hostname), (route) => route.fulfill({
    status: 503, headers: { 'Access-Control-Allow-Origin': '*' }, body: '' }));
  await page.route('**/agent/api/steam/avatar?id=*', (route) => route.fulfill({
    status: 200,
    contentType: 'image/svg+xml',
    body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"><circle cx=".5" cy=".5" r=".5"/></svg>',
  }));
  await page.goto('/panel/');
  // one page per widget type, at its default size, through the demo's stored settings
  const count = await page.evaluate(async () => {
    const widgets = await (await fetch('../widgets.json')).json();
    const opts = (w) => Object.fromEntries((w.options || []).map((o) => [o.key, o.default]));
    const pages = widgets.map((w, i) => ({ id: 'w' + i, title: w.name, icon: w.icon, enabled: true,
      widgets: [{ id: w.type, type: w.type, x: 0, y: 0, w: w.w, h: w.h, options: opts(w) }] }));
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null');
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...(cfg || {}), pages, shortcuts: cfg?.shortcuts || [] }));
    return widgets.length;
  });
  await page.reload();
  await expect(page.locator('#dots i')).toHaveCount(count);
  // visit every page so each widget gets data and renders
  for (let i = 0; i < count; i++) { await page.keyboard.press('ArrowRight'); await page.waitForTimeout(60); }
  await page.waitForTimeout(1500);
  const broken = await page.locator('.w > .empty').evaluateAll((els) => els.map((e) => e.textContent).filter((t) => /^[a-z]+: /.test(t)));
  expect(broken).toEqual([]);
  expect(errors).toEqual([]);
});

test('charts explain unknown metrics and keep any known series', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    const pages = [{ id: 'p1', title: 'One', icon: '📊', enabled: true,
      widgets: [
        { id: 'c1', type: 'chart', x: 0, y: 0, w: 4, h: 3, options: { metrics: ['nosuchmetric'] } },
        { id: 'c2', type: 'chart', x: 4, y: 0, w: 4, h: 3, options: { metrics: ['gpupower', 'futuremetric'] } },
        { id: 'c3', type: 'chart', x: 8, y: 0, w: 4, h: 3, options: { metrics: ['cpupower'] } },
      ] }];
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, shortcuts: cfg.shortcuts || [] }));
  });
  await page.reload();
  const card = page.locator('.w-chart');
  await expect(card.first().locator('canvas')).toBeVisible();
  await expect(card.first().locator('.s-label')).toHaveText('Processor');
  await expect(card.first().locator('.chart-missing')).toHaveText('Unknown metrics: nosuchmetric. Check for an app update.');
  await expect(card.nth(1).locator('canvas')).toBeVisible();
  await expect(card.nth(1).locator('.s-label')).toHaveText('GPU power');
  await expect(card.nth(1).locator('.chart-missing')).toHaveText('Unknown metrics: futuremetric. Check for an app update.');
  await expect(card.nth(2).locator('.chart-missing')).toHaveCount(0);
  expect(await page.locator('.w > .empty').count()).toBe(0);
  expect(errors).toEqual([]);
});

test('keys flip pages and the overview jumps to a page', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // the demo's settings are loaded (until then the built-in defaults may show the night screen after 00:30)
  await expect.poll(() => page.locator('#dots i').count()).toBeGreaterThan(10);
  await expect(page.locator('body')).not.toHaveClass(/night/);
  expect(await activeDot(page)).toBe(0);
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => activeDot(page)).toBe(1);
  await page.keyboard.press('ArrowLeft');
  await expect.poll(() => activeDot(page)).toBe(0);

  await page.keyboard.press('o');
  await expect(page.locator('#overview')).toBeVisible();
  await page.locator('.ov-tile').nth(3).click();
  await expect(page.locator('#overview')).toBeHidden();
  await expect.poll(() => activeDot(page)).toBe(3);
});

test('a webhook notification shows a toast', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('notify', { title: 'Build #7', text: 'main passed', level: 'ok' }));
  await expect(page.locator('#toast')).toContainText('main passed');
});

test('an event the agent queued while it restarted is still delivered', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => {
    window.PhoneBoxDemo.emit('notify', { title: 'One', text: 'first', level: 'ok' });
    window.PhoneBoxDemo.emit('notify', { title: 'Two', text: 'second', level: 'ok' });
  });
  await expect(page.locator('#toast')).toContainText('second');
  // the agent restarted: its event counter starts over below the phone's (PushStore keeps events in memory)
  await page.evaluate(() => { window.PhoneBoxDemo.restart(); window.PhoneBoxDemo.emit('notify', { title: 'After', text: 'post-restart', level: 'ok' }); });
  await expect(page.locator('#toast')).toContainText('post-restart');
});

test('the stream path also replays events the agent queued while it restarted', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // the phone runs on the live stream, not the poll: feed states so streaming() stays true
  await page.evaluate(() => {
    window.__evSeq = 5;
    window.onAgentStream('open');
    window.__feed = setInterval(() => window.onAgentStream('state', JSON.stringify({ ts: Date.now(), evSeq: window.__evSeq })), 500);
  });
  await page.waitForTimeout(1200); // a few streamed states: the panel now tracks seq 5
  await page.evaluate(() => {
    // the agent restarted: its counter starts over below the phone's, and an event is already queued
    window.PhoneBoxDemo.restart();
    window.PhoneBoxDemo.emit('notify', { title: 'Backlog', text: 'backlog event', level: 'ok' });
    window.__evSeq = 1; // streamed states now report the restarted counter
  });
  await page.waitForTimeout(700); // a streamed state has armed the replay
  // a live event arrives while the backlog is being replayed: it has to be applied after it, not before
  await page.evaluate(() => window.onAgentStream('event', JSON.stringify({ seq: 2, kind: 'notify', data: { title: 'Live', text: 'live event', level: 'ok' } })));
  await expect(page.locator('#toast')).toContainText('live event');
  await expect.poll(async () => page.evaluate(async () => (await import('/panel/main.js')).D.notes.slice(0, 2).map((n) => n.title))).toEqual(['Live', 'Backlog']);
});

test('a follow-up replay merges its events with the ones still held', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => {
    window.__evSeq = 5;
    window.onAgentStream('open');
    window.__feed = setInterval(() => window.onAgentStream('state', JSON.stringify({ ts: Date.now(), evSeq: window.__evSeq })), 500);
  });
  await page.waitForTimeout(1200);
  await page.evaluate(() => {
    window.PhoneBoxDemo.restart();
    window.PhoneBoxDemo.emit('notify', { title: 'Backlog', text: 'backlog event', level: 'ok' });
  });
  // freeze the first ?ev=0 answer at a snapshot taken before the flood: it cannot contain the flooded events
  // (the demo answers fetch() inside the page, so the interception wraps window.fetch rather than the network)
  await page.evaluate(() => {
    const answer = window.fetch('/api/state?ev=0').then((r) => r.json());
    const orig = window.fetch;
    window.fetch = async (input, init) => {
      const url = String(typeof input === 'string' ? input : input.url);
      if (window.__frozen && url.includes('/api/state') && url.includes('ev=0')) {
        const frozen = window.__frozen;
        window.__frozen = null;
        return new Response(JSON.stringify(await frozen), { headers: { 'Content-Type': 'application/json' } });
      }
      return orig(input, init);
    };
    window.__frozen = answer;
  });
  await page.evaluate(() => {
    window.__evSeq = 1;
    window.onAgentStream('state', JSON.stringify({ ts: Date.now(), evSeq: 1 })); // arms the replay in this same task
    // events 2–102 arrive live and are in the agent's backlog too; the agent only retains the last 50 (53–102),
    // so the follow-up replay returns the newer half while seqs 3–52 stay held in the buffer (2 was pushed out)
    const push = (kind, data) => window.onAgentStream('event', JSON.stringify(window.PhoneBoxDemo.emit(kind, data)));
    for (let i = 0; i < 50; i++) push('back', {});
    push('back', { rule: 'r1' });                            // seq 52 — held: must run before the goto below
    push('goto', { page: 3, rule: 'r1' });                   // seq 53 — first event the follow-up replay returns
    for (let i = 0; i < 49; i++) push('back', {});
    window.__evSeq = 102;                                    // streamed states now match the demo counter
  });
  // if the replayed goto ran first, the older back would remove it from the nav queue and the page never opens
  // (page numbers are 1-based for the user, so page 3 shows as index 2)
  await expect.poll(async () => page.evaluate(async () => (await import('/panel/main.js')).curPage), { timeout: 10000 }).toBe(2);
  await page.waitForTimeout(1500); // and it has to stay open — the back must not run after it
  await expect.poll(async () => page.evaluate(async () => (await import('/panel/main.js')).curPage)).toBe(2);
});

test('events sent before the first state of a reopened stream wait for it', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => {
    window.__evSeq = 5;
    window.onAgentStream('open');
    window.__feed = setInterval(() => window.onAgentStream('state', JSON.stringify({ ts: Date.now(), evSeq: window.__evSeq })), 500);
  });
  await page.waitForTimeout(1200);
  await page.evaluate(() => {
    // the stream reopened against a restarted agent: it can send an event before its first state identifies the run
    window.onAgentStream('close');
    window.onAgentStream('open');
    window.PhoneBoxDemo.restart();
    const early = window.PhoneBoxDemo.emit('notify', { title: 'Early', text: 'before the first state', level: 'ok' });
    window.onAgentStream('event', JSON.stringify(early)); // seq 1 arrives while the panel still tracks seq 5
    window.__evSeq = 1;
    window.onAgentStream('state', JSON.stringify({ ts: Date.now(), evSeq: 1 })); // the first state arms the replay
  });
  // the event must be applied exactly once: not on arrival and again when the replay delivers it
  const earlyCount = async () => page.evaluate(async () =>
    (await import('/panel/main.js')).D.notes.filter((n) => n.title === 'Early').length);
  await expect.poll(earlyCount, { timeout: 10000 }).toBe(1);
  await page.waitForTimeout(2500); // let any duplicate from the replay land — the count must stay 1
  await expect.poll(earlyCount).toBe(1);
});

test('a webhook switches pages after a countdown', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 3 }));
  await expect(page.locator('#navCard')).toHaveClass(/on/);
  await expect(page.locator('#nvLine')).toContainText('Switching to');
  expect(await activeDot(page)).toBe(0);                     // not yet: it counts down first
  await expect.poll(() => activeDot(page), { timeout: 8000 }).toBe(2);
  await expect(page.locator('#navCard')).not.toHaveClass(/on/);
});

test('next and prev from the computer step one page at a time, without a countdown (#616)', async ({ page }) => {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
    pages: [
      { id: 'home', title: 'Home', widgets: [{ id: 'n1', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Home' } }] },
      { id: 'mid', title: 'Mid', widgets: [{ id: 'n2', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Mid' } }] },
      { id: 'end', title: 'End', widgets: [{ id: 'n3', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'End' } }] },
    ],
  }));
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const dot = () => activeDot(page);
  // next: immediate, and no countdown card
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { step: 'next' }));
  await expect.poll(dot, { timeout: 8000 }).toBe(1);
  await expect(page.locator('#navCard')).not.toHaveClass(/on/);
  // next keeps stepping until the last page, where it stays
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { step: 'next' }));
  await expect.poll(dot).toBe(2);
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { step: 'next' }));
  await expect.poll(dot).toBe(2);
  // prev goes back, and stays at the first page
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { step: 'prev' }));
  await expect.poll(dot).toBe(1);
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { step: 'prev' }));
  await expect.poll(dot).toBe(0);
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { step: 'prev' }));
  await expect.poll(dot).toBe(0);
});

test('two next requests in a row each step one page (#616)', async ({ page }) => {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
    pages: [
      { id: 'home', title: 'Home', widgets: [{ id: 'n1', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Home' } }] },
      { id: 'mid', title: 'Mid', widgets: [{ id: 'n2', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Mid' } }] },
      { id: 'end', title: 'End', widgets: [{ id: 'n3', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'End' } }] },
    ],
  }));
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // back-to-back, with no wait between: the second request must start from the page the first one landed on
  await page.evaluate(() => {
    window.PhoneBoxDemo.emit('goto', { step: 'next' });
    window.PhoneBoxDemo.emit('goto', { step: 'next' });
  });
  await expect.poll(() => activeDot(page), { timeout: 8000 }).toBe(2);
});

test('a page id called "next" keeps going through the rule queue (#616)', async ({ page }) => {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0 },
    pages: [
      { id: 'home', title: 'Home', widgets: [{ id: 'n1', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Home' } }] },
      { id: 'next', title: 'Next', widgets: [{ id: 'n2', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Next' } }] },
    ],
  }));
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // "next" here is a page id, not a step: a rule switches to it through the queue like any other page
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 'next', rule: 'r1', name: 'The next page', hold: false }));
  await expect.poll(() => activeDot(page), { timeout: 8000 }).toBe(1);
});

test('Stay cancels a rule page switch', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 2, rule: 'music', name: 'Music', hold: true, countdown: 5 }));
  await expect(page.locator('#nvName')).toHaveText('Music');
  await page.locator('#nvStay').click();
  await expect(page.locator('#navCard')).not.toHaveClass(/on/);
  await page.waitForTimeout(1500);
  expect(await activeDot(page)).toBe(0);
});

test('a rule waits while the panel is in use and then goes on request', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.mouse.click(5, 300);                             // somebody is using the panel
  await page.evaluate(() => window.PhoneBoxDemo.emit('goto', { page: 4, rule: 'claude', name: 'Claude needs me', hold: true }));
  await expect(page.locator('#navChip')).toHaveClass(/on/);
  await expect(page.locator('#ncText')).toContainText('Claude needs me');
  await expect(page.locator('#navCard')).not.toHaveClass(/on/);
  await page.locator('#ncGo').click();
  await expect.poll(() => activeDot(page)).toBe(3);
});

test('rules firing one after another do not bounce the screen', async ({ page }) => {
  test.setTimeout(45_000);
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  // a rule that ends before its turn is dropped
  await page.evaluate(() => {
    window.PhoneBoxDemo.emit('goto', { page: 2, rule: 'flappy', hold: true, countdown: 2 });
    window.PhoneBoxDemo.emit('back', { rule: 'flappy' });
  });
  await page.waitForTimeout(2500);
  expect(await activeDot(page)).toBe(0);
  // the higher of two rules goes first; the lower one waits for the minimum time between switches
  await page.evaluate(() => {
    window.PhoneBoxDemo.emit('goto', { page: 2, rule: 'low', name: 'Low', priority: 2, hold: true, countdown: 0 });
    window.PhoneBoxDemo.emit('goto', { page: 5, rule: 'high', name: 'High', priority: 8, hold: true, countdown: 0 });
  });
  await expect.poll(() => activeDot(page)).toBe(4);
  await page.waitForTimeout(1500);
  expect(await activeDot(page)).toBe(4);                      // the lower rule does not take the page over
  // the high rule ends: the low one (still on) gets its turn instead of going back and forth
  await page.evaluate(() => window.PhoneBoxDemo.emit('back', { rule: 'high' }));
  await expect.poll(() => activeDot(page), { timeout: 20000 }).toBe(1);
});

test('a Claude Code permission prompt can be answered', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.ask());
  const prompt = page.locator('#approval');
  await expect(prompt).toBeVisible();
  await prompt.getByRole('button', { name: 'Allow' }).click();
  await expect(prompt).toBeHidden();
});

test('a Codex permission prompt shows the Codex mark, project and working directory', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.ask({ source: 'Codex', project: 'phonebox', cwd: '/Users/me/Desktop/applications/phonebox',
    tool: 'Run a command', detail: 'npm test', description: 'Run the test suite' }));
  const prompt = page.locator('#approval');
  await expect(prompt).toBeVisible();
  await expect(page.locator('#apLogo')).toHaveText('◎');
  await expect(page.locator('#apSource')).toHaveText('Codex');
  await expect(page.locator('#apProject')).toHaveText('phonebox');
  await expect(page.locator('#apCwd')).toHaveText('/Users/me/Desktop/applications/phonebox');
  await prompt.getByRole('button', { name: 'Deny' }).click();
  await expect(prompt).toBeHidden();
});

test('the page dots and the Allow / Deny buttons can sit at the top (#181)', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, night: { enabled: false }, display: { ...(cfg.display || {}), controlsTop: true } }));
  });
  await page.reload();
  await expect(page.locator('body')).toHaveClass(/controls-top/);
  const dots = await page.locator('#dots').boundingBox();
  expect(dots.y).toBeLessThan(40);
  await page.evaluate(() => window.PhoneBoxDemo.ask());
  const prompt = page.locator('#approval');
  await expect(prompt).toBeVisible();
  const allow = await prompt.getByRole('button', { name: 'Allow' }).boundingBox();
  const tool = await page.locator('#apTool').boundingBox();
  const head = await page.locator('.ap-head').boundingBox();
  expect(allow.y).toBeLessThan(tool.y); // the buttons come before the request
  expect(head.y).toBeLessThan(allow.y); // and after the header
});

test('a question from Claude is shown with its choices and left for the terminal', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(() => window.PhoneBoxDemo.ask({ kind: 'question', tool: 'Claude has a question', project: 'api-gateway',
    detail: 'Framework: Which framework?\n  • React — Component library\n  • Vue — Progressive framework' }));
  const prompt = page.locator('#approval');
  await expect(prompt).toBeVisible();
  await expect(prompt).toContainText('React — Component library');
  await expect(prompt.getByRole('button', { name: 'Allow' })).toBeHidden();
  await expect(prompt.getByRole('button', { name: 'Deny' })).toBeHidden();
  await prompt.getByRole('button', { name: 'Got it' }).click();
  await expect(prompt).toBeHidden();
});

test('a GPU without sensors (Apple) shows its memory, not 0 degrees', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const labels = await page.evaluate(async () => {
    const { METRICS } = await import('/panel/main.js');
    const apple = { pc: { gpu: { name: 'Apple M2 Pro', util: 37, temp: null, power: null, vramUsed: 2, vramTotal: 16, fan: null, clock: null }, ramUsedGb: 9.46, ramTotalGb: 16 } };
    const nvidia = { pc: { gpu: { name: 'RTX', util: 5, temp: 61.4, power: 120.2, vramUsed: 2, vramTotal: 16, fan: 30, clock: 1800 } } };
    return { gpu: METRICS.gpu.sub(apple), temp: METRICS.gputemp.sub(apple), ram: METRICS.ram.sub(apple), nvidia: METRICS.gpu.sub(nvidia), nvidiaTemp: METRICS.gputemp.sub(nvidia) };
  });
  expect(labels).toEqual({ gpu: '2 / 16 GB memory', temp: 'No GPU data', ram: '9.5 / 16 GB', nvidia: '61°C · 120 W', nvidiaTemp: 'Fan 30% · 1800 MHz' });
});

test('the CPU tile shows the package power next to the temperature, like the GPU tile (#442)', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const labels = await page.evaluate(async () => {
    const { METRICS, shortCpu } = await import('/panel/main.js');
    const cpuName = 'AMD Ryzen 5 5500U with Radeon Graphics';
    const pc = (cpuTemp, cpuPower, os = 'windows') => ({ os, pc: { cpuName, cpuTemp, cpuPower } });
    return { both: METRICS.cpu.sub(pc(61.6, 14.6)), temp: METRICS.cpu.sub(pc(61.6, null)), power: METRICS.cpu.sub(pc(null, 14.6)),
      none: METRICS.cpu.sub(pc(null, null)) === shortCpu(cpuName), value: METRICS.cpupower.val(pc(61.6, 14.6)),
      sub: METRICS.cpupower.sub(pc(61.6, 14.6)), windows: METRICS.cpupower.sub(pc(61.6, null)),
      linux: METRICS.cpupower.sub(pc(61.6, null, 'linux')), mac: METRICS.cpupower.sub(pc(61.6, null, 'macos')) };
  });
  // Windows gets its power from LibreHardwareMonitor, so the empty tile says what to install; elsewhere it can't help
  expect(labels).toEqual({ both: '62°C · 15 W', temp: '62°C', power: '15 W', none: true, value: 14.6,
    sub: 'Ryzen 5 5500U with', windows: 'needs LibreHardwareMonitor', linux: 'No power data', mac: 'No power data' });
});

test('a metric tile and a gauge can show the CPU power', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null');
    const pages = [{ id: 'power', title: 'Power', icon: '⚡', enabled: true, widgets: [
      { id: 't', type: 'stat', x: 0, y: 0, w: 3, h: 2, options: { metric: 'cpupower' } },
      { id: 'g', type: 'gauge', x: 3, y: 0, w: 2, h: 3, options: { metric: 'cpupower' } }] }];
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...(cfg || {}), pages, shortcuts: cfg?.shortcuts || [] }));
  });
  await page.reload();
  const tile = page.locator('.w-stat');
  await expect(tile.locator('.t-label')).toHaveText('CPU POWER');
  await expect(tile.locator('.t-val small')).toHaveText('W');
  await expect(tile.locator('.t-val b')).toHaveText(/^\d+$/);
  await expect(page.locator('.w-gauge .g-label')).toHaveText('CPU POWER');
  expect(errors).toEqual([]);
});

test('an AMD or Intel GPU without vendor tools shows its temperature, power share and shared memory', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const labels = await page.evaluate(async () => {
    const { METRICS } = await import('/panel/main.js');
    // what Windows itself reports for a Radeon card (D3DKMT + GPU counters), no LibreHardwareMonitor
    const amd = { pc: { gpu: { name: 'AMD Radeon RX 7900 XTX', vendor: 'amd', util: 12, temp: 54.2, power: null, powerPct: 11.7, vramUsed: 6, vramTotal: 24 } } };
    // an integrated GPU lives on shared memory: dedicated + shared is what counts
    const igpu = { pc: { gpu: { name: 'Intel(R) UHD Graphics 770', integrated: true, util: 3, temp: null, vramUsed: 0.1, vramTotal: 0.1, sharedUsed: 1.4, sharedTotal: 15.9 } } };
    const lhm = { pc: { gpu: { name: 'AMD Radeon RX 7900 XTX', temp: 52, hotSpot: 71, memTemp: 58, power: 312, powerLimit: 339, fan: 42 } } };
    return { amd: METRICS.gpu.sub(amd), amdPower: METRICS.gpupower.sub(amd), amdPowerUnit: METRICS.gpupower.unit(amd),
      igpu: METRICS.vram.sub(igpu), hot: METRICS.hotspot.sub(lhm), hotMissing: METRICS.hotspot.sub(amd), temp: METRICS.gputemp.sub(lhm) };
  });
  expect(labels).toEqual({ amd: '54°C · 12% power', amdPower: 'of the power limit', amdPowerUnit: '%', igpu: '1.5 / 16 GB',
    hot: 'Edge 52° · Memory 58°', hotMissing: 'needs LibreHardwareMonitor', temp: 'Hot spot 71° · Fan 42%' });
});

test('the GPU details widget shows the main card and the integrated one', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null');
    const pages = [{ id: 'gpus', title: 'GPUs', icon: '🎮', enabled: true, widgets: [
      { id: 'g0', type: 'gpu', x: 0, y: 0, w: 6, h: 6, options: { adapter: '0' } },
      { id: 'g1', type: 'gpu', x: 6, y: 0, w: 6, h: 6, options: { adapter: '1' } }] }];
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...(cfg || {}), pages, shortcuts: cfg?.shortcuts || [] }));
  });
  await page.reload();
  const [main, igpu] = [page.locator('.w-gpu').nth(0), page.locator('.w-gpu').nth(1)];
  await expect(main.locator('.s-name')).toHaveText('GeForce RTX 4080');
  await expect(main.locator('.s-label')).toHaveText('NVIDIA');
  await expect(main).toContainText('VRAM');
  await expect(main).toContainText('/ 320 W');
  await expect(main).toContainText('Driver 596.36');
  await expect(igpu.locator('.s-label')).toHaveText('AMD · iGPU');
  await expect(igpu).toContainText('GPU memory');
  await expect(igpu).toContainText('% of limit');
  expect(errors).toEqual([]);
});

test('portrait arranges the widgets in six columns', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const cols = await page.locator('.grid').first().evaluate((g) => getComputedStyle(g).gridTemplateColumns.split(' ').length);
  expect(cols).toBe(6);
  await page.setViewportSize({ width: 780, height: 360 });
  await expect.poll(() => page.locator('.grid').first().evaluate((g) => getComputedStyle(g).gridTemplateColumns.split(' ').length)).toBe(12);
});

test('the settings page loads and its sections open', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#pages');
  await expect(page.locator('#pageTabs .ptab')).not.toHaveCount(0);
  for (const section of ['Shortcuts', 'Rules', 'Webhooks', 'General', 'Advanced', 'Pages']) {
    await page.locator('#nav a', { hasText: section }).click();
    await expect(page.locator('section.on h2')).toHaveText(section);
  }
  expect(errors).toEqual([]);
});

test('pairing over Wi-Fi shows both QR codes and cancels the code when closed', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#general');
  // the demo answers the API inside the page, so watch the page's own fetch calls
  await page.evaluate(() => {
    window.__calls = [];
    const real = window.fetch;
    window.fetch = (url, opts) => { window.__calls.push(`${opts?.method || 'GET'} ${url}`); return real(url, opts); };
  });
  await page.locator('#lanPair').click();
  await expect(page.locator('#modal')).toBeVisible();
  await expect(page.locator('#modalBody h3')).toHaveText('Pair a phone with DEMO-PC');
  await expect(page.locator('#modalBody .pair-qr img')).toHaveCount(2);
  await expect(page.locator('#modalBody .pair-qr').nth(1)).toContainText(/Expires in [45]:\d\d/);
  await expect(page.locator('#modalBody')).toContainText('192.168.1.20, 100.101.102.103');
  await expect(page.locator('#modalBody .pair-qr').nth(1)).toContainText('confirm the code DE40 0001');
  await page.screenshot({ path: 'test-results/pair-dialog.png' });
  await page.locator('#modalClose').click();
  await expect.poll(() => page.evaluate(() => window.__calls)).toContain('POST /api/pair/cancel?attempt=1');
  await expect(page.locator('#modal')).toBeHidden();
  expect(errors).toEqual([]);
});

test('an edit in the settings page shows up on the panel right away', async ({ context }) => {
  const panel = await context.newPage();
  await panel.goto('/panel/');
  await expect(panel.locator('.w').first()).toBeVisible();

  const settings = await context.newPage();
  await settings.setViewportSize({ width: 1280, height: 860 });
  await settings.goto('/settings/#pages');
  await expect(settings.locator('#pageTabs .ptab')).not.toHaveCount(0);
  const title = settings.locator('#peHead input[placeholder="Page name"]');
  await title.fill('Edited in a test');
  await settings.locator('#save').click();

  // the overview lists the new name once the panel has the new settings (it is built when it opens)
  await expect.poll(async () => {
    await panel.keyboard.press('o');
    const text = await panel.locator('.ov-tile').first().textContent();
    await panel.keyboard.press('Escape');
    return text;
  }, { timeout: 10_000 }).toContain('Edited in a test');
});

test('the live preview in the layout editor runs the real panel', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#pages');
  await expect(page.locator('#pageTabs .ptab')).not.toHaveCount(0);
  await page.locator('#stageSeg button', { hasText: 'Live preview' }).click();
  const preview = page.frameLocator('#previewBox iframe');
  await expect(preview.locator('.w').first()).toBeVisible();
  // picking another page in the editor moves the preview there
  await page.locator('#pageTabs .ptab').nth(2).click();
  await expect.poll(() => preview.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')))).toBe(2);
});

test('the live preview does not enter unreachable night mode', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await page.evaluate(() => {
    const iframe = document.createElement('iframe');
    iframe.id = 'night-preview';
    iframe.src = '/panel/?preview=1';
    document.body.appendChild(iframe);
  });
  const preview = page.frameLocator('#night-preview');
  await expect(preview.locator('.w').first()).toBeVisible();
  await preview.locator('body').evaluate(async () => {
    const panel = await import('/panel/main.js');
    panel.cfg.night.enabled = false;
    panel.cfg.display.nightWhenUnreachable = true;
    panel.cfg.display.unreachableNightDelayMin = 1;
    panel.setOnline(false);
  });
  await page.clock.runFor(61_000);
  await expect(preview.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
});

test('rule options and page switching settings are there', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#rules');
  await page.locator('#rulePresets button', { hasText: 'Review requested' }).click();
  const rule = page.locator('#ruleList .rule').last();
  await expect(rule.locator('.ropts')).toHaveAttribute('open', '');
  await expect(rule.locator('.ropts summary')).toContainText('09:00-18:00');
  await expect(rule.locator('.day.on')).toHaveCount(5);
  await rule.locator('.day', { hasText: 'Sa' }).click();
  await expect(rule.locator('.day.on')).toHaveCount(6);
  await page.goto('/settings/#general');
  await expect(page.locator('#homePageSel option')).not.toHaveCount(0);
  await expect(page.locator('[data-bind="display.switchCountdownSec"]')).toHaveValue(/\d/);
});

test('rules can be added from the examples', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#rules');
  await expect(page.locator('#rulePresets button').first()).toBeVisible();
  const before = await page.locator('#ruleList .rule').count();
  await page.locator('#rulePresets button').first().click();
  await expect(page.locator('#ruleList .rule')).toHaveCount(before + 1);
  await expect(page.locator('#save')).toBeEnabled();
});

// The agent sends all-day events as the UTC midnight of their date; the panel has to show them on that date wherever
// the phone is (audit F9: west of UTC they showed a day early, east of UTC they ran into the next day).
for (const [zone, noon] of [['America/New_York', '2026-11-09T12:00:00-05:00'], ['Pacific/Auckland', '2026-11-09T12:00:00+13:00']]) {
  test(`all-day events land on their own date (${zone})`, async ({ browser, baseURL }) => {
    const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 }, timezoneId: zone });
    const page = await ctx.newPage();
    await page.clock.setFixedTime(new Date(noon));
    await page.goto('/panel/');
    await expect(page.locator('.w').first()).toBeVisible();
    // The first widgets belong to built-in defaults; wait for the demo's full page list before navigating.
    await expect.poll(() => page.locator('#dots i').count()).toBeGreaterThan(10);
    await expect(page.locator('body')).not.toHaveClass(/night/);
    const planner = await page.locator('#dots i').count() - 4; // Planner is the 5th page from the end of the demo
    for (let i = 0; i < planner; i++) await page.keyboard.press('ArrowRight');
    const heading = page.locator('.ag-day, .ag-row').filter({ hasText: /Release day|^(Today|Tomorrow)$/ });
    await expect(page.locator('.ag-row', { hasText: 'Release day' })).toBeVisible();
    const order = await heading.evaluateAll((els) => els.map((e) => e.textContent.trim()));
    expect(order[order.findIndex((t) => t.includes('Release day')) - 1]).toBe('Tomorrow');
    await ctx.close();
  });
}

test('clicking a RAM widget navigates to the Processes page sorted by RAM', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect.poll(() => page.locator('#dots i').count()).toBeGreaterThan(10);
  const ramLabel = page.locator('.w .t-label', { hasText: /^RAM/ }).first();
  await expect(ramLabel).toBeVisible();
  await expect(ramLabel).toContainText('%');
  await ramLabel.locator('..').click();
  const procHead = page.locator('.w .p-head');
  await expect(procHead).toBeVisible();
  await expect(procHead.locator('.seg button.on')).toHaveText('RAM');
  await expect(procHead.locator('.p-sum')).toContainText('Total RAM');
});

test('the landing page embeds the panel', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto('/');
  await expect(page.frameLocator('#panel').locator('.w').first()).toBeVisible();
  await page.getByRole('button', { name: /notification/ }).click();
  await expect(page.frameLocator('#panel').locator('#toast')).toBeVisible();
  expect(errors).toEqual([]);
});

test('the pairing page hands a checked link to the app and clears it from the address bar', async ({ browser, baseURL }) => {
  const ctx = await browser.newContext({ baseURL, userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/130 Mobile' });
  const page = await ctx.newPage();
  const fp = 'ab'.repeat(32);
  await page.goto(`/pair/#v=1&pc=Desk%20PC&fp=${fp}&port=8766&h=192.168.1.10,100.64.0.7&c=${'A'.repeat(22)}&extra=%23x;y`);
  await expect(page.locator('#pc')).toHaveText('Desk PC');
  await expect(page.locator('#hosts')).toHaveText('192.168.1.10, 100.64.0.7 (port 8766)');
  await expect(page.locator('#check')).toHaveText('ABAB ABAB');
  const href = await page.locator('#open').getAttribute('href');
  expect(href).toBe(`intent://pair?v=1&pc=Desk%20PC&fp=${fp}&port=8766&h=192.168.1.10%2C100.64.0.7&c=${'A'.repeat(22)}`
    + '#Intent;scheme=phonebox;package=net.oxog.phonebox.deskpanel;S.browser_fallback_url=https%3A%2F%2Fersinkoc.github.io%2Fphonebox%2Fpair%2F%3Fcannot-open%3D1;end');
  // the button stays pinned to the app's package; the plain link is a separate choice for browsers that open a store (#175)
  await expect(page.locator('#plainWrap')).toBeVisible();
  expect(await page.locator('#plain').getAttribute('href')).toBe(`phonebox://pair?v=1&pc=Desk%20PC&fp=${fp}&port=8766&h=192.168.1.10%2C100.64.0.7&c=${'A'.repeat(22)}`);
  expect(new URL(page.url()).hash).toBe('');
  // Follow the intent's fallback in the tab that already shows the pairing card.
  const fallback = new URL(decodeURIComponent(href.match(/S\.browser_fallback_url=([^;]+)/)[1]));
  await page.goto(`/pair/${fallback.search}${fallback.hash}`);
  await expect(page.locator('#cannotOpen')).toBeVisible();
  await expect(page.locator('#bad')).toBeHidden();
  await expect(page.locator('#ok')).toBeHidden();
  expect(page.url()).toBe(new URL('/pair/', baseURL).href);
  await page.goto('about:blank'); // a hash-only change would not run the page again
  await page.goto(`/pair/#v=1&pc=x&fp=${fp}&port=8766&h=8.8.8.8&c=${'A'.repeat(22)}`); // not a local address
  await expect(page.locator('#bad')).toBeVisible();
  await page.goto('about:blank');
  await page.goto(`/pair/#v=1&pc=x&fp=${fp}&port=99999&h=192.168.1.10&c=${'A'.repeat(22)}`); // no such port
  await expect(page.locator('#bad')).toBeVisible();
  await page.goto('about:blank');
  await page.goto(`/pair/#v=1&pc=x&fp=${fp}&port=8766&h=192.168.1.10,8.8.8.8&c=${'A'.repeat(22)}`); // a public address is dropped
  await expect(page.locator('#hosts')).toHaveText('192.168.1.10 (port 8766)');
  expect(await page.locator('#open').getAttribute('href')).toContain('&h=192.168.1.10&');
  await page.goto('about:blank');
  await page.goto(`/pair/#v=1&pc=x&fp=${fp}&port=8766&h=8.8.8.8&c=${'A'.repeat(22)}`);
  await expect(page.locator('#bad')).toBeVisible();
  await page.goto('about:blank');
  await page.goto('/pair/#v=1&fp=nope');
  await expect(page.locator('#bad')).toBeVisible();
  await expect(page.locator('#ok')).toBeHidden();
  // A fresh tab following the fallback also gets guidance for an app that is missing or already installed.
  await page.goto('about:blank');
  await page.goto(`/pair/${fallback.search}${fallback.hash}`);
  await expect(page.locator('#cannotOpen')).toBeVisible();
  await expect(page.getByRole('heading', { name: "The PhoneBox app didn't open" })).toBeVisible();
  await expect(page.locator('#cannotOpen')).toContainText("If PhoneBox isn't installed yet");
  await expect(page.getByRole('link', { name: 'Download the app' })).toBeVisible();
  await expect(page.locator('#bad')).toBeHidden();
  await expect(page.locator('#ok')).toBeHidden();
  expect(page.url()).toBe(new URL('/pair/', baseURL).href);
  await ctx.close();
});

test('interface chrome uses inline SVG icons instead of emoji', async ({ page }) => {
  const errors = watchErrors(page);
  // Sheet close, nav chip, offline pill and the WebView notice all carry inline icons.
  for (const sel of ['#psClose svg.ico', '#ncGo svg.ico', '#ncX svg.ico', '#offline svg.ico']) {
    await expect(page.locator(sel)).toBeAttached();
  }
  await expect(page.locator('#webviewWarn svg.ico')).toHaveCount(2); // warning mark + close
  // Toasts pick up a kind icon next to their message.
  await page.evaluate(() => window.PhoneBoxDemo.emit('notify', { title: 'Build', text: 'done' }));
  await expect(page.locator('#toast svg.ico')).toBeVisible();
  expect(errors).toEqual([]);
});

test('a gauge keeps its ring while the value does not change, and redraws it when it does', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  const r = await page.evaluate(async () => {
    const { D } = await import('/panel/main.js');
    const { W } = await import('/panel/registry.js');
    const box = document.createElement('section');
    document.body.append(box);
    const widget = W.gauge.create(box, { o: { metric: 'ram' }, page: { id: 'fixture' }, w: { id: 'gauge' } });
    const state = (used) => ({ ...D.state, pc: { ...D.state?.pc, ramUsedGb: used, ramTotalGb: 32 } });
    D.state = state(16); widget.update();
    const first = box.querySelector('.g-dial svg');
    D.state = state(16); widget.update();
    const same = box.querySelector('.g-dial svg') === first;
    D.state = state(24); widget.update();
    const ring = box.querySelector('.g-dial svg');
    return { same, redrawn: ring !== first, rings: box.querySelectorAll('.g-dial svg').length,
      offset: ring.querySelector('.fg').getAttribute('stroke-dashoffset') };
  });
  expect(r).toEqual({ same: true, redrawn: true, rings: 1, offset: '66.0' });
});
