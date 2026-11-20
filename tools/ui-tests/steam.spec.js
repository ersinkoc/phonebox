const { test, expect } = require('@playwright/test');

async function configureSteamFriends(page, testFlag) {
  await page.goto('/panel/');
  await page.evaluate((flag) => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    const pages = [{ id: 'steam', title: 'Steam', icon: '🎮', enabled: true, widgets: [
      { id: 'steam-friends', type: 'steam', x: 0, y: 0, w: 6, h: 5, options: { offline: true } },
    ] }];
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, shortcuts: cfg.shortcuts || [] }));
    localStorage.setItem(flag, '1');
  }, testFlag);
  await page.reload();
}

async function mockSteamAvatarFetches(page, failedRequests) {
  await page.addInitScript((failCount) => {
    // demo.js keeps the fetch it finds (the browser's) and then installs its own; avatar requests are counted, and the
    // first failCount fail, in front of that one. No re-entrancy guard: a guard shared by overlapping requests sent
    // some of them past the demo to the static server, so the Steam data sometimes never loaded (CI flake).
    const native = window.fetch.bind(window);
    let inner = null;
    window.__steamAvatarRequests = 0;
    const counting = async (input, init) => {
      const url = new URL(typeof input === 'string' ? input : input.url, location.href);
      if (url.pathname.endsWith('/api/steam/avatar')) {
        window.__steamAvatarRequests++;
        if (window.__steamAvatarRequests <= failCount) return new Response('', { status: 503 });
      }
      return inner(input, init);
    };
    Object.defineProperty(window, 'fetch', {
      configurable: true,
      get() { return inner ? counting : native; },
      set(fetch) { inner = fetch; },
    });
  }, failedRequests);
}

test('Steam friends show avatars, presence, last seen, and recent activity', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.addInitScript(() => {
    const create = URL.createObjectURL.bind(URL);
    const setTimeout = window.setTimeout.bind(window);
    window.__steamAvatarUrlCount = 0;
    window.__steamAvatarRefreshCallbacks = [];
    window.__steamAvatarRefreshDelays = [];
    window.__runSteamAvatarRefreshes = () => window.__steamAvatarRefreshCallbacks.splice(0).forEach((refresh) => refresh());
    URL.createObjectURL = (...args) => {
      window.__steamAvatarUrlCount++;
      return create(...args);
    };
    window.setTimeout = (callback, delay, ...args) => {
      const holdRefresh = localStorage.getItem('pb-test-steam-fast-refresh') === '1' && delay >= 86_399_900;
      if (!holdRefresh) return setTimeout(callback, delay, ...args);
      window.__steamAvatarRefreshDelays.push(delay);
      window.__steamAvatarRefreshCallbacks.push(() => callback(...args));
      return 0;
    };
  });
  await page.goto('/panel/');
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    localStorage.setItem('pb-test-steam-fast-refresh', '1');
    const pages = [{ id: 'steam', title: 'Steam', icon: '🎮', enabled: true, widgets: [
      { id: 'steam-friends', type: 'steam', x: 0, y: 0, w: 6, h: 5, options: { offline: true } },
      { id: 'steam-activity', type: 'steam-activity', x: 6, y: 0, w: 6, h: 4, options: {} },
    ] }];
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, shortcuts: cfg.shortcuts || [] }));
  });
  await page.reload();

  const friends = page.locator('.w-steam');
  await expect(friends.locator('.st-row')).toHaveCount(2);
  await expect(friends.locator('.st-row').nth(0)).toHaveClass(/\bonline\b/);
  await expect(friends.locator('.st-row').nth(0)).toHaveClass(/\bstate-1\b/);
  await expect(friends.locator('.st-row').nth(1)).toHaveClass(/\bstate-0\b/);
  await expect(friends.locator('.st-row').nth(1)).not.toHaveClass(/\bonline\b/);
  await expect(friends.locator('.st-name').first()).toHaveText('Ada');
  await expect(friends.locator('.st-sub').first()).toHaveText('Playing Hades II');
  await expect(friends.locator('.st-sub').nth(1)).toContainText('Last seen');
  const avatar = friends.locator('.st-avatar').first();
  await expect(avatar).toHaveAttribute('src', /^blob:/);
  await expect.poll(() => avatar.evaluate((image) => image.naturalWidth)).toBeGreaterThan(0);
  const activity = page.locator('.w-steam-activity');
  await expect(activity.locator('.st-event')).toHaveCount(1);
  await expect(activity.locator('.st-event .st-name')).toHaveText('Ada');
  await expect(activity.locator('.st-event .st-sub')).toHaveText('Started playing Hades II');
  await expect(activity.locator('.st-avatar')).toHaveCount(1);
  await expect(friends.locator('.st-avatar')).toHaveCount(2);
  await expect.poll(() => page.evaluate(() => window.__steamAvatarUrlCount)).toBe(2);
  await expect.poll(() => page.evaluate(() => window.__steamAvatarRefreshCallbacks.length)).toBe(2);
  const initialAvatarSources = await page.locator('.w-steam .st-portrait[data-steam-id]')
    .evaluateAll((roots) => Object.fromEntries(roots.map((root) => [root.dataset.steamId, root.querySelector('.st-avatar')?.src])));
  expect(await page.evaluate(() => window.__steamAvatarRefreshDelays.every((delay) => delay <= 86_400_000 && delay > 86_399_000))).toBe(true);
  await page.evaluate(() => window.__runSteamAvatarRefreshes());
  await expect.poll(() => page.evaluate(() => window.__steamAvatarUrlCount)).toBe(4);
  await expect.poll(async () => page.evaluate((initial) => {
    const refreshed = Object.fromEntries([...document.querySelectorAll('.w-steam .st-portrait[data-steam-id]')]
      .map((root) => [root.dataset.steamId, root.querySelector('.st-avatar')?.src]));
    const activityAvatar = document.querySelector('.w-steam-activity .st-portrait[data-steam-id="76561198000000001"] .st-avatar');
    return Object.keys(initial).every((id) => refreshed[id] && refreshed[id] !== initial[id])
      && activityAvatar?.src === refreshed['76561198000000001'];
  }, initialAvatarSources)).toBe(true);
  const refreshedAvatarSources = await page.locator('.w-steam .st-portrait[data-steam-id]')
    .evaluateAll((roots) => Object.fromEntries(roots.map((root) => [root.dataset.steamId, root.querySelector('.st-avatar')?.src])));
  for (const id of Object.keys(initialAvatarSources)) expect(refreshedAvatarSources[id]).not.toBe(initialAvatarSources[id]);
  const ada = page.locator('.w-steam-activity .st-portrait[data-steam-id="76561198000000001"] .st-avatar');
  await expect(ada).toHaveAttribute('src', refreshedAvatarSources['76561198000000001']);
  expect(errors).toEqual([]);
});

