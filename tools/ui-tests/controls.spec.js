const { test, expect } = require('@playwright/test');

async function show(page, type, options = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ type, options }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'control', type, x: 0, y: 0, w: 12, h: 6, options }] }] }));
  }, { type, options });
  await page.goto('/panel/');
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

test('application mixer changes only the chosen session and reports its actual state', async ({ page }) => {
  await show(page, 'audio-mixer');
  const spotify = page.getByRole('slider', { name: 'Spotify volume', exact: true });
  const browser = page.getByRole('slider', { name: 'Browser volume', exact: true });
  await expect(spotify).toHaveValue('65');
  await spotify.focus(); await spotify.press('ArrowLeft');
  await expect(spotify).toHaveValue('64'); await expect(browser).toHaveValue('30');
  await expect.poll(() => page.evaluate(async () => (await (await fetch('/agent/api/controls/state')).json()).audio.sessions.find(s => s.id === 'spotify').volume)).toBe(64);
  await page.getByRole('button', { name: 'Mute Spotify', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Mute Spotify', exact: true })).toHaveAttribute('aria-pressed', 'true');
});

test('output selection follows the authoritative default device', async ({ page }) => {
  await show(page, 'audio-outputs');
  const headphones = page.getByRole('button', { name: 'Headphones', exact: true });
  await headphones.click();
  await expect(headphones).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: 'Desk speakers', exact: true })).toHaveAttribute('aria-pressed', 'false');
});

test('push-to-talk releases the microphone after pointer release', async ({ page }) => {
  await show(page, 'push-to-talk');
  const hold = page.getByRole('button', { name: 'Hold to talk' });
  await expect(hold).toBeEnabled();
  await page.evaluate(() => { const original = window.fetch; window.controlRequests = [];
    window.fetch = (url, init) => { if (String(url).includes('/controls/command/ptt')) window.controlRequests.push(new URL(url, location.href).searchParams.get('op')); return original(url, init); }; });
  await hold.dispatchEvent('pointerdown', { pointerId: 1 });
  await expect.poll(() => page.evaluate(() => window.controlRequests.includes('begin'))).toBe(true);
  await hold.dispatchEvent('pointerup', { pointerId: 1 });
  await expect.poll(() => page.evaluate(() => window.controlRequests.includes('end'))).toBe(true);
});

test('unconfigured power and audio listening remain disabled', async ({ page }) => {
  await show(page, 'power-controls');
  await expect(page.getByRole('button', { name: /Hold:/ })).toHaveCount(3);
  for (const button of await page.getByRole('button', { name: /Hold:/ }).all()) await expect(button).toBeDisabled();
  await show(page, 'pc-listen');
  await expect(page.getByRole('button', { name: 'Listen', exact: true })).toBeDisabled();
});

