const { test, expect } = require('@playwright/test');
const { bodyTimeout } = require('./api-timeout');

for (const json of [true, false]) {
  test(`API deadline includes the ${json ? 'JSON' : 'text'} response body`, async ({ page }) => {
    expect(await bodyTimeout(page, { json })).toMatchObject({ bodyStarted: true, settled: true, aborted: true, error: 'AbortError' });
  });
}

test('a completed or rejected body clears its API deadline', async ({ page }) => {
  expect(await bodyTimeout(page, { mode: 'ok' })).toMatchObject({ settled: true, aborted: false, result: { ok: true } });
  expect(await bodyTimeout(page, { mode: 'error' })).toMatchObject({ settled: true, aborted: false, error: 'Error' });
});
