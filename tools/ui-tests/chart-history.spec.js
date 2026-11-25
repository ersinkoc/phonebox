// The charts move once per sample from the PC: a state sent again with the same sample (a media or volume change
// pokes the stream) adds no point, a new sample does, and an agent that sends no sample number keeps the old behaviour.
const { test, expect } = require('@playwright/test');

test('a state with the same pc sample adds no chart point; a new sample or an agent without seq does', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
  const result = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    let ts = 9000000000000;
    const state = (pc) => ({ ts: ts++, host: 'test', evSeq: 0, pc: { ramUsedGb: 1, ramTotalGb: 2, ...pc } });
    const len = () => m.hist.cpu.length;
    m.hist.cpu.length = 0;
    m.applyState(state({ cpu: 10, seq: 41 }));
    m.applyState(state({ cpu: 10, seq: 41 })); // the same sample again, for a track change
    m.applyState(state({ cpu: 10, seq: 41 }));
    const sameSample = len();
    m.applyState(state({ cpu: 20, seq: 42 }));
    const newSample = len();
    m.applyState(state({ cpu: 29, seq: 1, runId: 'first-run' }));
    m.applyState({ ...state({}), pc: null });
    m.applyState(state({ cpu: 30, seq: 1, runId: 'second-run' }));
    m.applyState(state({ cpu: 30, seq: 1, runId: 'second-run' }));
    const restarted = len();
    m.applyState(state({ cpu: 40 })); // an older agent: no seq
    m.applyState(state({ cpu: 40 }));
    const withoutSeq = len();
    return { sameSample, newSample, restarted, withoutSeq, last: m.hist.cpu.slice(-5) };
  });
  expect(result).toEqual({ sameSample: 1, newSample: 2, restarted: 4, withoutSeq: 6, last: [20, 29, 30, 40, 40] });
});
