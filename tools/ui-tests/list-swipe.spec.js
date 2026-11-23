// A sideways swipe that starts on a scrolling list (News feeds, a pushed list, Notifications, the checklist) turns the
// page like a swipe anywhere else (#445). The pager is a native horizontal scroller, so the lists must allow pan-x:
// with touch-action: pan-y the browser kept the sideways move from reaching it. A real touch swipe can't be driven
// reliably in a desktop browser, so this checks what decides it, the lists' touch-action.
const { test, expect } = require('@playwright/test');

test('scrolling lists let a sideways swipe through to the pager', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [],
    pages: [{ id: 'one', title: 'One', widgets: [
      { id: 'f', type: 'feeds', x: 0, y: 0, w: 6, h: 3 },
      { id: 'n', type: 'inbox', x: 6, y: 0, w: 6, h: 3 },
      { id: 'c', type: 'checklist', x: 0, y: 3, w: 6, h: 3 },
    ] }, { id: 'two', title: 'Two', widgets: [] }] })));
  await page.goto('/panel/');
  for (const sel of ['.fd-list', '.inbox', '.cl-list']) {
    const list = page.locator(`[data-id="one"] ${sel}`).first();
    await expect(list).toBeAttached();
    const ta = await list.evaluate((e) => getComputedStyle(e).touchAction);
    expect(ta, sel).toMatch(/pan-x/);
    expect(ta, sel).toMatch(/pan-y/);
  }
});
