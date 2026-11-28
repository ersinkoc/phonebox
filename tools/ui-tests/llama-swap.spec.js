const { test, expect } = require('@playwright/test');

const config = () => ({
  night: { enabled: false }, rules: [], shortcuts: [],
  display: { language: 'en' },
  pages: [{ id: 'one', title: 'One', widgets: [{ id: 'llama', type: 'llama-swap', x: 0, y: 0, w: 7, h: 6, options: {} }] }],
});

async function open(page) {
  await page.goto('/panel/');
  await page.evaluate((c) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify(c));
  }, config());
  await page.reload();
  // The startup loader asks /api/llamaswap, which the demo does not serve: wait for its answer (the offline
  // state) before a test injects its fixture, or that late answer could overwrite the fixture.
  await expect(page.locator('.ls-list .empty')).toHaveText('llama-swap is offline.');
}

// demo.js does not serve /api/llamaswap, so the widget is fed the data the agent's poll would deliver
async function llamaswap(page, d) {
  await page.evaluate(async (d) => {
    const { D, emit } = await import('/panel/main.js');
    D.llamaswap = d;
    emit('llamaswap');
  }, d);
}

const connected = {
  connected: true, error: null, version: 'v275',
  loadedModels: ['Qwen3.8-27B-GGUF'],
  todayRequests: 3, todayInputTokens: 30, todayOutputTokens: 10,
  genTps: 38.5,
  lastRequestAt: new Date(Date.now() - 10_000).toISOString(),
  inflight: 1,
  inflightRequests: [{ id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 950 }],
};

test('llama-swap shows the count, then the seconds of the oldest in-flight request ticking up', async ({ page }) => {
  await open(page);
  await llamaswap(page, connected);
  // the count renders first, the seconds after: "1 0s", not "0s 1"
  await expect(page.locator('.ls-hero-n b')).toHaveText('1');
  // the fixture seeds the request at 950 ms: the label is 1s from the first paint on (0s would hold only for
  // 50 ms - no assertion lives inside a window that short) and ticks up in place (the 1-second beat, no new data)
  await expect(page.locator('.ls-hero-t')).toHaveText('1s');
  await expect(page.locator('.ls-hero-t')).toHaveText('2s', { timeout: 5000 });
});

test('llama-swap goes offline and drops the in-flight display', async ({ page }) => {
  await open(page);
  await llamaswap(page, connected);
  await expect(page.locator('.ls-hero-n b')).toHaveText('1');
  await llamaswap(page, { connected: false, error: 'offline' });
  await expect(page.locator('.ls-list .empty')).toHaveText('llama-swap is offline.');
  // nothing of the connected state is left: no hero, no pulsing dot
  await expect(page.locator('.ls-hero')).toHaveCount(0);
  await expect(page.locator('.ls-dot')).toHaveClass('ls-dot');
});

test('llama-swap with no address says to set the address, not offline', async ({ page }) => {
  await open(page);
  await llamaswap(page, { connected: false, configured: false });
  await expect(page.locator('.ls-list .empty')).toHaveText('Set the server address in Settings › General › llama-swap.');
});

test('llama-swap says loading during the first refresh and offline when the request fails', async ({ page }) => {
  await open(page);
  // the demo page does not serve /api/llamaswap: a failed request publishes the unavailable state
  await expect(page.locator('.ls-list .empty')).toHaveText('llama-swap is offline.');
  // an address whose first refresh has not finished yet (connected is false, no error) says loading
  await llamaswap(page, { connected: false, configured: true });
  await expect(page.locator('.ls-list .empty')).toHaveText('Loading…');
});

test('llama-swap lists every loaded model and every in-flight request with its model and seconds', async ({ page }) => {
  await open(page);
  await llamaswap(page, {
    ...connected,
    loadedModels: ['Qwen3.8-27B-GGUF', 'Bonsai-27B-Q1_0'],
    inflight: 2,
    inflightRequests: [
      { id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 2_500 },
      { id: '8', model: 'Bonsai-27B-Q1_0', elapsedMs: 12_000 },
    ],
  });
  // every loaded model is listed, not just the first
  await expect(page.locator('.ls-models .ls-model')).toHaveCount(2);
  // one row per in-flight request, oldest first, each with its own model and seconds
  await expect(page.locator('.ls-reqs .ls-req')).toHaveCount(2);
  await expect(page.locator('.ls-reqs .ls-req').first()).toContainText('Bonsai-27B-Q1_0');
  await expect(page.locator('.ls-reqs .ls-rs')).toHaveText(['12s', '2s']);
  await expect(page.locator('.ls-reqs .ls-req').nth(1)).toContainText('Qwen3.8-27B-GGUF');
  // the speed is shown, the big one
  await expect(page.locator('.ls-spd > span:last-child')).toHaveText('38.5 tok/s');
  // and an offline card drops the request rows with the rest
  await llamaswap(page, { connected: false, error: 'offline' });
  await expect(page.locator('.ls-reqs .ls-req')).toHaveCount(0);
});

