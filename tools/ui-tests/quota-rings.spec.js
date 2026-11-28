const { test, expect } = require('@playwright/test');

// 配額卡有三個圓環（例如 Antigravity）時：每欄很窄、標籤行數也不同。圓環要同大小、頂端對齊，標籤不能超出自己的欄位。
// A quota card with three rings (Antigravity has three): the columns are narrow and their labels differ in length. The rings
// must be the same size and top-aligned, and no label may spill out of its own column.
const NOW = Date.now();
const quotas = {
  providers: [{
    id: 'antigravity', name: 'Antigravity', plan: 'Antigravity', updatedAt: NOW,
    limits: [
      { label: 'Weekly (Gemini)', percent: 26, resetsAt: NOW + 7 * 86400_000 },
      { label: '5 hour (Claude/GPT)', percent: 0, resetsAt: null },
      { label: 'Weekly (Claude/GPT)', percent: 0, resetsAt: null },
    ],
  }],
};

async function load(page, size) {
  await page.setViewportSize(size);
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], display: { idleReturnSec: 0 },
      pages: [{ id: 'q', title: 'Quotas', widgets: [{ id: 'q1', type: 'quotas', x: 0, y: 0, w: 6, h: 6, options: { providers: ['antigravity'] } }] }] }));
  });
  await page.goto('/panel/');
  await expect(page.locator('.w-quotas')).toHaveCount(1);
  await page.evaluate(async (q) => {
    const { D, emit } = await import('/panel/main.js');
    D.quotas = q;
    emit('quotas');
  }, quotas);
  await expect(page.locator('.q-dial')).toHaveCount(3);
}

for (const size of [{ width: 780, height: 360 }, { width: 750, height: 420 }]) {
  test(`three rings stay aligned and inside their columns at ${size.width}x${size.height}`, async ({ page }) => {
    await load(page, size);
    const boxes = await page.locator('.q-dial').evaluateAll((els) => els.map((e) => { const r = e.getBoundingClientRect(); return { top: r.top, w: r.width }; }));
    for (const b of boxes) {
      expect(Math.abs(b.top - boxes[0].top)).toBeLessThanOrEqual(1); // same top / 頂端對齊
      expect(Math.abs(b.w - boxes[0].w)).toBeLessThanOrEqual(1);     // same size / 同樣大小
    }
    // 每個標籤的左右邊都要落在自己那一欄裡 / every label sits inside its own ring column
    const spill = await page.locator('.q-ring').evaluateAll((cols) => cols.flatMap((col) => {
      const c = col.getBoundingClientRect();
      return [...col.querySelectorAll('.q-label, .q-left, .q-reset, .q-pace')].map((e) => {
        const r = e.getBoundingClientRect();
        return r.left < c.left - 1 || r.right > c.right + 1 || e.scrollWidth > e.clientWidth + 1;
      });
    }));
    expect(spill.every((s) => !s)).toBe(true);
  });
}

// The Quota ring shows when the limit resets as a clock time in the phone's zone and language, then the time left.
for (const { language, left } of [{ language: 'en', left: /in 2 h/ }, { language: 'de', left: /in 2/ }]) {
  test(`the quota ring shows the reset clock time in the phone's zone (${language})`, async ({ browser }) => {
    const context = await browser.newContext({ timezoneId: 'Asia/Kolkata', viewport: { width: 780, height: 360 } });
    const page = await context.newPage();
    await page.goto('/panel/style.css');
    await page.evaluate((language) => {
      localStorage.clear();
      localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], display: { idleReturnSec: 0, language, hour12: false },
        pages: [{ id: 'r', title: 'Ring', widgets: [{ id: 'r1', type: 'quotaring', x: 0, y: 0, w: 6, h: 6, options: { provider: 'claude', limit: '0' } }] }] }));
    }, language);
    await page.goto('/panel/');
    await expect(page.locator('.w-quotaring')).toHaveCount(1);
    // two hours from now, as a clock time in the browser's zone
    const clock = await page.evaluate(async () => {
      const { D, emit } = await import('/panel/main.js');
      const resetsAt = Date.now() + 2 * 3600_000 + 30_000;
      D.quotas = { providers: [{ id: 'claude', name: 'Claude', updatedAt: Date.now(), limits: [{ label: '5-hour session', percent: 17, resetsAt }] }] };
      emit('quotas');
      return new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(resetsAt);
    });
    await expect(page.locator('.w-quotaring .g-sub')).toContainText(clock);
    await expect(page.locator('.w-quotaring .g-sub')).toContainText(left);
    await expect(page.locator('.w-quotaring .g-sub')).not.toContainText('resets in');
    await context.close();
  });
}
