const { test, expect } = require('@playwright/test');

async function openNote(page) {
  await page.goto('/panel/style.css');
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
  await page.goto('/panel/');
  await page.locator('.note-t').click();
  const textarea = page.locator('.note-ta');
  await expect(textarea).toBeVisible();
  return textarea;
}

test('a note can be edited from the panel', async ({ page }) => {
  const ta = await openNote(page);
  await ta.fill('new text');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.note-t')).toHaveText('new text');
});

for (const stalled of ['headers', 'body']) {
  test(`a Note save recovers when the response ${stalled} stall`, async ({ page }) => {
    const time = new Date('2026-01-01T12:00:00Z');
    await page.clock.install({ time });
    const textarea = await openNote(page);
    await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
    await page.evaluate(stalled => {
      const original = window.fetch;
      window.noteSaveStarted = false;
      window.fetch = (url, init) => {
        if (!String(url).includes('/widget/one/n1/note?')) return original(url, init);
        window.fetch = original;
        window.noteSaveStarted = true;
        const pending = new Promise(resolve => { window.releaseNoteSave = resolve; });
        if (stalled === 'headers') return pending;
        return Promise.resolve({
          ok: true,
          headers: new Headers({ 'Content-Type': 'application/json' }),
          json: () => pending,
        });
      };
    }, stalled);
    await textarea.fill('unsaved draft');
    const save = page.getByRole('button', { name: 'Save' });
    await save.click();
    expect(await page.evaluate(() => window.noteSaveStarted)).toBe(true);
    await expect(save).toBeDisabled();
    await expect(textarea).toBeDisabled();
    await page.clock.runFor(8100);

    const editable = await textarea.isEnabled();
    const retryable = await save.isEnabled();
    expect({ editable, retryable },
      `EXPECTED: editable draft and enabled Save | ACTUAL: editable=${editable}, retryable=${retryable}`)
      .toEqual({ editable: true, retryable: true });
    await expect(textarea).toHaveValue('unsaved draft');
    await expect(page.locator('#toast')).toContainText('Could not save');
    await save.click();
    await expect(page.locator('.note-t')).toHaveText('unsaved draft');
    await page.evaluate(stalled => window.releaseNoteSave(stalled === 'headers' ? Response.json({}) : {}), stalled);
  });
}

test('a Note save still reports a conflicting edit', async ({ page }) => {
  const textarea = await openNote(page);
  await page.evaluate(() => {
    const original = window.fetch;
    window.fetch = (url, init) => String(url).includes('/widget/one/n1/note?')
      ? Promise.resolve(new Response(null, { status: 409 }))
      : original(url, init);
  });
  await textarea.fill('conflicting draft');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.note-edit')).toHaveCount(0);
  await expect(page.locator('#toast')).toContainText('The note was changed on the computer');
  await expect(page.locator('.note-t')).toHaveText('old text');
});

test('typing and Escape inside Note editor do not trigger global shortcuts and sibling Notes share cfgVersion', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-version', '1');
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'en', idleReturnSec: 0 },
      pages: [
        {
          id: 'one', title: 'One', enabled: true, widgets: [
            { id: 'n1', type: 'note', x: 0, y: 0, w: 6, h: 3, options: { text: 'first', color: '#fbbf24' } },
            { id: 'n2', type: 'note', x: 6, y: 0, w: 6, h: 3, options: { text: 'second', color: '#60a5fa' } },
          ],
        },
        {
          id: 'two', title: 'Two', enabled: true, widgets: [
            { id: 'n3', type: 'note', x: 0, y: 0, w: 6, h: 3, options: { text: 'page two', color: '#34d399' } },
          ],
        },
      ],
    }));
  });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).toHaveCount(2);

  await page.evaluate(() => {
    const orig = window.fetch;
    let serverVer = '1';
    window.fetch = async (input, init) => {
      const urlStr = String(typeof input === 'string' ? input : input.url);
      if (urlStr.includes('/api/widget/') && urlStr.includes('/note')) {
        const sentVer = new URL(urlStr, location.origin).searchParams.get('version');
        if (sentVer !== serverVer) {
          return new Response(JSON.stringify({ error: 'conflict' }), { status: 409, headers: { 'Content-Type': 'application/json' } });
        }
        const resp = await orig(input, init);
        const data = await resp.clone().json();
        serverVer = String(data.version);
        return resp;
      }
      return orig(input, init);
    };
  });

  await page.locator('.note-t').nth(0).click();
  const ta = page.locator('.note-ta');
  await expect(ta).toBeVisible();
  await ta.fill('');
  await page.keyboard.type('ok');
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#overview')).toBeHidden();
  expect(await page.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')))).toBe(0);
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.note-edit')).toHaveCount(0);

  // Escape closes .note-edit without opening #overview
  await page.locator('.note-t').nth(1).click();
  await expect(page.locator('.note-ta')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('.note-edit')).toHaveCount(0);
  await expect(page.locator('#overview')).toBeHidden();

  // Saving sibling Note 2 and re-editing Note 1 both send the latest shared cfgVersion
  await page.locator('.note-t').nth(1).click();
  await page.locator('.note-ta').fill('second updated');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.note-t').nth(1)).toHaveText('second updated');

  await page.locator('.note-t').nth(0).click();
  await page.locator('.note-ta').fill('first updated again');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.note-t').nth(0)).toHaveText('first updated again');
});
