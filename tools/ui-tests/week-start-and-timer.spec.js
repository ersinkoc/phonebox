const { test, expect } = require('@playwright/test');

test.use({ timezoneId: 'UTC', locale: 'en-US' });

test('changing the first day also moves the weekend and restarts weekly progress', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-11-01T12:00:00Z') });
  await page.goto('/panel/');
  await page.clock.setFixedTime(new Date('2026-11-01T12:00:00Z'));
  const results = await page.evaluate(async () => {
    const { cfg } = await import('/panel/main.js');
    const { W } = await import('/panel/registry.js');
    const calendar = document.createElement('div'), progress = document.createElement('div');
    const cal = W.calendar.create(calendar), prog = W.progress.create(progress);
    const snapshot = (weekStart) => {
      cfg.display = { ...cfg.display, weekStart };
      cal.update();
      prog.update();
      return {
        firstCell: calendar.querySelector('.cal-d').textContent,
        weekends: [...calendar.querySelectorAll('.cal-h')].map(c => c.classList.contains('we')),
        week: calendar.querySelector('.w-sub').textContent,
        progress: progress.querySelectorAll('.pr-row .track i')[1].style.width,
      };
    };
    return [snapshot('monday'), snapshot('sunday')];
  });
  expect(results[0]).toEqual({ firstCell: '26', weekends: [false, false, false, false, false, true, true], week: 'Week 44', progress: '92.8571%' });
  expect(results[1]).toEqual({ firstCell: '1', weekends: [true, false, false, false, false, false, true], week: 'Week 45', progress: '7.14286%' });
});

test('time widget controls stack to the right only in wide tiles', async ({ page }) => {
  await page.goto('/panel/');
  const directions = await page.evaluate(async () => {
    await import('/panel/main.js');
    const { W } = await import('/panel/registry.js');
    return ['pomodoro', 'timer', 'stopwatch', 'counter'].map(type => {
      const box = document.createElement('div');
      box.className = 'w w-' + type;
      document.body.append(box);
      W[type].create(box, { o: {}, page: { id: 'layout-test' }, w: { id: type } });
      const layout = box.querySelector('.timer-layout');
      box.style.width = '240px';
      box.style.height = '120px';
      const wide = getComputedStyle(layout).display;
      box.style.width = '120px';
      const narrow = getComputedStyle(layout).display;
      box.remove();
      return [wide, narrow];
    });
  });
  expect(directions).toEqual(Array(4).fill(['grid', 'contents']));
});

test('the Quick timer plays the opt-in chime once when it expires', async ({ page }) => {
  const start = new Date('2026-11-01T12:00:00Z');
  await page.clock.install({ time: start });
  await page.goto('/panel/');
  await page.clock.setFixedTime(start);
  await page.evaluate(async () => {
    const { W } = await import('/panel/registry.js');
    window.alarmNotes = 0;
    window.AudioContext = class {
      state = 'running';
      currentTime = 0;
      createOscillator() {
        return { frequency: { setValueAtTime() {} }, connect() {}, start() { window.alarmNotes++; }, stop() {} };
      }
      createGain() {
        return { gain: { setValueAtTime() {}, exponentialRampToValueAtTime() {}, linearRampToValueAtTime() {} }, connect() {} };
      }
    };
    window.alarmTimers = [false, true].map(sound => {
      const box = document.createElement('div');
      const timer = W.timer.create(box, { o: { sound }, page: { id: 'alarm-test' }, w: { id: String(sound) } });
      box.querySelector('.chip-btn').click();
      return timer;
    });
  });
  await page.clock.setFixedTime(new Date(start.getTime() + 60_000));
  const notes = await page.evaluate(() => {
    window.alarmTimers[0].update();
    const silent = window.alarmNotes;
    window.alarmTimers[1].update();
    const audible = window.alarmNotes;
    window.alarmTimers[1].update();
    return { silent, audible, final: window.alarmNotes };
  });
  expect(notes.silent).toBe(0);
  expect(notes.audible).toBeGreaterThan(0);
  expect(notes.final).toBe(notes.audible);
});
