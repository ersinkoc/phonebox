const { test, expect } = require('@playwright/test');

// The push rule editor: "has no update for" keeps its seconds apart from a comparison's value, and its labels are translated.
async function openRules(page, language, rule) {
  await page.goto('/panel/');
  await page.evaluate(async ({ language, rule }) => {
    localStorage.clear();
    const config = await (await fetch('/api/settings')).json();
    config.display.language = language;
    config.rules = [rule];
    localStorage.setItem('pb-demo-config', JSON.stringify(config));
  }, { language, rule });
  await page.goto('/settings/#rules');
  const card = page.locator('#ruleList > *').first();
  await expect(card.locator('select:has(option[value="stale"])')).toBeVisible();
  return card;
}

test('switching a push rule to "has no update for" and back keeps the comparison value', async ({ page }) => {
  const card = await openRules(page, 'en', { id: 'net', name: 'Net', when: 'push', match: 'router', op: '=', arg: 'offline', then: 'notify', text: '{detail}' });
  const op = card.locator('select:has(option[value="stale"])');

  await op.selectOption('stale');
  await expect(card.locator('input[type="number"][max="86400"]')).toHaveValue('60');
  await card.locator('input[type="number"][max="86400"]').fill('90');

  await op.selectOption('=');
  await expect(card.locator('input[placeholder="value"]')).toHaveValue('offline');

  await op.selectOption('stale');                     // the seconds typed before come back too
  await expect(card.locator('input[type="number"][max="86400"]')).toHaveValue('90');

  await op.selectOption('>=');
  await expect(card.locator('input[placeholder="value"]')).toHaveValue('offline');
});

test('the new push rule labels are translated in Vietnamese', async ({ page }) => {
  const card = await openRules(page, 'vi', { id: 'quiet', name: 'Quiet', when: 'push', match: 'temp', op: 'stale', arg: '60', then: 'notify', text: '{detail}' });
  const labels = await card.locator('select:has(option[value="stale"]) option').allTextContents();
  expect(labels).toEqual(expect.arrayContaining(['lớn hơn hoặc bằng', 'nhỏ hơn hoặc bằng', 'không có cập nhật trong']));
  await expect(card.getByText('giây', { exact: true })).toBeVisible();
});