test('llama-swap re-seeds a reused request id after the router restarts', async ({ page }) => {
  await open(page);
  // id 7 has been in flight for twenty seconds
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 20_000 }] });
  await expect(page.locator('.ls-hero-t')).toHaveText('20s');
  // the router restarted and renumbered from 1: the same id is a new request again at 500 ms -
  // the label must come back to 1s, not keep counting from the old local start
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 500 }] });
  await expect(page.locator('.ls-hero-t')).toHaveText('1s', { timeout: 3000 });
});

test('llama-swap re-seeds a reused id even when the restart drop is under fifteen seconds', async ({ page }) => {
  await open(page);
  // id 7 has been in flight for ten seconds
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 10_000 }] });
  await expect(page.locator('.ls-hero-t')).toHaveText('10s');
  // the router restarted and renumbered: the same id is a new request again at 500 ms. The drop is smaller than
  // a whole second of local growth can explain, so only the per-id server value - not a local comparison - catches it
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 500 }] });
  await expect(page.locator('.ls-hero-t')).toHaveText('1s', { timeout: 3000 });
});

test('llama-swap pins the today row to the bottom and shows the exact counts for the day range', async ({ page }) => {
  await open(page);
  await llamaswap(page, connected);
  // the row is pinned outside the scrolling list: a glance never has to scroll to it
  await expect(page.locator('.ls-list .ls-stats')).toHaveCount(0);
  await expect(page.locator('.ls-stats .ls-stat .ls-k')).toHaveText('Today');
  await expect(page.locator('.ls-stats .ls-stat .ls-v')).toHaveText('3 · 30 in / 10 out');
  // the agent asks for the local day's range, so the router's total is exact: no lower-bound "+"
  await llamaswap(page, { ...connected, todayRequests: 999, todayInputTokens: 4_000_000, todayOutputTokens: 2_000_000 });
  await expect(page.locator('.ls-stats .ls-stat .ls-v')).toHaveText('999 · 4.0M in / 2.0M out');
});

test('llama-swap does not reuse a timer across a poll gap that hid an id reuse', async ({ page }) => {
  // a local clock we can move without waiting: the gap is what the widget must notice
  await page.addInitScript(() => {
    const realNow = Date.now.bind(Date);
    let offset = 0;
    window.__advance = (ms) => { offset += ms; };
    Date.now = () => realNow() + offset;
  });
  await open(page);
  // id 7 has been in flight for half a second
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 500 }] });
  await expect(page.locator('.ls-hero-t')).toHaveText('1s');
  // the phone did not poll for a minute: a new request reused id 7 and is only at 1 s
  await page.evaluate(() => window.__advance(60_000));
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 1000 }] });
  // the old start would show about a minute; it must be the new request's own second
  await expect(page.locator('.ls-hero-t')).toHaveText('1s');
});

test('llama-swap re-seeds a reused id that comes back with a larger elapsed between two polls', async ({ page }) => {
  await page.addInitScript(() => {
    const realNow = Date.now.bind(Date);
    let offset = 0;
    window.__advance = (ms) => { offset += ms; };
    Date.now = () => realNow() + offset;
  });
  await open(page);
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '1', model: 'Qwen3.8-27B-GGUF', elapsedMs: 500 }] });
  await expect(page.locator('.ls-hero-t')).toHaveText('1s');
  // one ordinary poll later (no gap) the router has restarted, and a new request reused id 1 at one second
  await page.evaluate(() => window.__advance(5_000));
  await llamaswap(page, { ...connected, inflightRequests: [{ id: '1', model: 'Qwen3.8-27B-GGUF', elapsedMs: 1_000 }] });
  // the old local start would show about five seconds
  await expect(page.locator('.ls-hero-t')).toHaveText('1s');
});

test('llama-swap shows at most three in-flight rows, the overflow as +N', async ({ page }) => {
  await open(page);
  await llamaswap(page, {
    ...connected,
    inflight: 5,
    inflightRequests: [9, 8, 7, 6, 5].map((n, i) => ({ id: String(n), model: 'Qwen3.8-27B-GGUF', elapsedMs: n * 1000 + 400 - i })),
  });
  await expect(page.locator('.ls-reqs .ls-req')).toHaveCount(3);
  await expect(page.locator('.ls-reqs .ls-more')).toHaveText('+2');
});

test('llama-swap ticks the rows in place and drops a finished request', async ({ page }) => {
  await open(page);
  await llamaswap(page, {
    ...connected,
    inflight: 2,
    inflightRequests: [
      { id: '7', model: 'Qwen3.8-27B-GGUF', elapsedMs: 9_500 },
      { id: '8', model: 'Qwen3.8-27B-GGUF', elapsedMs: 1_200 },
    ],
  });
  await expect(page.locator('.ls-reqs .ls-req')).toHaveCount(2);
  // a one-second beat with no new data moves the seconds in the same row, not a re-render
  await page.locator('.ls-reqs .ls-req').first().evaluate((el) => { el.dataset.probe = '1'; });
  await expect(page.locator('.ls-reqs .ls-req[data-probe="1"] .ls-rs')).toHaveText('10s', { timeout: 5000 });
  // the oldest request finished: the next poll carries it no more, and its row is gone
  await llamaswap(page, { ...connected, inflight: 1, inflightRequests: [{ id: '8', model: 'Qwen3.8-27B-GGUF', elapsedMs: 11_000 }] });
  await expect(page.locator('.ls-reqs .ls-req')).toHaveCount(1);
});
