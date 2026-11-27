const { test, expect } = require('@playwright/test');

async function editTheme(page) {
  await page.goto('/settings/#general');
  const theme = page.locator('[data-bind="display.theme"]');
  await expect(theme).toBeVisible();
  const draft = (await theme.inputValue()) === 'graphite' ? 'warm' : 'graphite';
  await theme.selectOption(draft);
  await expect(page.locator('#save')).toBeEnabled();
  return draft;
}

async function saveWithFailedRequest(page, status) {
  return page.evaluate(async status => {
    const original = window.fetch;
    window.fetch = (url, options) => {
      if (String(url) !== '/api/settings' || options?.method !== 'PUT') return original(url, options);
      if (status === null) return Promise.reject(new TypeError('fixture connection lost'));
      return Promise.resolve(Response.json({ error: 'Settings changed elsewhere' }, { status }));
    };
    try {
      const result = await document.getElementById('save').onclick();
      return { result, toast: document.getElementById('toast').textContent };
    } catch (error) {
      return { result: String(error), toast: document.getElementById('toast').textContent };
    } finally {
      window.fetch = original;
    }
  }, status);
}

test('a hook button asks to save first while the settings have unsaved changes', async ({ page }) => {
  await page.goto('/settings/#general');
  const wsl = page.locator('[data-bind="cursor.watchWsl"]');
  await expect(wsl).toBeAttached();
  await wsl.evaluate(input => { input.checked = !input.checked; input.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect(page.locator('#save')).toBeEnabled();
  const sent = await page.evaluate(async () => {
    const original = window.fetch, requests = [];
    window.fetch = (url, options) => {
      if (/\/api\/(cursor|claude|codex)\/hook$/.test(String(url))) { requests.push(`${options?.method || 'GET'} ${url}`); return Promise.resolve(Response.json({})); }
      return original(url, options);
    };
    try {
      for (const name of ['cursor', 'claude', 'codex']) await document.getElementById(`${name}HookBtn`).onclick();
    } finally { window.fetch = original; }
    return requests;
  });
  expect(sent, 'the agent would install with the saved "Also watch Cursor in WSL", not the one on the page').toEqual([]);
  await expect(page.locator('#toast')).toHaveText('Save your changes first');
});

test('a settings transport failure reports the error and keeps the draft', async ({ page }) => {
  const draft = await editTheme(page);
  const conflict = await saveWithFailedRequest(page, 409);
  expect(conflict).toEqual({ result: false, toast: 'Settings changed elsewhere' });
  await page.locator('#toast').evaluate(element => { element.textContent = ''; });
  const failure = await saveWithFailedRequest(page, null);
  expect(failure, `CONTROL: HTTP conflict displayed. EXPECTED: error toast and false | ACTUAL: ${JSON.stringify(failure)}`)
    .toEqual({ result: false, toast: 'Could not save' });
  await expect(page.locator('#toast')).toHaveClass(/\berr\b/);
  await expect(page.locator('[data-bind="display.theme"]')).toHaveValue(draft);
  await expect(page.locator('#save')).toBeEnabled();
});

test('a settings save can be retried after the connection recovers', async ({ page }) => {
  const draft = await editTheme(page);
  expect((await saveWithFailedRequest(page, null)).result).toBe(false);
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  await expect(page.locator('[data-bind="display.theme"]')).toHaveValue(draft);
  await expect(page.locator('#toast')).toHaveText(/^Saved/);
});

test('a partial settings load and repeated Revert leave the form clean', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => localStorage.setItem('pb-demo-config', JSON.stringify({
    display: { language: 'en', theme: 'default' },
    pages: [],
  })));
  await page.goto('/settings/#general');
  await expect(page.locator('#displayLangSel')).toHaveValue('en');
  const save = page.locator('#save');
  const initialClean = await save.isDisabled();
  const theme = page.locator('[data-bind="display.theme"]');
  await theme.selectOption('graphite');
  await expect(save).toBeEnabled();
  await page.locator('#revert').click();
  await expect(theme).toHaveValue('default');
  const revertedClean = await save.isDisabled();
  expect({ initialClean, revertedClean },
    `CONTROL: theme changed and reverted. EXPECTED: clean on load and revert | ACTUAL: load=${initialClean}, revert=${revertedClean}`)
    .toEqual({ initialClean: true, revertedClean: true });
  await page.locator('#displayLangSel').selectOption('tr');
  await expect(save).toBeEnabled();
  await page.locator('#revert').click();
  await expect(page.locator('#displayLangSel')).toHaveValue('en');
  await expect(save).toBeDisabled();
});

