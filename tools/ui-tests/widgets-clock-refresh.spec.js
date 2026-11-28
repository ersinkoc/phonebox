const { test, expect } = require('@playwright/test');

test.use({ timezoneId: 'UTC' });

async function mountWidget(page, type) {
  await page.clock.install({ time: new Date('2026-01-01T12:15:00Z') });
  await page.goto('/panel/');
  await page.clock.setFixedTime(new Date('2026-01-01T12:15:00Z'));
  await page.evaluate(async type => {
    await import('/panel/main.js');
    const { W } = await import('/panel/registry.js');
    const box = document.createElement('section');
    box.id = 'clock-fixture';
    document.body.append(box);
    const widget = W[type].create(box, {
      o: { items: 'First task\nSecond task' }, page: { id: 'fixture' }, w: { id: type },
    });
    window.updateClockFixture = () => widget.update();
    window.updateClockFixture();
  }, type);
  return page.locator('#clock-fixture');
}

test('time progress refreshes on waking in the same minute of another hour', async ({ page }) => {
  const box = await mountWidget(page, 'progress');
  const day = box.locator('.pr-row').first().locator('.m-row span').last();
  await expect(day).toHaveText('51%');
  await page.clock.setFixedTime(new Date('2026-01-01T18:15:00Z'));
  await page.evaluate(() => window.updateClockFixture());
  await expect(day).toHaveText('76%');
});

test('word clock refreshes the day even when the displayed time is unchanged', async ({ page }) => {
  const box = await mountWidget(page, 'wordclock');
  const date = box.locator('.wc-date');
  await expect(date).toContainText('Thursday');
  const phrase = await box.locator('.wc-main').textContent();
  await page.clock.setFixedTime(new Date('2026-01-02T12:15:00Z'));
  await page.evaluate(() => window.updateClockFixture());
  await expect(date).toContainText('Friday');
  await expect(box.locator('.wc-main')).toHaveText(phrase);
});

test('a checklist tap before the first midnight tick uses the new day', async ({ page }) => {
  const box = await mountWidget(page, 'checklist');
  const first = box.getByRole('button', { name: /First task/ });
  const second = box.getByRole('button', { name: /Second task/ });
  await first.evaluate(button => button.click());
  await second.evaluate(button => button.click());
  await expect(box.locator('.cl-i.done')).toHaveCount(2);

  await page.clock.setFixedTime(new Date('2026-01-02T00:00:01Z'));
  await first.evaluate(button => button.click());
  await expect(box.locator('.cl-i.done')).toHaveText([/First task/]);
  const state = await page.evaluate(() => JSON.parse(localStorage.getItem('cl:fixture/checklist')));
  expect(state).toEqual({ day: 'Fri Jan 02 2026', done: [0] }); // the row's index, not its text (#765)
});