test('unsupported player modes are disabled instead of appearing to work', async ({ page }) => {
  await show(page, 'media-modes', { player: 'browser' });
  await expect(page.getByRole('button', { name: 'Shuffle', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Repeat', exact: true })).toBeDisabled();
});

test('OBS mixer mute is reflected by the integration state', async ({page})=>{
  await show(page,'obs-mixer',{control:true});
  await expect(page.getByRole('slider',{name:'Desktop Audio volume',exact:true})).toHaveValue('70');
  await page.getByRole('button',{name:'Mute Desktop Audio',exact:true}).click();
  await expect.poll(()=>page.evaluate(async()=>(await(await fetch('/agent/api/controls/obs')).json()).inputs[0].muted)).toBe(true);
  await expect(page.getByRole('button',{name:'Mute Desktop Audio',exact:true})).toHaveText('Unmute');
});

test('Spotify queue starts the selected item through an ID-only request',async({page})=>{
  await show(page,'media-queue');
  await page.getByRole('button',{name:'Paper Lanterns · Neon Harbor',exact:true}).click();
  await expect.poll(()=>page.evaluate(async()=>(await(await fetch('/agent/api/controls/spotify/state')).json()).current.name)).toBe('Paper Lanterns');
});

// #65's idea: the queue shows what plays now and what comes next, with covers from Spotify's image server only.
test('Spotify queue shows now playing and up next with covers, and covers can be turned off',async({page})=>{
  const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==','base64');
  const asked=[];
  await page.route('https://i.scdn.co/**',route=>{asked.push(route.request().url());route.fulfill({contentType:'image/png',body:png});});
  // covers off first, on a fresh page: no request to Spotify's image server at all
  await show(page,'media-queue',{covers:false});
  await expect(page.locator('.sq-item').first()).toBeVisible();
  await expect(page.locator('.w img')).toHaveCount(0);
  expect(asked).toEqual([]);

  await show(page,'media-queue');
  await expect(page.locator('.sq-now .sq-label')).toHaveText('Now playing');
  await expect(page.locator('.sq-head')).toHaveText('Up next');
  await expect(page.locator('.sq-now img')).toHaveCount(1);
  expect(await page.locator('.sq-item img').count()).toBeGreaterThan(0);
});

test('OBS controls are refused until a widget allows control, as on the agent', async ({page})=>{
  await show(page,'obs-mixer');
  await page.getByRole('button',{name:'Mute Desktop Audio',exact:true}).click();
  await expect(page.locator('.toast').filter({hasText:'Enable OBS control'})).toBeVisible();
  expect(await page.evaluate(async()=>(await(await fetch('/agent/api/controls/obs')).json()).inputs[0].muted)).toBe(false);
});

test('a fader shows its level as the filled part of the track and follows the computer after a tap', async ({page})=>{
  await show(page,'audio-mixer');
  const spotify=page.getByRole('slider',{name:'Spotify volume',exact:true});
  await expect(spotify).toHaveValue('65');
  expect(await spotify.evaluate(el=>el.style.getPropertyValue('--p'))).toBe('65%');
  // a touch that does not move the fader sends no change; the next state must still move it
  await spotify.dispatchEvent('pointerdown');await spotify.dispatchEvent('pointerup');
  await page.evaluate(()=>fetch('/agent/api/controls/command/session?id=spotify&value=20',{method:'POST'}));
  await expect(spotify).toHaveValue('20');
  expect(await page.locator('.control-deck').evaluate(el=>parseFloat(getComputedStyle(el).paddingLeft))).toBeGreaterThan(0);
});

test('meeting timer keeps elapsed time after reloading',async({page})=>{
  await show(page,'meeting-timer');
  await page.getByRole('button',{name:'Start',exact:true}).click();
  await expect(page.locator('.deck-track')).not.toHaveText('0:00');
  await page.getByRole('button',{name:'Pause',exact:true}).click();
  const elapsed=await page.locator('.deck-track').textContent();
  await page.reload();
  await expect(page.locator('.deck-track')).toHaveText(elapsed);
  await page.getByRole('button',{name:'Reset',exact:true}).click();
  await expect(page.locator('.deck-track')).toHaveText('0:00');
});

test('desktop control bindings and captured profiles survive Save without leaking to public config',async({page})=>{
  await page.setViewportSize({width:1280,height:860});await page.goto('/settings/#general');
  const root=page.locator('#controlSettings');
  await root.getByRole('button',{name:'Add control',exact:true}).click();
  await root.getByRole('textbox',{name:'Control label',exact:true}).fill('Private sound');
  await root.getByRole('combobox',{name:'Control kind',exact:true}).selectOption('sound');
  await root.getByRole('textbox',{name:'Target',exact:true}).fill('C:\\private-test\\sound.wav');
  await root.getByRole('button',{name:'Capture current audio profile',exact:true}).click();
  await expect(root.getByRole('textbox',{name:'Profile label',exact:true})).toHaveValue('Captured preview profile');
  await page.locator('#save').click();await expect(page.locator('#save')).toBeDisabled();
  await page.reload();
  await root.locator('summary').filter({hasText:'Private sound'}).click();
  await expect(root.getByRole('textbox',{name:'Target',exact:true})).toHaveValue('C:\\private-test\\sound.wav');
  const publicConfig=await page.evaluate(async()=>JSON.stringify(await(await fetch('/agent/api/config')).json()));
  expect(publicConfig).not.toContain('private-test');expect(publicConfig).not.toContain('Private sound');expect(JSON.parse(publicConfig).config.controls).toBeUndefined();
});

for(const viewport of [{width:780,height:360},{width:360,height:780}]){
  test('audio controls stay within the widget at '+viewport.width+'px',async({page})=>{
    await page.setViewportSize(viewport);await show(page,'audio-mixer');
    await expect(page.getByRole('slider',{name:'Spotify volume',exact:true})).toBeVisible();
    const box=page.locator('.control-deck');
    expect(await box.evaluate(el=>el.scrollWidth<=el.clientWidth+1)).toBe(true);
    await page.screenshot({path:'../../.temp_files/control-audio-'+viewport.width+'.png'});
  });
}

test('radio cards ignore overlapping taps while a station toggle command is in flight', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false },
      shortcuts: [],
      controls: {
        actions: [
          { id: 'r1', label: 'Jazz FM', kind: 'radio', target: 'https://example.com/jazz.mp3' },
          { id: 'r2', label: 'Rock FM', kind: 'radio', target: 'https://example.com/rock.mp3' },
        ],
      },
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'radio-w', type: 'radio', x: 0, y: 0, w: 12, h: 6, options: { view: 'grid' } }] }],
    }));
  });
  await page.reload();
  const jazz = page.getByRole('button', { name: /Jazz FM/ });
  const rock = page.getByRole('button', { name: /Rock FM/ });
  await expect(jazz).toBeVisible();
  await expect(rock).toBeVisible();

  await page.evaluate(() => {
    const orig = window.fetch.bind(window);
    window.__radioCmds = [];
    window.__gateNextCmd = true;
    let release;
    window.__releaseCmd = () => release && release();
    const gate = new Promise(r => { release = r; });
    window.fetch = async (url, init) => {
      if (String(url).includes('/controls/command/action')) {
        window.__radioCmds.push(new URL(url, location.href).searchParams.get('id'));
        if (window.__gateNextCmd) {
          window.__gateNextCmd = false;
          await gate;
        }
      }
      return orig(url, init);
    };
  });

  await jazz.click();
  await expect.poll(() => page.evaluate(() => window.__radioCmds)).toEqual(['r1']);
  await expect(jazz).toHaveAttribute('aria-pressed', 'true');

  // Tapping another station while Jazz FM's command is still in flight must not send a second POST
  await rock.click();
  await page.waitForTimeout(80);
  expect(await page.evaluate(() => window.__radioCmds)).toEqual(['r1']);

  await page.evaluate(() => window.__releaseCmd());
  await expect(jazz).toHaveAttribute('aria-pressed', 'true');

  // Once the first command settles, tapping Rock FM switches playback normally
  await rock.click();
  await expect.poll(() => page.evaluate(() => window.__radioCmds)).toEqual(['r1', 'r2']);
  await expect(rock).toHaveAttribute('aria-pressed', 'true');
  await expect(jazz).toHaveAttribute('aria-pressed', 'false');
});

