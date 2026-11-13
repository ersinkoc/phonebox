const { test, expect } = require('@playwright/test');

async function layout(page, widgets, extra = {}) {
  await page.goto('/panel/');
  await page.evaluate(({ widgets, extra }) => {
    localStorage.clear();
    // Widget interaction tests must not depend on the host's wall-clock night schedule.
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, ...extra, pages: [{ id: 'test', title: 'Test', enabled: true, widgets }], shortcuts: [] }));
  }, { widgets, extra });
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/);
}
const widget = (type, options = {}) => ({ id: 'test', type, x: 0, y: 0, w: 12, h: 6, options });

for (const viewport of [{ width: 780, height: 360 }, { width: 360, height: 780 }]) {
  test(`stack renders children, changes cards and follows live data at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    const errors = []; page.on('pageerror', e => errors.push(String(e)));
    await layout(page, [widget('stack', { widgets: [{ type: 'clock' }, { type: 'stat', options: { metric: 'cpu' } }, { type: 'weather', enabled: false }] })]);
    await expect(page.locator('.stack-dot')).toHaveCount(2);
    await expect(page.locator('.stack-item.active .w-clock')).toHaveCount(1);
    await page.getByRole('button', { name: 'stat', exact: true }).click();
    await expect(page.locator('.stack-item.active .w-stat')).toHaveCount(1);
    await expect(page.locator('.stack-item.active .t-label')).toHaveText('CPU');
    await expect(page.locator('.stack-item.active .t-val')).not.toContainText('—');
    await page.locator('.stack-item.active').evaluate(el => Promise.all(el.getAnimations().map(a => a.finished)));
    await page.screenshot({ path: `test-results/stack-${viewport.width}.png` });
    expect(errors).toEqual([]);
  });
}

test('stack rotates with the panel clock and an empty stack stays empty', async ({ page }) => {
  await page.clock.install();
  await layout(page, [widget('stack', { interval: 3, widgets: [{ type: 'clock' }, { type: 'weather' }] })]);
  await page.clock.fastForward(4000);
  await expect(page.locator('.stack-item.active .w-weather')).toHaveCount(1);
  await layout(page, [widget('stack', { widgets: [] })]);
  await expect(page.locator('.w-stack')).toContainText('no active widgets');
  await expect(page.locator('.stack-item')).toHaveCount(0);
});

test('quota selection preserves empty selections and explains missing data', async ({ page }) => {
  await layout(page, [widget('quotas', { providers: [] })]);
  await expect(page.locator('.w-quotas')).toContainText('No quota providers enabled');
  await layout(page, [widget('quotas', { providers: ['antigravity'] })]);
  await expect(page.locator('.w-quotas')).toContainText('Quota data is unavailable');
});

test('session sheet shows counters and keeps prompt content out of the panel', async ({ page }) => {
  await layout(page, [widget('sessions')]);
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    D.work = { sessions: [{ id: 'codex:test', tool: 'codex', name: 'Test session', project: 'project', status: 'busy', since: Date.now(), model: 'test-model', effort: 'high', totalTokens: 1234, inputTokens: 1000, outputTokens: 234, lastTool: 'read' }] };
    emit('work');
  });
  await page.getByRole('button', { name: /project/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('test-model');
  await expect(dialog).toContainText('1.2k');
  await expect(dialog).toContainText('read');
  await page.getByRole('button', { name: 'Close session details' }).click();
  await expect(dialog).toHaveCount(0);
});

test('uncontrollable volume shows the reading with disabled controls', async ({ page }) => {
  await layout(page, [widget('volume')]);
  await page.evaluate(async () => {
    // every later state poll reports the same, so the check does not race the next poll
    const real = window.fetch;
    window.fetch = async (...args) => {
      const r = await real(...args);
      if (!String(args[0]).includes('/api/state')) return r;
      const state = await r.json(); state.audio = { volume: 42, muted: false, canControl: false };
      return new Response(JSON.stringify(state), { headers: { 'Content-Type': 'application/json' } });
    };
    const { D, emit } = await import('/panel/main.js');
    D.state.audio = { volume: 42, muted: false, canControl: false };
    emit('state');
  });
  await expect(page.locator('.w-volume')).toContainText('42%');
  for (const button of await page.locator('.w-volume button').all()) await expect(button).toBeDisabled();
});

test('stack editor changes child options, enabled state and ordering', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('stack', { widgets: [{ type: 'clock' }, { type: 'weather' }] })]);
  await page.goto('/settings/');
  await page.locator('#canvas [title="Smart Stack"]').click();
  await expect(page.locator('.stack-row')).toHaveCount(2);
  await page.locator('.stack-row').first().getByTitle('Move down').click();
  await expect(page.locator('.stack-row-name').first()).toContainText('Weather');
  await page.locator('.stack-row').first().locator('input[type=checkbox]').uncheck();
  await expect(page.locator('.stack-row').first()).toHaveClass(/off/);
  await page.locator('.stack-row-name').last().click();
  await expect(page.locator('.stack-sub-config')).toBeVisible();
  await expect(page.locator('#stackQuickAdd option[value=command]')).toHaveCount(0);
});

test('two counters in a stack keep separate values after reload', async ({ page }) => {
  await layout(page, [widget('stack', { widgets: [{ type: 'counter' }, { type: 'counter' }] })]);
  await page.locator('.stack-item.active').getByRole('button', { name: '+', exact: true }).click();
  await page.locator('.stack-dot').nth(1).click();
  await page.locator('.stack-item.active').getByRole('button', { name: '+', exact: true }).click({ clickCount: 2 });
  await page.reload();
  await expect(page.locator('.stack-item.active .ct-n')).toHaveText('1');
  await page.locator('.stack-dot').nth(1).click();
  await expect(page.locator('.stack-item.active .ct-n')).toHaveText('2');
});

test('multi option preserves saved unlisted quota provider IDs when toggling choices', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('quotas', { providers: ['claude', 'alibaba'] })]);
  await page.goto('/settings/');
  await page.locator('#canvas [title="AI quotas"]').click();
  const codexCheckbox = page.locator('#inspector label.check', { hasText: 'Codex' }).locator('input[type="checkbox"]');
  await expect(codexCheckbox).toBeVisible();
  await codexCheckbox.check();
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.providers);
  expect(saved).toContain('claude');
  expect(saved).toContain('codex');
  expect(saved).toContain('alibaba');
});
