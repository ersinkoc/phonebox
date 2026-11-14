const { test, expect } = require('@playwright/test');

async function show(page, type, options = {}) {
  await page.goto('/panel/');
  await page.evaluate(({ type, options }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'controls', title: 'Controls', widgets: [{ id: 'control', type, x: 0, y: 0, w: 12, h: 6, options }] }] }));
  }, { type, options });
  await page.reload();
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
