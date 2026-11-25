const { test, expect } = require('@playwright/test');

test('AI sessions widget renders the Grok glyph, name and token note', async ({ page }) => {
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
    window.onAppPause(true);
    D.work = {
      sessions: [{
        id: 'grok:11111111-2222-3333-4444-555555555555', tool: 'grok', name: 'desk-widget-1111', project: 'desk-widget',
        status: 'busy', waitingFor: null, since: Date.now() - 10_000, startedAt: Date.now() - 120_000,
        model: 'grok-demo', effort: 'high', totalTokens: 4200, inputTokens: 3000, outputTokens: 1200,
      }],
      github: { user: null, notifications: 0, reviewRequests: [], myPrs: [], error: null },
    };
    emit('work');
  });

  const row = page.locator('button.sess');
  await expect(row).toHaveCount(1);
  await expect(row.locator('.tl.grok')).toHaveText('✶');
  await expect(row.locator('.nm')).toHaveText('desk-widget');

  await row.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('.session-panel-head')).toContainText('✶ Grok');
  await expect(dialog.locator('.session-note')).toContainText('Token totals are for this local session');
  await expect(dialog.locator('.session-conv')).toHaveCount(0);
});
