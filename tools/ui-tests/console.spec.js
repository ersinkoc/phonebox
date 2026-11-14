const { test, expect } = require('@playwright/test');

function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  return errors;
}

async function seedConsolePanel(page) {
  await page.goto('/panel/', { waitUntil: 'networkidle' });
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false },
      shortcuts: [],
      display: { idleReturnSec: 0 },
      pages: [
        {
          id: 'console-main',
          title: 'Console',
          widgets: [
            { id: 'pad-follow', type: 'app-keypad', x: 0, y: 0, w: 4, h: 4, options: { mode: 'follow', showHeader: true, showHints: true, unmatched: 'default' } },
            { id: 'dial-follow', type: 'app-dial', x: 4, y: 0, w: 2, h: 4, options: { mode: 'follow', axis: 1, showPills: true } },
            { id: 'pad-pinned', type: 'app-keypad', x: 6, y: 0, w: 4, h: 4, options: { mode: 'pinned', profileId: 'desktop', showHeader: true, showHints: true } },
            { id: 'roll-horiz', type: 'app-roller', x: 0, y: 4, w: 6, h: 2, options: { mode: 'follow', axis: 1, orientation: 'auto', showReset: true } },
            { id: 'roll-vert', type: 'app-roller', x: 10, y: 0, w: 2, h: 6, options: { mode: 'follow', axis: 1, orientation: 'auto', showReset: false } },
          ],
        },
        {
          id: 'second',
          title: 'Second',
          widgets: [{ id: 'n2', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page 2' } }],
        },
      ],
    }));
  });
  await page.reload({ waitUntil: 'networkidle' });
  await expect(page.locator('.w-app-keypad')).toHaveCount(2);
  await expect(page.locator('.w-app-dial')).toHaveCount(1);
  await expect(page.locator('.w-app-roller')).toHaveCount(2);
}

const activeDot = (page) => page.locator('#dots i').evaluateAll((dots) => dots.findIndex((d) => d.classList.contains('on')));

test('console keypad, dial, and horizontal/vertical rollers render, switch context, and share pin state', async ({ page }) => {
  const errors = watchErrors(page);
  await seedConsolePanel(page);

  const followPad = page.locator('.w-app-keypad').nth(0);
  const pinnedPad = page.locator('.w-app-keypad').nth(1);
  const dial = page.locator('.w-app-dial').first();
  const horizRoller = page.locator('.w-app-roller').nth(0).locator('.cr-root');
  const vertRoller = page.locator('.w-app-roller').nth(1).locator('.cr-root');

  await expect(horizRoller).toHaveClass(/is-horiz/);
  await expect(vertRoller).toHaveClass(/is-vert/);
  await expect(pinnedPad.locator('.ck-pin')).toHaveText('📌 Locked');

  await page.evaluate(() => window.PhoneBoxDemo.setActiveApp('Code', 'main.js'));
  await expect(followPad.locator('.ck-name')).toHaveText('VS Code');
  await expect(followPad.locator('.ck-sub')).toHaveText('Code & Edit');
  await expect(dial.locator('.cd-prof')).toContainText('VS Code');
  await expect(pinnedPad.locator('.ck-name')).toHaveText('Desktop');

  await followPad.locator('.ck-title').click();
  await expect(followPad.locator('.ck-picker')).toBeVisible();
  await followPad.locator('.ck-pick', { hasText: 'OBS Studio' }).click();

  await expect(followPad.locator('.ck-pin')).toHaveText('📌 Pinned');
  await expect(followPad.locator('.ck-name')).toHaveText('OBS Studio');
  await expect(dial.locator('.cd-prof')).toContainText('OBS Studio');
  await expect(horizRoller.locator('.cr-prof')).toHaveText('OBS Studio');
  await expect(pinnedPad.locator('.ck-name')).toHaveText('Desktop');

  await followPad.locator('.ck-pin').click();
  await expect(followPad.locator('.ck-pin')).toHaveText('⚡ Auto');
  await expect(followPad.locator('.ck-name')).toHaveText('VS Code');

  const broken = await page.locator('.w > .empty').allTextContents();
  expect(broken.filter((t) => /^[a-z]+: /.test(t))).toEqual([]);
  expect(errors).toEqual([]);
});

