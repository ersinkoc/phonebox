const { test, expect } = require('@playwright/test');

// The red box at the bottom of the panel (index.html) reports what breaks the panel itself. A widget's image that
// fails to load is the widget's to handle and must not put it up for good (#200).
test('a widget image that fails to load does not show the failure box', async ({ page }) => {
  await page.goto('/panel/');
  await page.waitForFunction(() => window.__pbUp === true);
  await page.evaluate(() => {
    const img = document.createElement('img');
    img.id = 'broken-cover';
    img.src = '/panel/missing-cover-on-purpose.png'; // a 404 (the demo answers /agent/api/art itself)
    document.body.append(img);
  });
  // the load has failed once the image is complete without any pixels
  await page.waitForFunction(() => { const i = document.getElementById('broken-cover'); return i.complete && i.naturalWidth === 0; });
  await page.waitForTimeout(100);
  await expect(page.locator('#jsfail')).toBeHidden();
});

test('a script that fails to load is still reported, and a tap puts the box away', async ({ page }) => {
  await page.goto('/panel/');
  await page.waitForFunction(() => window.__pbUp === true);
  await page.evaluate(() => new Promise((resolve) => {
    const s = document.createElement('script');
    s.onerror = () => setTimeout(resolve, 50);
    s.src = '/panel/missing-on-purpose.js';
    document.body.append(s);
  }));
  const box = page.locator('#jsfail');
  await expect(box).toBeVisible();
  await expect(box).toContainText('LOAD FAIL');
  await box.click();
  await expect(box).toBeHidden();
  // a new problem shows it again, without the old line
  await page.evaluate(() => window.__pbFail('ERR something new'));
  await expect(box).toHaveText('ERR something new');
});
