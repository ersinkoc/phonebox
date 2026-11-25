const { test, expect } = require('@playwright/test');

// The Discord current-channel bar (discord-bar) and the page reflow it relies on. The demo answers /api/discord with
// you in "Game night" (Ada, Linus); leaving and joining go through the bar's buttons or the demo's POST actions.

const bar = (id, x, y, w, h, options = {}) => ({ id, type: 'discord-bar', x, y, w, h, options: { control: true, ...options } });
const note = (id, x, y, w, h) => ({ id, type: 'note', x, y, w, h, options: { text: id } });

async function show(page, widgets, { discord = { screenShare: true, chat: true }, more = [] } = {}) {
  await page.goto('/panel/');
  await page.evaluate(({ pages, discord }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], discord, pages }));
  }, { pages: [{ id: 'home', title: 'Home', enabled: true, widgets }, ...more], discord });
  await page.reload();
}
const area = (page, id) => page.locator(`.w-note:has-text("${id}")`).evaluate((el) => el.closest('.w').style.gridArea);
// the demo's own actions, as the agent's: joining needs more than the bar's Control (allowControl is on where it is used)
const post = (page, path) => page.evaluate(async (path) => { const m = await import('/panel/main.js'); return (await fetch(m.API + path, { method: 'POST' })).status; }, path);
const join = (page) => post(page, '/discord/join?channel=c1');

test('the bar shows the channel, who is there and its buttons, and mutes', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page, [bar('b', 0, 0, 12, 1), note('below', 0, 1, 12, 5)]);
  const b = page.locator('.w-discord-bar');
  await expect(b.locator('.dcb-name')).toHaveText('Game night');
  await expect(b.locator('.dcb-chip', { hasText: 'Ada' })).toBeVisible();
  for (const kind of ['mute', 'deafen', 'leave']) await expect(b.locator(`.dcb-btn.${kind}`)).toHaveAttribute('aria-label', /.+/);
  await b.locator('.dcb-btn.mute').click();
  await expect(b.locator('.dcb-btn.mute')).toHaveClass(/\bon\b/);
  expect(errors).toEqual([]);
});

test('outside a channel the bar hides after a short wait and the widget below takes its row', async ({ page }) => {
  await show(page, [bar('b', 0, 0, 12, 1), note('below', 0, 1, 12, 5)], { discord: { allowControl: true } });
  const b = page.locator('.w-discord-bar');
  await expect(b.locator('.dcb-name')).toHaveText('Game night');
  expect(await area(page, 'below')).toBe('2 / 1 / span 5 / span 12');
  await b.locator('.dcb-btn.leave').click();
  // a grace period first: a short reconnect must not make the page jump
  await page.waitForTimeout(1200);
  await expect(b).toBeVisible();
  await expect(b).toBeHidden({ timeout: 5000 });
  expect(await area(page, 'below')).toBe('1 / 1 / span 6 / span 12');
  // back in a channel: everything returns to its own cells
  await join(page);
  await expect(b).toBeVisible({ timeout: 5000 });
  await expect.poll(() => area(page, 'below')).toBe('2 / 1 / span 5 / span 12');
});

test('two stacked bars give both rows to the widget below them', async ({ page }) => {
  await show(page, [bar('a', 0, 0, 12, 1), bar('b', 0, 1, 12, 1), note('below', 0, 2, 12, 4)]);
  await expect(page.locator('.w-discord-bar').first().locator('.dcb-name')).toHaveText('Game night');
  await page.locator('.w-discord-bar').first().locator('.dcb-btn.leave').click();
  await expect(page.locator('.w-discord-bar').first()).toBeHidden({ timeout: 6000 });
  await expect(page.locator('.w-discord-bar').nth(1)).toBeHidden({ timeout: 6000 });
  expect(await area(page, 'below')).toBe('1 / 1 / span 6 / span 12');
});

test('with nothing below, the widget above grows down into the bar row', async ({ page }) => {
  await show(page, [note('above', 0, 0, 12, 5), bar('b', 0, 5, 12, 1)]);
  await expect(page.locator('.dcb-name')).toHaveText('Game night');
  await page.locator('.dcb-btn.leave').click();
  await expect(page.locator('.w-discord-bar')).toBeHidden({ timeout: 6000 });
  expect(await area(page, 'above')).toBe('1 / 1 / span 6 / span 12');
});

