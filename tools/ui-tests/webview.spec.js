const { test, expect } = require('@playwright/test');

test('settings general tab stays quiet for modern WebView and warns when outdated', async ({ page }) => {
  await page.goto('/settings/#general');
  await expect(page.locator('#webviewProblem')).toBeHidden();
  await expect(page.locator('#phoneList')).toContainText('WebView 134');
  await expect(page.locator('#phoneList')).not.toContainText('⚠');

  await page.evaluate(async () => {
    const real = window.fetch;
    const problem = 'SM-G960F has an outdated WebView (74, com.android.chrome); the panel needs Chromium 80+ to run at all. Update Google Chrome from Google Play (it provides WebView on this device).';
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
  await expect(page.locator('#webviewProblem svg.ico')).toBeVisible();
  await expect(page.locator('#webviewProblem')).toContainText('SM-G960F has an outdated WebView');
  await expect(page.locator('#phoneList')).toContainText('WebView 74');
  await expect(page.locator('#phoneList .hint svg.ico')).toBeVisible();
  await expect(page.locator('#phoneList')).toContainText('SM-G960F has an outdated WebView');
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

test('the iOS shell shows no Android WebView notice, even without container query units', async ({ browser }) => {
  // the notice tells the user to update Android System WebView or Chrome: no use on iOS, whose WebKit comes with the OS
  const ctx = await browser.newContext();
  try {
    const iosPage = await ctx.newPage();
    await iosPage.addInitScript(() => {
      window.__phoneboxIOS = true; // set by the iOS shell at document start
      const orig = window.CSS && window.CSS.supports ? window.CSS.supports.bind(window.CSS) : () => false;
      window.CSS.supports = (prop, val) => (String(prop).includes('cqw') || String(val).includes('cqw')) ? false : orig(prop, val);
    });
    await iosPage.goto('/panel/');
    await iosPage.waitForFunction(() => window.__pbUp); // booted, so the notice had its chance to show
    await expect(iosPage.locator('#webviewWarn')).toBeHidden();
  } finally {
    await ctx.close();
  }
});

test('panel says the WebView is too old to run it, and keeps the notice up', async ({ browser }) => {
  // the version is the one in use: a phone maker's own WebView reports its own, whatever Google Play installed
  const ctx = await browser.newContext({ userAgent: 'Mozilla/5.0 (Linux; Android 5.0; GEM-701L) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/37.0.0.0 Safari/537.36' });
  try {
    const oldPage = await ctx.newPage();
    // a WebView before Chromium 80 can't parse ?. — the inline check compiles a snippet with new Function
    await oldPage.addInitScript(() => {
      window.Function = new Proxy(window.Function, {
        construct(target, args) {
          if (String(args[args.length - 1]).includes('?.')) throw new SyntaxError('Unexpected token .');
          return Reflect.construct(target, args);
        },
      });
    });
    await oldPage.goto('/panel/');
    const warn = oldPage.locator('#webviewWarn');
    await expect(warn).toBeVisible();
    await expect(warn).toContainText('This Android System WebView (version 37) is too old to run the panel');
    await expect(warn).toContainText('version 80 or later');
    await expect(oldPage.locator('#webviewWarnClose')).toBeHidden();
    // the boot-failure box would only bury the notice under syntax errors
    await oldPage.waitForTimeout(500);
    await expect(oldPage.locator('#jsfail')).toBeHidden();
  } finally {
    await ctx.close();
  }
});
