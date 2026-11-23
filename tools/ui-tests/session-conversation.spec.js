const { test, expect } = require('@playwright/test');

async function layout(page, widgets, extra = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ widgets, extra }) => {
    localStorage.clear();
    // Widget interaction tests must not depend on the host's wall-clock night schedule.
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, ...extra, pages: [{ id: 'test', title: 'Test', enabled: true, widgets }], shortcuts: [] }));
  }, { widgets, extra });
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/);
}
const widget = (type, options = {}) => ({ id: 'test', type, x: 0, y: 0, w: 12, h: 6, options });

const session = (over = {}) => ({ id: 'claude:t1', tool: 'claude', name: 'develop-int-61', project: 'develop-int', status: 'idle', since: Date.now() - 60_000,
  startedAt: Date.now() - 3600_000, model: null, effort: null, totalTokens: null, inputTokens: null, outputTokens: null, cachedInputTokens: null,
  reasoningTokens: null, lastInputTokens: null, contextWindow: null, lastTool: null, turnStartedAt: null, outcome: null, ...over });

async function open(page, sessions) {
  await layout(page, [widget('sessions')]);
  await page.evaluate(async (sessions) => {
    const { D, emit } = await import('/panel/main.js');
    D.work = { sessions, github: { user: null, notifications: 0, reviewRequests: [], myPrs: [], error: null } };
    emit('work');
  }, sessions);
}

test('a Claude Code session shows its recent steps, the last exchange and the whole conversation', async ({ page }) => {
  await open(page, [session()]);
  await page.locator('button.sess').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('.conv-step')).toHaveCount(3);
  await expect(dialog.locator('.conv-step.on')).toContainText('Bash');
  await expect(dialog.locator('.conv-step', { hasText: 'Edit' })).toContainText('upload.js');
  // the last exchange: the newest message of each side, not the whole list
  await expect(dialog.locator('.conv-msg')).toHaveCount(2);
  await expect(dialog.locator('.conv-msg.user')).toContainText('Does it also cover timeouts?');
  await expect(dialog.locator('.conv-msg.assistant')).toContainText('a timeout counts as a failed attempt');
  await dialog.getByRole('button', { name: /Show the conversation \(4\)/ }).click();
  await expect(dialog.locator('.conv-list .conv-msg')).toHaveCount(4);
  await expect(dialog.locator('.conv-list .conv-msg').first()).toContainText('Add a retry');
  await dialog.getByRole('button', { name: 'Hide the conversation' }).click();
  await expect(dialog.locator('.conv-list')).toHaveCount(0);
});

test('with the setting off the dialog says so and shows no text', async ({ page }) => {
  await open(page, [session()]);
  // the demo answers inside the page, so the request is replaced there
  await page.evaluate(() => {
    const real = window.fetch;
    window.fetch = async (...args) => String(args[0]).includes('/work/conversation')
      ? new Response('{"enabled":false}', { headers: { 'Content-Type': 'application/json' } }) : real(...args);
  });
  await page.locator('button.sess').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('.session-conv')).toContainText('The conversation is off');
  await expect(dialog.locator('.conv-msg, .conv-step')).toHaveCount(0);
});

test('Codex sessions have no conversation section', async ({ page }) => {
  await open(page, [session({ id: 'codex:t1', tool: 'codex' })]);
  await page.locator('button.sess').click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.locator('.session-conv')).toHaveCount(0);
});

