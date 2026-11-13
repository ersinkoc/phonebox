const { test, expect } = require('@playwright/test');

test('settings general tab stays quiet for modern WebView and warns when outdated', async ({ page }) => {
  await page.goto('/settings/#general');
  await expect(page.locator('#webviewProblem')).toBeHidden();
  await expect(page.locator('#phoneList')).toContainText('WebView 134');
  await expect(page.locator('#phoneList')).not.toContainText('⚠');

  await page.evaluate(async () => {
    const real = window.fetch;
    const problem = 'SM-G960F has an outdated WebView (74, com.android.chrome); widget scaling needs Chromium 105+. Update Google Chrome from Google Play (it provides WebView on this device).';
    window.fetch = async (input, init) => {
      const r = await real(input, init);
      const url = String(input);
      if (url.endsWith('/api/info')) {
        const d = await r.json();
        d.webviewProblem = problem;
        return new Response(JSON.stringify(d), { headers: { 'Content-Type': 'application/json' } });
      }
      if (url.endsWith('/api/phones')) {
        return new Response(JSON.stringify([
          { id: '3f9a1c0d7b2e', model: 'SM-G960F', lastSeen: Date.now() - 1000, via: 'Wi-Fi', webView: 'com.android.chrome/74.0.3729.185', webviewProblem: problem, name: null, pages: null },
        ]), { headers: { 'Content-Type': 'application/json' } });
      }
      return r;
    };
    await loadInfo();
    await loadPhones();
  });

  await expect(page.locator('#webviewProblem')).toBeVisible();
  await expect(page.locator('#webviewProblem')).toContainText('⚠ SM-G960F has an outdated WebView');
  await expect(page.locator('#phoneList')).toContainText('WebView 74');
  await expect(page.locator('#phoneList')).toContainText('⚠ SM-G960F has an outdated WebView');
});

test('panel shows dismissible notice only when container query units are unsupported', async ({ page, browser }) => {
  await page.goto('/panel/');
  await expect(page.locator('#webviewWarn')).toBeHidden();

  const ctx = await browser.newContext();
  try {
    const oldPage = await ctx.newPage();
    await oldPage.addInitScript(() => {
      const orig = window.CSS && window.CSS.supports ? window.CSS.supports.bind(window.CSS) : () => false;
      window.CSS = window.CSS || {};
      window.CSS.supports = (prop, val) => (String(prop).includes('cqw') || String(val).includes('cqw')) ? false : orig(prop, val);
    });

    await oldPage.goto('/panel/?preview=1');
    await expect(oldPage.locator('#webviewWarn')).toBeHidden();

    await oldPage.goto('/panel/');
    await expect(oldPage.locator('#webviewWarn')).toBeVisible();
    await expect(oldPage.locator('#webviewWarn')).toContainText('Outdated WebView');

    await oldPage.getByRole('button', { name: 'Dismiss WebView notice' }).click();
    await expect(oldPage.locator('#webviewWarn')).toBeHidden();

    await oldPage.reload();
    await expect(oldPage.locator('#webviewWarn')).toBeHidden();
  } finally {
    await ctx.close();
  }
});
