const { test, expect } = require('@playwright/test');

// WorkBuddy rows end as completed, stopped or failed, or read unknown when the feed goes quiet (#701).
async function load(page, language = 'en') {
  await page.goto('/panel/');
  await page.evaluate((lang) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: lang },
      pages: [{ id: 'one', title: 'One', widgets: [{ id: 's', type: 'sessions', x: 0, y: 0, w: 7, h: 6, options: {} }] }],
    }));
  }, language);
  await page.reload();
}

// the agent's clock as the panel counts it, so the heartbeats below are set on the same clock
const setWork = (page, sessions, workbuddy = { installed: true, approvals: false, sessions: true }) => page.evaluate(async ({ list, wb }) => {
  const { D, emit, agentClock } = await import('/panel/main.js');
  window.onAppPause(true);
  const now = agentClock();
  D.work = {
    sessions: list.map((s, i) => ({
      id: 'workbuddy:' + s.name, tool: 'workbuddy', name: s.name, project: s.name, status: s.status, waitingFor: null,
      since: s.noTime ? 0 : now - 60_000, startedAt: now - 120_000, heartbeat: s.noTime ? 0 : now - s.beatAgo,
      recent: s.noTime ? false : s.recent ?? s.beatAgo < 90_000, outcome: null, pid: 100 + i,
    })),
    github: { user: null, notifications: 0, reviewRequests: [], myPrs: [], error: null },
    workbuddy: wb,
  };
  emit('work');
}, { list: sessions, wb: workbuddy });

const mixed = [
  { name: 'live', status: 'busy', beatAgo: 5_000 },
  { name: 'done', status: 'completed', beatAgo: 5_000 },
  { name: 'boom', status: 'failed', beatAgo: 5_000 },
  { name: 'quiet', status: 'unknown', beatAgo: 120_000 },
];

test('the headline counts finished and unknown WorkBuddy rows', async ({ page }) => {
  await load(page);
  await setWork(page, mixed);
  await expect(page.locator('button.sess')).toHaveCount(4);
  await expect(page.locator('.w-sum')).toHaveText('1 working · 2 finished · 1 unknown');
  await expect(page.locator('button.sess.stale .stale-badge')).toHaveCount(1);

  // a list of finished rows only still reads as sessions, never as a count of NaN
  await setWork(page, [{ name: 'done', status: 'completed', beatAgo: 5_000 }]);
  await expect(page.locator('.w-sum')).toHaveText('1 finished');
});

test('the finished and unknown counts are translated', async ({ page }) => {
  await load(page, 'de');
  await setWork(page, mixed);
  await expect(page.locator('.w-sum')).toHaveText('1 aktiv · 2 fertig · 1 unbekannt');
});

test('every language translates the finished and unknown counts, Romanian from both of its dictionaries', async ({ page }) => {
  await load(page);
  const result = await page.evaluate(async () => {
    const I18n = window.PhoneBoxI18n;
    const { tp } = await import('/panel/main.js');
    const missing = [];
    const ro = {};
    for (const { code } of I18n.LANGUAGES) {
      if (code === 'en') continue;
      I18n.setLang(code);
      for (const text of ['{n} finished', '{n} unknown']) {
        const out = tp(text);
        if (out === text || !out.includes('{n}')) missing.push(`${code}: ${text} -> ${out}`);
      }
      if (code === 'ro') for (const text of ['{n} working', 'WorkBuddy check', '{n} sessions']) ro[text] = tp(text);
    }
    return { missing, ro };
  });
  expect(result.missing).toEqual([]);
  expect(result.ro['{n} working']).toBe('{n} lucrează');
  expect(result.ro['WorkBuddy check']).toBe('Verificare WorkBuddy');
  expect(result.ro['{n} sessions']).toBe('{n} sesiuni');
});

test('the heartbeat age is counted on the computer\'s clock, not the phone\'s', async ({ page }) => {
  await load(page);
  // the computer's clock runs five minutes behind the phone's: a heartbeat 10 s before its clock is 10 s old
  await page.evaluate(async () => {
    const { D, applyState, emit } = await import('/panel/main.js');
    window.onAppPause(true);
    const pc = Date.now() - 300_000;
    applyState({ ...(D.state || {}), ts: pc });
    D.work = {
      sessions: [{ id: 'workbuddy:skew', tool: 'workbuddy', name: 'skew', project: 'skew', status: 'busy', waitingFor: null,
        since: pc - 60_000, startedAt: pc - 120_000, heartbeat: pc - 10_000, recent: true, outcome: null, pid: 7 }],
      github: { user: null, notifications: 0, reviewRequests: [], myPrs: [], error: null },
      workbuddy: { installed: true, approvals: true, sessions: true },
    };
    emit('work');
  });
  await page.locator('button.sess').click();
  const age = parseInt(await page.locator('.wb-check-row').nth(2).locator('strong').textContent(), 10);
  expect(age).toBeGreaterThanOrEqual(10);
  expect(age).toBeLessThan(15);
});

test('the WorkBuddy check keeps counting the heartbeat while it is open and turns stale', async ({ page }) => {
  await load(page);
  await setWork(page, [{ name: 'live', status: 'busy', beatAgo: 5_000 }]);
  await page.locator('button.sess').click();
  const rows = page.locator('.wb-check-row');
  await expect(rows).toHaveCount(3);
  await expect(rows.nth(0).locator('strong')).toHaveText('installed');
  await expect(rows.nth(1).locator('strong')).toHaveText('off');
  const beat = rows.nth(2).locator('strong');
  await expect(beat).toHaveText(/^\d+s ago$/);
  const first = parseInt(await beat.textContent(), 10);
  await expect.poll(async () => parseInt(await beat.textContent(), 10), { timeout: 5000 }).toBeGreaterThanOrEqual(first + 2);

  // the agent judges the feed stale: the open card follows the next work frame
  await setWork(page, [{ name: 'live', status: 'unknown', beatAgo: 120_000 }]);
  await expect(beat).toHaveText('stale');
  await expect(beat).toHaveClass(/wb-unknown/);
});

test('a WorkBuddy file with no time at all reads unknown, not stale', async ({ page }) => {
  await load(page);
  await setWork(page, [{ name: 'blank', status: 'busy', beatAgo: 0, noTime: true }]);
  await page.locator('button.sess').click();
  const beat = page.locator('.wb-check-row').nth(2).locator('strong');
  await expect(beat).toHaveText('unknown');
  await expect(beat).toHaveClass(/wb-unknown/);
});

test('a "recent" judgment from a frame that stopped updating turns stale once the heartbeat is 90 s old', async ({ page }) => {
  await load(page);
  // the last frame said recent, but its heartbeat is already past the window: the feed went quiet after it
  await setWork(page, [{ name: 'frozen', status: 'busy', beatAgo: 95_000, recent: true }]);
  await page.locator('button.sess').click();
  const beat = page.locator('.wb-check-row').nth(2).locator('strong');
  await expect(beat).toHaveText('stale');
  await expect(beat).toHaveClass(/wb-unknown/);
});
