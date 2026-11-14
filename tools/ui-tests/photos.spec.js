const { test, expect } = require('@playwright/test');

function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const url = m.location()?.url || '';
    if (/Failed to load resource/.test(m.text()) && url && !url.startsWith('http://127.0.0.1:8781')) return;
    errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

async function showPhotos(page, options = {}, asStack = false) {
  await page.goto('/panel/');
  await page.evaluate(({ options, asStack }) => {
    localStorage.clear();
    const widget = asStack
      ? { id: 'st1', type: 'stack', x: 0, y: 0, w: 12, h: 6, options: { widgets: [{ id: 'ph-child', type: 'photos', enabled: true, options }] } }
      : { id: 'ph1', type: 'photos', x: 0, y: 0, w: 12, h: 6, options };
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false },
      shortcuts: [],
      pages: [{ id: 'photos-page', title: 'Photos', icon: '📸', enabled: true, widgets: [widget] }]
    }));
  }, { options, asStack });
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

test('photo frame renders blob slide, en-GB date, clock overlay, tap next and timed rotation', async ({ page }) => {
  await page.clock.install();
  const errors = watchErrors(page);

  await showPhotos(page, {
    provider: 'icloud',
    shareUrl: 'https://www.icloud.com/sharedalbum/#B0zDemoAlbum',
    order: 'oldest',
    interval: 5,
    fit: 'contain-blur',
    transition: 'fade',
    info: 'caption-date',
    clockOverlay: true,
    tapAction: 'next',
    title: 'Trip',
  });

  const activeImg = page.locator('.w-photos .ph-layer.on .ph-img');
  await expect(activeImg).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.w-photos .ph-title')).toHaveText('Trip');
  await expect(page.locator('.w-photos .ph-clock')).toHaveText(/^\d{2}:\d{2}$/);
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');
  await expect(page.locator('.w-photos .ph-sub')).toContainText('14 Jul 2025');

  // Tap advances to second photo
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Cliffside ocean trail');
  await expect(page.locator('.w-photos .ph-sub')).toContainText('2 Aug 2025');

  // Timed rotation advances to third photo
  await page.clock.fastForward(6000);
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Midnight fjord glow');
  await expect(page.locator('.w-photos .ph-sub')).toContainText('19 Nov 2025');

  expect(errors).toEqual([]);
});

test('photo frame supports pause and info tap modes and Smart Stack embedding', async ({ page }) => {
  await page.clock.install();
  const errors = watchErrors(page);

  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    order: 'oldest',
    interval: 5,
    tapAction: 'pause',
  });
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-state')).toHaveText('Paused');
  await page.clock.fastForward(8000);
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');

  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    info: 'caption-date',
    tapAction: 'info',
  });
  await expect(page.locator('.w-photos .ph-meta')).toBeVisible();
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-meta')).toBeHidden();
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-meta')).toBeVisible();

  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    title: 'In Stack',
  }, true);
  await expect(page.locator('.stack-item.active .w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.stack-item.active .w-photos .ph-title')).toHaveText('In Stack');

  await showPhotos(page, {
    provider: 'local',
    folder: '~/OneDrive/CloudOnly',
    downloadCloudFiles: false,
  });
  await expect(page.locator('.w-photos .empty')).toContainText('Download cloud-only files on demand');

  await showPhotos(page, {
    provider: 'local',
    folder: '~/OneDrive/CloudOnly',
    downloadCloudFiles: true,
    title: 'Cloud Ready',
  });
  await expect(page.locator('.w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);

  expect(errors).toEqual([]);
});

