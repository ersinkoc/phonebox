// Settings > General > Plex music control: Check connection and the playlist picker. Against the web demo; the Plex
// routes are answered by a wrapper around the page's fetch, so nothing here needs an agent, a Plex server or a player.
const { test, expect } = require('@playwright/test');

const connection = (url, token) => ({ plexUrl: url, plexToken: token, plexPlayerUrl: 'http://127.0.0.1:32500' });
const PLAYLISTS = [{ id: '12', name: 'Chill' }, { id: '13', name: 'Focus' }];

// what the Advanced section's "Apply JSON" button does with the text in its box
async function applyJson(page, controls) {
  await page.evaluate(async (controls) => {
    const cfg = await (await fetch('/api/settings')).json();
    cfg.controls = { ...cfg.controls, ...controls };
    document.getElementById('json').value = JSON.stringify(cfg);
    document.getElementById('applyJson').click();
  }, controls);
}

// every Plex request waits until the test answers it (in the order they were made) or the page aborts it
async function holdPlexRequests(page) {
  await page.evaluate(() => {
    const original = window.fetch;
    window.__held = [];
    window.fetch = (url, options) => {
      if (!String(url).startsWith('/api/controls/plex/')) return original(url, options);
      return new Promise((resolve, reject) => {
        window.__held.push((body) => resolve(body instanceof Response ? body : Response.json(body)));
        options?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
      });
    };
  });
}
const answer = (page, index, body) => page.evaluate(([i, body]) => window.__held[i](body), [index, body]);
// an answer with a status code and a raw text body (not JSON when text is not valid JSON)
const answerRaw = (page, index, status, text) => page.evaluate(([i, status, text]) => window.__held[i](new Response(text, { status })), [index, status, text]);

async function open(page) {
  await page.goto('/settings/#general');
  // the page's own first load sets the saved baseline last; settings applied before it ends would be overwritten
  await page.waitForFunction(() => typeof saved === 'string' && saved !== '');
  await applyJson(page, {
    ...connection('https://plex.example.test', 'token-a'),
    actions: [{ id: 'pl1', label: 'Mine', kind: 'plex-playlist', target: '', enabled: true }],
  });
  // the agent checks the saved settings, so the connection is saved before any check
  expect(await page.evaluate(() => save())).toBe(true);
  const block = page.locator('#controlSettings details', { hasText: 'Plex music control' }).first();
  await block.locator('summary').first().click();
  await page.locator('#controlSettings summary', { hasText: 'Mine' }).first().click();
  return {
    status: block.locator('p.hint').last(),
    check: block.getByRole('button', { name: 'Check Plex connection' }),
    url: block.getByLabel('Plex server address'),
    token: block.getByLabel('Plex token (X-Plex-Token)'),
    load: page.getByRole('button', { name: 'Load Plex playlists' }),
    picker: page.getByLabel('Choose a Plex playlist…'),
    loadStatus: page.getByRole('button', { name: 'Load Plex playlists' }).locator('xpath=following-sibling::p'),
  };
}

test('Check connection gives up when the agent does not answer', async ({ page }) => {
  await page.clock.install();
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.check.click();
  await expect(ui.status).toHaveText('Checking…');
  await page.clock.fastForward(31_000);
  await expect(ui.status).toHaveText('Control request failed');
});

test('Check connection shows the answer for the settings it was asked with', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.check.click();
  await expect(ui.status).toHaveText('Checking…');
  await answer(page, 0, { connected: true, playing: false, current: null, queue: [] });
  await expect(ui.status).toHaveText('Connected');
});

test('a Check answer that arrives after the server address changed is not shown', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.check.click();
  await expect(ui.status).toHaveText('Checking…');
  await ui.url.fill('https://other.example.test');
  await answer(page, 0, { connected: true, playing: false, current: null, queue: [] });
  await expect(ui.status).toHaveText('');
});

