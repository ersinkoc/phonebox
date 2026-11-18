const { expect } = require('@playwright/test');

async function queuedSwitch(page, { rule = true, mode = 'pause' } = {}) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { idleReturnSec: 0, touchGraceSec: 0, minDwellSec: 0, switchCountdownSec: 0 },
      pages: ['one', 'two', 'three'].map(id => ({ id, title: id, widgets: [
        { id: id + '-note', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: id } },
      ] })),
    }));
  });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).toHaveCount(3);
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
  await page.evaluate(async ({ rule, mode }) => {
    const m = await import('/panel/main.js');
    m.applyState({ ts: 9000000000000, evSeq: 0, rulesPaused: mode === 'resume' ? Date.now() + 3600000 : null });
    m.gotoPageRef('one');
    m.requestPage({ page: 'three', rule: rule ? 'r1' : undefined, countdown: 0 });
    const original = window.fetch;
    window.fetch = (url, ...args) => String(url).includes('/rules/pause')
      ? Promise.resolve(new Response(JSON.stringify({ pausedUntil: mode === 'resume' ? null : Date.now() + 3600000 }), {
        status: mode === 'failure' ? 500 : 200, headers: { 'Content-Type': 'application/json' },
      })) : original(url, ...args);
    try { if (mode !== 'control') await m.toggleRulesPause(); }
    finally { window.fetch = original; }
    m.pump();
  }, { rule, mode });
  await page.clock.runFor(500);
  return page.locator('#dots i').evaluateAll(dots => dots.findIndex(dot => dot.classList.contains('on')));
}

module.exports = { queuedSwitch };
