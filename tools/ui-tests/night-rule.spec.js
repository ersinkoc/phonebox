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