test('a Check answer that arrives after Apply JSON brought another server is not shown', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.check.click();
  await applyJson(page, connection('https://another.example.test', 'token-a'));
  await answer(page, 0, { connected: true, playing: false, current: null, queue: [] });
  await expect(page.locator('#controlSettings p.hint', { hasText: 'Connected' })).toHaveCount(0);
});

test('only the newest of two Check clicks writes the status', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.check.click();
  await expect.poll(() => page.evaluate(() => window.__held.length)).toBe(1);
  await ui.check.click();
  await expect.poll(() => page.evaluate(() => window.__held.length)).toBe(2);
  await answer(page, 1, { connected: false, error: 'Check the Plex token in desktop settings' });
  await expect(ui.status).toHaveText('Check the Plex token in desktop settings');
  await answer(page, 0, { connected: true, playing: false, current: null, queue: [] });
  await expect(ui.status).toHaveText('Check the Plex token in desktop settings');
});

test('Check with unsaved Plex fields asks to save first and sends nothing', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.url.fill('https://other.example.test');
  await ui.check.click();
  await expect(ui.status).toHaveText('Save your changes first');
  expect(await page.evaluate(() => window.__held.length)).toBe(0);
});

test('a Check answer that arrives after an unsaved edit does not overwrite the save-first message', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.check.click();
  await expect(ui.status).toHaveText('Checking…');
  await ui.token.fill('token-b');
  await ui.check.click();
  await expect(ui.status).toHaveText('Save your changes first');
  await answer(page, 0, { connected: true, playing: false, current: null, queue: [] });
  await expect(ui.status).toHaveText('Save your changes first');
});

test('editing a Plex field after a completed check clears the result', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.check.click();
  await answer(page, 0, { connected: true, playing: false, current: null, queue: [] });
  await expect(ui.status).toHaveText('Connected');
  await ui.url.fill('https://other.example.test');
  await expect(ui.status).toHaveText('');
});

test('Load playlists fills the picker and clears the status', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.load.click();
  await expect(ui.loadStatus).toHaveText('Loading…');
  await answer(page, 0, PLAYLISTS);
  await expect(ui.picker.locator('option')).toHaveText(['Choose a Plex playlist…', 'Chill', 'Focus']);
  await expect(ui.loadStatus).toHaveText('');
});

test('Load playlists gives up when the agent does not answer', async ({ page }) => {
  await page.clock.install();
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.load.click();
  await expect(ui.loadStatus).toHaveText('Loading…');
  await page.clock.fastForward(31_000);
  await expect(ui.loadStatus).toHaveText('Control request failed');
});

test('a playlist list that arrives after the token changed does not fill the picker', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.load.click();
  await expect(ui.loadStatus).toHaveText('Loading…');
  await ui.token.fill('token-b');
  await answer(page, 0, PLAYLISTS);
  await expect(ui.picker.locator('option')).toHaveText(['Choose a Plex playlist…']);
  await expect(ui.loadStatus).toHaveText('');
});

test('the loaded playlists are dropped when the token changes', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.load.click();
  await answer(page, 0, PLAYLISTS);
  await expect(ui.picker.locator('option')).toHaveCount(3);
  await ui.token.fill('token-b');
  await expect(ui.picker.locator('option')).toHaveText(['Choose a Plex playlist…']);
});

test('only the newest of two Load clicks writes the list', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.load.click();
  await expect.poll(() => page.evaluate(() => window.__held.length)).toBe(1);
  await ui.load.click();
  await expect.poll(() => page.evaluate(() => window.__held.length)).toBe(2);
  await answer(page, 1, [{ id: '13', name: 'Focus' }]);
  await expect(ui.picker.locator('option')).toHaveText(['Choose a Plex playlist…', 'Focus']);
  await answer(page, 0, PLAYLISTS);
  await expect(ui.picker.locator('option')).toHaveText(['Choose a Plex playlist…', 'Focus']);
});