test('settings page configures Cloud photos OAuth card, Photo frame providers, cloud warning, folder browser and Smart Stack child', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/settings/');
  await expect(page.locator('#cloudPhotosCard')).toBeAttached();

  // General › Cloud photos OAuth card
  await page.locator('nav a[href="#general"]').click();
  await expect(page.locator('#cloudPhotosCard')).toBeVisible();
  await expect(page.locator('#cloudPhotosProviders')).toContainText('OneDrive');
  await expect(page.locator('#cloudPhotosProviders')).toContainText('Google Drive');
  await expect(page.locator('#cloudPhotosProviders')).toContainText('Dropbox');

  // Layout Editor: blank page + add Photo frame widget
  await page.locator('nav a[href="#pages"]').click();
  await page.getByRole('button', { name: 'New page' }).click();
  await expect(page.locator('#modal')).toBeVisible();
  await page.getByRole('button', { name: /Blank page/ }).click();
  await page.locator('#palette button.pal', { hasText: 'Photo frame' }).click();

  const inspector = page.locator('#inspector .photos-inspector');
  await expect(inspector).toBeVisible();

  const providerSelect = inspector.locator('select').first();
  await providerSelect.selectOption('icloud');
  await expect(inspector.locator('input[placeholder*="icloud.com/sharedalbum"]')).toBeVisible();

  await providerSelect.selectOption('nextcloud');
  await expect(inspector.locator('input[placeholder*="cloud.example.com"]')).toBeVisible();
  await expect(inspector.locator('input[type="password"]')).toBeVisible();

  await providerSelect.selectOption('onedrive');
  await inspector.getByRole('button', { name: 'Browse…' }).click();
  await expect(page.locator('#modal')).toBeVisible();
  await page.locator('#modalBody button', { hasText: 'Select' }).first().click();
  await expect(page.locator('#modal')).toBeHidden();

  await inspector.getByRole('button', { name: '5m', exact: true }).click();
  await expect(inspector.locator('input[type="number"]')).toHaveValue('300');

  await inspector.getByRole('button', { name: 'Test source' }).click();
  await expect(inspector).toContainText('Found 3 photos');

  // Cloud-only folder warning and explicit download opt-in toggle
  await inspector.locator('.chip', { hasText: 'OneDrive › CloudOnly' }).click();
  await expect(inspector.locator('.photos-warn')).toBeVisible();
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud-only files detected (3)');
  const dlCheck = inspector.getByLabel('Download cloud-only files on demand');
  await expect(dlCheck).not.toBeChecked();
  await dlCheck.check();
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud download enabled');
  await expect(inspector).toContainText('photos will be downloaded onto this computer as they are shown');

  // Smart Stack child configuration
  await page.locator('#palette button.pal', { hasText: 'Smart Stack' }).click();
  await expect(page.locator('#stackQuickAdd')).toBeVisible();
  await page.locator('#stackQuickAdd').selectOption('photos');
  await page.getByRole('button', { name: '＋ Add' }).click();
  const subCfg = page.locator('.stack-sub-config .photos-inspector');
  await expect(subCfg).toBeVisible();
  await subCfg.locator('select').first().selectOption('icloud');
  await expect(subCfg.locator('input[placeholder*="icloud.com/sharedalbum"]')).toBeVisible();

  expect(errors).toEqual([]);
});

test('slow image downloads are not aborted by interval ticks while loading', async ({ page }) => {
  await page.clock.install();
  const errors = watchErrors(page);

  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    order: 'oldest',
    interval: 5,
    tapAction: 'next',
  });
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');

  // Hold the second photo download longer than multiple 5s rotation intervals
  await page.evaluate(() => {
    const origFetch = window.fetch.bind(window);
    window.__slowCalls = 0;
    window.__slowAborted = false;
    let release;
    window.__releaseSlow = () => release && release();
    const gate = new Promise((r) => { release = r; });

    window.fetch = async (input, init = {}) => {
      const url = typeof input === 'string' ? input : input.url;
      if (url.includes('/api/photos/image/')) {
        window.__slowCalls++;
        if (init?.signal) {
          init.signal.addEventListener('abort', () => { window.__slowAborted = true; }, { once: true });
        }
        await gate;
        if (init?.signal?.aborted) throw new DOMException('Aborted', 'AbortError');
      }
      return origFetch(input, init);
    };
  });

  await page.clock.fastForward(6000);
  await expect.poll(() => page.evaluate(() => window.__slowCalls)).toBe(1);

  for (let i = 0; i < 6; i++) {
    await page.clock.fastForward(2000);
  }
  expect(await page.evaluate(() => ({ calls: window.__slowCalls, aborted: window.__slowAborted }))).toEqual({
    calls: 1,
    aborted: false,
  });

  await page.evaluate(() => window.__releaseSlow());
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Cliffside ocean trail');
  expect(errors).toEqual([]);
});

