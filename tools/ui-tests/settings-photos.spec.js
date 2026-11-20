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
