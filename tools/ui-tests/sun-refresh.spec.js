const { test, expect } = require('@playwright/test');

for (const missing of [false, true]) {
  test('sunrise widget refreshes in the same minute with ' + (missing ? 'no solar times' : 'new solar times'), async ({ page }) => {
    await page.clock.install({ time: new Date(2026, 0, 1, 12, 0, 0) });
    await page.goto('/panel/');
    const result = await page.evaluate(async missing => {
      const m = await import('/panel/main.js');
      const { W } = await import('/panel/registry.js');
      const box = document.createElement('div');
      document.body.append(box);
      const widget = W.sun.create(box);
      const snapshot = (rise, set) => ({ daily: { sunrise: [rise], sunset: [set] } });
      const label = () => box.querySelector('.sr').textContent;
      m.D.weather = snapshot('2026-01-01T06:00', '2026-01-01T18:00');
      widget.update();
      const control = { actual: label(), expected: '↑ ' + m.fmtHM(new Date('2026-01-01T06:00')) };
      m.D.weather = missing ? snapshot(null, null) : snapshot('2026-01-01T08:00', '2026-01-01T20:00');
      widget.update();
      return { control, actual: label(), expected: missing ? '' : '↑ ' + m.fmtHM(new Date('2026-01-01T08:00')) };
    }, missing);
    expect(result.control.actual).toBe(result.control.expected);
    expect(result.actual, 'EXPECTED: "' + result.expected + '" | ACTUAL: "' + result.actual + '"').toBe(result.expected);
  });
}