test('manifest 503 backs off for 30s and empty feed cancels in-flight slide load', async ({ page }) => {
  await page.clock.install();
  const errors = watchErrors(page);

  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false },
      shortcuts: [],
      pages: [{ id: 'blank', title: 'Blank', icon: '📄', enabled: true, widgets: [] }]
    }));
  });
  await page.reload();

  await page.evaluate(() => {
    const origFetch = window.fetch.bind(window);
    window.__feedCalls = 0;
    window.__failFeed = true;
    window.__emptyFeed = false;
    window.__imgStarted = false;
    window.__imgAborted = false;
    window.__holdNextImg = false;

    window.fetch = async (input, init = {}) => {
      const url = typeof input === 'string' ? input : input.url;
      if (url.includes('/api/photos/feed')) {
        window.__feedCalls++;
        if (window.__failFeed) {
          return new Response(JSON.stringify({ error: 'temporary' }), { status: 503, headers: { 'Content-Type': 'application/json' } });
        }
        if (window.__emptyFeed) {
          return new Response(JSON.stringify({
            configured: true, provider: 'local', sourceLabel: 'Favorites',
            updatedAt: Date.now(), stale: false, error: 'Source is now empty', photos: []
          }), { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
      }
      if (url.includes('/api/photos/image/') && window.__holdNextImg) {
        window.__imgStarted = true;
        return new Promise((_, reject) => {
          if (init?.signal?.aborted) {
            window.__imgAborted = true;
            reject(new DOMException('Aborted', 'AbortError'));
            return;
          }
          init?.signal?.addEventListener('abort', () => {
            window.__imgAborted = true;
            reject(new DOMException('Aborted', 'AbortError'));
          }, { once: true });
        });
      }
      return origFetch(input, init);
    };

    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false },
      shortcuts: [],
      pages: [{
        id: 'photos-page', title: 'Photos', icon: '📸', enabled: true,
        widgets: [{ id: 'ph1', type: 'photos', x: 0, y: 0, w: 12, h: 6, options: { provider: 'local', folder: '~/Pictures/Favorites', order: 'oldest', interval: 60, tapAction: 'next' } }]
      }]
    }));
    window.PhoneBoxDemo.emit('refresh');
  });

  await expect(page.locator('.w-photos .empty')).toHaveText('Waiting for photo source from the agent');
  expect(await page.evaluate(() => window.__feedCalls)).toBe(1);

  for (let i = 0; i < 6; i++) {
    await page.clock.fastForward(2000);
  }
  expect(await page.evaluate(() => window.__feedCalls)).toBe(1);

  await page.evaluate(() => { window.__failFeed = false; });
  await page.clock.fastForward(22_000);
  await expect(page.locator('.w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);
  expect(await page.evaluate(() => window.__feedCalls)).toBe(2);

  // Trigger an in-flight image load, then deliver an empty manifest after 15m
  await page.evaluate(() => {
    window.__holdNextImg = true;
    window.__emptyFeed = true;
  });
  await page.locator('.w-photos .ph-stage').click();
  await expect.poll(() => page.evaluate(() => window.__imgStarted)).toBe(true);

  await page.clock.fastForward(15 * 60_000 + 2000);
  await expect.poll(() => page.evaluate(() => window.__imgAborted)).toBe(true);
  await expect(page.locator('.w-photos .ph-layer.on')).toHaveCount(0);
  await expect(page.locator('.w-photos .empty')).toHaveText('Source is now empty');
  expect(errors).toEqual([]);
});

