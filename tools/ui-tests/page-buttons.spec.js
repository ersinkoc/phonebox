// Page buttons widget: one button per page (or the ticked ones), the current page highlighted, a tap goes there.
const { test, expect } = require('@playwright/test');

const page = (id, title, icon, widgets = []) => ({ id, title, icon, widgets });
const buttons = (ids) => ({ id: 'pb', type: 'pagebuttons', x: 0, y: 0, w: 12, h: 2, options: ids ? { ids } : {} });
const note = (id) => ({ id: 'n' + id, type: 'note', x: 0, y: 2, w: 12, h: 4, options: { text: id } });

async function show(p, pages) {
  await p.goto('/panel/style.css');
  await p.evaluate((pages) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], pages,
      display: { idleReturnSec: 0, minDwellSec: 0, switchCountdownSec: 0 },
    }));
  }, pages);
  await p.goto('/panel/');
}
const activeDot = (p) => p.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')));

test('a page button opens its page and the current one is highlighted', async ({ page: p }) => {
  await show(p, [
    page('home', 'Home', '🏠', [buttons(), note('home')]),
    page('music', 'Music', '🎵', [note('music')]),
    page('stats', 'Stats', '', [note('stats')]),
    page('games', 'Games', '🎮', [note('games')]),
  ]);
  const b = p.locator('.pgb-b');
  await expect(b).toHaveCount(4);
  await expect(b.nth(0)).toHaveClass(/\bon\b/);
  await expect(b.nth(2)).toHaveText('Stats');
  await expect(b.nth(3)).toHaveAttribute('aria-label', '4 · Games');
  await b.nth(3).click();
  await expect.poll(() => activeDot(p)).toBe(3);
});

test('only the ticked pages get a button; if none of them is left, every page does', async ({ page: p }) => {
  await show(p, [
    page('home', 'Home', '🏠', [buttons(['music', 'games'])]),
    page('music', 'Music', '🎵'),
    page('games', 'Games', '🎮'),
    page('other', 'Other', ''),
  ]);
  await expect(p.locator('.pgb-b')).toHaveCount(2);
  await expect(p.locator('.pgb-b').nth(1)).toHaveAttribute('aria-label', '3 · Games');

  await show(p, [page('home', 'Home', '', [buttons(['gone'])]), page('two', 'Two', '')]);
  await expect(p.locator('.pgb-b')).toHaveCount(2);
});

test('narrow buttons keep only their icon; a page without an icon keeps its name', async ({ page: p }) => {
  await p.setViewportSize({ width: 800, height: 400 });
  await show(p, [
    page('home', 'Home', '🏠', [{ id: 'pb', type: 'pagebuttons', x: 0, y: 0, w: 2, h: 2, options: {} }]),
    page('music', 'Music', '🎵'), page('stats', 'Stats', ''), page('games', 'Games', '🎮'),
    page('ai', 'AI', '🤖'), page('cal', 'Agenda', '📅'),
  ]);
  await expect(p.locator('.pgb-b')).toHaveCount(6);
  await expect(p.locator('.pgb-b').nth(1).locator('.pgb-t')).toBeHidden();
  await expect(p.locator('.pgb-b').nth(2).locator('.pgb-t')).toBeVisible();
});
