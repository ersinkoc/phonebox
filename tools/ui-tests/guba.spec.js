const { test, expect } = require('@playwright/test');

test('Stock forum renders posts from the agent', async ({ page }) => {
  await page.addInitScript(() => {
    const cfg = JSON.stringify({
      display: { language: 'en' }, night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'stocks', title: 'Markets', widgets: [{ id: 'forum', type: 'guba', x: 0, y: 0, w: 5, h: 4 }] }],
    });
    localStorage.setItem('cfg', cfg);
    localStorage.setItem('pb-demo-config', cfg);
  });
  await page.goto('/panel/');
  const forum = page.locator('.w-guba');
  await expect(forum.locator('.w-title')).toContainText('Stock forum · 002317');
  await expect(forum.locator('.gb-list')).toContainText('Morning market notes');
  await expect(forum.locator('.gb-list')).toContainText('Comments 2');
});