test('settings browser handles Google root, OneDrive collections, Dropbox cloud paths, and test races', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/settings/');

  await page.locator('nav a[href="#pages"]').click();
  await page.getByRole('button', { name: 'New page' }).click();
  await page.getByRole('button', { name: /Blank page/ }).click();
  await page.locator('#palette button.pal', { hasText: 'Photo frame' }).click();

  const inspector = page.locator('#inspector .photos-inspector');
  const providerSelect = inspector.locator('select').first();

  await page.evaluate(() => {
    const origFetch = window.fetch.bind(window);
    window.__testRequests = [];
    window.__delayNextTest = false;
    window.__delayedTestAborted = false;
    let resolveDelayed;
    window.__finishDelayedTest = () => resolveDelayed && resolveDelayed();
    const delayedGate = () => new Promise((r) => { resolveDelayed = r; });

    window.fetch = async (input, init = {}) => {
      const url = typeof input === 'string' ? input : input.url;
      if (url.includes('/api/photos/test')) {
        try { window.__testRequests.push(JSON.parse(init.body || '{}')); } catch (_) {}
        if (window.__delayNextTest) {
          window.__delayNextTest = false;
          if (init?.signal) {
            init.signal.addEventListener('abort', () => { window.__delayedTestAborted = true; }, { once: true });
          }
          await delayedGate();
          return new Response(JSON.stringify({
            ok: true,
            message: 'STALE RESULT SHOULD NEVER APPEAR',
            count: 99,
            sourceLabel: 'Stale',
            cloudOnlyCount: 7,
            remoteStream: false,
          }), { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
      }
      return origFetch(input, init);
    };
  });

  // 1. Google My Drive -> Use this folder saves albumId: 'root'
  await providerSelect.selectOption('google');
  await inspector.getByRole('button', { name: 'Browse…' }).click();
  await expect(page.locator('#modalBody')).toContainText('My Drive');
  await page.locator('#modalBody').getByRole('button', { name: 'Use this folder' }).click();
  await expect(page.locator('#modal')).toBeHidden();
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud download notice');
  await inspector.getByRole('button', { name: 'Test source' }).click();
  await expect(inspector).toContainText('Found 3 photos');
  expect(await page.evaluate(() => window.__testRequests.at(-1)?.options)).toMatchObject({
    provider: 'google',
    folder: 'My Drive',
    albumId: 'root',
  });

  // 2. OneDrive root -> Use this folder saves albumId: 'root', and Photo Albums opens albums
  await providerSelect.selectOption('onedrive');
  await inspector.getByRole('button', { name: 'Browse…' }).click();
  await page.locator('#modalBody').getByRole('button', { name: 'Use this folder' }).click();
  await expect(page.locator('#modal')).toBeHidden();
  await inspector.getByRole('button', { name: 'Test source' }).click();
  await expect(inspector).toContainText('Found 3 photos');
  expect(await page.evaluate(() => window.__testRequests.at(-1)?.options)).toMatchObject({
    provider: 'onedrive',
    folder: '/',
    albumId: 'root',
  });

  await inspector.getByRole('button', { name: 'Browse…' }).click();
  const albumsRow = page.locator('#modalBody .browse-row', { hasText: 'Photo Albums' });
  await expect(albumsRow.getByRole('button', { name: 'Select' })).toHaveCount(0);
  await albumsRow.getByRole('button', { name: 'Open ›' }).click();
  await expect(page.locator('#modalBody').getByRole('button', { name: 'Use this folder' })).toHaveCount(0);
  await page.locator('#modalBody .browse-row', { hasText: 'Summer Trips' }).getByRole('button', { name: 'Select' }).click();
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud download notice');

  // 3. Dropbox /Camera Uploads stays remote (Cloud download notice)
  await providerSelect.selectOption('dropbox');
  await inspector.getByRole('button', { name: 'Browse…' }).click();
  await page.locator('#modalBody .browse-row', { hasText: 'Camera Uploads' }).getByRole('button', { name: 'Select' }).click();
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud download notice');

  // Typing /Camera Uploads clears albumId; Test source resolves remoteStream=true -> Cloud download notice
  const folderInput = inspector.locator('input[type="text"]').first();
  await folderInput.fill('/Camera Uploads');
  await inspector.getByRole('button', { name: 'Test source' }).click();
  await expect(inspector).toContainText('Found 3 photos');
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud download notice');

  // 4. Editing source while /api/photos/test is in-flight aborts and discards stale result
  await page.evaluate(() => { window.__delayNextTest = true; });
  await inspector.getByRole('button', { name: 'Test source' }).click();
  await expect(inspector).toContainText('Checking source…');

  await folderInput.fill('~/Pictures/Other');
  await expect.poll(() => page.evaluate(() => window.__delayedTestAborted)).toBe(true);
  await expect(inspector).not.toContainText('Checking source…');

  await page.evaluate(() => window.__finishDelayedTest());
  await expect(inspector).not.toContainText('STALE RESULT SHOULD NEVER APPEAR');
  await expect(inspector).not.toContainText('Cloud-only files detected (7)');

  expect(errors).toEqual([]);
});

test('photo frame preserves visible slide on transient fetch failure and recovers after initial failure', async ({ page }) => {
  await page.clock.install();
  const errors = watchErrors(page);

  await page.addInitScript(() => {
    let innerFetch = window.fetch;
    Object.defineProperty(window, 'fetch', {
      configurable: true,
      get() {
        return async (input, init = {}) => {
          const url = typeof input === 'string' ? input : (input?.url || '');
          if (sessionStorage.getItem('pb-test-fail-images') === '1' && url.includes('/api/photos/image/')) {
            return new Response('unavailable', { status: 503 });
          }
          return innerFetch.call(window, input, init);
        };
      },
      set(fn) { innerFetch = fn; },
    });
  });

  // 1. Initial photo displays, then image fetches fail -> tapping next preserves the current visible photo
  await page.goto('/panel/');
  await page.evaluate(() => sessionStorage.removeItem('pb-test-fail-images'));
  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    order: 'oldest',
    tapAction: 'next',
  });
  await expect(page.locator('.w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');

  await page.evaluate(() => sessionStorage.setItem('pb-test-fail-images', '1'));
  await page.locator('.w-photos .ph-stage').click();
  await page.waitForTimeout(150);
  await expect(page.locator('.w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.w-photos .empty')).toBeHidden();

  // 2. Initial image fetch fails -> shows empty state -> later manifest refresh recovers the photo
  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    order: 'oldest',
  });
  await expect(page.locator('.w-photos .empty')).toContainText('Photos could not be loaded');
  await expect(page.locator('.w-photos .ph-layer.on')).toHaveCount(0);

  await page.evaluate(() => sessionStorage.removeItem('pb-test-fail-images'));
  await page.clock.fastForward(15 * 60_000 + 2000);
  await expect(page.locator('.w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.w-photos .empty')).toBeHidden();

  expect(errors).toEqual([]);
});