// ---- Plex playlists widget: the inspector checklist (layout editor)
// The page starts with a plex-playlists widget selected and every Plex request held until the test answers it.
async function inspector(page, { actions = [], ids, controls = connection('https://plex.example.test', 'token-a') } = {}) {
  await page.goto('/settings/#pages');
  await page.waitForFunction(() => typeof saved === 'string' && saved !== '' && cfg && cfg.pages);
  await holdPlexRequests(page);
  // the playlists are loaded only for the saved connection, so the connection is saved first
  expect(await page.evaluate(async ([actions, controls]) => {
    cfg.controls = { ...(cfg.controls || {}), ...controls, actions, profiles: [] };
    return save();
  }, [actions, controls])).toBe(true);
  await page.evaluate(([ids]) => {
    const p = curPage();
    p.widgets.push({ id: 'pp-test', type: 'plex-playlists', x: 0, y: 0, w: 6, h: 4, options: ids ? { ids } : {} });
    selWidget = p.widgets.length - 1;
    renderInspector();
  }, [ids]);
  const field = page.locator('#inspector .field', { hasText: 'Plex playlists (none ticked = all)' });
  return { field, boxes: field.locator('label.check'), status: field.locator('p.hint'), refresh: field.getByRole('button', { name: 'Refresh' }) };
}
const cfgState = (page) => page.evaluate(() => ({ actions: cfg.controls.actions, ids: curPage().widgets.at(-1).options.ids }));

test('the inspector lists the playlists from Plex, unticked', async ({ page }) => {
  const ui = await inspector(page);
  await expect(ui.field).toBeVisible();
  await expect(ui.status).toHaveText('Loading…');
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveText(['Chill', 'Focus']);
  await expect(ui.field.locator('input:checked')).toHaveCount(0);
  await expect(ui.status).toHaveText('');
});

test('ticking a playlist creates its control and stores the id', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, PLAYLISTS);
  await ui.boxes.filter({ hasText: 'Chill' }).locator('input').check();
  const s = await cfgState(page);
  expect(s.actions).toEqual([expect.objectContaining({ id: 'plex-12', kind: 'plex-playlist', target: '12', label: 'Chill', enabled: true })]);
  expect(s.ids).toEqual(['plex-12']);
  await expect(page.locator('#save')).toBeEnabled();
  await ui.boxes.filter({ hasText: 'Chill' }).locator('input').uncheck();
  const u = await cfgState(page);
  expect(u.ids).toEqual([]);
  expect(u.actions.map((a) => a.id)).toEqual(['plex-12']);
});

test('ticking reuses a playlist control that was set up by hand and enables it', async ({ page }) => {
  const ui = await inspector(page, { actions: [{ id: 'pl1', label: 'Mine', kind: 'plex-playlist', target: '13', enabled: false }] });
  await answer(page, 0, PLAYLISTS);
  await ui.boxes.filter({ hasText: 'Focus' }).locator('input').check();
  const s = await cfgState(page);
  expect(s.actions).toHaveLength(1);
  expect(s.actions[0]).toEqual(expect.objectContaining({ id: 'pl1', enabled: true }));
  expect(s.ids).toEqual(['pl1']);
});

test('a new playlist control gets another id when plex-<id> is taken', async ({ page }) => {
  const ui = await inspector(page, { actions: [{ id: 'plex-12', label: 'Other', kind: 'shortcut', target: 'x', enabled: true }] });
  await answer(page, 0, PLAYLISTS);
  await ui.boxes.filter({ hasText: 'Chill' }).locator('input').check();
  const s = await cfgState(page);
  expect(s.actions.map((a) => a.id)).toEqual(['plex-12', 'plex-12-2']);
  expect(s.ids).toEqual(['plex-12-2']);
});