test('vertical swipe on keypad changes sub-page and dial/roller drags emit axis actions without flipping pages', async ({ page }) => {
  const errors = watchErrors(page);
  await seedConsolePanel(page);
  await page.evaluate(() => window.PhoneBoxDemo.setActiveApp('Code', 'main.js'));

  const followPad = page.locator('.w-app-keypad').nth(0);
  await expect(followPad.locator('.ck-sub')).toHaveText('Code & Edit');

  // Vertical swipe across the keypad switches sub-page without firing a slot click or flipping #pager.
  const gridBox = await followPad.locator('.ck-grid').boundingBox();
  const gx = gridBox.x + gridBox.width / 2;
  const gy = gridBox.y + gridBox.height * 0.75;
  await page.mouse.move(gx, gy);
  await page.mouse.down();
  for (let i = 1; i <= 6; i++) await page.mouse.move(gx, gy - (i * 12));
  await page.mouse.up();

  await expect(followPad.locator('.ck-sub')).toHaveText('Run & Debug');
  expect(await page.evaluate(() => window.PhoneBoxDemo.lastConsoleAction())).toBeNull();
  expect(await activeDot(page)).toBe(0);

  // Arc drag on rotary dial emits axis delta and does not switch pager page.
  const ringBox = await page.locator('.cd-ring').boundingBox();
  const cx = ringBox.x + ringBox.width / 2;
  const cy = ringBox.y + ringBox.height / 2;
  const r = Math.min(ringBox.width, ringBox.height) * 0.4;
  await page.mouse.move(cx, cy - r);
  await page.mouse.down();
  for (let i = 1; i <= 10; i++) {
    const a = -Math.PI / 2 + (i / 10) * Math.PI;
    await page.mouse.move(cx + Math.cos(a) * r, cy + Math.sin(a) * r);
  }
  await page.mouse.up();

  await expect.poll(() => page.evaluate(() => window.PhoneBoxDemo.lastConsoleAction()?.kind)).toBe('axis');
  expect(await activeDot(page)).toBe(0);

  // Drag horizontal roller rightward.
  const hrBox = await page.locator('.w-app-roller').nth(0).locator('.cr-track').boundingBox();
  await page.evaluate(() => { window.__resetLast = true; });
  await page.mouse.move(hrBox.x + 12, hrBox.y + hrBox.height / 2);
  await page.mouse.down();
  for (let i = 1; i <= 6; i++) await page.mouse.move(hrBox.x + 12 + i * 10, hrBox.y + hrBox.height / 2);
  await page.mouse.up();
  await expect.poll(() => page.evaluate(() => window.PhoneBoxDemo.lastConsoleAction()?.kind)).toBe('axis');
  expect(await activeDot(page)).toBe(0);

  // Tap horizontal roller reset/tap button.
  await page.locator('.w-app-roller').nth(0).locator('.cr-tap').click();
  await expect.poll(() => page.evaluate(() => window.PhoneBoxDemo.lastConsoleAction()?.kind)).toBe('axis-tap');
  expect(errors).toEqual([]);
});

test('desktop settings Console tab adds preset, picks running window, records shortcut keys, and persists after save', async ({ page }) => {
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.goto('/panel/', { waitUntil: 'networkidle' });
  await page.evaluate(() => localStorage.clear());

  await page.goto('/settings/#console', { waitUntil: 'networkidle' });
  await expect(page.locator('#nav a[href="#console"]')).toHaveClass(/\bon\b/);

  // Add from preset (Premiere Pro).
  await page.locator('#conPresetBtn').click();
  const modal = page.locator('#modal');
  await expect(modal).toBeVisible();
  await modal.locator('.tpl', { hasText: 'Premiere Pro' }).click();
  await expect(page.locator('.con-ptab', { hasText: 'Premiere Pro' })).toHaveClass(/\bon\b/);

  // Pick running app from /api/console/windows.
  await page.locator('#conWinBtn').click();
  await expect(modal).toBeVisible();
  const winRow = modal.locator('.con-win-row', { hasText: 'Spotify' });
  await expect(winRow).toBeVisible();
  await winRow.click();
  await expect(page.locator('.con-ptab', { hasText: 'Spotify' })).toHaveClass(/\bon\b/);

  // Record a keyboard chord into slot 1.
  await page.locator('#conEditor .con-rec').first().click();
  await expect(page.locator('#conEditor .con-rec.recording')).toBeVisible();
  await page.keyboard.press('Control+Shift+K');
  await expect(page.locator('#conEditor input[placeholder*="Ctrl+Shift+B"]')).toHaveValue('Ctrl+Shift+K');

  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();

  await page.reload();
  await expect(page.locator('.con-ptab', { hasText: 'Premiere Pro' })).toBeVisible();
  await page.locator('.con-ptab', { hasText: 'Spotify' }).click();
  await expect(page.locator('#conEditor input[placeholder*="Ctrl+Shift+B"]')).toHaveValue('Ctrl+Shift+K');
  expect(errors).toEqual([]);
});