test('Steam avatar retries after its cooldown while the portrait remains mounted', async ({ page }) => {
  await mockSteamAvatarFetches(page, 1);
  await page.addInitScript(() => {
    const setTimeout = window.setTimeout.bind(window);
    const dateNow = Date.now.bind(Date);
    let steamTimeOffset = 0;
    Date.now = () => dateNow() + steamTimeOffset;
    let shortenedSteamRetries = 0;
    window.__steamAvatarShortenedRetries = [];
    window.setTimeout = (callback, delay, ...args) => {
      const shouldShorten = localStorage.getItem('pb-test-steam-fast-retry') === '1'
        && delay >= 59_900 && delay <= 60_100 && shortenedSteamRetries < 2;
      if (shouldShorten) {
        shortenedSteamRetries++;
        window.__steamAvatarShortenedRetries.push(delay);
      }
      if (!shouldShorten) return setTimeout(callback, delay, ...args);
      return setTimeout(() => {
        steamTimeOffset += delay;
        callback(...args);
      }, 100);
    };
  });
  await configureSteamFriends(page, 'pb-test-steam-fast-retry');

  const avatars = page.locator('.w-steam .st-avatar');
  await expect(page.locator('.w-steam .st-row')).toHaveCount(2);
  await expect.poll(() => page.evaluate(() => window.__steamAvatarRequests)).toBe(3);
  expect(await page.evaluate(() => window.__steamAvatarShortenedRetries.length)).toBeGreaterThan(0);
  await expect(avatars).toHaveCount(2);
  await expect.poll(() => avatars.evaluateAll((images) => images.every((image) => image.naturalWidth > 0))).toBe(true);
});

test('Steam avatar retry is canceled after its portrait is removed', async ({ page }) => {
  await mockSteamAvatarFetches(page, Number.MAX_SAFE_INTEGER);
  await page.addInitScript(() => {
    const setTimeout = window.setTimeout.bind(window);
    window.__steamAvatarShortenedRetries = [];
    window.__steamAvatarRetryCallbacksRunAt = [];
    window.__steamPortraitsRemoved = false;
    window.setTimeout = (callback, delay, ...args) => {
      const shouldShorten = localStorage.getItem('pb-test-steam-fast-retry') === '1'
        && delay >= 59_900 && delay <= 60_100;
      if (shouldShorten) window.__steamAvatarShortenedRetries.push(delay);
      const shortened = shouldShorten ? 800 : delay;
      if (!shouldShorten) return setTimeout(callback, shortened, ...args);
      return setTimeout(() => {
        window.__steamAvatarRetryCallbacksRunAt.push({ at: Date.now(), afterRemoval: window.__steamPortraitsRemoved });
        callback(...args);
      }, shortened);
    };
  });
  await configureSteamFriends(page, 'pb-test-steam-fast-retry');

  await expect(page.locator('.w-steam .st-row')).toHaveCount(2);
  await expect.poll(() => page.evaluate(() => window.__steamAvatarRequests)).toBe(2);
  await expect.poll(() => page.evaluate(() => window.__steamAvatarShortenedRetries.length)).toBe(2);
  await page.locator('.st-portrait[data-steam-id]').evaluateAll((roots) => {
    window.__steamPortraitsRemoved = true;
    roots.forEach((root) => root.remove());
  });
  await page.waitForTimeout(900);
  const retryRuns = await page.evaluate(() => window.__steamAvatarRetryCallbacksRunAt);
  expect(retryRuns.filter((run) => run.afterRemoval)).toEqual([]);
});
