const { test, expect } = require('@playwright/test');
const { progressAt } = require('./time-progress');

test.use({ timezoneId: 'America/New_York' });

for (const [name, instant, hours, dayHours, weekHours] of [
  ['ordinary day', '2026-10-31T16:00:00Z', 12, 24, 168],
  ['fall clock change', '2026-11-02T04:30:00Z', 24.5, 25, 169],
  ['spring clock change', '2026-03-09T03:30:00Z', 22.5, 23, 167],
]) {
  test(`time progress uses calendar boundaries on ${name}`, async ({ page }) => {
    const rows = await progressAt(page, instant);
    expect(rows[0].width).toBeCloseTo(hours / dayHours * 100, 2);
    if (dayHours !== 24) expect(rows[1].width).toBeCloseTo((weekHours - 0.5) / weekHours * 100, 2);
    for (const row of rows) {
      expect(row.width).toBeGreaterThanOrEqual(0);
      expect(row.width).toBeLessThanOrEqual(100);
    }
  });
}