test('with Hide outside a voice channel off it stays and says so', async ({ page }) => {
  await show(page, [bar('b', 0, 0, 12, 1, { autoHide: false }), note('below', 0, 1, 12, 5)]);
  await page.locator('.dcb-btn.leave').click();
  await page.waitForTimeout(4000);
  await expect(page.locator('.w-discord-bar')).toBeVisible();
  await expect(page.locator('.w-discord-bar .empty')).toBeVisible();
  expect(await area(page, 'below')).toBe('2 / 1 / span 5 / span 12');
});

test('as a Smart Stack card it never hides (a stack has no rows to give away)', async ({ page }) => {
  await show(page, [{ id: 's', type: 'stack', x: 0, y: 0, w: 12, h: 2, options: { interval: 0, widgets: [{ id: 'b', type: 'discord-bar', options: { control: true } }] } },
    note('below', 0, 2, 12, 4)]);
  const b = page.locator('.stack-item.active .w-discord-bar');
  await expect(b.locator('.dcb-name')).toHaveText('Game night');
  expect(await post(page, '/discord/leave')).toBe(200);
  await page.waitForTimeout(4500);
  await expect(b).toBeVisible();
  await expect(b.locator('.empty')).toBeVisible();
});

test('in the settings live preview it never hides', async ({ page }) => {
  const widgets = [bar('b', 0, 0, 12, 1), note('below', 0, 1, 12, 5)];
  await show(page, widgets);
  // the settings page's live preview: the panel in a frame of the same origin, given its settings by message
  await page.evaluate((widgets) => new Promise((done) => {
    const f = document.createElement('iframe');
    f.src = '/panel/?preview';
    f.style.cssText = 'position:fixed;inset:0;width:780px;height:360px;border:0;z-index:99999;background:#000';
    f.onload = () => {
      f.contentWindow.postMessage({ type: 'phonebox-preview', page: 0, config: { pages: [{ id: 'home', title: 'Home', enabled: true, widgets }] } }, location.origin);
      done();
    };
    document.body.appendChild(f);
  }), widgets);
  const frame = page.frameLocator('iframe');
  await expect(frame.locator('.dcb-name')).toHaveText('Game night');
  // the preview runs its own demo, so it leaves the channel there
  const inner = page.frames().find((f) => f.url().includes('preview'));
  expect(await post(inner, '/discord/leave')).toBe(200);
  await page.waitForTimeout(4500);
  await expect(frame.locator('.w-discord-bar')).toBeVisible();
});

test('Screen opens the share view of the Discord widget, and Stop ends a share from the bar', async ({ page }) => {
  await show(page, [bar('b', 0, 0, 12, 1), note('below', 0, 1, 12, 5)], {
    more: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [{ id: 'd', type: 'discord', x: 0, y: 0, w: 12, h: 6, options: { control: true } }] }] });
  await page.locator('.dcb-btn.share').click();
  const app = page.locator('.dc-app', { hasText: 'Space Racers' });
  await expect(app).toBeVisible({ timeout: 5000 });
  await app.click();
  await page.evaluate(async () => (await import('/panel/main.js')).goPage(0, true));
  const stop = page.locator('.dcb-btn.share.live');
  await expect(stop).toBeVisible({ timeout: 6000 });
  await expect(page.locator('.dcb-badge')).toBeVisible();
  // the state alone carries no name (PRIVACY.md): the bar doesn't show it
  await expect(page.locator('.dcb-sub')).toHaveCount(0);
  await stop.click();
  await expect(page.locator('.dcb-btn.share:not(.live)')).toBeVisible({ timeout: 6000 });
});

test('Screen opens the share tab in the side-by-side Discord layout', async ({ page }) => {
  await show(page, [bar('b', 0, 0, 12, 1)], {
    more: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [
      { id: 'd', type: 'discord', x: 0, y: 0, w: 12, h: 6, options: { control: true, layout: 'focus' } },
    ] }],
  });
  await page.locator('.dcb-btn.share').click();
  await expect(page.getByRole('button', { name: 'Space Racers' })).toBeVisible();
});

