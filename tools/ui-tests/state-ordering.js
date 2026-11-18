const { expect } = require('@playwright/test');

async function staleState(page, options = {}) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
  return page.evaluate(async options => {
    const m = await import('/panel/main.js');
    const snapshot = (ts, host, cpu) => ({ ts, host, evSeq: 0, pc: { cpu, ramUsedGb: 1, ramTotalGb: 2 } });
    m.applyState(snapshot(9000000000000, 'control', 10));
    if (m.D.state.host !== 'control') throw new Error('Unaffected control failed');
    const original = window.fetch;
    let release;
    let entered;
    const ready = new Promise(r => entered = r);
    window.fetch = (url, ...rest) => {
      if (String(url).includes('/state')) {
        entered();
        return new Promise(resolve => release = () => resolve(new Response(JSON.stringify(snapshot(9000000000001, 'stale', 11)), { headers: { 'Content-Type': 'application/json' } })));
      }
      return original(url, ...rest);
    };
    const pending = m.poll();
    await ready; // the old request is suspended at the network boundary
    m.applyState(snapshot(9000000000002, 'newest', 22));
    release(); // explicitly complete the old request after the newer snapshot
    await pending;
    window.fetch = original;
    const result = { host: m.D.state.host, cpu: m.D.state.pc.cpu, hist: m.hist?.cpu?.at(-1) };
    if (options.edges) {
      m.applyState(snapshot(9000000000002, 'equal', 33));
      result.equal = m.D.state.host;
      m.applyState({ host: 'without timestamp', evSeq: 0 });
      result.withoutTimestamp = m.D.state.host;
    }
    return result;
  }, options);
}
module.exports = { staleState };

