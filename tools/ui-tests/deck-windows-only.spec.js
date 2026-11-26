const { test, expect } = require('@playwright/test');

// #613: on Linux and macOS the agent reports only the system output (PortableDeckNative: no sessions, no inputs, one
// output that cannot be switched; the demo answers the same for __demoOs). The deck widgets that need Windows Core
// Audio say so instead of an empty list.
async function show(page, type, os) {
  await page.addInitScript((os) => { window.__demoOs = os; }, os);
  await page.goto('/panel/style.css');
  await page.evaluate((type) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'control', type, x: 0, y: 0, w: 12, h: 6, options: {} }] }] }));
  }, type);
  await page.goto('/panel/');
}

for (const [type, text] of [
  ['audio-mixer', 'Per-app volume is only on Windows for now'],
  ['audio-outputs', 'Switching audio devices is only on Windows for now'],
  ['push-to-talk', 'Microphone control is only on Windows for now'],
]) {
  test(`${type} says it is Windows-only on Linux`, async ({ page }) => {
    await show(page, type, 'linux');
    await expect(page.getByText(text, { exact: true })).toBeVisible();
  });

  test(`${type} keeps its controls on Windows`, async ({ page }) => {
    await show(page, type, 'windows');
    await expect(page.locator('.w-control, .deck-list, button').first()).toBeVisible();
    await expect(page.getByText(text, { exact: true })).toHaveCount(0);
  });
}

test('device-volume controls the system output on Linux', async ({ page }) => {
  await show(page, 'device-volume', 'linux');
  const output = page.getByRole('slider', { name: 'System output volume', exact: true });
  await expect(output).toBeVisible();
  const before = Number(await output.inputValue());
  await output.focus(); await output.press('ArrowLeft');
  await expect(output).toHaveValue(String(before - 1));
  // the command reached the demo agent's "default" device, which follows the computer's volume
  await expect.poll(() => page.evaluate(async () => (await (await fetch('/agent/api/controls/state')).json()).audio.devices[0].volume)).toBe(before - 1);
});

test('a mute-only device command keeps a volume set elsewhere on Linux', async ({ page }) => {
  await show(page, 'device-volume', 'linux');
  await expect(page.getByRole('slider', { name: 'System output volume', exact: true })).toBeVisible();
  const after = await page.evaluate(async () => {
    const post = (p) => fetch(p, { method: 'POST' }).then((r) => r.json());
    await post('/agent/api/volume?level=23');                                      // e.g. the volume keys
    await post('/agent/api/controls/command/device?id=default&muted=true');       // mute only
    return (await (await fetch('/agent/api/controls/state')).json()).audio.devices[0];
  });
  expect(after).toMatchObject({ volume: 23, muted: true });
});