test("the bar's own Control does not start shares or join channels", async ({ page }) => {
  await show(page, [bar('b', 0, 0, 12, 1)], { more: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [{ id: 'd', type: 'discord', x: 0, y: 0, w: 12, h: 6, options: { control: false } }] }] });
  await expect(page.locator('.dcb-name')).toHaveText('Game night');
  // no Discord widget with Control: no Screen button to start a share
  await expect(page.locator('.dcb-btn.share')).toHaveCount(0);
  const out = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    const post = async (p) => (await fetch(m.API + p, { method: 'POST' })).status;
    return { join: await post('/discord/join?channel=c3'), pick: await post('/discord/share/pick'), mute: await post('/discord/mute') };
  });
  expect(out).toEqual({ join: 400, pick: 400, mute: 200 });
});

test('at the smallest size Mic and Leave fit inside the bar', async ({ page }) => {
  await page.setViewportSize({ width: 780, height: 360 });
  await show(page, [bar('b', 0, 0, 2, 1), note('rest', 2, 0, 10, 6)]);
  const b = page.locator('.w-discord-bar');
  await expect(b.locator('.dcb-btn.mute')).toBeVisible();
  const box = await b.boundingBox();
  for (const kind of ['mute', 'leave']) {
    const r = await b.locator(`.dcb-btn.${kind}`).boundingBox();
    expect(r.y).toBeGreaterThanOrEqual(box.y - 0.5);
    expect(r.y + r.height).toBeLessThanOrEqual(box.y + box.height + 0.5);
    expect(r.x + r.width).toBeLessThanOrEqual(box.x + box.width + 0.5);
  }
});

test('rebuilt outside a channel, the bar stays hidden: the widgets around it do not jump', async ({ page }) => {
  await show(page, [bar('b', 0, 0, 12, 1), note('below', 0, 1, 12, 5)]);
  const b = page.locator('.w-discord-bar');
  await expect(b.locator('.dcb-name')).toHaveText('Game night');
  await b.locator('.dcb-btn.leave').click();
  await expect(b).toBeHidden({ timeout: 6000 });
  // a rebuild of the page (here a rotation and back) creates the bar again
  await page.setViewportSize({ width: 360, height: 780 });
  await page.setViewportSize({ width: 780, height: 360 });
  const seen = await page.evaluate(() => new Promise((done) => {
    let shown = false; const until = Date.now() + 1500;
    const look = () => { const el = document.querySelector('.w-discord-bar'); if (el && !el.hidden) shown = true; if (Date.now() < until) requestAnimationFrame(look); else done(shown); };
    look();
  }));
  expect(seen).toBe(false);
  expect(await area(page, 'below')).toBe('1 / 1 / span 6 / span 12');
});

test('while you share, Stop stays inside even the smallest bar', async ({ page }) => {
  await page.setViewportSize({ width: 780, height: 360 });
  await show(page, [bar('b', 0, 0, 2, 1), note('rest', 2, 0, 10, 6)], {
    more: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [{ id: 'd', type: 'discord', x: 0, y: 0, w: 12, h: 6, options: { control: true } }] }] });
  const b = page.locator('.w-discord-bar');
  await expect(b.locator('.dcb-btn.mute')).toBeVisible();
  // a share started by the Discord widget's Control
  expect(await post(page, '/discord/share/app?pid=4242')).toBe(200);
  const stop = b.locator('.dcb-btn.share.live');
  await expect(stop).toBeVisible({ timeout: 6000 });
  const box = await b.boundingBox();
  for (const btn of [stop, b.locator('.dcb-btn.mute'), b.locator('.dcb-btn.leave')]) {
    const r = await btn.boundingBox();
    expect(r.y + r.height).toBeLessThanOrEqual(box.y + box.height + 0.5);
    expect(r.x + r.width).toBeLessThanOrEqual(box.x + box.width + 0.5);
    expect(r.x).toBeGreaterThanOrEqual(box.x - 0.5);
  }
});
