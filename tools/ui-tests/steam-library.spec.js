const { test, expect } = require('@playwright/test');

// Steam library widget, against the web demo's made-up games (tools/pages/demo.js answers /api/steam/library, /games,
// /launch and draws the covers). Demo order by last played: Harbor Lights (running), Starfall Tactics, Moss Garden
// (updating 42%), Deep Circuit, Paper Comet (update queued), Lantern Keep, Glass Orchard (paused 70%), Tidal Forge,
// Quiet Summit, Iron Meadow (never played).
const config = (options, extra = {}) => ({ night: { enabled: false }, shortcuts: [], ...extra, pages: [{ id: 'test', title: 'Test', enabled: true,
  widgets: [{ id: 'lib', type: 'steam-library', x: 0, y: 0, w: 12, h: 6, options }] }] });

async function open(page, url, options, extra) {
  await page.goto(url);
  await page.evaluate((c) => { localStorage.clear(); localStorage.setItem('pb-demo-config', JSON.stringify(c)); }, config(options, extra));
  await page.reload();
}

const watch = (page) => { const errors = []; page.on('pageerror', (e) => errors.push(String(e))); return errors; };
const toast = (page, text) => page.locator('#toast').filter({ hasText: text });

test('the library shows 8 games by default, newest first, with what Steam is doing', async ({ page }) => {
  const errors = watch(page);
  await open(page, '/panel/', {});
  const tiles = page.locator('.w-steam-library .sl-tile');
  await expect(tiles).toHaveCount(8);
  await expect(tiles.locator('.sl-name')).toHaveText(['Harbor Lights', 'Starfall Tactics', 'Moss Garden', 'Deep Circuit', 'Paper Comet',
    'Lantern Keep', 'Glass Orchard', 'Tidal Forge']);
  await expect(page.locator('.w-steam-library .sl-count')).toHaveText(' 10'); // how many are installed
  await expect(tiles.nth(0)).toHaveClass(/running/);
  await expect(tiles.nth(0).locator('.sl-status')).toHaveText('Playing now');
  await expect(tiles.nth(2).locator('.sl-status')).toHaveText('Updating 42%');
  await expect(tiles.nth(2).locator('.sl-progress')).toHaveAttribute('style', /width: 42%/);
  await expect(tiles.nth(4).locator('.sl-status')).toHaveText('Update queued');
  await expect(tiles.nth(6).locator('.sl-status')).toHaveText('Paused at 70%');
  // covers load (the demo draws them); the initials stay underneath
  await expect(tiles.nth(1).locator('.sl-cover')).toHaveClass(/has-art/);
  await expect(tiles.nth(1).locator('.sl-initials')).toHaveText('ST');
  await expect(tiles.nth(1)).toHaveAttribute('aria-label', 'Starfall Tactics');
  expect(errors).toEqual([]);
});

test('favorites come first in the order they were picked; the rest can be hidden; the count is an option', async ({ page }) => {
  await open(page, '/panel/', { favorites: ['900100', '900040', '123'], limit: 4 });
  const tiles = page.locator('.w-steam-library .sl-tile');
  // 123 is not installed (any more): it is skipped, not shown empty
  await expect(tiles.locator('.sl-name')).toHaveText(['Iron Meadow', 'Deep Circuit', 'Harbor Lights', 'Starfall Tactics']);
  await expect(tiles.nth(0)).toHaveClass(/fav/);
  await expect(tiles.nth(2)).not.toHaveClass(/fav/);

  await open(page, '/panel/', { favorites: ['900040', '900100'], onlyFavorites: true });
  await expect(tiles.locator('.sl-name')).toHaveText(['Deep Circuit', 'Iron Meadow']);

  await open(page, '/panel/', { favorites: ['123'], onlyFavorites: true });
  await expect(page.locator('.w-steam-library .empty')).toHaveText('None of the favorite games is installed');

  await open(page, '/panel/', { limit: 60 });
  await expect(tiles).toHaveCount(10);
});

