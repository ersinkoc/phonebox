// Settings › General › Photo frame: the Immich album picker. Against the web demo; the album request is answered by a
// wrapper around the page's fetch (the demo already answers /api in the page), so nothing here needs an agent or an
// Immich server.
const { test, expect } = require('@playwright/test');

const ALBUM_A = '11111111-1111-1111-1111-111111111111';
const ALBUM_B = '22222222-2222-2222-2222-222222222222';

const immich = (album, name) => ({
  source: 'immich', folder: '', subfolders: true,
  immichUrl: 'http://192.168.1.10:2283', immichKey: 'placeholder-key', immichAlbum: album, immichAlbumName: name,
});

// the page's fetch answers /api/immich/albums with these albums, or never answers (until the request is aborted)
async function answerAlbums(page, albums) {
  await page.evaluate((albums) => {
    const original = window.fetch;
    window.fetch = (url, options) => {
      if (String(url) !== '/api/immich/albums') return original(url, options);
      if (albums === null) {
        return new Promise((_, reject) => options?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))));
      }
      return Promise.resolve(Response.json({ albums }));
    };
  }, albums);
}

// what the Advanced section's "Apply JSON" button does with the text in its box
async function applyJson(page, photos) {
  await page.evaluate(async (photos) => {
    const cfg = await (await fetch('/api/settings')).json();
    cfg.photos = photos;
    document.getElementById('json').value = JSON.stringify(cfg);
    document.getElementById('applyJson').click();
  }, photos);
}

test('the album list follows the settings applied from JSON', async ({ page }) => {
  await page.goto('/settings/#general');
  await expect(page.locator('#photosSource')).toBeVisible();

  await applyJson(page, immich(ALBUM_A, 'Family trip'));
  const albums = page.locator('#photosImmichAlbum');
  await expect(albums).toBeVisible();
  await expect(albums).toHaveValue(ALBUM_A);
  await expect(albums.locator('option:checked')).toHaveText('Family trip');

  await applyJson(page, immich(ALBUM_B, 'Garden'));
  await expect(albums).toHaveValue(ALBUM_B);
  await expect(albums.locator('option:checked')).toHaveText('Garden');
});

test('loading the albums gives up when the agent does not answer', async ({ page }) => {
  await page.clock.install();
  await page.goto('/settings/#general');
  await applyJson(page, immich('', ''));
  await answerAlbums(page, null);
  await expect(page.locator('#photosImmichLoad')).toBeVisible();
  await page.locator('#photosImmichLoad').click();
  const state = page.locator('#photosImmichState');
  await expect(state).toHaveText('Loading…');
  await page.clock.fastForward(36_000);
  await expect(state).toHaveText('Could not check');
});

test('a loaded album list shows the albums and keeps the chosen one', async ({ page }) => {
  await page.goto('/settings/#general');
  await applyJson(page, immich(ALBUM_B, 'Garden'));
  await answerAlbums(page, [{ id: ALBUM_A, name: 'Family trip', count: 128 }, { id: ALBUM_B, name: 'Garden', count: 42 }]);
  await page.locator('#photosImmichLoad').click();
  const albums = page.locator('#photosImmichAlbum');
  await expect(page.locator('#photosImmichState')).toHaveText('2 albums');
  await expect(albums).toHaveValue(ALBUM_B);
  await expect(albums.locator('option')).toHaveText(['Choose an album…', 'Family trip (128)', 'Garden (42)']);
  await albums.selectOption(ALBUM_A);
  await expect(page.locator('#save')).toBeEnabled();
});

// ---- the album list belongs to the address and key it was loaded with ----

const LOADED = [{ id: ALBUM_A, name: 'Family trip', count: 128 }, { id: ALBUM_B, name: 'Garden', count: 42 }];

// every album request waits until the test answers it, in the order they were made
async function holdAlbumRequests(page) {
  await page.evaluate(() => {
    const original = window.fetch;
    window.__held = [];
    window.fetch = (url, options) => {
      if (String(url) !== '/api/immich/albums') return original(url, options);
      return new Promise((resolve, reject) => {
        window.__held.push((albums) => resolve(Response.json({ albums })));
        options?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
      });
    };
  });
}
const answerRequest = (page, index, albums) => page.evaluate(([i, albums]) => window.__held[i](albums), [index, albums]);

async function loadedAlbumsFrom(page) {
  await page.goto('/settings/#general');
  await applyJson(page, immich(ALBUM_B, 'Garden'));
  await answerAlbums(page, LOADED);
  await page.locator('#photosImmichLoad').click();
  await expect(page.locator('#photosImmichState')).toHaveText('2 albums');
  await expect(page.locator('#photosImmichAlbum option')).toHaveCount(3);
}