test('pressing Save twice while a save runs sends one request and keeps the busy state until it ends', async ({ page }) => {
  await editTheme(page);
  const outcome = await page.evaluate(async () => {
    const original = window.fetch;
    let puts = 0;
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    window.fetch = async (url, options) => {
      if (String(url) !== '/api/settings' || options?.method !== 'PUT') return original(url, options);
      puts++;
      await gate;
      return Response.json({ error: 'Settings changed elsewhere' }, { status: 409 });
    };
    const btn = document.getElementById('save');
    try {
      const first = btn.onclick();
      const second = btn.onclick();
      const busyWhileRunning = btn.classList.contains('busy');
      release();
      const results = await Promise.all([first, second]);
      return { puts, busyWhileRunning, busyAfter: btn.classList.contains('busy'), results };
    } finally {
      window.fetch = original;
    }
  });
  expect(outcome).toEqual({ puts: 1, busyWhileRunning: true, busyAfter: false, results: [false, false] });
});

test('a section introduction is measured again after the language changes', async ({ page }) => {
  await page.goto('/settings/#pages');
  const lead = page.locator('#pages > .lead');
  await expect(lead).toHaveClass(/\blong\b/);
  // a short text marks it as not expandable; switching the language puts the full text back
  await lead.evaluate(el => { el.textContent = 'Short.'; fitLeads(); });
  await expect(lead).not.toHaveClass(/\blong\b/);
  await page.evaluate(() => applyLanguageUi('en'));
  await expect(lead).toHaveClass(/\blong\b/);
  await expect(lead).toHaveAttribute('role', 'button');
});

// An option that does nothing while another is off is not offered: the Trackpad's scroll ticks follow its haptic feedback.
test('a widget option that needs another is shown only while that one is on', async ({ page }) => {
  await page.goto('/settings/#pages');
  await page.waitForFunction(() => cfg && cfg.pages); // the settings are loaded
  await page.evaluate(() => {
    const p = curPage();
    p.widgets.push({ id: 'tp-test', type: 'trackpad', x: 0, y: 0, w: 6, h: 6, options: { control: true } });
    selWidget = p.widgets.length - 1;
    renderInspector();
  });
  const inspector = page.locator('#inspector');
  const ticks = inspector.getByText('Tick while scrolling');
  const haptics = inspector.locator('.field', { hasText: 'Haptic feedback' }).locator('select');
  const inertial = inspector.locator('label.check', { hasText: 'Inertial scrolling' }).locator('input[type="checkbox"]');
  const doubleTap = inspector.locator('label.check', { hasText: 'Double-tap to drag' }).locator('input[type="checkbox"]');
  const holdInput = inspector.locator('.field', { hasText: /Drag hold duration|Double-tap interval/ }).locator('label');
  // the Trackpad's options come in groups, each under its own heading, with the switch that allows control first
  await expect(inspector.locator('h4.opt-section')).toHaveText(['Pointer', 'Scrolling', 'Haptics']);
  expect(await inspector.locator('h4.opt-section, .field label, label.check').allTextContents().then((t) => t.slice(0, 6).map((x) => x.trim())))
    .toEqual(['Allow controlling the mouse from the phone', 'Pointer', 'Pointer sensitivity', 'Pointer acceleration', 'Double-tap to drag', 'Drag hold duration (ms)']);
  await doubleTap.check();
  await expect(holdInput).toHaveText('Double-tap interval (ms)');
  await doubleTap.uncheck();
  await expect(holdInput).toHaveText('Drag hold duration (ms)');
  await expect(haptics).toHaveValue('normal');
  await expect(inertial).toBeChecked();
  await expect(ticks).toBeVisible();
  await haptics.selectOption('off');
  await expect(ticks).toHaveCount(0);
  await haptics.selectOption('light');
  await expect(ticks).toBeVisible();
});

// The Smart Stack's own settings for a card draw the same groups and hide the same options as the widget's.
test('the settings of a Trackpad inside a Smart Stack have its groups and hide the scroll tick with haptics off', async ({ page }) => {
  await page.goto('/settings/#pages');
  await page.waitForFunction(() => cfg && cfg.pages);
  await page.evaluate(() => {
    const p = curPage();
    p.widgets.push({ id: 'stack-test', type: 'stack', x: 0, y: 0, w: 6, h: 6, options: { widgets: [{ type: 'trackpad', options: { control: true, haptics: 'off' } }] } });
    selWidget = p.widgets.length - 1;
    activeStackSubIdx = 0;
    renderInspector();
  });
  const config = page.locator('#inspector .stack-sub-config');
  await expect(config.locator('h4.opt-section')).toHaveText(['Pointer', 'Scrolling', 'Haptics']);
  await expect(config.getByText('Drag hold duration (ms)')).toBeVisible();
  await config.locator('label.check', { hasText: 'Double-tap to drag' }).locator('input[type="checkbox"]').check();
  await expect(config.getByText('Double-tap interval (ms)')).toBeVisible();
  await expect(config.getByText('Haptic feedback')).toBeVisible();
  await expect(config.getByText('Tick while scrolling')).toHaveCount(0);
});