test('the library only shows the games until starting them is turned on', async ({ page }) => {
  await open(page, '/panel/', {});
  const tiles = page.locator('.w-steam-library .sl-tile');
  await expect(tiles).toHaveCount(8);
  await tiles.nth(1).click();
  await expect(tiles.nth(1)).not.toHaveClass(/armed/);
  await expect(toast(page, 'Let the phone start games')).toBeVisible();
});

test('with starting turned on, the first tap arms a game and the second starts it', async ({ page }) => {
  const errors = watch(page);
  await open(page, '/panel/', { allowLaunch: true });
  const tiles = page.locator('.w-steam-library .sl-tile');
  await tiles.nth(1).click();
  await expect(tiles.nth(1)).toHaveClass(/armed/);
  await expect(tiles.nth(1).locator('.sl-status')).toHaveText('Tap again to play');
  await tiles.nth(1).click();
  await expect(toast(page, 'Starting Starfall Tactics')).toBeVisible();
  // the game being played is not started twice
  await tiles.nth(0).click();
  await expect(toast(page, 'Harbor Lights is already running')).toBeVisible();

  // without asking first, one tap starts it
  await open(page, '/panel/', { allowLaunch: true, confirm: false });
  await tiles.nth(3).click();
  await expect(toast(page, 'Starting Deep Circuit')).toBeVisible();
  expect(errors).toEqual([]);
});

test('a keyboard user keeps the focus on the tile and confirms with a second Enter', async ({ page }) => {
  await open(page, '/panel/', { allowLaunch: true });
  const tile = page.locator('.w-steam-library .sl-tile').nth(1);
  await tile.focus();
  await page.keyboard.press('Enter');
  await expect(tile).toHaveClass(/armed/);
  await expect(tile).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(toast(page, 'Starting Starfall Tactics')).toBeVisible();
  await expect(tile).toBeFocused();
});

test('each widget decides for itself: the one with starting off never asks the agent', async ({ page }) => {
  const errors = watch(page);
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [
      { id: 'on', type: 'steam-library', x: 0, y: 0, w: 6, h: 6, options: { allowLaunch: true, confirm: false, limit: 2 } },
      { id: 'off', type: 'steam-library', x: 6, y: 0, w: 6, h: 6, options: { allowLaunch: false, confirm: false, limit: 2 } }] }] }));
  });
  await page.reload();
  await page.evaluate(() => {
    window.__launches = [];
    const real = window.fetch;
    window.fetch = (url, ...rest) => { if (String(url).includes('/steam/launch')) window.__launches.push(String(url)); return real(url, ...rest); };
  });
  const [on, off] = [page.locator('.w-steam-library').nth(0), page.locator('.w-steam-library').nth(1)];
  await expect(off.locator('.sl-tile')).toHaveCount(2);

  // the "off" widget: a tap only says how to turn it on, and nothing is sent to the agent
  await off.locator('.sl-tile').nth(1).click();
  await off.locator('.sl-tile').nth(1).click();
  await expect(toast(page, 'Let the phone start games')).toBeVisible();
  await expect(off.locator('.sl-tile.armed')).toHaveCount(0);
  expect(await page.evaluate(() => window.__launches)).toEqual([]);

  // the "on" widget names itself, and the agent (here the demo) checks that widget's option
  await on.locator('.sl-tile').nth(1).click();
  await expect(toast(page, 'Starting Starfall Tactics')).toBeVisible();
  const launches = await page.evaluate(() => window.__launches);
  expect(launches).toHaveLength(1);
  expect(launches[0]).toContain('widget=' + encodeURIComponent('test/on'));
  expect(errors).toEqual([]);
});

