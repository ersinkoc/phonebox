// "Mics off, camera off" (#387): the panel says what happened to the microphones and to the OBS virtual camera, and
// never shows a camera OBS did not stop as off.
const { test, expect } = require('@playwright/test');

async function show(page, settings = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate((settings) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], ...settings,
      pages: [{ id: 'controls', title: 'Controls', widgets: [
        { id: 'privacy', type: 'mics-camera-off', x: 0, y: 0, w: 6, h: 6, options: {} },
        { id: 'camera', type: 'obs-camera', x: 6, y: 0, w: 6, h: 6, options: { control: true } },
      ] }] }));
  }, settings);
  await page.goto('/panel/');
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

const devices = (page) => page.evaluate(async () => (await (await fetch('/agent/api/controls/state')).json()).audio.devices);
const command = (page, path) => page.evaluate(async (path) => (await fetch('/agent/api/controls/' + path, { method: 'POST' })).json(), path);

test('mics off, camera off mutes the microphones, leaves the speakers and says the OBS camera stopped', async ({ page }) => {
  await show(page);
  await expect(page.locator('.control-deck .w-title').first()).toHaveText('Mics off, camera off');
  // an open microphone, unmuted speakers and a running virtual camera
  const mic = (await devices(page)).find((d) => d.direction === 'input');
  expect((await command(page, `command/device?id=${mic.id}&muted=false`)).ok).toBe(true);
  expect((await command(page, 'obs/camera')).ok).toBe(true);
  const outputsBefore = (await devices(page)).filter((d) => d.direction === 'output').map((d) => [d.id, d.muted]);
  expect(outputsBefore.length).toBeGreaterThan(0);

  await page.getByRole('button', { name: 'Mute microphones + stop OBS camera' }).click();
  await expect(page.locator('#toast')).toHaveClass(/\bon\b/);
  await expect(page.locator('#toast')).toHaveClass(/\bok\b/);
  await expect(page.locator('#toast')).toHaveText('Microphones muted · OBS virtual camera stopped');
  const after = await devices(page);
  expect(after.filter((d) => d.direction === 'input').every((d) => d.muted)).toBe(true);
  expect(after.filter((d) => d.direction === 'output').map((d) => [d.id, d.muted])).toEqual(outputsBefore);
  expect((await page.evaluate(() => fetch('/agent/api/controls/obs').then((r) => r.json()))).cameraActive).toBe(false);

  // a second tap: the camera is not started again, and is reported as already off
  await page.getByRole('button', { name: 'Mute microphones + stop OBS camera' }).click();
  await expect(page.locator('#toast')).toHaveText('Microphones muted · OBS virtual camera was already off');
});

test('mics off, camera off says plainly that the camera was not turned off when OBS is not connected', async ({ page }) => {
  await show(page, { obs: { enabled: false } });
  await page.getByRole('button', { name: 'Mute microphones + stop OBS camera' }).click();
  await expect(page.locator('#toast')).toHaveClass(/\berr\b/);
  await expect(page.locator('#toast')).toHaveText('Microphones muted · Camera not turned off: OBS is not connected');
  expect((await devices(page)).filter((d) => d.direction === 'input').every((d) => d.muted)).toBe(true);
});

test('mics off, camera off reports in the panel language', async ({ page }) => {
  await show(page, { obs: { enabled: false }, display: { language: 'de' } });
  await expect(page.locator('html')).toHaveAttribute('lang', 'de');
  await expect(page.locator('.control-deck .w-title').first()).toHaveText('Mikros aus, Kamera aus');
  await page.getByRole('button', { name: 'Mikrofone stummschalten + OBS-Kamera stoppen' }).click();
  await expect(page.locator('#toast')).toHaveText('Mikrofone stummgeschaltet · Kamera nicht ausgeschaltet: OBS ist nicht verbunden');
});
