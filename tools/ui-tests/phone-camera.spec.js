const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

test('webcam controls request phone approval and can revoke a session without saving settings', async ({ page }) => {
  const errors = []; page.on('pageerror', e => errors.push(String(e)));
  await page.goto('/settings/#general');
  await expect(page.locator('#phoneCameraRequest')).toBeEnabled();
  await expect(page.locator('#phoneCameraStatus')).toHaveText('Camera off');
  await expect(page.locator('#phoneCameraUrl')).toHaveValue('http://127.0.0.1:8781/phone-camera');
  await page.evaluate(() => {
    const original = window.fetch;
    window.cameraFixture = { state: 'off', requests: 0, stops: 0, saves: 0 };
    window.fetch = (url, options) => {
      const fixture = window.cameraFixture;
      if (String(url).startsWith('/api/phone-camera/request?')) {
        fixture.requests++; fixture.state = 'waiting'; return Promise.resolve(Response.json({}));
      }
      if (String(url) === '/api/phone-camera/stop') {
        fixture.stops++; fixture.state = 'off'; return Promise.resolve(Response.json({}));
      }
      if (String(url) === '/api/phone-camera/status') return Promise.resolve(Response.json({ state: fixture.state }));
      if (String(url) === '/api/settings' && options?.method === 'PUT') fixture.saves++;
      return original(url, options);
    };
  });
  await page.locator('#phoneCameraRequest').click();
  await expect(page.locator('#phoneCameraStatus')).toContainText('Waiting for phone approval');
  await expect(page.locator('#phoneCameraRequest')).toBeDisabled();
  await page.locator('#phoneCameraStop').click();
  await expect(page.locator('#phoneCameraStatus')).toHaveText('Camera off');
  expect(await page.evaluate(() => window.cameraFixture)).toEqual({ state: 'off', requests: 1, stops: 1, saves: 0 });
  expect(errors).toEqual([]);
  const card = page.locator('.card').filter({ has: page.getByRole('heading', { name: 'Phone as webcam', exact: true }) });
  for (const width of [780, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await card.evaluate(element => element.scrollIntoView({ block: 'center' }));
    await card.screenshot({ path: path.join(__dirname, `../../.temp_files/phone-camera-settings-${width}.png`) });
  }
});

test('OBS output rotates frames and clears the last picture on stop and connection failure', async ({ page }) => {
  const source = fs.readFileSync(path.join(__dirname, '../../agent/PhoneBox.Agent/PhoneCameraEndpoints.cs'), 'utf8');
  const html = source.match(/internal const string Page = """([\s\S]*?)""";/)[1];
  await page.goto('/panel/style.css');
  const jpeg = Buffer.from(await page.evaluate(() => {
    const canvas = document.createElement('canvas'); canvas.width = 200; canvas.height = 100;
    const context = canvas.getContext('2d'); context.fillStyle = '#ff0000'; context.fillRect(0, 0, 200, 100);
    return canvas.toDataURL('image/jpeg').split(',')[1];
  }), 'base64');
  let mode = 'live';
  await page.route('**/phone-camera', route => route.fulfill({ contentType: 'text/html', body: html }));
  await page.route('**/api/phone-camera/frame', route => mode === 'live'
    ? route.fulfill({ contentType: 'image/jpeg', headers: { 'X-Camera-Rotation': '90' }, body: jpeg })
    : mode === 'off' ? route.fulfill({ status: 204 }) : route.abort());
  await page.goto('/phone-camera');
  const pixel = () => page.evaluate(() => [...document.querySelector('canvas').getContext('2d').getImageData(0, 0, 1, 1).data]);
  await expect.poll(async () => (await pixel())[0]).toBeGreaterThan(240);
  expect(await page.locator('canvas').evaluate(c => [c.width, c.height])).toEqual([100, 200]);
  mode = 'off';
  await expect.poll(pixel).toEqual([17, 17, 17, 255]);
  mode = 'live';
  await expect.poll(async () => (await pixel())[0]).toBeGreaterThan(240);
  mode = 'error';
  await expect.poll(pixel).toEqual([17, 17, 17, 255]);
});

test('native installation and activation require separate explicit clicks and never request phone video', async ({ page }) => {
  await page.goto('/settings/#general');
  await page.evaluate(async () => {
    const original = window.fetch;
    const fixture = window.nativeFixture = { installed: false, running: false, actions: [], videoRequests: 0 };
    window.fetch = (url, options) => {
      if (String(url) === '/api/phone-camera/native') return Promise.resolve(Response.json({
        supported: true, packaged: true, installed: fixture.installed, running: fixture.running, busy: false, error: null,
      }));
      if (String(url).startsWith('/api/phone-camera/native/')) {
        const action = String(url).split('/').pop(); fixture.actions.push(action);
        if (action === 'install') fixture.installed = true;
        if (action === 'enable') fixture.running = true;
        if (action === 'disable') fixture.running = false;
        if (action === 'uninstall') fixture.installed = false;
        return Promise.resolve(Response.json({}));
      }
      if (String(url).startsWith('/api/phone-camera/request')) fixture.videoRequests++;
      return original(url, options);
    };
    await nativeCameraRefresh();
  });
  await expect(page.locator('#nativeCameraInstall')).toBeEnabled();
  await expect(page.locator('#nativeCameraEnable')).toBeDisabled();
  expect(await page.evaluate(() => window.nativeFixture.actions)).toEqual([]);
  await page.locator('#nativeCameraInstall').click();
  await expect(page.locator('#nativeCameraEnable')).toBeEnabled();
  expect(await page.evaluate(() => window.nativeFixture.running)).toBe(false);
  await page.locator('#nativeCameraEnable').click();
  await expect(page.locator('#nativeCameraStatus')).toContainText('available to calling apps');
  const card = page.locator('.card').filter({ has: page.getByRole('heading', { name: 'Phone as webcam', exact: true }) });
  for (const width of [780, 1280]) {
    await page.setViewportSize({ width, height: 1000 });
    await card.evaluate(element => element.scrollIntoView({ block: 'center' }));
    await card.screenshot({ path: path.join(__dirname, `../../.temp_files/native-camera-settings-${width}.png`) });
  }
  await expect(page.locator('#nativeCameraInstall')).toBeDisabled();
  await expect(page.locator('#nativeCameraUninstall')).toBeDisabled();
  await page.locator('#nativeCameraDisable').click();
  await expect(page.locator('#nativeCameraUninstall')).toBeEnabled();
  await page.locator('#nativeCameraUninstall').click();
  expect(await page.evaluate(() => window.nativeFixture)).toEqual({
    installed: false, running: false, actions: ['install', 'enable', 'disable', 'uninstall'], videoRequests: 0,
  });
});
