const { test, expect } = require('@playwright/test');

const config = (antigravity) => ({
  display: { language: 'en', theme: 'default' },
  pages: [],
  antigravity: { sessions: false, shareSessionTitles: false, ...antigravity },
});

async function seed(page, cfg) {
  await page.goto('/panel/style.css');
  await page.evaluate((c) => localStorage.setItem('pb-demo-config', JSON.stringify(c)), cfg);
  await page.goto('/settings/#general');
}

test('Antigravity session switches save and survive reload', async ({ page }) => {
  await seed(page, config({}));
  const sessToggle = page.locator('[data-bind="antigravity.sessions"]');
  const titleToggle = page.locator('[data-bind="antigravity.shareSessionTitles"]');
  await expect(sessToggle).not.toBeChecked();
  await expect(titleToggle).not.toBeChecked();

  await page.locator('label.switch', { has: sessToggle }).click();
  await titleToggle.check();

  await page.locator('#save').click();
  await expect(page.locator('#toast')).toHaveText(/^Saved/);

  await page.reload();
  await expect(page.locator('[data-bind="antigravity.sessions"]')).toBeChecked();
  await expect(page.locator('[data-bind="antigravity.shareSessionTitles"]')).toBeChecked();
});

test('AI sessions widget renders Antigravity glyph, title and token note', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'en' },
      pages: [{ id: 'one', title: 'One', widgets: [{ id: 's', type: 'sessions', x: 0, y: 0, w: 7, h: 6, options: {} }] }],
    }));
  });
  await page.reload();
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    window.onAppPause(true); // stop the work poll, or the demo's sessions can replace the fixture
    D.work = {
      sessions: [{
        id: 'antigravity:conv-1', tool: 'antigravity', name: 'phonebox-conv', project: 'phonebox',
        status: 'busy', waitingFor: null, since: Date.now() - 10_000, startedAt: Date.now() - 120_000,
        model: 'gemini-4-carbon-medium', totalTokens: 4200, inputTokens: 3000, outputTokens: 1200,
      }],
      github: { user: null, notifications: 0, reviewRequests: [], myPrs: [], error: null },
    };
    emit('work');
  });

  const row = page.locator('button.sess');
  await expect(row).toHaveCount(1);
  await expect(row.locator('.tl.antigravity')).toHaveText('✦');
  await expect(row.locator('.nm')).toHaveText('phonebox');

  await row.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('.session-panel-head')).toContainText('✦ Antigravity');
  await expect(dialog.locator('.session-note')).toContainText('Token totals are for this local session');
});
