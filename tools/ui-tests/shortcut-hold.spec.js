// A key can have a long press: a tap runs the key, holding it runs another shortcut (its "long press" in the settings).
// The demo flips a toggle key when it runs, so a toggle is what shows which key the agent ran.
const { test, expect } = require('@playwright/test');

const SHORTCUTS = [
  { id: 'tap', label: 'Tap', icon: '🔊', color: '#6b7280', toggle: true, cmd: 'builtin:keys', longPress: 'hold' },
  { id: 'hold', label: 'Hold', icon: '🎚️', color: '#6b7280', toggle: true, cmd: 'builtin:keys' },
  { id: 'plain', label: 'Plain', icon: '🔘', color: '#6b7280', toggle: true, cmd: 'builtin:keys' },
];

const runs = (page) => page.evaluate(() => window.__runs);

async function open(page) {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS,
    pages: [{ id: 'hold-test', title: 'Keys', widgets: SHORTCUTS.map((s, i) => ({ id: 'b' + i, type: 'button', x: i * 4, y: 0, w: 4, h: 6, options: { id: s.id } })) }],
  }));
  await page.goto('/panel/');
  const key = (i) => page.locator('[data-id="hold-test"] .w-button').nth(i).locator('.key');
  await expect(key(0)).toBeVisible();
  // the demo answers in the page (no network request), so the runs are recorded around its fetch
  await page.evaluate(() => {
    const original = window.fetch; window.__runs = [];
    window.fetch = (input, init) => { const u = new URL(String(input.url || input), location.href); if (u.pathname.includes('/api/run/')) window.__runs.push(decodeURIComponent(u.pathname.split('/').pop()) + u.search); return original(input, init); };
  });
  return key;
}

/** A finger held on the key for `ms`. */
async function press(page, locator, ms) {
  const box = await locator.boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.waitForTimeout(ms);
  await page.mouse.up();
}

test('a tap on a key with a long press still runs the key', async ({ page }) => {
  const key = await open(page);
  await key(0).click();
  await expect.poll(() => runs(page)).toEqual(['tap']);
});

test('holding a key with a long press runs the long-press shortcut once, not the key', async ({ page }) => {
  const key = await open(page);
  await press(page, key(0), 700);
  await expect.poll(() => runs(page)).toEqual(['tap?hold=true']);
  await page.waitForTimeout(300);
  expect(await runs(page)).toEqual(['tap?hold=true']);
});

test('a short press does not count as a hold, and the tap after a hold works again', async ({ page }) => {
  const key = await open(page);
  await press(page, key(0), 700);
  await expect.poll(async () => (await runs(page)).length).toBe(1);
  await key(0).click();
  await expect.poll(() => runs(page)).toEqual(['tap?hold=true', 'tap']);
});

test('a key without a long press is untouched by holding it', async ({ page }) => {
  const key = await open(page);
  await press(page, key(2), 700);
  await expect.poll(() => runs(page)).toEqual(['plain']);
});

test('moving the finger before the hold is over cancels the hold', async ({ page }) => {
  const key = await open(page);
  const box = await key(0).boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 20, box.y + box.height / 2 + 20); // still on the key, but far enough to be a drag
  await page.waitForTimeout(700);
  await page.mouse.up();
  await page.waitForTimeout(200);
  // released on the key it is an ordinary tap; what matters is that no hold ran
  expect(await runs(page)).not.toContain('tap?hold=true');
});

test('holding the right mouse button does not count as a hold', async ({ page }) => {
  const key = await open(page);
  const box = await key(0).boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down({ button: 'right' });
  await page.waitForTimeout(700);
  await page.mouse.up({ button: 'right' });
  await page.waitForTimeout(200);
  expect(await runs(page)).toEqual([]);
});

test('a second touch on the key neither starts a second hold nor lets the first one also tap', async ({ page }) => {
  const key = await open(page);
  // two touches on the key: the second one lands after the first has started its hold
  const box = await key(0).boundingBox(), x = box.x + box.width / 2, y = box.y + box.height / 2;
  await key(0).evaluate((el, [x, y]) => {
    const fire = (type, id) => el.dispatchEvent(new PointerEvent(type, { pointerId: id, pointerType: 'touch', clientX: x, clientY: y, bubbles: true, isPrimary: id === 7 }));
    fire('pointerdown', 7);
    window.__second = () => { fire('pointerdown', 8); fire('pointerup', 8); };
    window.__first = () => { fire('pointerup', 7); el.click(); };
  }, [x, y]);
  await page.waitForTimeout(700);                       // the first touch has held long enough
  await page.evaluate(() => window.__second());          // a second finger taps and lifts
  await page.evaluate(() => window.__first());           // then the first lifts, and the browser sends its click
  await page.waitForTimeout(200);
  expect(await runs(page)).toEqual(['tap?hold=true']);   // one hold, and no tap
});

test('the key editor offers the other keys as the long press, and not the key itself', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#shortcuts');
  const keys = page.locator('#deck .key:not(.add)');
  await expect(keys.first()).toBeVisible();
  const count = await keys.count();
  expect(count).toBeGreaterThan(1);
  const own = (await keys.first().locator('.kl').textContent()).trim();
  const select = page.locator('section.on select').filter({ has: page.locator('option', { hasText: /^Nothing \(a long press/ }) });
  await expect(select).toBeVisible();
  const options = await select.locator('option').allTextContents();
  expect(options.length).toBe(count); // "Nothing" plus every key but this one
  expect(options.slice(1)).not.toContain(own);
  await select.selectOption({ index: 1 });
  await expect(select).not.toHaveValue('');
});
