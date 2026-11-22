// Settings > General > Plex music control: Check connection. Against the web demo; the Plex
// routes are answered by a wrapper around the page's fetch, so nothing here needs an agent, a Plex server or a player.
const { test, expect } = require('@playwright/test');

const connection = (url, token) => ({ plexUrl: url, plexToken: token, plexPlayerUrl: 'http://127.0.0.1:32500' });

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

async function open(page) {
  await page.goto('/settings/#general');
  await applyJson(page, connection('https://plex.example.test', 'token-a'));
  // the agent checks the saved settings, so the connection is saved before any check
  expect(await page.evaluate(() => save())).toBe(true);
  const block = page.locator('#controlSettings details', { hasText: 'Plex music control' }).first();
  await block.locator('summary').first().click();
  return {
    status: block.locator('p.hint').last(),
    check: block.getByRole('button', { name: 'Check Plex connection' }),
    url: block.getByLabel('Plex server address'),
    token: block.getByLabel('Plex token (X-Plex-Token)'),
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
