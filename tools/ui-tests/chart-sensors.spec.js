const { test, expect } = require('@playwright/test');

test('every chart area is painted before the first metric line', async ({ page }) => {
  await page.goto('/panel/');
  const painted = await page.evaluate(async () => {
    const { chart } = await import('/panel/main.js');
    const order = [];
    const context = {
      clearRect() {}, beginPath() {}, moveTo() {}, lineTo() {}, closePath() {},
      createLinearGradient: () => ({ addColorStop() {} }),
      fill: () => order.push('area'),
      stroke: () => order.push(context.strokeStyle),
    };
    const canvas = { clientWidth: 200, clientHeight: 100, getContext: () => context };
    chart(canvas, [
      { data: [78, 79, 78], color: '#ffcc00', max: 100 },
      { data: [80, 77, 80], color: '#00cc00', max: 100 },
    ]);
    return order;
  });
  expect(painted).toEqual(['area', 'area', '#ffcc00', '#00cc00']);
});

test('Pages shows Windows sensor setup and the state reported by the agent', async ({ page }) => {
  await page.goto('/settings/');
  const report = (os, lhm) => page.evaluate(async ({ os, lhm }) => {
    const read = window.fetch;
    window.fetch = (input, ...rest) => input === '/api/info'
      ? Promise.resolve(Response.json({ os, sensors: { lhm }, devices: [], version: 'test' }))
      : read(input, ...rest);
    try { await loadInfo(); }
    finally { window.fetch = read; }
  }, { os, lhm });
  await report('windows', 'unavailable');
  const help = page.locator('#lhmHelp');
  await expect(help).toBeVisible();
  await expect(help).toContainText('Options › Remote Web Server › Run on port 8085');
  await expect(help.getByRole('link', { name: 'LibreHardwareMonitor' })).toHaveAttribute('href',
    'https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases');
  const status = help.getByRole('status');
  await expect(status).toHaveText('Sensor source unavailable');
  await report('windows', 'connected');
  await expect(status).toHaveText('Sensor source connected');
  await report('linux', null);
  await expect(help).toBeHidden();
});

test('the Windows sensor port rejects invalid values and stays set after Save', async ({ page }) => {
  await page.goto('/settings/');
  const port = page.locator('#lhmPort');
  const hint = page.locator('#lhmSetup');
  const save = page.locator('#save');
  const savedPort = () => page.evaluate(async () => (await (await fetch('/api/settings')).json()).libreHardwareMonitorPort);
  await expect(port).toBeVisible();
  await expect(port).toHaveValue('8085');
  const originalPort = await savedPort();

  await port.fill('65536');
  await save.click();
  expect(await port.evaluate(input => input.validity.rangeOverflow)).toBe(true);
  await expect(save).toBeEnabled();
  expect(await savedPort()).toBe(originalPort);

  await port.fill('18085');
  await expect(hint).toContainText('port 18085');
  await save.click();
  await expect(save).toBeDisabled();
  expect(await savedPort()).toBe(18085);
  await page.reload();
  await expect(port).toHaveValue('18085');
  await expect(hint).toContainText('port 18085');
});
