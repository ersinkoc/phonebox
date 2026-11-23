// Swiping left on the last page wraps to the first, and right on the first wraps to the last: wherever an ordinary
// swipe would turn the page, a key included, and not where a control keeps sideways moves for itself (#487).
const { test, expect } = require('@playwright/test');

const SHORTCUTS = [{ id: 'k', label: 'Key', icon: '🔘', color: '#6b7280', cmd: 'builtin:keys' }];

async function open(page) {
  await page.setViewportSize({ width: 800, height: 400 });
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS, display: { idleReturnSec: 0 },
    pages: [
      { id: 'home', title: 'Home', widgets: [{ id: 'n', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Home' } }] },
      { id: 'mid', title: 'Mid', widgets: [{ id: 'm', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Mid' } }] },
      { id: 'keys', title: 'Keys', widgets: [{ id: 'b', type: 'button', x: 0, y: 0, w: 8, h: 6, options: { id: 'k' } }] },
    ],
  }));
  await page.goto('/panel/');
  await expect(page.locator('body')).not.toHaveClass(/night/);
  await page.evaluate(() => window.onAndroidKey('prev')); // Home → the last page
  await expect.poll(() => active(page)).toBe(2);
  return page.locator('[data-id="keys"] .w-button .key');
}
const active = (page) => page.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')));
// a quick swipe 160 px to the left, as touch events (the wrap listens for them on the pager)
const swipeLeft = (el) => el.evaluate((node) => {
  const mk = (type, x) => {
    const e = new Event(type, { bubbles: true, cancelable: true });
    const touch = { clientX: x, clientY: 200 };
    e.touches = type === 'touchstart' ? [touch] : [];
    e.changedTouches = [touch];
    return e;
  };
  node.dispatchEvent(mk('touchstart', 400));
  node.dispatchEvent(mk('touchend', 240));
});

test('a swipe that starts on a key wraps from the last page to the first', async ({ page }) => {
  const key = await open(page);
  await swipeLeft(key);
  await expect.poll(() => active(page)).toBe(0);
});

test('a swipe that starts on something that keeps sideways moves does not wrap', async ({ page }) => {
  const key = await open(page);
  await key.evaluate((el) => { el.style.touchAction = 'none'; });
  await swipeLeft(key);
  await page.waitForTimeout(300);
  expect(await active(page)).toBe(2);
});