test('a full Control Deck leaves the box unticked and shows a message', async ({ page }) => {
  const actions = Array.from({ length: 512 }, (_, i) => ({ id: 'a' + i, label: 'A' + i, kind: 'shortcut', target: 'x', enabled: true }));
  const ui = await inspector(page, { actions });
  await answer(page, 0, PLAYLISTS);
  const box = ui.boxes.filter({ hasText: 'Chill' }).locator('input');
  await box.click();
  await expect(box).not.toBeChecked();
  await expect(page.locator('#toast')).toContainText('Control Deck is full');
  const s = await cfgState(page);
  expect(s.actions).toHaveLength(512);
  expect(s.ids).toBeUndefined();
});

test('a saved playlist the server no longer has shows as unavailable and can be removed', async ({ page }) => {
  const ui = await inspector(page, { ids: ['plex-99'], actions: [{ id: 'plex-99', label: 'Gone', kind: 'plex-playlist', target: '99', enabled: true }] });
  await expect(ui.boxes).toHaveText(['Gone']);
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveText(['Chill', 'Focus', /Unavailable:? Gone/]);
  await ui.boxes.filter({ hasText: 'Unavailable' }).locator('input').uncheck();
  expect((await cfgState(page)).ids).toEqual([]);
});

for (const body of [[], {}]) {
  test(`an answer of ${JSON.stringify(body)} shows that no playlists were found`, async ({ page }) => {
    const ui = await inspector(page);
    await answer(page, 0, body);
    await expect(ui.status).toContainText('No Plex playlists found');
    await expect(ui.boxes).toHaveCount(0);
  });
}

test('bad entries are skipped', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, [{ id: 5, name: 'N' }, { id: 'abc', name: 'A' }, 'x', null, { id: '14' }]);
  await expect(ui.boxes).toHaveText(['14']);
});

test('a playlist name is shown as text', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, [{ id: '12', name: '<b>x</b>' }]);
  await expect(ui.boxes).toHaveText(['<b>x</b>']);
  await expect(ui.field.locator('b')).toHaveCount(0);
});

test('a list that arrives after the token changed is ignored', async ({ page }) => {
  const ui = await inspector(page);
  await page.evaluate(() => { cfg.controls.plexToken = 'token-b'; });
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveCount(0);
  await expect(ui.status).toHaveText('');
});

test('a list that arrives after Apply JSON brought another server is ignored', async ({ page }) => {
  const ui = await inspector(page);
  await applyJson(page, connection('https://another.example.test', 'token-a'));
  await answer(page, 0, PLAYLISTS);
  await expect(page.locator('#inspector .field label.check', { hasText: 'Chill' })).toHaveCount(0);
});

test('only the newest of two Refresh clicks fills the list', async ({ page }) => {
  const ui = await inspector(page);
  await ui.refresh.click();
  await expect.poll(() => page.evaluate(() => window.__held.length)).toBe(2);
  await answer(page, 1, [{ id: '13', name: 'Focus' }]);
  await expect(ui.boxes).toHaveText(['Focus']);
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveText(['Focus']);
});

test('the checklist gives up when the agent does not answer', async ({ page }) => {
  await page.clock.install();
  const ui = await inspector(page);
  await expect(ui.status).toHaveText('Loading…');
  await page.clock.fastForward(31_000);
  await expect(ui.status).toHaveText('Control request failed');
});

// ---- Tester additions: spec edge cases 1, 4, 5, 8, 16, 17 and the stale list after a connection change
const NO_CONNECTION = connection('', '');
const CHILL = { id: 'plex-12', label: 'Chill', kind: 'plex-playlist', target: '12', enabled: true };

test('edge 1: no Plex connection gives an empty answer, saved ticks still show and can be unticked', async ({ page }) => {
  const ui = await inspector(page, { ids: ['plex-12'], actions: [CHILL], controls: NO_CONNECTION });
  await answer(page, 0, []);
  await expect(ui.status).toContainText('No Plex playlists found. Save the server address and token first.');
  await expect(ui.boxes).toHaveText([/Unavailable: Chill/]);
  await expect(ui.boxes.locator('input')).toBeChecked();
  await ui.boxes.locator('input').uncheck();
  expect((await cfgState(page)).ids).toEqual([]);
});

