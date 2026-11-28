const { test, expect } = require('@playwright/test');

// #613: without a full audio backend (macOS, or Linux without pactl) the agent reports only the system output
// (PortableDeckNative: no sessions, no inputs, one output that cannot be switched; the demo answers the same for
// __demoOs). The deck widgets that need more say so instead of an empty list. With pactl (__demoPulse) Linux reports
// every device and application stream, and the widgets work as on Windows.
async function show(page, type, os, pulse = false) {
  await page.addInitScript(([os, pulse]) => { window.__demoOs = os; window.__demoPulse = pulse; }, [os, pulse]);
  await page.goto('/panel/style.css');
  await page.evaluate((type) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'control', type, x: 0, y: 0, w: 12, h: 6, options: {} }] }] }));
  }, type);
  await page.goto('/panel/');
}

// each widget's own control: a session's volume slider, an output's button, the enabled push-to-talk button
for (const [type, text, control] of [
  ['audio-mixer', 'Per-app volume is only on Windows for now', (page) => page.getByRole('slider', { name: 'Spotify volume', exact: true })],
  ['audio-outputs', 'Switching audio devices is only on Windows for now', (page) => page.getByRole('button', { name: 'Headphones', exact: true })],
  ['push-to-talk', 'Microphone control is only on Windows for now', (page) => page.getByRole('button', { name: 'Hold to talk' })],
]) {
  test(`${type} says it is Windows-only on Linux`, async ({ page }) => {
    await show(page, type, 'linux');
    await expect(page.getByText(text, { exact: true })).toBeVisible();
    await expect(control(page).and(page.locator(':enabled'))).toHaveCount(0); // push-to-talk stays, disabled
  });

  for (const [label, os, pulse] of [['on Windows', 'windows', false], ['on Linux with pactl', 'linux', true]]) {
    test(`${type} keeps its controls ${label}`, async ({ page }) => {
      await show(page, type, os, pulse);
      await expect(control(page)).toBeVisible();
      await expect(control(page)).toBeEnabled();
      await expect(page.getByText(text, { exact: true })).toHaveCount(0);
    });
  }

  // pactl answered once, then the sound server stopped: the agent reports audio unavailable, which is not Windows-only
  test(`${type} says audio is unavailable when the sound server stops`, async ({ page }) => {
    await show(page, type, 'linux', 'down');
    await expect(page.getByText('The sound server did not answer (pactl)', { exact: true })).toBeVisible();
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