test('a slow earlier answer does not replace a newer one, and closing stops the refresh', async ({ page }) => {
  await open(page, [session()]);
  await page.evaluate(() => {
    const real = window.fetch;
    window.__conversationCalls = 0;
    window.fetch = async (...args) => {
      if (!String(args[0]).includes('/work/conversation')) return real(...args);
      const n = ++window.__conversationCalls;
      // 1: fast, on. 2: slow, on (it arrives after the third). 3: fast, off.
      if (n === 2) await new Promise((r) => setTimeout(r, 3500));
      const body = n === 3 ? '{"enabled":false}' : '{"enabled":true,"messages":[{"role":"user","text":"hello","at":1}],"steps":[]}';
      return new Response(body, { headers: { 'Content-Type': 'application/json' } });
    };
  });
  await page.locator('button.sess').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('.conv-msg')).toHaveCount(1);                                                  // answer 1
  await expect(dialog.locator('.session-conv')).toContainText('The conversation is off', { timeout: 8000 }); // answer 3
  await page.waitForTimeout(1500);                                                                           // answer 2 arrives late
  await expect(dialog.locator('.session-conv')).toContainText('The conversation is off');
  await expect(dialog.locator('.conv-msg')).toHaveCount(0);
  await page.getByRole('button', { name: 'Close session details' }).click();
  const after = await page.evaluate(() => window.__conversationCalls);
  await page.waitForTimeout(3500);
  expect(await page.evaluate(() => window.__conversationCalls)).toBe(after);
});

test('the ages of the steps keep moving while the answers stay the same', async ({ page }) => {
  await open(page, [session()]);
  await page.evaluate(() => {
    // exactly the same answer every time, and a clock that can be moved on
    const real = window.fetch, now = Date.now, base = now();
    window.__skew = 0;
    Date.now = () => now() + window.__skew;
    const body = JSON.stringify({ enabled: true, messages: [{ role: 'user', text: 'hello', at: base - 5000 }],
      steps: [{ tool: 'Bash', detail: 'npm', at: base - 20_000, running: true }, { tool: 'Read', detail: 'a.js', at: base - 40_000, running: false }] });
    window.fetch = async (...args) => String(args[0]).includes('/work/conversation')
      ? new Response(body, { headers: { 'Content-Type': 'application/json' } }) : real(...args);
  });
  await page.locator('button.sess').click();
  const dialog = page.getByRole('dialog');
  const running = dialog.locator('.conv-step.on .cs-a');
  await expect(running).toHaveText(/^running (now|\d+s)$/);
  const before = await running.textContent();
  await page.evaluate(() => { window.__skew = 300_000; }); // five minutes later, and the chat has not changed
  await expect(running).toHaveText(/^running [56]m/, { timeout: 6000 });
  expect(await running.textContent()).not.toBe(before);
  await expect(dialog.locator('.conv-step:not(.on) .cs-a')).toContainText('m ago');
});

test('the open conversation follows new messages while it is read to the end, and stays put when scrolled back', async ({ page }) => {
  await open(page, [session()]);
  await page.evaluate(() => {
    const real = window.fetch;
    const long = 'A long answer that wraps over several lines on the phone. '.repeat(6);
    window.__messages = Array.from({ length: 12 }, (_, i) => ({ role: i % 2 ? 'assistant' : 'user', text: `message ${i} ${long}`, at: i }));
    window.fetch = async (...args) => String(args[0]).includes('/work/conversation')
      ? new Response(JSON.stringify({ enabled: true, messages: window.__messages, steps: [] }), { headers: { 'Content-Type': 'application/json' } })
      : real(...args);
  });
  await page.locator('button.sess').click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: /Show the conversation \(12\)/ }).click();
  const list = dialog.locator('.conv-list');
  const gap = () => list.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight);
  expect(await list.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  expect(await gap()).toBeLessThan(2);
  // a new message while the end is in view: the list moves on to it
  await page.evaluate(() => window.__messages.push({ role: 'user', text: 'newest one', at: 99 }));
  await expect(dialog.locator('.conv-list .conv-msg')).toHaveCount(13, { timeout: 6000 });
  expect(await gap()).toBeLessThan(2);
  // scrolled back to read: another message does not pull the list away
  await list.evaluate((el) => { el.scrollTop = 0; });
  await page.evaluate(() => window.__messages.push({ role: 'assistant', text: 'and another', at: 100 }));
  await expect(dialog.locator('.conv-list .conv-msg')).toHaveCount(14, { timeout: 6000 });
  expect(await list.evaluate((el) => el.scrollTop)).toBe(0);
});
