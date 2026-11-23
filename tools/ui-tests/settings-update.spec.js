const { test, expect } = require('@playwright/test');

// A waiting update shows in the settings page's header, seen from every section: the Updates card is far down
// General, so an update used to go unnoticed. The button runs the same update as the card's "Update now".
async function open(page, status, installed = true) {
  await page.goto('/settings/#shortcuts');
  await expect(page.locator('#save')).toBeVisible();
  await page.evaluate(([st, installed]) => {
    const original = window.fetch;
    window.applied = 0;
    window.updateStatus = st;
    window.fetch = (url, options) => {
      const path = String(url);
      if (path === '/api/update') return Promise.resolve(Response.json({ status: window.updateStatus, auto: true, installed }));
      if (path === '/api/update/apply') { window.applied++; return Promise.resolve(Response.json({ ok: true })); }
      return original(url, options);
    };
    return loadInfo();
  }, [status, installed]);
  return page.locator('#updTop');
}
const status = (over) => ({ agentVersion: '0.9.22', appVersion: '0.9.22', latest: '0.9.23', agentUpdate: false, appUpdate: false,
  lastCheck: Date.now(), state: 'idle', error: null, ...over });

test('no update, no button in the header', async ({ page }) => {
  const top = await open(page, status({ latest: '0.9.22' }));
  await expect(top).toBeHidden();
});

test('an update shows in the header on every section, and the button installs it', async ({ page }) => {
  const top = await open(page, status({ agentUpdate: true, appUpdate: true }));
  await expect(top).toBeVisible();
  await expect(top).toHaveText('Update to 0.9.23');
  await expect(top).toHaveAttribute('title', /0\.9\.22 → 0\.9\.23/);
  await page.goto('/settings/#webhooks'); // same document: the header stays
  await expect(top).toBeVisible();
  await top.click();
  await expect.poll(() => page.evaluate(() => window.applied)).toBe(1);
});

test('the header button waits for unsaved changes, since the agent restarts', async ({ page }) => {
  const top = await open(page, status({ agentUpdate: true }));
  await page.goto('/settings/#general');
  const theme = page.locator('[data-bind="display.theme"]');
  await theme.selectOption((await theme.inputValue()) === 'graphite' ? 'warm' : 'graphite');
  await expect(page.locator('#save')).toBeEnabled();
  await top.click();
  await expect(page.locator('#toast')).toContainText('Save your changes first');
  expect(await page.evaluate(() => window.applied)).toBe(0);
});

test('while the update runs the button says so and cannot be pressed again', async ({ page }) => {
  const top = await open(page, status({ agentUpdate: true, state: 'updating agent' }));
  await expect(top).toHaveText('Updating…');
  await expect(top).toBeDisabled();
});

test('a copy outside the installed folder gets no update button for itself, only for the phone app', async ({ page }) => {
  // it never updates itself (#497): a button would do nothing; the Updates card says why instead
  const top = await open(page, status({ agentUpdate: true }), false);
  await expect(top).toBeHidden();
  await expect(page.locator('#updApply')).toBeHidden();
  await expect(page.locator('#updInfo')).toContainText('not the installed copy');
  await page.evaluate(() => { window.updateStatus = { ...window.updateStatus, appUpdate: true }; return loadInfo(); });
  await expect(top).toBeVisible();
  await expect(top).not.toHaveAttribute('title', /Agent:/);
});