test('deckCommand discards an in-flight deckState request so stale data is not reused', async ({ page }) => {
  await show(page, 'audio-mixer');
  await expect(page.getByRole('slider', { name: 'Spotify volume', exact: true })).toBeVisible();
  const result = await page.evaluate(async () => {
    const mod = await import('/panel/widgets/controls.js');
    const originalFetch = window.fetch;
    let releaseFirstState;
    const firstStateGate = new Promise(resolve => { releaseFirstState = resolve; });
    let stateCalls = 0;

    window.fetch = async (url, init) => {
      if (String(url).includes('/controls/state')) {
        stateCalls++;
        if (stateCalls === 1) {
          await firstStateGate;
          return new Response(JSON.stringify({ audio: { sessions: [{ id: 'spotify', volume: 11 }] } }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }
        return new Response(JSON.stringify({ audio: { sessions: [{ id: 'spotify', volume: 42 }] } }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (String(url).includes('/controls/command/')) {
        return new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return originalFetch(url, init);
    };

    try {
      mod.invalidateDeckState();
      const firstInFlight = mod.deckState();
      await mod.deckCommand('session', { id: 'spotify', volume: 42 });
      const freshAfterCommand = await mod.deckState();
      releaseFirstState();
      await firstInFlight;
      const afterStaleResolves = await mod.deckState();
      return {
        stateCalls,
        freshVolume: freshAfterCommand.audio.sessions[0].volume,
        cachedVolume: afterStaleResolves.audio.sessions[0].volume,
      };
    } finally {
      window.fetch = originalFetch;
    }
  });

  expect(result.stateCalls).toBe(2);
  expect(result.freshVolume).toBe(42);
  expect(result.cachedVolume).toBe(42);
});

test('meeting-status renders live on-air state and out-of-order work/push polls or cancelled volume drags do not regress', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'en', idleReturnSec: 0 },
      pages: [{
        id: 'one', title: 'One', enabled: true, widgets: [
          { id: 'ms1', type: 'meeting-status', x: 0, y: 0, w: 6, h: 3, options: { title: 'IN CALL' } },
          { id: 'vol1', type: 'volume', x: 6, y: 0, w: 6, h: 3, options: {} },
        ],
      }],
    }));
  });
  await page.goto('/panel/');
  await page.addStyleTag({ content: '* { transition: none !important; animation: none !important; }' });
  await expect(page.locator('.w-meeting-status .oa-sign')).toHaveText('IN CALL');

  const out = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    const origFetch = window.fetch;

    m.applyState({ ts: Date.now(), privacy: { mic: true, camera: false, micApps: ['Zoom'] }, audio: { volume: 30, muted: false, canControl: true } });
    const meetingBg = getComputedStyle(document.querySelector('.w-meeting-status .oa-sign')).backgroundColor;

    // Gated out-of-order loadWork() vs SSE 'work'
    let releaseStaleWork;
    const workGate = new Promise((r) => { releaseStaleWork = r; });
    let gateWork = true;
    window.fetch = (input, init) => {
      const u = String(typeof input === 'string' ? input : input.url);
      if (gateWork && u.endsWith('/api/work')) {
        gateWork = false;
        return workGate.then(() => Response.json({ sessions: [{ id: 's1', name: 'Claude', status: 'busy' }], prs: null }));
      }
      return origFetch(input, init);
    };
    m.applyWork({ sessions: [{ id: 's1', name: 'Claude', status: 'busy' }], prs: null });
    const staleWork = m.loadWork();
    window.onAgentStream('open', '{}');
    window.onAgentStream('work', JSON.stringify({ sessions: [{ id: 's1', name: 'Claude', status: 'waiting' }], prs: null }));
    releaseStaleWork();
    await staleWork;
    window.onAgentStream('close', '{}');

    // Gated out-of-order loadPush()
    const pushGates = [];
    window.fetch = (input, init) => {
      const u = String(typeof input === 'string' ? input : input.url);
      if (u.endsWith('/api/push')) return new Promise((r) => pushGates.push(r));
      return origFetch(input, init);
    };
    const p1 = m.loadPush();
    const p2 = m.loadPush();
    pushGates[1](Response.json({ version: 5, items: { k: { data: 'fresh' } } }));
    await p2;
    pushGates[0](Response.json({ version: 4, items: { k: { data: 'stale' } } }));
    await p1;
    window.fetch = origFetch;

    // Volume pointercancel releases dragging
    const bar = document.querySelector('.w-volume .vb');
    const rect = bar.getBoundingClientRect();
    bar.dispatchEvent(new PointerEvent('pointerdown', { pointerId: 1, clientX: rect.left + 10, clientY: rect.top + 5, bubbles: true }));
    bar.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1, bubbles: true }));
    m.applyState({ ts: Date.now() + 500, privacy: { mic: true, camera: false }, audio: { volume: 82, muted: false, canControl: true } });

    return {
      meetingBg,
      workStatus: m.D.work.sessions[0].status,
      pushVal: m.D.push.k.data,
      volText: document.querySelector('.w-volume .vb-v').textContent,
    };
  });

  expect(out).toEqual({
    meetingBg: 'rgb(220, 38, 38)',
    workStatus: 'waiting',
    pushVal: 'fresh',
    volText: '82%',
  });
});

