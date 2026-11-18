const { test, expect } = require('@playwright/test');

// Dictation: the Android bridge recognizes speech (faked here), the widget sends the text to the agent (the demo).
// The fake has to be there before main.js builds the bridge, so it comes from an init script.
async function layout(page, options, { app = true } = {}) {
  if (app) {
    await page.addInitScript(() => {
      window.__listen = [];
      window.__cancelled = [];
      let next = 0;
      window.Android = {
        canListen: () => true,
        listen: (key, lang, offline) => {
          const id = ++next;
          window.__listen.push({ lang, offline });
          if (window.__hold) return id; // a session that keeps listening until it is cancelled
          setTimeout(() => window.onAndroidSpeech('ready', '', id), 10);
          setTimeout(() => window.onAndroidSpeech('partial', 'hello', id), 30);
          setTimeout(() => window.onAndroidSpeech('final', 'hello world', id), 60);
          return id;
        },
        stopListening: () => {},
        cancelListening: (key, id) => { window.__cancelled.push(id); setTimeout(() => window.onAndroidSpeech('error', 'cancelled', id), 5); },
      };
    });
  }
  await page.goto('/panel/');
  await page.evaluate((options) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'dc', type: 'dictate', x: 0, y: 0, w: 12, h: 6, options }] }] }));
  }, options);
  await page.reload();
  await expect(page.locator('.w.dc')).toBeVisible();
  await page.evaluate(() => {
    window.__posts = [];
    const demo = window.fetch;
    window.fetch = (url, init) => {
      if (init && init.method === 'POST' && String(url).includes('/dictate')) window.__posts.push([String(url).replace(/^.*\/agent\/api/, ''), init.body ?? null]);
      return demo(url, init);
    };
  });
}

test('dictation sends what the phone heard for its own widget', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, { control: true, language: 'tr-TR' });
  await page.locator('.dc-mic').click();
  await expect(page.locator('.dc-text')).toHaveText('hello world');
  // in the query, no body: the app's transport doesn't carry request bodies
  await expect.poll(() => page.evaluate(() => window.__posts)).toEqual([['/dictate?widget=test%2Fdc&text=hello%20world', null]]);
  await expect(page.locator('.dc-note')).toHaveText('Typed on the computer');
  expect(await page.evaluate(() => window.__listen)).toEqual([{ lang: 'tr-TR', offline: false }]);
  expect(errors).toEqual([]);
});

test('dictation without its switch keeps the text on the phone', async ({ page }) => {
  await layout(page, {});
  await expect(page.locator('.dc-note')).toContainText('Type the text on the computer');
  await page.locator('.dc-mic').click();
  await expect(page.locator('.dc-text')).toHaveText('hello world');
  expect(await page.evaluate(() => window.__posts)).toEqual([]);
});

test('a session from another widget never answers or ends this one', async ({ page }) => {
  await layout(page, { control: true });
  await page.evaluate(() => { window.__hold = true; });
  await page.locator('.dc-mic').click();
  await expect(page.locator('.dc-mic')).toHaveClass(/on/);
  // events for an id this widget didn't start (another widget's session) are ignored
  await page.evaluate(() => { window.onAndroidSpeech('final', 'not mine', 99); window.onAndroidSpeech('error', 'cancelled', 99); });
  await expect(page.locator('.dc-mic')).toHaveClass(/on/);
  await expect(page.locator('.dc-text')).toHaveText('');
  expect(await page.evaluate(() => window.__posts)).toEqual([]);
});

test('a dictation too long to type whole is not sent cut short', async ({ page }) => {
  await page.addInitScript(() => { window.__long = 'x'.repeat(1200); });
  await layout(page, { control: true });
  await page.evaluate(() => {
    window.__hold = true; // no default events: only the long final below
    const listen = window.Android.listen;
    window.Android.listen = (key, lang, offline) => { const id = listen(key, lang, offline); setTimeout(() => window.onAndroidSpeech('final', window.__long, id), 80); return id; };
  });
  await page.locator('.dc-mic').click();
  await expect(page.locator('.dc-note')).toContainText('too long');
  expect(await page.evaluate(() => window.__posts)).toEqual([]);
});

test('dictation in a browser or an older app says to update', async ({ page }) => {
  await layout(page, { control: true }, { app: false });
  await expect(page.locator('.dc-note')).toHaveText('Update the PhoneBox app to dictate');
});
