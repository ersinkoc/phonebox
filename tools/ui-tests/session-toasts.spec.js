const { test, expect } = require('@playwright/test');

// A session that finishes shows a "finished" toast unless Settings › Display turns it off (#275). The "needs input" toast
// always shows: it asks the user to do something.
async function load(page, display) {
  await page.addInitScript((d) => {
    const config = JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], display: d, pages: [{ id: 'p', title: 'P', widgets: [] }] });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  }, display);
  await page.goto('/panel/');
  await page.evaluate(() => import('/panel/main.js'));
}
// moves one made-up session through the given states, as the work poll would
const run = (page, ...states) => page.evaluate(async (list) => {
  const { notifyTransitions } = await import('/panel/main.js');
  document.getElementById('toast').className = 'toast';
  for (const status of list) notifyTransitions([{ id: 'claude:toast-test', name: 'Build', status, waitingFor: status === 'waiting' ? 'permission prompt' : null }]);
  const t = document.getElementById('toast');
  return { on: t.classList.contains('on'), text: t.textContent };
}, states);

test('a finished session shows a toast by default', async ({ page }) => {
  await load(page, { vibrate: true });
  const r = await run(page, 'busy', 'idle');
  expect(r.on).toBe(true);
  expect(r.text).toContain('Build finished');
});

test('the finished toast can be turned off, and the needs-input toast still shows', async ({ page }) => {
  await load(page, { vibrate: true, finishToast: false });
  expect((await run(page, 'busy', 'idle')).on, 'EXPECTED: no toast when a session finishes').toBe(false);
  const r = await run(page, 'waiting');
  expect(r.on).toBe(true);
  expect(r.text).toContain('permission prompt');
});
