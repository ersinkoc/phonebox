// The Synced lyrics widget against the web demo's made-up songs (tools/pages/demo.js); LRCLIB itself is never asked.
const { test, expect } = require('@playwright/test');

async function showPage(page, widgets, display = {}) {
  await page.goto('/panel/');
  await page.evaluate(({ widgets, display }) => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, display: { ...(cfg.display || {}), ...display },
      pages: [{ id: 'test', title: 'Test', icon: '', enabled: true, widgets }], shortcuts: cfg.shortcuts || [] }));
  }, { widgets, display });
  await page.reload();
}

function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

test('LRC lyrics are read in time order and the sung line is found by position', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const { parseLrc, lyricAt } = await import('./widgets/lyrics.js');
    const lines = parseLrc('[ar: Someone]\n[00:10.50] second\n[00:01.00][01:02.00] chorus\nno time\n[00:20] third\n[00:30:25] colon');
    return { lines, at: [lyricAt(lines, 0), lyricAt(lines, 1000), lyricAt(lines, 10_499), lyricAt(lines, 10_500), lyricAt(lines, 99_000)] };
  });
  expect(out.lines).toEqual([
    { ms: 1000, text: 'chorus' }, { ms: 10500, text: 'second' }, { ms: 20000, text: 'third' }, { ms: 30250, text: 'colon' }, { ms: 62000, text: 'chorus' },
  ]);
  expect(out.at).toEqual([-1, 0, 0, 1, 4]);
});

test('the Synced lyrics widget follows the song, shows plain lyrics and says when there are none', async ({ page }) => {
  const errors = watchErrors(page);
  await showPage(page, [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6, options: { lookup: true } }]);
  const lyrics = page.locator('.w-lyrics');
  await expect(lyrics).toHaveClass(/timed/);
  await expect(lyrics.locator('.ly-line')).toHaveCount(20);
  // the demo song started at 1:01: "Midnight circuit, hold the current" (0:58) or a later line is lit, earlier ones are past
  const now = lyrics.locator('.ly-line.now');
  await expect(now).toHaveCount(1);
  const index = await now.evaluate((el) => [...el.parentNode.children].indexOf(el));
  expect(index).toBeGreaterThanOrEqual(8);
  await expect(lyrics.locator('.ly-line.past')).toHaveCount(index);
  await expect(lyrics.locator('.ly-credit')).toHaveText('LRCLIB');
  await expect(lyrics.locator('.w-title')).toHaveText('Synced lyrics');

  // the next song has plain lyrics only, the third none
  await page.evaluate(() => fetch('/agent/api/media/next', { method: 'POST' }));
  await expect(lyrics).toHaveClass(/plain/, { timeout: 8000 });
  await expect(lyrics.locator('.ly-note')).toHaveText('These lyrics are not timed');
  await page.evaluate(() => fetch('/agent/api/media/next', { method: 'POST' }));
  await expect(lyrics.locator('.ly-note')).toHaveText('No lyrics found for this song', { timeout: 8000 });
  expect(errors).toEqual([]);
});

test('lyrics are looked up only with the lookup option turned on', async ({ page }) => {
  await showPage(page, [{ id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 4 }]);
  expect((await page.evaluate(() => fetch('/agent/api/lyrics?widget=test%2Fc').then((r) => r.json()))).status).toBe('off');
  // a widget whose settings don't mention the option has it off (the catalog's default)
  await showPage(page, [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6 }]);
  await expect(page.locator('.w-lyrics .ly-note')).toHaveText('Turn on “Look up lyrics on LRCLIB” in this widget’s options');
  await showPage(page, [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6, options: { lookup: false } }]);
  await expect(page.locator('.w-lyrics .ly-note')).toHaveText('Turn on “Look up lyrics on LRCLIB” in this widget’s options');
  await expect(page.locator('.w-lyrics .ly-line')).toHaveCount(0);
  // a widget with the option off stays off next to one that has it on
  await showPage(page, [{ id: 'on', type: 'lyrics', x: 0, y: 0, w: 6, h: 6, options: { lookup: true } }, { id: 'off', type: 'lyrics', x: 6, y: 0, w: 6, h: 6, options: { lookup: false } }]);
  await expect(page.locator('.w-lyrics').nth(0)).toHaveClass(/timed/);
  await expect(page.locator('.w-lyrics').nth(1).locator('.ly-note')).toHaveText('Turn on “Look up lyrics on LRCLIB” in this widget’s options');
  await expect(page.locator('.w-lyrics').nth(1).locator('.ly-line')).toHaveCount(0);
});

test('the agent looks lyrics up for the widget named in the request, not for any widget', async ({ page }) => {
  await showPage(page, [{ id: 'on', type: 'lyrics', x: 0, y: 0, w: 6, h: 6, options: { lookup: true } }, { id: 'off', type: 'lyrics', x: 6, y: 0, w: 6, h: 6, options: { lookup: false } }]);
  const ask = (widget) => page.evaluate((widget) => fetch('/agent/api/lyrics' + (widget ? '?widget=' + encodeURIComponent(widget) : '')).then((r) => r.json()), widget);
  expect((await ask('test/off')).status).toBe('off');
  expect((await ask(null)).status).toBe('off');
  expect((await ask('test/on')).status).toBe('ok');
  // the phone names its own widget
  await page.evaluate(() => {
    window.__asked = [];
    const real = window.fetch;
    window.fetch = (url, ...rest) => { if (String(url).includes('/api/lyrics')) window.__asked.push(String(url)); return real(url, ...rest); };
  });
  await page.evaluate(() => fetch('/agent/api/media/next', { method: 'POST' }));
  await expect.poll(() => page.evaluate(() => window.__asked.length)).toBeGreaterThan(0);
  const asked = await page.evaluate(() => window.__asked);
  expect(asked.every((u) => u.includes('widget=' + encodeURIComponent('test/on')))).toBe(true);
});