for (const [name, status, text] of [['a non-2xx answer', 500, '{"error":"x"}'], ['a body that is not JSON', 200, '<html>oops</html>']]) {
  test(`edge 4: ${name} shows the request failed and keeps the saved ticks`, async ({ page }) => {
    const ui = await inspector(page, { ids: ['plex-12'], actions: [CHILL] });
    await answerRaw(page, 0, status, text);
    await expect(ui.status).toHaveText('Control request failed');
    await expect(ui.boxes).toHaveText(['Chill']);
    await expect(ui.boxes.locator('input')).toBeChecked();
  });
}

test('edge 5: a string answer shows that no playlists were found', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, 'x');
  await expect(ui.status).toContainText('No Plex playlists found');
  await expect(ui.boxes).toHaveCount(0);
});

test('edge 5: a null answer shows that the request failed', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, null);
  await expect(ui.status).toHaveText('Control request failed');
  await expect(ui.boxes).toHaveCount(0);
});

test('a list with only bad entries shows that no playlists were found and no boxes', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, [{ id: 5 }, { id: 'abc' }, 'x']);
  await expect(ui.status).toContainText('No Plex playlists found');
  await expect(ui.boxes).toHaveCount(0);
});

test('unticking a playlist removes every control id that targets it and keeps the controls', async ({ page }) => {
  const actions = [
    { id: 'a', label: 'A', kind: 'plex-playlist', target: '12', enabled: true },
    { id: 'b', label: 'B', kind: 'plex-playlist', target: '12', enabled: true },
  ];
  const ui = await inspector(page, { ids: ['a', 'b'], actions });
  await answer(page, 0, PLAYLISTS);
  const box = ui.boxes.filter({ hasText: 'Chill' }).locator('input');
  await expect(box).toBeChecked();
  await box.uncheck();
  const s = await cfgState(page);
  expect(s.ids).toEqual([]);
  expect(s.actions.map((a) => a.id)).toEqual(['a', 'b']);
});

test('edge 8: a saved tick that points to no control shows its raw id and can be unticked', async ({ page }) => {
  const ui = await inspector(page, { ids: ['plex-77'] });
  await expect(ui.boxes).toHaveText(['plex-77']);
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveText(['Chill', 'Focus', /Unavailable: plex-77/]);
  await ui.boxes.filter({ hasText: 'plex-77' }).locator('input').uncheck();
  expect((await cfgState(page)).ids).toEqual([]);
});

test('edge 17: ticks are stored in playlist order, not tick order', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, PLAYLISTS);
  await ui.boxes.filter({ hasText: 'Focus' }).locator('input').check();
  await ui.boxes.filter({ hasText: 'Chill' }).locator('input').check();
  expect((await cfgState(page)).ids).toEqual(['plex-12', 'plex-13']);
});

test('edge 16: a second plex-playlists widget reuses the control the first one created', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, PLAYLISTS);
  await ui.boxes.filter({ hasText: 'Chill' }).locator('input').check();
  await page.evaluate(() => {
    curPage().widgets.push({ id: 'pp-test-2', type: 'plex-playlists', x: 0, y: 4, w: 6, h: 4, options: {} });
    selWidget = curPage().widgets.length - 1;
    renderInspector();
  });
  const second = page.locator('#inspector .field', { hasText: 'Plex playlists (none ticked = all)' });
  await expect(second.locator('label.check', { hasText: 'Chill' }).locator('input')).not.toBeChecked();
  await second.locator('label.check', { hasText: 'Chill' }).locator('input').check();
  const s = await cfgState(page);
  expect(s.actions.filter((a) => a.kind === 'plex-playlist')).toHaveLength(1);
  expect(s.ids).toEqual(['plex-12']);
});

