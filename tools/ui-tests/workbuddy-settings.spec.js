const { test, expect } = require('@playwright/test');

test('WorkBuddy session and phone approval sharing are off by default', async ({ page }) => {
  await page.goto('/settings/#general');
  await expect(page.locator('#workbuddyHookBtn')).toBeVisible();
  await expect(page.locator('#workbuddyHookState')).toHaveText('Not installed');
  await expect(page.locator('[data-bind="workBuddy.sessions"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="workBuddy.phoneApprovals"]')).not.toBeChecked();
});
