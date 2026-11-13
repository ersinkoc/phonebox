const { test, expect } = require('@playwright/test');

function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const url = m.location()?.url || '';
    if (/Failed to load resource/.test(m.text()) && url && !url.startsWith('http://127.0.0.1:8781')) return;
    errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

async function openPlugins(page) {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#plugins');
  await expect(page.locator('section.on h2')).toHaveText('Plugins');
  await expect(page.locator('#plMarketList .pl-card')).toHaveCount(3);
}

async function installFromMarketplace(page, pluginName) {
  const card = page.locator('#plMarketList .pl-card', { hasText: pluginName });
  await card.getByRole('button', { name: 'Install', exact: true }).click();
  const modal = page.locator('#modal');
  await expect(modal).toBeVisible();
  await expect(modal.locator('.pl-perm').first()).toBeVisible();
  await modal.getByRole('button', { name: 'Approve & install' }).click();
  await expect(modal).toBeHidden();
  await expect(page.locator('#plInstalled .pl-item', { hasText: pluginName })).toBeVisible();
}

async function addWidgetOnFirstPageAndSave(page, widgetName, disableControl = false) {
  await page.locator('#nav a', { hasText: 'Pages' }).click();
  await expect(page.locator('section.on h2')).toHaveText('Pages');
  await page.locator('#pageTabs .ptab').first().click();
  await page.locator('#peHead').getByRole('button', { name: 'Clear', exact: true }).click();
  const pal = page.locator('#palette .pal', { hasText: widgetName });
  await expect(pal).toBeVisible();
  await pal.click();
  const controlBox = page.locator('#inspector').getByLabel('Allow phone controls');
  await expect(controlBox).toBeChecked();
  if (disableControl) {
    await controlBox.uncheck();
    await expect(controlBox).not.toBeChecked();
  }
  await page.locator('#save').click();
  await expect(page.locator('#toast')).toContainText('Saved');
}

test.beforeEach(async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => { try { localStorage.clear(); } catch (_) {} });
});

test('marketplace installs Google Meet after permission approval and renders working controls on the panel', async ({ page }) => {
  const errors = watchErrors(page);
  await openPlugins(page);
  await expect(page.locator('input[data-bind="plugins.repo"]')).toHaveValue('ersinkoc/phonebox-plugins');
  await expect(page.locator('#plMarketState')).toContainText('3 available');

  await installFromMarketplace(page, 'Google Meet');
  await addWidgetOnFirstPageAndSave(page, 'Google Meet');

  await page.goto('/panel/');
  const widget = page.locator('.w-plugin').first();
  await expect(widget).toBeVisible();
  const muteBtn = widget.getByRole('button', { name: /\bMute$/ });
  await expect(muteBtn).toBeVisible();
  await muteBtn.click();

  const unmuteBtn = widget.getByRole('button', { name: /\bUnmute$/ });
  await expect(unmuteBtn).toBeVisible();
  await expect(unmuteBtn).toHaveAttribute('aria-pressed', 'true');
  expect(errors).toEqual([]);
});

test('disabling Allow phone controls blocks plugin actions on the panel', async ({ page }) => {
  const errors = watchErrors(page);
  await openPlugins(page);
  await installFromMarketplace(page, 'Google Meet');
  await addWidgetOnFirstPageAndSave(page, 'Google Meet', true);

  await page.goto('/panel/');
  const widget = page.locator('.w-plugin').first();
  await expect(widget).toBeVisible();
  const muteBtn = widget.getByRole('button', { name: /\bMute$/ });
  await muteBtn.click();

  await expect(page.locator('#toast')).toContainText('Phone control is turned off in widget settings');
  await expect(widget.getByRole('button', { name: /\bMute$/ })).toHaveAttribute('aria-pressed', 'false');
  expect(errors).toEqual([]);
});

test('plugin secret settings are preserved across partial updates and cleared on uninstall', async ({ page }) => {
  const errors = watchErrors(page);
  await openPlugins(page);
  await installFromMarketplace(page, 'Gitea');

  const giteaItem = page.locator('#plInstalled .pl-item', { hasText: 'Gitea' });
  await giteaItem.getByRole('button', { name: 'Settings', exact: true }).click();
  const modal = page.locator('#modalBody');
  await expect(modal).toBeVisible();
  await modal.locator('input[type="text"]').fill('https://git.internal.test');
  await modal.locator('input[type="password"]').fill('super-secret-token');
  await modal.getByRole('button', { name: 'Save plugin settings' }).click();
  await expect(page.locator('#modal')).toBeHidden();

  let overview = await page.evaluate(async () => (await fetch('/api/plugins')).json());
  let gitea = overview.installed.find((x) => x.id === 'gitea');
  expect(gitea.secretKeysSet).toContain('token');
  expect(gitea.settings.baseUrl).toBe('https://git.internal.test');
  expect(gitea.settings.token).toBeUndefined();

  await giteaItem.getByRole('button', { name: 'Settings', exact: true }).click();
  const pw = modal.locator('input[type="password"]');
  await expect(pw).toHaveAttribute('placeholder', 'Saved — leave blank to keep, or enter new');
  await expect(modal.getByRole('button', { name: 'Clear', exact: true })).toBeVisible();
  await modal.locator('input[type="text"]').fill('https://git2.internal.test');
  await modal.getByRole('button', { name: 'Save plugin settings' }).click();
  await expect(page.locator('#modal')).toBeHidden();

  overview = await page.evaluate(async () => (await fetch('/api/plugins')).json());
  gitea = overview.installed.find((x) => x.id === 'gitea');
  expect(gitea.secretKeysSet).toContain('token');
  expect(gitea.settings.baseUrl).toBe('https://git2.internal.test');
  expect(gitea.settings.token).toBeUndefined();

  page.once('dialog', (d) => d.accept());
  await giteaItem.getByRole('button', { name: 'Uninstall', exact: true }).click();
  await expect(page.locator('#plInstalled .pl-item')).toHaveCount(0);
  expect(errors).toEqual([]);
});