test('a list loaded for an earlier connection is not shown after the connection changes', async ({ page }) => {
  const ui = await inspector(page, { ids: ['plex-12'], actions: [CHILL] });
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveText(['Chill', 'Focus']);
  await page.evaluate(() => { cfg.controls.plexToken = 'token-b'; renderInspector(); });
  await expect(page.locator('#inspector .field label.check', { hasText: 'Focus' })).toHaveCount(0);
  await expect(page.locator('#inspector .field label.check')).toHaveText(['Chill']);
});

test('a live playlist with two controls on its target is not shown as unavailable', async ({ page }) => {
  const actions = [
    { id: 'a', label: 'A', kind: 'plex-playlist', target: '12', enabled: true },
    { id: 'b', label: 'B', kind: 'plex-playlist', target: '12', enabled: true },
  ];
  const ui = await inspector(page, { ids: ['a', 'b'], actions });
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveText(['Chill', 'Focus']);
});

test('edge 15: a Plex playlists widget inside a stack writes its ids to the stack item', async ({ page }) => {
  await page.goto('/settings/#pages');
  await page.waitForFunction(() => typeof saved === 'string' && saved !== '' && cfg && cfg.pages);
  await holdPlexRequests(page);
  expect(await page.evaluate(async (controls) => {
    cfg.controls = { ...(cfg.controls || {}), ...controls, actions: [], profiles: [] };
    return save();
  }, connection('https://plex.example.test', 'token-a'))).toBe(true);
  await page.evaluate(() => {
    const p = curPage();
    p.widgets.push({ id: 'st-test', type: 'stack', x: 0, y: 0, w: 6, h: 4, options: { widgets: [{ id: 'pp-in', type: 'plex-playlists', enabled: true, options: {} }] } });
    selWidget = p.widgets.length - 1;
    activeStackSubIdx = 0;
    renderInspector();
  });
  const field = page.locator('#inspector .stack-sub-config .field', { hasText: 'Plex playlists (none ticked = all)' });
  await expect(field).toBeVisible();
  await answer(page, 0, PLAYLISTS);
  await field.locator('label.check', { hasText: 'Chill' }).locator('input').check();
  const s = await page.evaluate(() => ({ ids: curPage().widgets.at(-1).options.widgets[0].options.ids, actions: cfg.controls.actions.map((a) => a.id) }));
  expect(s.ids).toEqual(['plex-12']);
  expect(s.actions).toEqual(['plex-12']);
});

test('the checklist asks to save first when the Plex fields are not saved, and loads nothing', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, PLAYLISTS);
  await expect(ui.boxes).toHaveCount(2);
  await page.evaluate(() => { cfg.controls.plexToken = 'token-unsaved'; });
  await ui.refresh.click();
  await expect(ui.status).toHaveText('Save your changes first');
  expect(await page.evaluate(() => window.__held.length)).toBe(1);
});

test('Load Plex playlists asks to save first when the Plex fields are not saved, and sends nothing', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.token.fill('token-unsaved');
  await ui.load.click();
  await expect(ui.loadStatus).toHaveText('Save your changes first');
  expect(await page.evaluate(() => window.__held.length)).toBe(0);
  await expect(ui.picker.locator('option')).toHaveText(['Choose a Plex playlist…']);
});

test('a failed playlist read shows its reason in the checklist and stores no list', async ({ page }) => {
  const ui = await inspector(page);
  await answer(page, 0, { error: 'Check the Plex token in desktop settings' });
  await expect(ui.status).toHaveText('Check the Plex token in desktop settings');
  await expect(ui.boxes).toHaveCount(0);
  expect(await page.evaluate(() => plexList)).toBe(null);
});

test('a failed playlist read shows its reason under Load Plex playlists', async ({ page }) => {
  const ui = await open(page);
  await holdPlexRequests(page);
  await ui.load.click();
  await answer(page, 0, { error: 'The Plex server is unavailable' });
  await expect(ui.loadStatus).toHaveText('The Plex server is unavailable');
  await expect(ui.picker.locator('option')).toHaveText(['Choose a Plex playlist…']);
});
