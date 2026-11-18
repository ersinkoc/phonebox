const { test, expect } = require('@playwright/test');

test('a note can be edited from the panel', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'en' },
      pages: [{ id: 'one', title: 'One', enabled: true, widgets: [
        { id: 'n1', type: 'note', x: 0, y: 0, w: 6, h: 3, options: { text: 'old text', color: '#fbbf24' } },
      ] }],
    }));
  });
  await page.reload();
  await page.locator('.note-t').click();
  const ta = page.locator('.note-ta');
  await expect(ta).toBeVisible();
  await ta.fill('new text');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.note-t')).toHaveText('new text');
});