test('a player that reports no position gets the words as a page, and a note why', async ({ page }) => {
  const errors = watchErrors(page);
  await showPage(page, [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6, options: { lookup: true } }]);
  const lyrics = page.locator('.w-lyrics');
  await expect(lyrics).toHaveClass(/timed/);
  // the player stops reporting a timeline (no length, no position)
  await page.evaluate(() => {
    const real = window.fetch;
    window.__realFetch = real;
    window.fetch = async (url, ...rest) => {
      const res = await real(url, ...rest);
      if (!String(url).includes('/api/state')) return res;
      const body = await res.clone().json();
      if (body.media) { body.media.durMs = 0; body.media.posMs = 0; }
      return new Response(JSON.stringify(body), { status: res.status, headers: { 'content-type': 'application/json' } });
    };
  });
  await expect(lyrics).toHaveClass(/plain/, { timeout: 8000 });
  await expect(lyrics.locator('.ly-note')).toHaveText('This player doesn’t report the position, so the lines can’t follow the song');
  await expect(lyrics.locator('.ly-line')).toHaveCount(20);
  await expect(lyrics.locator('.ly-line.now')).toHaveCount(0);
  // the position comes back: the lines follow the song again
  await page.evaluate(() => { window.fetch = window.__realFetch; });
  await expect(lyrics).toHaveClass(/timed/, { timeout: 8000 });
  await expect(lyrics.locator('.ly-line.now')).toHaveCount(1);
  expect(errors).toEqual([]);
});

test('the widget speaks the panel language', async ({ page }) => {
  await showPage(page, [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6, options: { lookup: false } }], { language: 'pt-BR' });
  await expect(page.locator('.w-lyrics .w-title')).toHaveText('Letras sincronizadas');
  await expect(page.locator('.w-lyrics .ly-note')).toHaveText('Ligue “Buscar letras no LRCLIB” nas opções deste widget');
  await showPage(page, [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6, options: { lookup: false } }], { language: 'vi' });
  await expect(page.locator('.w-lyrics .w-title')).toHaveText('Lời bài hát đồng bộ');
  await expect(page.locator('.w-lyrics .ly-note')).toHaveText('Bật “Tra lời bài hát trên LRCLIB” trong tùy chọn của widget này');
});

test('an answer to an older request does not replace the newer one (A → B → A)', async ({ page }) => {
  const errors = watchErrors(page);
  await showPage(page, [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6, options: { lookup: true } }]);
  const lyrics = page.locator('.w-lyrics');
  await expect(lyrics).toHaveClass(/timed/);
  // the next request is slow and ends in an error; the ones after it answer at once
  await page.evaluate(() => {
    const real = window.fetch;
    let slowOne = true;
    window.fetch = (url, ...rest) => {
      if (slowOne && String(url).includes('/api/lyrics')) {
        slowOne = false;
        return new Promise((resolve) => {
          window.__answerSlowOne = () => resolve(new Response(JSON.stringify({ status: 'error' }), { headers: { 'content-type': 'application/json' } }));
        });
      }
      return real(url, ...rest);
    };
  });
  const media = (action) => page.evaluate((action) => fetch('/agent/api/media/' + action, { method: 'POST' }), action);
  await media('next'); // song B: the slow request
  await expect.poll(() => page.evaluate(() => typeof window.__answerSlowOne)).toBe('function');
  await media('prev'); // back to A
  await expect(lyrics).toHaveClass(/timed/, { timeout: 8000 });
  await media('next'); // B again: plain lyrics, from a new request
  await expect(lyrics).toHaveClass(/plain/, { timeout: 8000 });
  await expect(lyrics.locator('.ly-note')).toHaveText('These lyrics are not timed');
  // the first request for B answers late, with an error: it is ignored
  await page.evaluate(() => window.__answerSlowOne());
  await page.waitForTimeout(500);
  await expect(lyrics).toHaveClass(/plain/);
  await expect(lyrics.locator('.ly-note')).toHaveText('These lyrics are not timed');
  expect(errors).toEqual([]);
});

test('nothing the agent can look up reads as nothing playing, never as empty lyrics', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'l', type: 'lyrics', x: 0, y: 0, w: 12, h: 6, options: { lookup: true } }] }] }));
  });
  await page.reload();
  const lyrics = page.locator('.w-lyrics');
  await expect(lyrics).toHaveClass(/timed/);
  // the agent answers "idle" (a title it can't look up), then a status this panel doesn't know
  await page.evaluate(() => {
    const real = window.fetch;
    window.__lyricsAnswer = { status: 'idle' };
    window.fetch = (url, ...rest) => String(url).includes('/api/lyrics')
      ? Promise.resolve(new Response(JSON.stringify(window.__lyricsAnswer), { headers: { 'content-type': 'application/json' } }))
      : real(url, ...rest);
  });
  await page.evaluate(() => fetch('/agent/api/media/next', { method: 'POST' }));
  await expect(lyrics.locator('.ly-note')).toHaveText('Nothing playing', { timeout: 8000 });
  await expect(lyrics.locator('.ly-line')).toHaveCount(0);
  await expect(lyrics).not.toContainText('undefined');
  await page.evaluate(() => { window.__lyricsAnswer = { status: 'later' }; });
  await page.evaluate(() => fetch('/agent/api/media/next', { method: 'POST' }));
  await expect(lyrics.locator('.ly-note')).toHaveText('Lyrics are unavailable right now', { timeout: 8000 });
  await expect(lyrics).not.toContainText('undefined');
  expect(errors).toEqual([]);
});
