const { test, expect } = require('@playwright/test');

test('the night preset only runs while the rule is active', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#rules');
  await page.locator('#rulePresets button', { hasText: 'Away → night screen' }).click();
  const rule = page.locator('#ruleList .rule').last();
  await expect(rule.getByRole('button', { name: 'Test', exact: true })).toHaveCount(0);
  await expect(rule).toContainText('then the night screen turns off');
  await expect(rule.getByRole('checkbox')).toHaveCount(1);
});

test('an active night rule dims the panel until the rule ends', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  const result = await page.evaluate(async () => {
    const panel = await import('/panel/main.js');
    panel.cfg.night = { enabled: false };
    panel.cfg.rules = [{ id: 'away', then: 'night' }];
    panel.applyState({ ...panel.D.state, rules: ['away'] });
    const active = document.body.classList.contains('night');
    panel.applyState({ ...panel.D.state, rules: [] });
    const ended = !document.body.classList.contains('night');
    panel.applyState({ ...panel.D.state, rules: ['away'] });
    document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    const awake = !document.body.classList.contains('night');
    return { active, awake, ended };
  });
  expect(result).toEqual({ active: true, awake: true, ended: true });
});

async function openDarkPanel(page) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.evaluate(async () => {
    const key = 'pb-demo-config';
    const saved = JSON.parse(localStorage.getItem(key) || '{}');
    saved.night = { enabled: false };
    saved.display = { ...(saved.display || {}), nightWhenDark: true };
    localStorage.setItem(key, JSON.stringify(saved));
    const panel = await import('/panel/main.js');
    panel.cfg.night = { enabled: false };
    panel.cfg.display = { ...panel.cfg.display, nightWhenDark: true };
    window.onAndroidSensor('light', 1);
  });
}

test('interacting with the phone resets the 30 s dark-room timer', async ({ page }) => {
  await openDarkPanel(page);

  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  // Tap at 20 s resets the 30 s countdown.
  await page.mouse.click(200, 200);
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  // Continuous touch drag before 30 s expires resets the countdown again.
  await page.evaluate(() => document.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, pointerType: 'touch', buttons: 1 })));
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  await page.clock.runFor(12_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);
});

test('interacting while woken resets the 30 s wake hold before the night screen returns', async ({ page }) => {
  await openDarkPanel(page);

  await page.clock.runFor(31_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);

  await page.mouse.click(200, 200);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  await page.clock.runFor(20_000);
  await page.mouse.click(200, 200);

  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  await page.clock.runFor(12_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);
});

test('a continuous single touch or drag longer than 30 s keeps the night screen off until 30 s after release', async ({ page }) => {
  await openDarkPanel(page);

  // Hold a single touch down for 45 s (longer than the 30 s dark-room timeout) while moving slowly.
  await page.mouse.move(200, 200);
  await page.mouse.down();
  for (let i = 0; i < 4; i++) {
    await page.clock.runFor(11_250);
    await page.mouse.move(210 + i * 10, 210);
    await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
  }

  // Lift the finger at 45 s: night screen stays off for 30 s after release, then turns on.
  await page.mouse.up();
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  await page.clock.runFor(12_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);
});

test('a continuous hold without movement longer than 30 s while woken prevents night screen until 30 s after release', async ({ page }) => {
  await openDarkPanel(page);

  await page.clock.runFor(31_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);

  // Wake with a tap, wait 10 s, then press and hold stationary for 40 s without lifting or moving.
  await page.mouse.click(200, 200);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  await page.clock.runFor(10_000);
  await page.mouse.move(200, 200);
  await page.mouse.down();
  await page.clock.runFor(40_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  // After release, night screen stays off for another 30 s before turning back on.
  await page.mouse.up();
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  await page.clock.runFor(12_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);
});

test('a continuous WebView touch pan after pointercancel keeps the night screen off until 30 s after touchend', async ({ page }) => {
  await openDarkPanel(page);

  await page.evaluate(() => {
    const fireTouch = (type, active) => {
      const e = new Event(type, { bubbles: true });
      e.touches = active ? [{ clientX: 200, clientY: 200 }] : [];
      document.body.dispatchEvent(e);
    };
    window.__fireTouch = fireTouch;
    fireTouch('touchstart', true);
    document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerId: 1, pointerType: 'touch' }));
    document.body.dispatchEvent(new PointerEvent('pointercancel', { bubbles: true, pointerId: 1, pointerType: 'touch' }));
  });

  for (let i = 0; i < 4; i++) {
    await page.clock.runFor(11_250);
    await page.evaluate(() => window.__fireTouch('touchmove', true));
    await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
  }

  await page.evaluate(() => window.__fireTouch('touchend', false));
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  await page.clock.runFor(12_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);
});

test('a scheduled night wake hold and press across schedule start keep the night screen off for 30 s after release', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:40') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.evaluate(async () => {
    const key = 'pb-demo-config';
    const saved = JSON.parse(localStorage.getItem(key) || '{}');
    saved.night = { enabled: true, start: '12:01', end: '18:00', brightness: 0.02 };
    saved.display = { ...(saved.display || {}), nightWhenDark: false, nightWhenUnreachable: false };
    localStorage.setItem(key, JSON.stringify(saved));
    const panel = await import('/panel/main.js');
    panel.cfg.night = saved.night;
    panel.cfg.display = { ...panel.cfg.display, nightWhenDark: false, nightWhenUnreachable: false };
  });

  // Press at 12:00:50 and hold across the 12:01:00 schedule start until 12:01:10.
  await page.clock.runFor(10_000);
  await page.mouse.move(200, 200);
  await page.mouse.down();
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  // Release at 12:01:10: night stays off for 30 s after release, then turns on.
  await page.mouse.up();
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
  await page.clock.runFor(12_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);

  // Wake during scheduled night, then tap at 20 s to verify wakeUntil itself resets independently of darkRoom.
  await page.mouse.click(200, 200);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
  await page.clock.runFor(20_000);
  await page.mouse.click(200, 200);
  await page.clock.runFor(20_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
  await page.clock.runFor(12_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);
});

test('a pointerdown with no pointerup stops counting after 2 minutes and clears on pause', async ({ page }) => {
  await openDarkPanel(page);

  await page.evaluate(() => document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerId: 1, pointerType: 'touch' })));
  await page.clock.runFor(90_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);

  // After 2 minutes without move or release the contact is pruned, so the 30 s timer finishes.
  await page.clock.runFor(65_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);

  // A held contact also clears immediately when the app pauses.
  await page.mouse.click(200, 200);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
  await page.mouse.move(200, 200);
  await page.mouse.down();
  await page.clock.runFor(35_000);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)night($|\s)/);
  await page.evaluate(() => window.onAppPause(true));
  await page.clock.runFor(32_000);
  await expect(page.locator('body')).toHaveClass(/(^|\s)night($|\s)/);
});
