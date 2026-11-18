const { expect } = require('@playwright/test');

async function bodyTimeout(page, { json = true, mode = 'pending' } = {}) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
  await page.evaluate(async ({ json, mode }) => {
    const m = await import('/panel/main.js');
    const original = window.fetch;
    const f = { bodyStarted: false, settled: false, aborted: false, result: null, error: null };
    window.__apiTimeout = f;
    window.fetch = async (url, opts) => {
      if (!String(url).endsWith('/audit-body')) return original(url, opts);
      f.signal = opts.signal;
      const body = () => {
        f.bodyStarted = true;
        if (mode === 'ok') return Promise.resolve(json ? { ok: true } : 'ok');
        if (mode === 'error') return Promise.reject(new Error('body failed'));
        return new Promise((resolve, reject) => {
          f.release = () => resolve(json ? { ok: true } : 'ok');
          opts.signal.addEventListener('abort', () => { f.aborted = true; reject(new DOMException('body aborted', 'AbortError')); }, { once: true });
        });
      };
      return { ok: true, headers: new Headers({ 'Content-Type': json ? 'application/json' : 'text/plain' }), json: body, text: body };
    };
    f.pending = m.api('/audit-body', 'GET', 1000).then(result => { f.result = result; }, error => { f.error = error.name; })
      .finally(() => { f.settled = true; });
    window.fetch = original;
    await Promise.resolve();
    await Promise.resolve();
  }, { json, mode });
  await page.clock.runFor(1500);
  return page.evaluate(async () => {
    const f = window.__apiTimeout;
    const result = { bodyStarted: f.bodyStarted, settled: f.settled, aborted: f.signal.aborted, error: f.error, result: f.result };
    f.release?.();
    await f.pending;
    delete window.__apiTimeout;
    return result;
  });
}

module.exports = { bodyTimeout };
