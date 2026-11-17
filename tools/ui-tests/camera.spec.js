const { test, expect } = require('@playwright/test');

// Camera widget: the demo answers /api/camera like CameraFeeds (a picture only for a camera widget on an enabled page
// with a camera chosen). The widget must ask only while it is really on screen, or the agent keeps pulling the camera.
const CAM = { id: 'cam0000door', name: 'Front door', url: 'rtsp://u:p@10.0.0.2/live', transport: 'tcp' };

async function show(page, { widget = { camera: CAM.id }, cameras = [CAM], pages } = {}) {
  await page.addInitScript(() => {
    window.__camRequests = 0;
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__cam) return;
      const g = (input, init) => {
        const url = typeof input === 'string' ? input : input.url;
        if (url.includes('/api/camera')) window.__camRequests++;
        return f(input, init);
      };
      g.__cam = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  });
  await page.goto('/panel/');
  await page.evaluate(({ widget, cameras, pages }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], cameras: { ffmpeg: '', sources: cameras },
      pages: pages || [{ id: 'cams', title: 'Cams', enabled: true, widgets: [{ id: 'cam', type: 'camera', x: 0, y: 0, w: 6, h: 4, options: widget }] }] }));
  }, { widget, cameras, pages });
  await page.reload();
}

test('the camera widget shows the latest picture and keeps it coming', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page);
  const img = page.locator('.cam-img');
  await expect(img).toBeVisible();
  await expect(page.locator('.cam-note')).toBeHidden();
  const first = await img.getAttribute('src');
  await expect.poll(() => img.getAttribute('src')).not.toBe(first);
  // pictures are blobs, and the previous one is released (only one object URL is alive)
  expect(first).toMatch(/^blob:/);
  expect(errors).toEqual([]);
});

test('without a chosen camera the widget says so and shows no picture', async ({ page }) => {
  await show(page, { widget: {} });
  await expect(page.locator('.cam-note')).toHaveText('Choose a camera in this widget’s settings');
  await expect(page.locator('.cam-img')).toBeHidden();
});

test('a removed camera is treated like no camera', async ({ page }) => {
  await show(page, { cameras: [] });
  await expect(page.locator('.cam-note')).toHaveText('Choose a camera in this widget’s settings');
});

test('the widget stops asking under the night screen', async ({ page }) => {
  await show(page);
  await expect(page.locator('.cam-img')).toBeVisible();
  await page.evaluate(() => document.body.classList.add('night'));
  await page.waitForTimeout(1200); // let a request in flight finish
  const n = await page.evaluate(() => window.__camRequests);
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() => window.__camRequests)).toBe(n);
  // and comes back when the night screen goes
  await page.evaluate(() => document.body.classList.remove('night'));
  await expect.poll(() => page.evaluate(() => window.__camRequests), { timeout: 5000 }).toBeGreaterThan(n);
});

test('a camera in a Smart Stack card that is not showing is not asked for', async ({ page }) => {
  await show(page, {
    pages: [{ id: 'st', title: 'Stack', enabled: true, widgets: [{ id: 'stk', type: 'stack', x: 0, y: 0, w: 6, h: 4, options: { interval: 0, widgets: [
      { id: 'clock1', type: 'clock', enabled: true, options: {} },
      { id: 'cam1', type: 'camera', enabled: true, options: { camera: CAM.id } },
    ] } }] }],
  });
  await expect(page.locator('.w-clock, .clock').first()).toBeVisible();
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() => window.__camRequests)).toBe(0);
});

test('a camera on another page is not asked for', async ({ page }) => {
  await show(page, {
    pages: [
      { id: 'home', title: 'Home', enabled: true, widgets: [{ id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: {} }] },
      { id: 'cams', title: 'Cams', enabled: true, widgets: [{ id: 'cam', type: 'camera', x: 0, y: 0, w: 6, h: 4, options: { camera: CAM.id } }] },
    ],
  });
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() => window.__camRequests)).toBe(0);
});

test('settings: a camera added in General can be chosen in the widget, and its address is not shown there', async ({ page }) => {
  await show(page, { widget: {}, cameras: [] });
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.goto('/settings/#general');
  await page.locator('#cameraAdd').click();
  const row = page.locator('#cameraList .cam-row').first();
  await row.locator('input').nth(0).fill('Front door');
  await row.locator('input').nth(1).fill('rtsp://admin:hunter2@10.0.0.2:554/live');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const saved = await page.evaluate(async () => (await (await fetch('/api/settings')).json()).cameras.sources);
  expect(saved).toHaveLength(1);
  expect(saved[0].id).toMatch(/^[a-z0-9]{12}$/);
  // the widget's camera option lists it by name
  await page.goto('/settings/#pages');
  await page.locator('#canvas [title="Camera"]').click();
  const select = page.locator('#inspector select').first();
  await expect(select.locator('option', { hasText: 'Front door' })).toHaveCount(1);
  await expect(page.locator('#inspector')).not.toContainText('hunter2');
});