test('the album list is dropped when the address changes', async ({ page }) => {
  await loadedAlbumsFrom(page);
  await page.locator('[data-bind="photos.immichUrl"]').fill('http://192.168.1.99:2283');
  await expect(page.locator('#photosImmichAlbum option')).toHaveText(['Garden']); // only the chosen album, not the other server's
  await expect(page.locator('#photosImmichAlbum')).toHaveValue(ALBUM_B);
  await expect(page.locator('#photosImmichState')).toHaveText('');
});

test('the album list is dropped when the key changes', async ({ page }) => {
  await loadedAlbumsFrom(page);
  await page.locator('[data-bind="photos.immichKey"]').fill('another-key');
  await expect(page.locator('#photosImmichAlbum option')).toHaveText(['Garden']);
  await expect(page.locator('#photosImmichState')).toHaveText('');
});

test('the album list is dropped when Apply JSON brings another server', async ({ page }) => {
  await loadedAlbumsFrom(page);
  await applyJson(page, { ...immich(ALBUM_B, 'Garden'), immichUrl: 'http://192.168.1.99:2283' });
  await expect(page.locator('#photosImmichAlbum option')).toHaveText(['Garden']);
});

test('the album list stays when Apply JSON brings the same server and another album', async ({ page }) => {
  await loadedAlbumsFrom(page);
  await applyJson(page, immich(ALBUM_A, 'Family trip'));
  await expect(page.locator('#photosImmichAlbum option')).toHaveText(['Choose an album…', 'Family trip (128)', 'Garden (42)']);
  await expect(page.locator('#photosImmichAlbum')).toHaveValue(ALBUM_A);
});

test('an answer that arrives after the address changed is dropped', async ({ page }) => {
  await page.goto('/settings/#general');
  await applyJson(page, immich(ALBUM_B, 'Garden'));
  await holdAlbumRequests(page);
  await page.locator('#photosImmichLoad').click();
  await page.locator('[data-bind="photos.immichUrl"]').fill('http://192.168.1.99:2283');
  await answerRequest(page, 0, LOADED);
  await expect(page.locator('#photosImmichState')).not.toHaveText('2 albums');
  await expect(page.locator('#photosImmichAlbum option')).toHaveText(['Garden']);
});

test('only the newest load counts', async ({ page }) => {
  await page.goto('/settings/#general');
  await applyJson(page, immich(ALBUM_B, 'Garden'));
  await holdAlbumRequests(page);
  await page.locator('#photosImmichLoad').click();
  await page.locator('#photosImmichLoad').click();
  await answerRequest(page, 1, LOADED); // the second request answers first
  await expect(page.locator('#photosImmichState')).toHaveText('2 albums');
  await answerRequest(page, 0, [{ id: ALBUM_A, name: 'Old answer', count: 1 }]); // the first one is late
  await expect(page.locator('#photosImmichAlbum option')).toHaveText(['Choose an album…', 'Family trip (128)', 'Garden (42)']);
});

test('the key is not written into the page markup', async ({ page }) => {
  await loadedAlbumsFrom(page);
  await page.locator('[data-bind="photos.immichKey"]').fill('sk-markup-check-7421');
  await page.locator('#photosImmichLoad').click();
  await expect(page.locator('#photosImmichState')).toHaveText('2 albums');
  expect(await page.content()).not.toContain('sk-markup-check-7421');
});

test('a load that times out after the address changed leaves no Loading… text behind', async ({ page }) => {
  await page.clock.install();
  await page.goto('/settings/#general');
  await applyJson(page, immich('', ''));
  await answerAlbums(page, null); // the request never answers, and is rejected when it is aborted
  const state = page.locator('#photosImmichState');
  await page.locator('#photosImmichLoad').click();
  await expect(state).toHaveText('Loading…');
  await page.locator('[data-bind="photos.immichUrl"]').fill('http://192.168.1.99:2283');
  await page.clock.fastForward(36_000);
  await expect(state).toHaveText(''); // not "Loading…", and not a failure for an address nobody asked about
});

test('an older request that times out does not change the status of a newer one', async ({ page }) => {
  await page.clock.install();
  await page.goto('/settings/#general');
  await applyJson(page, immich('', ''));
  await holdAlbumRequests(page);
  const state = page.locator('#photosImmichState');
  await page.locator('#photosImmichLoad').click();
  await page.clock.fastForward(20_000);
  await page.locator('#photosImmichLoad').click(); // the second request starts 20 s later
  await expect(state).toHaveText('Loading…');
  await page.clock.fastForward(20_000); // the first one's 35 s are up, the second one's are not
  await expect(state).toHaveText('Loading…');
});
