// "Unmute microphones" (#645): after the emergency mute or "Mics off, camera off", one tap opens again the microphones
// they closed, and only those; the button is not there until there is something to open.
const { test, expect } = require('@playwright/test');

async function show(page, type) {
  await page.goto('/panel/style.css');
  await page.evaluate((type) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'privacy', type, x: 0, y: 0, w: 6, h: 6, options: {} }] }] }));
  }, type);
  await page.goto('/panel/');
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

const state = (page) => page.evaluate(async () => (await fetch('/agent/api/controls/state')).json());
const mic = async (page) => (await state(page)).audio.devices.find((d) => d.direction === 'input');
const command = (page, path) => page.evaluate(async (path) => (await fetch('/agent/api/controls/' + path, { method: 'POST' })).json(), path);

for (const [type, mute] of [['panic-mute', 'Mute audio + microphones'], ['mics-camera-off', 'Mute microphones + stop OBS camera']]) {
  test(`${type}: unmute opens the microphone it closed`, async ({ page }) => {
    await show(page, type);
    const unmute = page.getByRole('button', { name: 'Unmute microphones' });
    const { id } = await mic(page);
    expect((await command(page, `command/device?id=${id}&muted=false`)).ok).toBe(true);
    await page.waitForTimeout(1500); // a state poll or two: still nothing to open
    await expect(unmute).toBeHidden();

    await page.getByRole('button', { name: mute }).click();
    await expect(unmute).toBeVisible();
    expect((await mic(page)).muted).toBe(true);
    const speakers = (await state(page)).audio.devices.filter((d) => d.direction === 'output').map((d) => [d.id, d.muted]);

    await unmute.click();
    await expect(page.locator('#toast')).toHaveText('Microphones unmuted');
    await expect(page.locator('#toast')).toHaveClass(/\bok\b/);
    expect((await mic(page)).muted).toBe(false);
    // the outputs stay as the mute left them
    expect((await state(page)).audio.devices.filter((d) => d.direction === 'output').map((d) => [d.id, d.muted])).toEqual(speakers);
    await expect(unmute).toBeHidden();
  });
}

test('a microphone that was already muted is not this button\'s to open', async ({ page }) => {
  await show(page, 'panic-mute');
  expect((await mic(page)).muted).toBe(true); // the demo's microphone starts muted
  await page.getByRole('button', { name: 'Mute audio + microphones' }).click();
  await page.waitForTimeout(1500);
  await expect(page.getByRole('button', { name: 'Unmute microphones' })).toBeHidden();
  expect((await state(page)).micsToUnmute).toBe(0);
  const result = await command(page, 'command/mics-on');
  expect(result).toMatchObject({ ok: false, mics: 'none' });
  expect((await mic(page)).muted).toBe(true);
});

test('the button and its answer are translated', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], display: { language: 'de' },
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'privacy', type: 'panic-mute', x: 0, y: 0, w: 6, h: 6, options: {} }] }] }));
  });
  await page.goto('/panel/');
  const { id } = await mic(page);
  await command(page, `command/device?id=${id}&muted=false`);
  await command(page, 'command/panic');
  const unmute = page.getByRole('button', { name: 'Mikrofone wieder einschalten' });
  await expect(unmute).toBeVisible();
  await unmute.click();
  await expect(page.locator('#toast')).toHaveText('Mikrofone wieder eingeschaltet');
});