test('Control Deck settings update on input without blur and Smart Stack canvas drop enforces defaults and 12-item cap', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#general');
  await expect(page.locator('[data-bind="ping.host"]')).toBeVisible();

  await page.evaluate(() => {
    const cfgObj = JSON.parse(localStorage.getItem('pb-demo-config'));
    const eleven = Array.from({ length: 11 }, (_, i) => ({ id: `card-seed-${i}`, type: 'clock', enabled: true, options: {} }));
    cfgObj.pages = [{
      id: 'stack-page', title: 'Stack Page', enabled: true,
      widgets: [{ id: 'st1', type: 'stack', x: 0, y: 0, w: 6, h: 4, options: { widgets: eleven, interval: 10, showDots: true } }],
    }];
    localStorage.setItem('pb-demo-config', JSON.stringify(cfgObj));
  });
  await page.reload();
  await expect(page.locator('#save')).toBeDisabled();

  await page.locator('#controlSettings summary').filter({ hasText: 'Spotify connection' }).click();
  await page.getByLabel('Spotify Client ID').focus();
  await page.keyboard.type('cid-live');
  await expect(page.locator('#save')).toBeEnabled();

  await page.locator('#controlSettings summary').filter({ hasText: /^Meeting$/ }).click();
  await page.getByLabel('Profile action IDs').first().focus();
  await page.keyboard.type('build\ntests');
  await page.keyboard.press('Control+s');
  await expect(page.locator('#save')).toBeDisabled();

  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')));
  expect(saved.controls.spotifyClientId).toBe('cid-live');
  expect(saved.controls.profiles[0].actions).toEqual(['build', 'tests']);

  await page.goto('/settings/#pages');
  await expect(page.locator('#canvas .wb').first()).toBeVisible();
  const dropRes = await page.evaluate(() => {
    const stackEl = document.querySelector('#canvas .wb');
    const drop = (type) => {
      const dt = new DataTransfer();
      dt.setData('application/phonebox-widget-type', type);
      stackEl.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
    };
    drop('pomodoro');
    drop('onair');
    const items = cfg.pages[0].widgets[0].options.widgets;
    return { count: items.length, id: items[11]?.id, focus: items[11]?.options?.focus };
  });
  expect(dropRes.count).toBe(12);
  expect(dropRes.id).toMatch(/^card-/);
  expect(dropRes.focus).toBe(25);
});
