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
      since: now - 60_000, startedAt: now - 120_000, heartbeat: now - s.beatAgo, recent: s.beatAgo < 90_000, outcome: null, pid: 100 + i,
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
