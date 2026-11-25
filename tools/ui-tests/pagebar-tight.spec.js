const { test, expect } = require('@playwright/test');

// 頁面很多的橫向選單列比螢幕寬時，只有目前頁面保留名稱（.pb-tight）；寬螢幕與側邊欄不受影響。
// A bottom page bar wider than the screen keeps a name only on the current page; wide screens and the side bar are untouched.
const DEFAULT_PAGES = Array.from({ length: 11 }, (_, i) => ({ id: 'p' + i, title: 'Page ' + (i + 1), icon: '★', widgets: [] }));

async function load(page, size, display = {}, pages = DEFAULT_PAGES) {
  await page.setViewportSize(size);
  await page.goto('/panel/style.css');
  await page.evaluate(([pages, display]) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], pages,
      display: { idleReturnSec: 0, pageBar: 'bar', ...display } }));
  }, [pages, display]);
  await page.goto('/panel/');
  await expect(page.locator('#pager .page')).toHaveCount(pages.length);
  await expect(page.locator('#dots')).toHaveClass(/pagebar/); // the bar is on (pageBar: 'bar')
}

const visibleNames = (page) => page.locator('#dots .dot .dot-nm:visible').count();

test('a narrow screen with many pages shows only the current name', async ({ page }) => {
  await load(page, { width: 750, height: 360 });
  await expect(page.locator('#dots')).toHaveClass(/pb-tight/);
  expect(await visibleNames(page)).toBe(1);
});

test('a wide screen keeps every name', async ({ page }) => {
  await load(page, { width: 1600, height: 900 });
  await expect(page.locator('#dots')).not.toHaveClass(/pb-tight/);
  expect(await visibleNames(page)).toBe(DEFAULT_PAGES.length);
});

test('the side bar is never tightened', async ({ page }) => {
  await load(page, { width: 750, height: 360 }, { pageBarSide: 'left' });
  await expect(page.locator('#dots')).toHaveClass(/pb-vertical/);
  await expect(page.locator('#dots')).not.toHaveClass(/pb-tight/);
  expect(await visibleNames(page)).toBe(DEFAULT_PAGES.length);
});

// 沒有圖示的頁面不能被隱藏名稱（否則按鈕變空白）：只有圖示後面的名稱才會被收起。
// A page without an icon must keep its name when the bar is tightened (otherwise its button goes blank).
test('a page without an icon keeps its name when tightened', async ({ page }) => {
  const pages = DEFAULT_PAGES.map((p, i) => (i === 5 ? { ...p, icon: '' } : p));
  await load(page, { width: 750, height: 360 }, {}, pages);
  await expect(page.locator('#dots')).toHaveClass(/pb-tight/);
  await expect(page.locator('#dots .dot').nth(5).locator('.dot-nm')).toBeVisible();
  expect(await visibleNames(page)).toBe(2); // 目前頁 + 無圖示頁 / the current page + the icon-less one
});
