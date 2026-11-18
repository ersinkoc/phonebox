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