test('the agent checks the option of the widget named in the request, not of any other', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [], pages: [
      { id: 'hidden', title: 'Off', enabled: false, widgets: [{ id: 'a', type: 'steam-library', x: 0, y: 0, w: 6, h: 4, options: { allowLaunch: true } }] },
      { id: 'test', title: 'On', enabled: true, widgets: [
        { id: 'on', type: 'steam-library', x: 0, y: 0, w: 6, h: 4, options: { allowLaunch: true } },
        { id: 'off', type: 'steam-library', x: 6, y: 0, w: 6, h: 4, options: {} }] }] }));
  });
  await page.reload();
  const launch = (widget) => page.evaluate(async (widget) => {
    const res = await fetch('/agent/api/steam/launch?id=900020' + (widget ? '&widget=' + encodeURIComponent(widget) : ''), { method: 'POST', headers: { 'X-PhoneBox-Panel': '1' } });
    return { status: res.status, code: (await res.json()).code };
  }, widget);
  expect(await launch('test/off')).toEqual({ status: 400, code: 'off' });
  expect(await launch('hidden/a')).toEqual({ status: 400, code: 'off' }); // a disabled page
  expect(await launch(null)).toEqual({ status: 400, code: 'off' });
  expect(await launch('test/on')).toEqual({ status: 200, code: undefined });
});

test('the library speaks the panel language', async ({ page }) => {
  await open(page, '/panel/', { allowLaunch: true }, { display: { language: 'pt-BR' } });
  const tiles = page.locator('.w-steam-library .sl-tile');
  await expect(page.locator('.w-steam-library .w-title')).toContainText('Biblioteca da Steam');
  await expect(tiles.nth(0).locator('.sl-status')).toHaveText('Jogando agora');
  await expect(tiles.nth(2).locator('.sl-status')).toHaveText('Atualizando 42%');
  await tiles.nth(1).click();
  await expect(tiles.nth(1).locator('.sl-status')).toHaveText('Toque de novo para jogar');

  await open(page, '/panel/', {}, { display: { language: 'vi' } });
  await expect(tiles.nth(0).locator('.sl-status')).toHaveText('Đang chơi');
});

test('pickGames keeps the favorites order, skips missing ones and applies the limit', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const { pickGames } = await import('/panel/widgets/steam-library.js');
    const games = ['1', '2', '3', '4', '5'].map((id) => ({ id, name: 'G' + id }));
    const ids = (list) => list.map((g) => g.id).join();
    return {
      plain: ids(pickGames(games, [], false, 3)),
      favs: ids(pickGames(games, ['4', '9', '2'], false, 10)),
      only: ids(pickGames(games, ['4', '9', '2'], true, 10)),
      limited: ids(pickGames(games, ['5', '4', '3'], false, 2)),
      numbers: ids(pickGames(games, [3], false, 1)),
      none: ids(pickGames(undefined, ['1'], false, 5)),
    };
  });
  expect(out).toEqual({ plain: '1,2,3', favs: '4,2,1,3,5', only: '4,2', limited: '5,4', numbers: '3', none: '' });
});

test('the settings editor picks favorite games from the installed list', async ({ page }) => {
  const errors = watch(page);
  await page.setViewportSize({ width: 1280, height: 860 });
  await open(page, '/settings/#pages', { favorites: ['900040', '555'] });
  await page.locator('#canvas .wb').first().click(); // selects the widget and opens its settings
  const field = page.locator('#inspector .field').filter({ hasText: /^Favorite games \(/ });
  const boxes = field.locator('label.check');
  await expect(boxes).toHaveCount(11); // the 10 installed games by name, then the saved one that is gone
  await expect(boxes.nth(0)).toHaveText('Deep Circuit');
  await expect(boxes.nth(10)).toContainText('555');
  await expect(boxes.filter({ hasText: 'Deep Circuit' }).locator('input')).toBeChecked();
  await boxes.filter({ hasText: 'Moss Garden' }).locator('input').check();
  await boxes.filter({ hasText: 'Deep Circuit' }).locator('input').uncheck();
  await boxes.filter({ hasText: 'Iron Meadow' }).locator('input').check();
  await page.locator('#save').click();
  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.favorites);
  expect(saved).toEqual(['555', '900030', '900100']); // in the order they were ticked
  expect(errors).toEqual([]);
});
