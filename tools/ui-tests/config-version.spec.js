// The config version and the panel's own writes: a late answer must not take the version or the config back, a note
// save updates the config the panel holds, a save that timed out but was stored is not lost on retry, and a PC clock
// that steps back does not freeze the state.
const { test, expect } = require('@playwright/test');

async function openPanel(page) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { language: 'en' },
      pages: [{ id: 'one', title: 'One', enabled: true, widgets: [
        { id: 'n1', type: 'note', x: 0, y: 0, w: 6, h: 3, options: { text: 'old text', color: '#fbbf24' } },
      ] }],
    }));
  });
  await page.goto('/panel/');
  await expect(page.locator('.note-t')).toHaveText('old text');
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
}

test('the panel\'s own write and its config reads are ordered by request, not by version number', async ({ page }) => {
  await openPanel(page);
  const result = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    const original = window.fetch;
    const replies = [];
    window.fetch = (url, ...rest) => String(url).endsWith('/config')
      ? new Promise((resolve) => replies.push(resolve))
      : original(url, ...rest);
    const answer = (version, theme) => replies.shift()(Response.json({ version, config: { display: { theme } } }));
    try {
      // a read that left before a save answers after it: the save's version and config stay
      const late = m.loadConfig();
      const mark = m.configMark();
      const savedFirst = !m.configReadSince(mark);
      m.localWriteStored('2000');
      answer('1000', 'graphite');
      await late;
      const afterLateRead = { version: m.cfgVersion, theme: m.cfg.display?.theme ?? null };

      // a read that left after a save and applied first: the save's late answer leaves it alone
      const mark2 = m.configMark();
      const fresh = m.loadConfig();
      answer('3000', 'midnight');
      await fresh;
      const saveMayApply = !m.configReadSince(mark2);

      // a restarted agent may count from a lower version: a new read still applies it
      const restarted = m.loadConfig();
      answer('5', 'warm');
      await restarted;
      return { savedFirst, afterLateRead, saveMayApply, afterRestart: m.cfgVersion };
    } finally { window.fetch = original; }
  });
  expect(result).toEqual({
    savedFirst: true,
    afterLateRead: { version: '2000', theme: null },
    saveMayApply: false,
    afterRestart: '5',
  });
});

test('a note save updates the config the panel holds before the version', async ({ page }) => {
  await openPanel(page);
  await page.locator('.note-t').click();
  await page.locator('.note-ta').fill('saved text');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.note-t')).toHaveText('saved text');
  const held = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    return {
      text: m.cfg.pages.find((p) => p.id === 'one').widgets.find((w) => w.id === 'n1').options.text,
      cached: JSON.parse(localStorage.getItem('cfg')).pages[0].widgets[0].options.text,
    };
  });
  expect(held).toEqual({ text: 'saved text', cached: 'saved text' });
});

test('a save that timed out but was stored is saved again on top of itself, not lost', async ({ page }) => {
  await openPanel(page);
  await page.evaluate(() => {
    const original = window.fetch;
    let saves = 0;
    window.fetch = async (url, init) => {
      if (!String(url).includes('/widget/one/n1/note?')) return original(url, init);
      saves++;
      if (saves === 1) { await original(url, init); return new Promise(() => {}); } // stored, but the answer never comes
      if (saves === 2) return new Response(null, { status: 409 });                   // the agent has a newer version now
      return original(url, init);
    };
  });
  await page.locator('.note-t').click();
  const ta = page.locator('.note-ta');
  await ta.fill('first draft');
  const save = page.getByRole('button', { name: 'Save' });
  await save.click();
  await page.clock.runFor(8100);
  await expect(save).toBeEnabled();
  await ta.fill('first draft, then more');
  await save.click();
  await expect(page.locator('.note-edit')).toHaveCount(0);
  await expect(page.locator('.note-t')).toHaveText('first draft, then more');
  await expect(page.locator('#toast')).not.toContainText('changed on the computer');
});

test('a state from a PC clock that stepped back is applied, a slightly late one is not', async ({ page }) => {
  await openPanel(page);
  const hosts = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    const snap = (ts, host) => ({ ts, host, evSeq: 0, pc: { cpu: 1, ramUsedGb: 1, ramTotalGb: 2 } });
    m.applyState(snap(9000000060000, 'now'));
    m.applyState(snap(9000000058000, 'late answer'));   // 2 s older: a late poll
    const late = m.D.state.host;
    m.applyState(snap(9000000000000, 'clock stepped back')); // a minute older: the PC clock moved
    return { late, stepped: m.D.state.host };
  });
  expect(hosts).toEqual({ late: 'now', stepped: 'clock stepped back' });
});
