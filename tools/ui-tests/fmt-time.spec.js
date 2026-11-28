const { test, expect } = require('@playwright/test');

// fmtTime is the clock the Pomodoro dial, the timer dial, the media position/duration and the approval countdown
// all read. Under an hour it is M:SS; from an hour it must carry the hours, or a 90-minute focus reads "90:00".
test('fmtTime shows hours once a duration reaches an hour', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const { fmtTime } = await import('/panel/main.js');
    return {
      zero: fmtTime(0),
      seconds: fmtTime(45000),
      minutes: fmtTime(5 * 60000 + 3000),
      underHour: fmtTime(59 * 60000 + 59000),
      oneHour: fmtTime(60 * 60000),
      ninety: fmtTime(90 * 60000),
      oneHourTwoMinThreeSec: fmtTime(3723000),
      tenHours: fmtTime(10 * 3600000),
      negative: fmtTime(-5000),
    };
  });
  expect(out).toEqual({
    zero: '0:00',
    seconds: '0:45',
    minutes: '5:03',
    underHour: '59:59',
    oneHour: '1:00:00',
    ninety: '1:30:00',
    oneHourTwoMinThreeSec: '1:02:03',
    tenHours: '10:00:00',
    negative: '0:00',
  });
});
