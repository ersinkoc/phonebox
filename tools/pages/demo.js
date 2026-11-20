// PhoneBox web demo: answers the panel's and the settings page's API calls in the browser with made-up data,
// so both run on a static site (GitHub Pages) without a phone or an agent. Load it before app.js / the settings script.
// The panel and the settings page talk through a BroadcastChannel: layout changes and test notifications show up live.
(() => {
  'use strict';
  const ROOT = new URL('.', document.currentScript.src).href;
  const VERSION = document.currentScript.dataset.version || 'demo'; // stamped by tools/pages/build.py from the agent's version
  const realFetch = window.fetch.bind(window);
  const bc = new BroadcastChannel('phonebox-demo');
  const START = Date.now();
  const now = () => Date.now();
  const rnd = (a, b) => a + Math.random() * (b - a);
  const wave = (period, lo, hi, phase = 0, noise = 0) =>
    lo + (hi - lo) * (0.5 + 0.5 * Math.sin((2 * Math.PI * (now() - START)) / 1000 / period + phase)) + rnd(-noise, noise);

  const SHORTCUTS = [
    ['terminal', 'Terminal', '⌨️', '#4b5563'], ['code', 'VS Code', '📝', '#2f80ed'], ['browser', 'Browser', '🌐', '#0ea5e9'],
    ['files', 'Files', '📁', '#e5b84b'], ['music', 'Spotify', '🎧', '#1db954'], ['chat', 'Discord', '💬', '#6366f1'],
    ['snip', 'Snip', '✂️', '#14b8a6'], ['taskmgr', 'Task Manager', '📊', '#22c55e'], ['mute', 'Mute', '🔇', '#f59e0b'],
    ['screenoff', 'Screen Off', '🌙', '#8b5cf6'], ['lock', 'Lock', '🔒', '#ef4444'], ['settings', 'Settings', '⚙️', '#94a3b8'],
  ].map(([id, label, icon, color]) => ({ id, label, icon, color, cmd: id === 'mute' ? 'builtin:mute' : 'demo', args: null, toggle: false }));
  const PAGES = ['home', 'dashboard', 'ai', 'work', 'system', 'procs', 'deck', 'music', 'status', 'ambient', 'world', 'daily', 'markets', 'feeds', 'focus', 'calm', 'clock', 'automation', 'planner', 'nightstand', 'weekend', 'focuswork',
    'codexbar', 'devops', 'audio-deck', 'stream-deck', 'meeting-deck', 'development-deck', 'music-deck', 'system-deck'];

  let catalog = null;
  async function loadCatalog() {
    if (catalog) return catalog;
    const [widgets, templates] = await Promise.all([realFetch(ROOT + 'widgets.json').then((r) => r.json()), realFetch(ROOT + 'templates.json').then((r) => r.json())]);
    return (catalog = { cols: 12, rows: 6, widgets, templates });
  }

  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v ? JSON.parse(v) : d; } catch (_) { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (_) {} },
  };

  async function loadConfig() {
    let c = store.get('pb-demo-config', null);
    if (c) return c;
    const { templates } = await loadCatalog();
    // A tab can save a layout while the first catalog request is still pending.
    c = store.get('pb-demo-config', null);
    if (c) return c;
    const byId = Object.fromEntries(templates.map((t) => [t.id, t]));
    const rule = (r) => ({ enabled: true, match: null, value: null, page: null, text: null, level: null, key: null, pushValue: null, whileActive: true, shortcut: null,
      priority: 5, forSec: 0, cooldownMin: 0, days: null, during: null, countdownSec: null, op: null, arg: null, ...r });
    const action = (id, label, kind, target, value = 0) => ({ id, label, kind, target, argument: null, value, device: null });
    // every section the agent serves, with its defaults (Config.cs), so the settings page shows what a real one does
    c = {
      codexBar: { providers: ['claude', 'codex', 'antigravity'], rotate: true, pageSize: 2, rotateInterval: 8 },
      controls: { allowListening: false, allowPower: false, actions: [
        action('build', 'Build', 'task', 'npm run build'), action('tests', 'Run tests', 'task', 'npm test'),
        action('web', 'Web dev server', 'server', 'npm run dev'), action('api', 'API dev server', 'server', 'dotnet watch'),
        action('meet-mic', 'Mute in meeting', 'meeting', 'Ctrl+D'), action('meet-cam', 'Camera', 'meeting', 'Ctrl+E'), action('meet-hand', 'Raise hand', 'meeting', 'Ctrl+Alt+H'),
        action('chime', 'Chime', 'sound', 'chime.wav', 75), action('applause', 'Applause', 'sound', 'applause.wav', 75),
      ], profiles: [{ id: 'meeting', label: 'Meeting', levels: [], actions: [], output: null, input: null, defaults: {} },
        { id: 'music', label: 'Music', levels: [], actions: [], output: null, input: null, defaults: {} }],
        spotifyToken: null, spotifyDevice: null, mixerExecutable: null, mixerLibrary: null, spotifyClientId: null, spotifyRefreshToken: null },
      deviceSerial: null, adbPath: null,
      weather: { city: 'Tallinn', lat: 59.437, lon: 24.745, unit: 'celsius', model: '' },
      night: { enabled: false, start: '00:30', end: '07:30', brightness: 0.02 },
      display: { appearance: null, showSeconds: true, hour12: false, idleReturnSec: 0, vibrate: true, volumeKeys: true, orientation: 'landscape', theme: 'default', accent: '#2dd4bf',
        pixelShift: false, idleDimMin: 0,
        artTint: true, autoBrightness: false, nightWhenDark: false, proximityWake: true, pin: false, homeScreen: false, keepAwake: true, openOnConnect: true, followComputerLock: false,
        homePage: null, switchCountdownSec: 3, touchGraceSec: 20, minDwellSec: 15, holdMaxMin: 30 },
      lan: { enabled: true, port: 8766, token: 'demo', webhookToken: 'demo-webhook-key' },
      updates: { auto: true, repo: 'ersinkoc/phonebox' },
      claude: { phoneApprovals: true, timeoutSec: 90, onlyWhenAway: false },
      pages: PAGES.filter((id) => byId[id]).map((id) => {
        const t = byId[id], seen = {};
        return { appearance: null, id, title: t.title, icon: t.icon, enabled: true, idleSec: null, widgets: t.widgets.map((w) => {
          const n = (seen[w.type] = (seen[w.type] || 0) + 1);
          const control = w.type === 'docker' || /^obs(-|$)/.test(w.type) ? { control: true } : {};
          return { ...w, id: n === 1 ? w.type : `${w.type}-${n}`, options: { ...(w.options || {}), ...control } };
        }) };
      }),
      shortcuts: SHORTCUTS,
      rules: [
        rule({ id: 'claude-needs-me', name: 'Claude needs me', when: 'session-waiting', then: 'goto', page: 'ai', priority: 8 }),
        rule({ id: 'music', name: 'Music', when: 'media-playing', match: 'Spotify', then: 'goto', page: 'music', priority: 2 }),
        rule({ id: 'in-a-meeting', name: 'In a meeting', when: 'calendar-event', then: 'push', key: 'onair', pushValue: '{detail}' }),
        rule({ id: 'review-requested', name: 'Review requested', when: 'review-requested', then: 'notify', text: '{detail}', whileActive: false, days: 'mon,tue,wed,thu,fri', during: '09:00-18:00' }),
        rule({ id: 'evenings', name: 'Evenings', when: 'time', match: '22:00-07:00', then: 'goto', page: 'calm', priority: 2 }),
      ],
      charge: { enabled: true, min: 50, max: 80, method: 'ha', entity: 'switch.phone_charger', onShortcut: null, offShortcut: null },
      phones: [], calendars: [],
      homeAssistant: { url: null, token: null },
      obs: { enabled: true, url: 'ws://127.0.0.1:4455', password: null },
    };
    c = JSON.parse(JSON.stringify(c, (_, v) => v === null ? undefined : v)); // the agent leaves unset (null) settings out
    store.set('pb-demo-config', c);
    return c;
  }
  async function config() {
    const c = await loadConfig();
    muteKeys = (c.shortcuts || []).filter((x) => x.cmd === 'builtin:mute').map((x) => x.id);
    return c;
  }
  const cfgVersion = () => store.get('pb-demo-version', 1);
  // every widget of the layout, Smart Stack children included (AgentConfig.Widgets)
  const widgets = (c) => (c.pages || []).flatMap((p) => p.widgets || []).flatMap((w) => [w, ...(w.type === 'stack' ? (w.options?.widgets || []).filter((x) => x.enabled !== false) : [])]);
  const allows = (c, test) => widgets(c).some((w) => test(w.type) && w.options?.control === true);
  const isObs = (type) => type === 'obs' || type.startsWith('obs-');
  // like AgentConfig.Widgets(enabledPagesOnly: true): what may control the computer comes from enabled pages only
  const shownWidgets = (c) => widgets({ ...c, pages: (c.pages || []).filter((p) => p.enabled !== false) });
  const feedsCanOpen = (c) => shownWidgets(c).some((w) => w.type === 'feeds' && w.options?.open === true);

  // What the agent sends to a phone (AgentConfig.ToPublic): no commands, keys, tokens or what a rule matches on.
  function publicConfig(c) {
    const options = (w) => w.type === 'command' ? Object.fromEntries(Object.entries(w.options || {}).filter(([k]) => k === 'title' || k === 'interval')) : w.options ?? null;
    return {
      weather: c.weather, codexBar: c.codexBar, night: c.night, display: c.display,
      pages: (c.pages || []).map((p) => ({ id: p.id, title: p.title, icon: p.icon, enabled: p.enabled !== false, idleSec: p.idleSec ?? null, appearance: p.appearance ?? null,
        widgets: (p.widgets || []).map((w) => ({ id: w.id, type: w.type, x: w.x, y: w.y, w: w.w, h: w.h, appearance: w.appearance ?? null, options: options(w) })) })),
      // iconRev / iconUpload: the agent sets them when a shortcut has a picture (and when the user uploaded it). The demo
      // has no uploads and serves no /agent/api/shortcuts/icon, so a config that sets them also needs that image mocked
      // (see button-cover.spec.js); without one the key keeps its emoji.
      shortcuts: (c.shortcuts || []).map(({ id, label, icon, color, toggle, cmd, iconRev, iconUpload }) => ({ id, label, icon, color, toggle: !!toggle || cmd === 'builtin:mute', iconRev: iconRev ?? null, iconUpload: iconUpload === true })),
      rules: (c.rules || []).map(({ id, name, enabled, when }) => ({ id, name, enabled: enabled !== false, when })),
      charge: { enabled: !!c.charge?.enabled, min: c.charge?.min ?? 50, max: c.charge?.max ?? 80 },
    };
  }

  // ---- webhook-style events (notifications, page switches) shared between tabs
  const events = [];
  let seq = 0;
  function emit(kind, data, broadcast = true) {
    const e = { seq: ++seq, kind, data, at: now() };
    events.push(e);
    if (events.length > 50) events.shift();
    if (broadcast) bc.postMessage({ type: 'event', kind, data });
    return e;
  }
  const push = {
    temperature: { data: 21.5, at: now() - 180e3, expiresAt: null },
    build: { data: { value: 'passed', sub: 'main · 3m 12s' }, at: now() - 240e3, expiresAt: null },
    deploy: { data: { value: 'running', sub: 'production' }, at: now() - 60e3, expiresAt: null },
    progress: { data: { value: 64 }, at: now() - 30e3, expiresAt: null },
    todo: { data: { items: [{ label: 'Try the layout editor', value: 'now', level: 'warn' }, { label: 'Widgets', value: 'done', level: 'ok' },
      { label: 'Webhooks', value: 'done', level: 'ok' }, 'Water the plants'] }, at: now() - 600e3, expiresAt: null },
  };
  let pushVersion = 1;
  let rulesPaused = 0;
  // the phone reports its battery through /api/phone/battery; /api/charge serves the last report
  let phoneBattery = { level: 72, plugged: true, temp: 29, at: now() - 20e3, source: 'usb', usb: 'configured' };
  let approvals = [];
  const SAMPLE_PROMPTS = [
    { tool: 'Run a command', detail: 'git push origin main --force-with-lease', description: 'Push the rebased branch', project: 'api-gateway' },
    { tool: 'Edit a file', detail: 'src/routes/billing.ts\n\n  export const limit = rateLimit({ windowMs: 60_000, max: 120 })', description: '', project: 'docs-site' },
    { tool: 'Fetch a web page', detail: 'https://docs.github.com/en/rest/pulls', description: '', project: 'blog-engine' },
  ];
  function ask(sample, broadcast = true) {
    const s = sample || SAMPLE_PROMPTS[Math.floor(Math.random() * SAMPLE_PROMPTS.length)];
    const a = { id: Math.random().toString(16).slice(2, 10), source: 'Claude Code', session: 'demo', createdAt: now(), expiresAt: now() + 90e3, ...s };
    approvals.push(a);
    if (broadcast) bc.postMessage({ type: 'ask', sample: s, id: a.id });
    emit('approval', { id: a.id }, false);
    return a;
  }
  bc.onmessage = (m) => {
    const d = m.data || {};
    if (d.type === 'ask') { const a = ask(d.sample, false); a.id = d.id; }
    if (d.type === 'decided') { approvals = approvals.filter((x) => x.id !== d.id); emit('approval-done', { id: d.id }, false); }
    if (d.type === 'event') emit(d.kind, d.data, false);
    if (d.type === 'push') setPush(d.key, d.value, d.ttl);
  };
  function setPush(key, value, ttl = null) { push[key] = { data: value, at: now(), expiresAt: ttl > 0 ? now() + ttl * 1000 : null }; pushVersion++; }
  const livePush = () => { for (const k of Object.keys(push)) if (push[k].expiresAt && push[k].expiresAt <= now()) { delete push[k]; pushVersion++; } return push; };

  // ---- made-up computer
  const audio = { volume: 42, muted: false, canControl: true };
  const TRACKS = [['Midnight Circuit', 'Neon Harbor', 'Low Tide Lights', 214000], ['Paper Lanterns', 'Neon Harbor', 'Low Tide Lights', 187000], ['Slow Orbit', 'Hollow Pines', 'Northern Rooms', 243000]];
  const player = { track: 0, playing: true, posMs: 61000, at: now() };
  const position = () => { const dur = TRACKS[player.track][3]; return player.playing ? (player.posMs + now() - player.at) % dur : Math.min(player.posMs, dur); };
  function mediaAction(action, ms) {
    const pos = position();
    if (action === 'playpause' || action === 'play' || action === 'pause') { player.posMs = pos; player.at = now(); player.playing = action === 'playpause' ? !player.playing : action === 'play'; }
    else if (action === 'next' || action === 'prev') { player.track = (player.track + (action === 'next' ? 1 : TRACKS.length - 1)) % TRACKS.length; player.posMs = 0; player.at = now(); }
    else if (action === 'seek' && ms != null && ms >= 0) { player.posMs = ms; player.at = now(); }
    else return false;
    return true;
  }
  function state(since) {
    const [title, artist, album, dur] = TRACKS[player.track];
    return {
      update: null, ts: now(), host: 'DESK-PC', cfgVersion: cfgVersion(), pushVersion, evSeq: seq, rules: ['music'], rulesPaused: rulesPaused > now() ? rulesPaused : null,
      events: since == null ? [] : events.filter((e) => e.seq > since),
      pc: {
        cpu: Math.max(1, wave(37, 8, 46, 0, 6)), cpuName: 'AMD Ryzen 9 7950X 16-Core Processor', cpuTemp: wave(60, 52, 68),
        ramUsedGb: wave(120, 21, 24), ramTotalGb: 64, netDownBps: Math.max(0, wave(23, 0.2e6, 9e6, 0, 1e6)), netUpBps: Math.max(0, wave(17, 40e3, 600e3, 1, 50e3)),
        uptimeSec: 3 * 86400 + 5 * 3600 + Math.floor((now() - START) / 1000),
        disks: [{ name: 'C:', usedGb: 612, totalGb: 1000 }, { name: 'D:', usedGb: 1450, totalGb: 2000 }, { name: 'E:', usedGb: 310, totalGb: 4000 }],
        ...(() => {
          const gpus = [
            { name: 'NVIDIA GeForce RTX 4080', util: Math.max(0, wave(29, 4, 38, 2, 4)), temp: wave(50, 46, 61), power: wave(29, 38, 140, 2),
              powerLimit: 320, clock: 2520, fan: 34, vramUsed: 5.8, vramTotal: 16, vendor: 'nvidia', source: 'nvidia-smi+windows', integrated: false,
              memClock: 11201, sharedUsed: 0.3, sharedTotal: 31.9, powerPct: null, fanRpm: 1180, tempMax: 90, driver: '596.36', hotSpot: null, memTemp: null },
            { name: 'AMD Radeon(TM) Graphics', util: Math.max(0, wave(41, 0, 6, 0, 1)), temp: wave(60, 50, 58), power: null, powerLimit: null,
              vramUsed: 0.1, vramTotal: 0.5, fan: null, clock: null, vendor: 'amd', source: 'windows', integrated: true, memClock: 2400,
              sharedUsed: 0.2, sharedTotal: 31.9, powerPct: wave(37, 6, 14), fanRpm: null, tempMax: null, driver: '32.0.21045.11001', hotSpot: null, memTemp: null },
          ];
          return { gpu: gpus[0], gpus };
        })(),
      },
      media: { app: 'Spotify', title, artist, album, durMs: dur, posMs: position(), playing: player.playing, canSeek: true, artId: player.track + 1 },
      audio: { ...audio },
      ping: { host: '1.1.1.1', ms: wave(13, 9, 16), avgMs: 12, lossPct: 0 },
      // the microphone is "in use" for 20 s of every minute, to show the On air widget
      privacy: (() => { const on = (now() / 1000) % 60 < 20; return { mic: on, camera: false, micApps: on ? ['Zoom'] : [], cameraApps: [] }; })(),
      obs: { streaming: !!obs.streamingSince, recording: !!obs.recordingSince, recordPaused: obs.paused },
      toggles: { ...toggles, ...Object.fromEntries(muteKeys.map((id) => [id, audio.muted])) },
    };
  }

  const toggles = {};
  let muteKeys = ['mute']; // ids of the shortcuts whose command is builtin:mute; their state is the computer's

  // made-up OBS: streaming for a while, buttons and scenes work
  const dc = { muted: false, deaf: false, in: 'c1' };
  const obs = { streamingSince: now() - 754e3, recordingSince: 0, paused: false, scene: 'Desk', mic: false };
  function obsState() {
    return { enabled: true, control: true, snapshot: {
      connected: true, error: null, streaming: !!obs.streamingSince, streamMs: obs.streamingSince ? now() - obs.streamingSince : 0,
      recording: !!obs.recordingSince, recordPaused: obs.paused, recordMs: obs.recordingSince ? now() - obs.recordingSince : 0,
      scene: obs.scene, scenes: ['Desk', 'Camera', 'Screen share', 'BRB'], micMuted: obs.mic, micName: 'Mic/Aux' } };
  }

  // made-up Home Assistant entities; switching flips them
  const ha = {
    'light.desk': { name: 'Desk lamp', state: 'on' }, 'light.ceiling': { name: 'Ceiling', state: 'off' },
    'switch.coffee': { name: 'Coffee machine', state: 'off' }, 'sensor.office_temperature': { name: 'Office', state: '21.4', unit: '°C', deviceClass: 'temperature' },
    'sensor.office_humidity': { name: 'Humidity', state: '43', unit: '%', deviceClass: 'humidity' }, 'binary_sensor.front_door': { name: 'Front door', state: 'off', deviceClass: 'door' },
    'lock.front_door': { name: 'Door lock', state: 'locked' }, 'scene.movie': { name: 'Movie night', state: 'scening' },
  };
  function haStates(ids) {
    const list = (ids.length ? ids : Object.keys(ha)).filter((id) => ha[id]).map((id) => ({ id, unit: null, deviceClass: null, icon: null, changed: now() - 6e5, ...ha[id] }));
    return { configured: true, error: null, entities: list };
  }
  function haToggle(id) {
    const e = ha[id];
    if (!e) return;
    if (id.startsWith('lock.')) e.state = e.state === 'locked' ? 'unlocked' : 'locked';
    else if (/^(light|switch)\./.test(id)) e.state = e.state === 'on' ? 'off' : 'on';
  }

  // made-up containers; start / stop / restart change them
  const containers = [
    { id: 'a1b2c3d4e5f6', name: 'shop-web-1', image: 'nginx:1.27', project: 'shop', state: 'running', status: 'Up 3 hours', health: null, cpu: 0.4, mem: '12MiB' },
    { id: 'b2c3d4e5f6a1', name: 'shop-api-1', image: 'node:22-alpine', project: 'shop', state: 'running', status: 'Up 3 hours (healthy)', health: 'healthy', cpu: 6.2, mem: '148MiB' },
    { id: 'c3d4e5f6a1b2', name: 'shop-db-1', image: 'postgres:17', project: 'shop', state: 'running', status: 'Up 3 hours', health: null, cpu: 1.1, mem: '86MiB' },
    { id: 'd4e5f6a1b2c3', name: 'shop-worker-1', image: 'node:22-alpine', project: 'shop', state: 'running', status: 'Up 12 minutes (unhealthy)', health: 'unhealthy', cpu: 31.5, mem: '402MiB' },
    { id: 'e5f6a1b2c3d4', name: 'redis', image: 'redis:7', project: null, state: 'exited', status: 'Exited (0) 2 days ago', health: null, cpu: null, mem: null },
  ];
  function docker() {
    const live = containers.map((c) => c.state === 'running' && c.cpu != null ? { ...c, cpu: Math.max(0.1, c.cpu * (0.8 + Math.random() * 0.4)) } : c);
    return { snapshot: { available: true, error: null, containers: live, updated: now() } };
  }

  // made-up calendar around the current time: one meeting is on now
  function calendar() {
    const day = new Date(new Date().toDateString()).getTime(), H = 3600e3, M = 60e3, t = now();
    const ev = (title, start, mins, cal, color, location, allDay = false) => ({ title, start, end: start + mins * M, allDay, location, calendar: cal, color });
    const work = '#60a5fa', home = '#34d399';
    const utcDay = (n) => { const d = new Date(); return Date.UTC(d.getFullYear(), d.getMonth(), d.getDate() + n); };
    return { updated: t - 4 * M, errors: [], events: [
      ev('Design review', t - 20 * M, 45, 'Work', work, 'Room 4'),
      ev('1:1 with Sam', t + 90 * M, 30, 'Work', work, null),
      ev('Gym', day + 18.5 * H, 60, 'Personal', home, null),
      ev('Release day', utcDay(1), 24 * 60, 'Work', work, null, true), // all-day: UTC midnight of the date, as the agent sends it
      ev('Sprint planning', day + 33.5 * H, 60, 'Work', work, 'Big room'),
      ev('Lunch with Alex', day + 36 * H, 60, 'Personal', home, 'Noodle bar'),
      ev('Dentist', day + 2 * 24 * H + 10 * H, 45, 'Personal', home, null),
    ].sort((a, b) => a.start - b.start) };
  }

  function quotas() {
    const n = now();
    return { providers: [
      { id: 'claude', name: 'Claude', plan: 'Max 5x', updatedAt: n - 120e3, extra: '', error: null, codex: null, source: 'local', limits: [
        { label: '5-hour session', percent: 34, resetsAt: n + 2.6 * 3600e3, windowSec: 18000 },
        { label: 'Weekly', percent: 52, resetsAt: n + 3.2 * 86400e3, windowSec: 604800 },
        { label: 'Weekly · Fable', percent: 9, resetsAt: n + 3.2 * 86400e3, windowSec: 604800 }] },
      { id: 'codex', name: 'Codex', plan: 'Pro', updatedAt: n - 300e3, extra: 'Credits: 1,250', error: null, source: 'local',
        codex: { model: 'Demo coding model', effort: 'high', totalTokens: 187000, totalInput: 150000, totalOutput: 37000, totalCached: 90000, totalReasoning: 12000, lastInput: 42000, contextWindow: 256000 }, limits: [
        { label: '5-hour session', percent: 12, resetsAt: n + 4.1 * 3600e3, windowSec: 18000 },
        { label: 'Weekly', percent: 27, resetsAt: n + 5.5 * 86400e3, windowSec: 604800 }] },
    ] };
  }

  function work() {
    const n = now();
    const s = (i, tool, name, status, mins, waitingFor = null) => ({ id: `${tool}:${i}`, tool, name, project: name, status, waitingFor, since: n - mins * 60e3, startedAt: n - 3 * 3600e3,
      pid: 4000 + i * 211, outcome: null, model: null, effort: null, totalTokens: null, inputTokens: null, outputTokens: null, cachedInputTokens: null, reasoningTokens: null,
      lastInputTokens: null, contextWindow: null, lastTool: null, turnStartedAt: null,
      ...(tool === 'codex' ? { model: 'Demo coding model', effort: 'high', totalTokens: 187000, inputTokens: 150000, outputTokens: 37000, cachedInputTokens: 90000, reasoningTokens: 12000, lastInputTokens: 42000, contextWindow: 256000, lastTool: 'read', turnStartedAt: n - 45000 } : {}) });
    const pr = (repo, number, title, ci = null, review = null, draft = false, author = null) => ({ repo, number, title, ci, review, draft, author, updated: n - (number % 9 + 1) * 3600e3 });
    return {
      sessions: [s(1, 'claude', 'api-gateway', 'busy', 4), s(2, 'claude', 'docs-site', 'waiting', 2, 'approve a file edit'), s(3, 'codex', 'mobile-app', 'busy', 11),
        s(4, 'claude', 'infra', 'shell', 1), s(5, 'claude', 'blog-engine', 'idle', 38), s(6, 'codex', 'data-pipeline', 'idle', 74), s(7, 'claude', 'design-system', 'idle', 125),
        s(8, 'cursor', 'phonebox', 'busy', 3), s(9, 'cursor', 'fueltrack', 'idle', 1)],
      github: { user: 'octocat', notifications: 7, error: null, updatedAt: n - 90e3,
        reviewRequests: [pr('acme/api', 412, 'Rate limiter for public endpoints', null, null, false, 'hubot'), pr('acme/web', 1288, 'Dark mode for the dashboard', null, null, false, 'monalisa'),
          pr('acme/infra', 77, 'Move staging to a new region', null, null, false, 'hubot')],
        myPrs: [pr('acme/api', 409, 'Cache user sessions in Redis', 'SUCCESS', 'APPROVED'), pr('acme/web', 1281, 'Faster image uploads', 'PENDING'),
          pr('acme/cli', 53, 'New "deploy" command', 'FAILURE', 'CHANGES_REQUESTED'), pr('acme/docs', 210, 'Getting started rewrite', null, null, true)] },
    };
  }

  const PROCS = [['code', 'Visual Studio Code', 9, 6.2, 2100], ['chrome', 'Google Chrome', 14, 4.8, 3400], ['node', 'Node.js', 6, 3.9, 1200],
    ['claude', 'Claude', 7, 2.7, 1800], ['docker', 'Docker Desktop', 4, 2.1, 2600], ['spotify', 'Spotify', 5, 1.4, 420], ['discord', 'Discord', 6, 1.1, 610],
    ['explorer', 'Windows Explorer', 3, 0.9, 380], ['obs64', 'OBS Studio', 1, 0.8, 520], ['slack', 'Slack', 5, 0.6, 700], ['steam', 'Steam', 3, 0.4, 340],
    ['onedrive', 'Microsoft OneDrive', 1, 0.3, 120], ['python', 'Python', 2, 0.3, 160], ['figma', 'Figma', 3, 0.2, 640]];
  const DEMO_APPS = ['Calculator', 'Calendar', 'Figma', 'Finder', 'Mail', 'Maps', 'Music', 'Notes', 'OBS', 'Photos', 'Safari', 'Slack',
    'Spotify', 'System Settings', 'Terminal', 'Visual Studio Code', 'Xcode', 'Zoom']
    .map((name, i) => ({ id: 'demo-app-' + i, name, hue: (i * 47) % 360 }));
  const procs = () => ({ total: 312, groupCount: 141, groups: PROCS.map(([name, title, count, cpu, memMb]) =>
    ({ name, title, count, cpu: Math.round(cpu * rnd(0.8, 1.2) * 10) / 10, memMb, locked: name === 'explorer', hasIcon: false })) });
  function procDetail(name) {
    const p = PROCS.find((x) => x[0] === name) || PROCS[0];
    return { name: p[0], title: p[1], locked: p[0] === 'explorer', instances: Array.from({ length: p[2] }, (_, i) => ({
      pid: 4100 + i * 37, cpu: Math.round((p[3] / p[2]) * 10) / 10, memMb: Math.round(p[4] / p[2]), startedAt: now() - (i + 1) * 1800e3,
      locked: p[0] === 'explorer', session: p[0] === 'claude' && i < 3 ? ['api-gateway', 'docs-site', 'infra'][i] : null, window: null, args: i ? '--type=renderer' : null })) };
  }

  const ART = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 300"><defs>' +
    '<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ff5f6d"/><stop offset=".55" stop-color="#7b2ff7"/><stop offset="1" stop-color="#12c2e9"/></linearGradient>' +
    '<radialGradient id="s" cx=".5" cy=".42" r=".5"><stop offset="0" stop-color="#ffd86f"/><stop offset="1" stop-color="#ff6f61"/></radialGradient></defs>' +
    '<rect width="300" height="300" fill="url(#g)"/><circle cx="150" cy="130" r="70" fill="url(#s)"/><rect y="170" width="300" height="130" fill="#1b1037" opacity=".85"/>' +
    '<g stroke="#ff6fd8" stroke-width="3" opacity=".9">' + Array.from({ length: 9 }, (_, i) => `<line x1="0" y1="${180 + i * 14}" x2="300" y2="${180 + i * 14}"/>`).join('') +
    Array.from({ length: 13 }, (_, i) => `<line x1="150" y1="170" x2="${-300 + i * 75}" y2="300"/>`).join('') + '</g></svg>');

  // ---- router
  const DEMO_PHOTOS = [['ph1', '#f97316', '#7c3aed'], ['ph2', '#0ea5e9', '#22c55e'], ['ph3', '#ec4899', '#facc15'], ['ph4', '#14b8a6', '#1e3a8a']];
  // headlines as the agent sends them: no links, the phone opens one by its id
  const DEMO_FEEDS = () => [
    ['f1', 'A tiny e-ink weather station that runs for a year on one charge', 'Hackaday', 18],
    ['f2', 'Rust 2.0 roadmap: what changes for async code', 'Tech news', 47],
    ['f3', 'Why old phones make great desk dashboards', 'Blog', 95],
    ['f4', 'The James Webb telescope finds water in a young planetary system', 'Science', 160],
    ['f5', 'Ten keyboard shortcuts that save an hour a week', 'Tech news', 240],
    ['f6', 'Inside the factory that still makes film cameras', 'Hackaday', 420],
  ].map(([id, title, source, min]) => ({ id, title, source, published: now() - min * 60_000, openable: true }));
  // "pageId/widgetId", or "pageId/stackId:cardId" for a Smart Stack card (PanelWidgets.Find)
  const panelWidget = (c, key, type) => (c.pages || []).filter((p) => p.enabled !== false).flatMap((p) => (p.widgets || []).flatMap((w) =>
    // card-<i> counts every card, disabled ones too, like StackWidgets.Children and stack.js
    [[`${p.id}/${w.id}`, w], ...(w.type === 'stack' ? (w.options?.widgets || []).map((x, i) => [x, i]).filter(([x]) => x.enabled !== false)
      .map(([x, i]) => [`${p.id}/${w.id}:${x.id || `card-${i}`}`, x]) : [])]))
    .find(([k, w]) => w.type === type && k === key)?.[1];
  const consoleWidget = (c, key) => panelWidget(c, key, 'app-console');
  const DEMO_CONSOLE = { id: 'photoshop', label: 'Photoshop', keys: ['Brush', 'Eraser', 'Undo', 'Redo', 'Zoom in', 'Zoom out', 'Fit', 'Save'],
    dial: { left: true, right: true, press: true } };
  const json = (o, status = 200, headers = {}) => new Response(JSON.stringify(o), { status, headers: { 'Content-Type': 'application/json; charset=utf-8', ...headers } });
  const parse = (body) => { if (body == null || body === '') return null; try { return JSON.parse(body); } catch (_) { return String(body); } };

  // Control Deck preview data only. Native integrations live in the desktop agent.
  const deck = {
    platform: 'windows', hardwareLoading: false, mediaError: null, ducking: false, sleep: [],
    audio: { available: true, error: null, defaults:{'output:0':'speaker','output:1':'speaker','output:2':'speaker','input:0':'mic','input:1':'mic','input:2':'mic'}, devices: [
      { id: 'speaker', name: 'Desk speakers', direction: 'output', default: true, volume: 42, muted: false, peak: .45, channels: 2, canControl: true, channelVolumes:[42,42] },
      { id: 'headphones', name: 'Headphones', direction: 'output', default: false, volume: 60, muted: false, peak: .2, channels: 2, canControl: true, channelVolumes:[60,60] },
      { id: 'mic', name: 'Desk microphone', direction: 'input', default: true, volume: 75, muted: true, peak: .15, channels: 1, canControl: true, channelVolumes: null },
    ], sessions: [
      { id: 'spotify', name: 'Spotify', pid: 101, deviceId: 'speaker', volume: 65, muted: false, peak: .55 },
      { id: 'browser', name: 'Browser', pid: 102, deviceId: 'speaker', volume: 30, muted: false, peak: .25 },
    ] },
    media: [
      { id: 'spotify', name: 'Spotify', title: 'Midnight Circuit', playing: true, position: 61000, duration: 214000, canSeek: true, canShuffle: true, canRepeat: true, shuffle: false, repeat: 'None' },
      { id: 'browser', name: 'Browser', title: 'Engineering session', playing: false, position: 10000, duration: 600000, canSeek: true, canShuffle: false, canRepeat: false, shuffle: false, repeat: 'None' },
    ],
    monitors: [{ id: 'display', name: 'Desk display', brightness: 60, width: 2560, height: 1440, hz: 144 }],
    powerPlans: [{ id: 'balanced', name: 'Balanced', kind: 'power-plan', state: 'Active', available: true }, { id: 'performance', name: 'High performance', kind: 'power-plan', state: 'Available', available: true },
      { id: 'saver', name: 'Power saver', kind: 'power-plan', state: 'Available', available: true }], midi: [],
  };
  const actionState = {}; // what a configured control last did: "Running", "Finished", "Stopped"…
  async function deckState() {
    const c = await config(), controls = c.controls || {};
    // the output device follows the computer's volume, and the first player is the one in /api/state
    Object.assign(deck.audio.devices[0], { volume: audio.volume, muted: audio.muted });
    Object.assign(deck.media[0], { title: TRACKS[player.track][0], playing: player.playing, position: position(), duration: TRACKS[player.track][3] });
    return { ...deck, allowListening: !!controls.allowListening, allowPower: !!controls.allowPower,
      // ControlDeck.Describe / DescribeShortcut: ids, labels and kinds only
      actions: (controls.actions || []).filter((a) => a.enabled !== false).map((a) => ({ id: a.id, name: a.label, kind: a.kind, state: actionState[a.id] ?? (/^(app|project|shortcut|focus)$/.test(a.kind) ? (c.shortcuts.some((x) => x.id === a.target) ? 'Ready' : 'Shortcut is missing') : null),
        available: !/^(app|project|shortcut|focus)$/.test(a.kind) || c.shortcuts.some((x) => x.id === a.target) })),
      shortcuts: (c.shortcuts || []).map((x) => ({ id: x.id, name: x.label, kind: { 'builtin:keys': 'keys', 'builtin:text': 'text', 'builtin:macro': 'macro' }[x.cmd] || (String(x.cmd).startsWith('builtin:') ? 'shortcut' : 'app'), state: null, available: true })),
      profiles: (controls.profiles || []).map((x) => ({ id: x.id, name: x.label, kind: 'profile', state: null, available: true })) };
  }
  const deckLeases = new Map();
  // one made-up OBS behind both the OBS widget (/api/obs) and the Control Deck's OBS widgets (/api/controls/obs)
  const deckObsExtra = { inputs: [{ id: 'Desktop Audio', name: 'Desktop Audio', volume: 70, muted: false }, { id: 'Mic/Aux', name: 'Mic/Aux', volume: 80, muted: false }],
    sources: { Desk: [{ id: 1, name: 'Webcam', enabled: true }, { id: 2, name: 'Screen capture', enabled: true }, { id: 3, name: 'Lower third', enabled: false }],
      Camera: [{ id: 4, name: 'Webcam', enabled: true }], 'Screen share': [{ id: 5, name: 'Screen capture', enabled: true }], BRB: [{ id: 6, name: 'Be right back card', enabled: true }] },
    transitions: [{ transitionName: 'Fade', transitionKind: 'fade_transition' }, { transitionName: 'Cut', transitionKind: 'cut_transition' }], transition: 'Fade', duration: 300, replayActive: false, cameraActive: false };
  function deckObsState() {
    const o = obsState().snapshot, x = deckObsExtra;
    x.inputs[1].muted = obs.mic; // the microphone the OBS widget mutes
    return { connected: true, scene: o.scene, scenes: o.scenes, inputs: x.inputs, sources: x.sources[o.scene] || [], streaming: o.streaming, recording: o.recording, recordPaused: o.recordPaused,
      streamMs: o.streamMs, recordMs: o.recordMs, transitions: x.transitions, transition: x.transition, duration: x.duration, replayAvailable: true, replayActive: x.replayActive, cameraAvailable: true, cameraActive: x.cameraActive };
  }
  function obsAct(action, scene) {
    if (action === 'stream') obs.streamingSince = obs.streamingSince ? 0 : now();
    else if (action === 'record') { obs.recordingSince = obs.recordingSince ? 0 : now(); obs.paused = false; }
    else if (action === 'pause') { if (obs.recordingSince) obs.paused = !obs.paused; }
    else if (action === 'mic') obs.mic = !obs.mic;
    else if (action === 'scene') { if (obsState().snapshot.scenes.includes(scene)) obs.scene = scene; }
    else return false;
    return true;
  }
  const spotifyId = (i) => 'spotify:track:' + String(i).padStart(22, '0');
  const spotifyTrack = (i) => ({ id: spotifyId(i), name: TRACKS[i][0], artist: TRACKS[i][1], image: 'https://i.scdn.co/image/demo' + i });
  const deckSpotify = () => ({ connected: true, current: spotifyTrack(player.track), playing: player.playing,
    queue: TRACKS.map((_, i) => i).filter((i) => i !== player.track).map(spotifyTrack) });
  async function controlCommand(kind, q) {
    const id = q.get('id'), op = q.get('op'), value = q.has('value') ? +q.get('value') : null;
    const c = await config();
    if (kind === 'device' || kind === 'session') {
      const item = (kind === 'device' ? deck.audio.devices : deck.audio.sessions).find(x => x.id === id);
      if (!item) return { ok: false, error: 'Device unavailable' };
      if (value != null) {
        if(kind==='device' && (op==='left'||op==='right'))item.channelVolumes[op==='left'?0:1]=value;
        else item.volume = Math.max(0, Math.min(100, value));
      }
      if (q.has('muted')) item.muted = q.get('muted') === 'true';
      if (item === deck.audio.devices[0]) Object.assign(audio, { volume: item.volume, muted: item.muted });
    } else if (kind === 'default') {
      const item = deck.audio.devices.find(d => d.id === id); if (!item) return { ok: false, error: 'Device unavailable' };
      const roles=op==='media'?[1]:op==='communications'?[2]:op==='console'?[0]:[0,1,2];
      for(const role of roles)deck.audio.defaults[item.direction+':'+role]=id;
      if(roles.includes(1))deck.audio.devices.filter(d => d.direction === item.direction).forEach(d => d.default = d.id === id);
    } else if (kind === 'media') {
      const item = deck.media.find(m => m.id === id); if (!item) return { ok: false, error: 'Player unavailable' };
      if (item === deck.media[0]) { if (['playpause', 'next', 'prev', 'seek'].includes(op)) mediaAction(op, value); }
      else if (op === 'playpause') item.playing = !item.playing;
      else if (op === 'seek') item.position = value;
      if (op === 'shuffle') item.shuffle = !!value;
      if (op === 'repeat') item.repeat = ['None', 'Track', 'List'][value];
    } else if (kind === 'ptt') {
      if (op === 'begin') {
        const mic = deck.audio.devices.find(d => d.id === id && d.direction === 'input'); if (!mic) return { ok: false, error: 'No microphone' };
        const lease = 'demo-' + Math.random(); deckLeases.set(lease, { mic, muted: mic.muted }); mic.muted = false;
        return { ok: true, lease };
      }
      const lease = deckLeases.get(q.get('lease')); if (!lease) return { ok: false, error: 'Expired lease' };
      if (op === 'end') { lease.mic.muted = lease.muted; deckLeases.delete(q.get('lease')); }
    } else if (kind === 'duck') deck.ducking = op !== 'off';
    else if (kind === 'brightness') deck.monitors[0].brightness = Math.max(0, Math.min(100, value));
    else if (kind === 'panic') { deck.audio.devices.forEach(d => d.muted = true); audio.muted = true; }
    else if (kind === 'sleep') deck.sleep = value ? [{ id, at: now() + value * 60000 }] : [];
    else if (kind === 'power-plan') {
      if (!deck.powerPlans.some(x => x.id === id)) return { ok: false, error: 'Power plan unavailable' };
      deck.powerPlans.forEach(x => x.state = x.id === id ? 'Active' : 'Available');
    } else if (kind === 'profile') {
      if (!(c.controls?.profiles || []).some(x => x.id === id)) return { ok: false, error: 'Profile unavailable' };
    } else if (kind === 'action') {
      // "shortcut:<id>" runs one of the settings' shortcuts; anything else is a configured control
      if (id?.startsWith('shortcut:')) return c.shortcuts.some(x => x.id === id.slice(9)) ? { ok: true } : { ok: false, error: 'Configured shortcut is missing' };
      const a = (c.controls?.actions || []).find(x => x.id === id); if (!a) return { ok: false, error: 'Control is not configured' };
      if (a.enabled === false && !(op === 'stop' && /^(task|server|sound|radio)$/.test(a.kind))) return { ok: false, error: 'Control is disabled' };
      if (a.kind === 'server') actionState[id] = op === 'stop' ? 'Stopped' : 'Running';
      else if (a.kind === 'task') { if (op === 'stop') actionState[id] = 'Stopped'; else { actionState[id] = 'Running'; setTimeout(() => { actionState[id] = 'Finished'; }, 4000); } }
      else if (a.kind === 'radio') {
        const stop = op === 'stop' || (op === 'toggle' && actionState[id] === 'Playing');
        for (const r of (c.controls?.actions || []).filter(x => x.kind === 'radio')) actionState[r.id] = 'Stopped';
        if (!stop) actionState[id] = 'Playing';
      }
      else if (a.kind === 'sound') { if (op === 'stop') delete actionState[id]; else actionState[id] = 'Playing'; }
    } else if (kind === 'sound-stop') { for (const a of c.controls?.actions || []) if ((!id || a.id === id) && actionState[a.id] === 'Playing') delete actionState[a.id]; }
    else if (kind === 'power-arm' || kind === 'power') return { ok: false, error: 'Power controls are disabled in desktop settings' };
    else if (!['desktop', 'lock', 'monitor-off'].includes(kind)) return { ok: false, error: 'Unknown control' };
    return { ok: true };
  }

  async function handle(method, path, q, body, headers) {
    if (path === '/api/controls/obs') return json(deckObsState());
    if (path.startsWith('/api/controls/obs/') && method === 'POST') {
      // ObsMonitor.DeckAct: only when a widget allows control, and only for inputs, sources and scenes OBS really has
      if (!allows(await config(), isObs)) return json({ ok: false, error: "Enable OBS control in the widget's desktop settings" });
      const action = path.split('/').pop(), id = q.get('id'), value = q.has('value') ? +q.get('value') : null, enabled = q.has('enabled') ? q.get('enabled') === 'true' : null;
      const x = deckObsExtra, input = x.inputs.find((i) => i.id === id), source = (x.sources[obs.scene] || []).find((i) => String(i.id) === id);
      const refused = json({ ok: false, error: 'OBS refused this control' });
      if (action === 'volume') { if (!input || !(value >= 0 && value <= 100)) return refused; input.volume = value; }
      else if (action === 'mute') { if (!input || enabled == null) return refused; input.muted = enabled; if (input === x.inputs[1]) obs.mic = enabled; }
      else if (action === 'scene') { if (!obsAct('scene', id) || obs.scene !== id) return refused; }
      else if (action === 'source') { if (!source || enabled == null) return refused; source.enabled = enabled; }
      else if (action === 'transition') { if (!x.transitions.some((t) => t.transitionName === id)) return refused; x.transition = id; }
      else if (action === 'duration') { if (!(value >= 50 && value <= 10000)) return refused; x.duration = value; }
      else if (action === 'record' || action === 'pause' || action === 'stream') obsAct(action);
      else if (action === 'replay') x.replayActive = !x.replayActive;
      else if (action === 'save-replay') { if (!x.replayActive) return refused; }
      else if (action === 'camera') x.cameraActive = !x.cameraActive;
      else return refused;
      return json({ ok: true });
    }
    if (path === '/api/controls/spotify/state') return json(deckSpotify());
    if (path.startsWith('/api/controls/spotify/action/') && method === 'POST') {
      const action = path.split('/').pop();
      if (action === 'play-queued') {
        const i = TRACKS.findIndex((_, n) => spotifyId(n) === q.get('id'));
        if (i < 0 || i === player.track) return json({ ok: false, error: 'That queue item is no longer available' });
        Object.assign(player, { track: i, posMs: 0, at: now(), playing: true });
      } else if (action !== 'favorite' && action !== 'playlist') return json({ ok: false, error: 'Unknown Spotify control' });
      return json({ ok: true });
    }
    if (path === '/api/controls/spotify/connect') return json({ error: 'Spotify sign-in needs the installed computer agent' }, 400);
    if (path === '/api/controls/spotify/disconnect') return json({ ok: true });
    if (path === '/api/controls/capture') return json({ id: 'profile-' + Date.now().toString(36), label: 'Captured preview profile',
      levels: [...deck.audio.devices.map((d) => ({ kind: 'device', id: d.id, volume: d.volume, muted: d.muted })), ...deck.audio.sessions.map((d) => ({ kind: 'session', id: d.id, volume: d.volume, muted: d.muted }))],
      actions: [], output: null, input: null, defaults: { ...deck.audio.defaults } });
    if (path.startsWith('/api/controls/task-output/')) return new Response(actionState[decodeURIComponent(path.split('/').pop())] ? '> made-up output: the demo runs no commands\n' : '', { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
    if (path === '/api/controls/state') return json(await deckState());
    if (path.startsWith('/api/controls/command/') && method === 'POST') return json(await controlCommand(path.split('/').pop(), q));
    if (path === '/api/state') return json(state(q.has('ev') ? +q.get('ev') : null));
    if (path === '/api/config') return json({ version: cfgVersion(), config: publicConfig(await config()) });
    if (path.startsWith('/api/widget/') && path.endsWith('/note') && method === 'POST') {
      const parts = path.split('/');
      const pageId = decodeURIComponent(parts[3]);
      const widgetId = decodeURIComponent(parts[4]);
      const c = await config();
      const pg = (c.pages || []).find((p) => p.id === pageId);
      const w = pg && (pg.widgets || []).find((x) => x.id === widgetId);
      if (!w || w.type !== 'note') return json({ error: 'option not allowed' }, 400);
      w.options = { ...(w.options || {}), text: q.get('text') || '' };
      store.set('pb-demo-config', c);
      store.set('pb-demo-version', cfgVersion() + 1);
      return json({ version: cfgVersion() });
    }
    if (path === '/api/volume' && method === 'POST') {
      // the agent answers with the new volume state (AudioSnapshot)
      const op = q.get('op'), level = q.has('level') ? +q.get('level') : null;
      if (op === 'mute') audio.muted = !audio.muted;
      else if (op === 'up' || op === 'down') audio.volume = Math.max(0, Math.min(100, audio.volume + (op === 'up' ? 5 : -5)));
      else if (Number.isFinite(level)) audio.volume = Math.max(0, Math.min(100, Math.round(level)));
      else return json({}, 400);
      return json({ ...audio });
    }
    if (path.startsWith('/api/media/') && method === 'POST') return json({}, mediaAction(path.slice(11), q.has('ms') ? +q.get('ms') : null) ? 200 : 400);
    if (path === '/api/procs/kill' && method === 'POST') {
      // KillResult; refused unless an enabled page has a processes widget that allows it (AgentConfig.AllowKill)
      const c = await config(), allowed = (c.pages || []).filter((p) => p.enabled !== false).some((p) => widgets({ pages: [p] }).some((w) => w.type === 'processes' && w.options?.allowKill !== false));
      if (!allowed) return json({ killed: 0, failed: 0, error: 'Terminating processes is disabled in the settings' }, 403);
      const i = PROCS.findIndex((x) => x[0] === q.get('name'));
      if (q.has('pid')) { const g = PROCS.find((x) => procDetail(x[0]).instances.some((n) => n.pid === +q.get('pid'))); if (g && g[0] !== 'explorer' && g[2] > 1) { g[2]--; return json({ killed: 1, failed: 0, error: null }); } if (g && g[0] !== 'explorer') { PROCS.splice(PROCS.indexOf(g), 1); return json({ killed: 1, failed: 0, error: null }); } }
      else if (i >= 0 && PROCS[i][0] !== 'explorer') { const [g] = PROCS.splice(i, 1); return json({ killed: g[2], failed: 0, error: null }); }
      return json({ killed: 0, failed: 0, error: i >= 0 || q.has('pid') ? 'That process is protected' : 'No such process' }, 409);
    }
    if (path.startsWith('/api/run/') && method === 'POST') {
      // on / off keys flip in the demo; the mute key mutes the made-up computer
      const id = decodeURIComponent(path.slice(9)), sc = (await config()).shortcuts.find((s) => s.id === id);
      if (!sc) return json({ title: 'An error occurred while processing your request.', status: 500, detail: 'unknown shortcut' }, 500);
      if (sc.cmd === 'builtin:mute') audio.muted = !audio.muted;
      else if (sc.toggle) toggles[id] = !toggles[id];
      return json({});
    }
    if (path === '/api/quotas') return json(quotas());
    // made-up recent messages and steps of a Claude Code session (the real ones come from its chat file, and only when switched on)
    if (path === '/api/work/conversation') {
      const n = Date.now();
      return json({ enabled: true,
        messages: [
          { role: 'user', text: 'Add a retry to the upload helper', at: n - 9 * 60e3 },
          { role: 'assistant', text: 'I added three retries with a short pause between them and a test for the failing case.', at: n - 7 * 60e3 },
          { role: 'user', text: 'Does it also cover timeouts?', at: n - 2 * 60e3 },
          { role: 'assistant', text: 'Yes, a timeout counts as a failed attempt and is retried the same way.', at: n - 60e3 },
        ],
        steps: [
          { tool: 'Read', detail: 'upload.js', at: n - 8 * 60e3, running: false },
          { tool: 'Edit', detail: 'upload.js', at: n - 7 * 60e3, running: false },
          { tool: 'Bash', detail: 'npm', at: n - 20e3, running: true },
        ] });
    }
    if (path === '/api/calendar') return json(calendar());
    if (path === '/api/docker') return json({ ...docker(), control: allows(await config(), (t) => t === 'docker') });
    if (path === '/api/obs') { const c = await config(); return json({ ...obsState(), enabled: c.obs?.enabled !== false, control: widgets(c).some((w) => w.type === 'obs' && w.options?.control === true) }); }
    if (path === '/api/discord') {
      const c = await config();
      return json({ enabled: true, control: widgets(c).some((w) => w.type === 'discord' && w.options?.control === true), snapshot: {
        connected: true, error: null, user: 'demo', muted: dc.muted, deafened: dc.deaf, channelId: dc.in, channelName: dc.in ? 'Game night' : null, channelKind: dc.in ? 'voice' : null,
        members: dc.in ? [{ id: '1', name: 'demo', muted: dc.muted, deafened: dc.deaf }, { id: '2', name: 'Ada', muted: false, deafened: false }, { id: '3', name: 'Linus', muted: true, deafened: false }] : [],
        channels: [{ id: 'c1', name: 'Game night', guild: 'Friends' }, { id: 'c2', name: 'Stand-up', guild: 'Work' }],
        notifications: [{ ts: now() - 60000, title: 'Ada', body: 'are you joining?' }] } });
    }
    if (path.startsWith('/api/discord/') && method === 'POST') {
      if (!allows(await config(), (t) => t === 'discord')) return json({ error: "controlling Discord is turned off in the widget's settings" }, 400);
      const a = path.slice(13);
      if (a === 'mute') dc.muted = !dc.muted; else if (a === 'deafen') dc.deaf = !dc.deaf; else if (a === 'leave') dc.in = null; else if (a === 'join') dc.in = q.get('channel'); else return json({ error: 'unknown action' }, 400);
      return json({ ok: true });
    }
    if (path === '/api/phones') return json([
      { id: '3f9a1c0d7b2e', model: 'UMIDIGI BISON', lastSeen: now() - 2000, via: 'USB', webView: 'com.google.android.webview/134.0.6998.135', webviewProblem: null, name: null, pages: null },
      { id: 'c81e728d9d4c', model: 'samsung SM-X200', lastSeen: now() - 140000, via: 'Wi-Fi', webView: 'com.google.android.webview/134.0.6998.135', webviewProblem: null, name: null, pages: null },
    ]);
    if (path === '/api/network/adapters') return json([{ name: 'Ethernet', description: 'Intel(R) Ethernet Controller I225-V', auto: true },
      { name: 'Wi-Fi', description: 'Intel(R) Wi-Fi 6E AX211 160MHz', auto: true }, { name: 'WireGuard', description: 'WireGuard Tunnel', auto: false }]);
    if (path === '/api/batteries') return json({ devices: [
      { name: 'WH-1000XM5', level: 64, kind: 'headphones', charging: null },
      { name: 'MX Master 3S', level: 85, kind: 'mouse', charging: null },
      { name: 'MX Keys', level: 18, kind: 'keyboard', charging: null },
      { name: 'Xbox Wireless Controller', level: 40, kind: 'controller', charging: null },
    ] });
    // photo frame: like the agent, ids from an enabled page's widget, then one image by id (generated pictures here)
    if (path === '/api/photos') { const c = await config(); return json(shownWidgets(c).some((w) => w.type === 'photos') ? { ids: DEMO_PHOTOS.map((p) => p[0]), error: null, scanning: false } : { ids: [], error: 'Add a Photo frame widget to a page to show photos', scanning: false }); }
    if (path.startsWith('/api/photos/')) {
      const p = DEMO_PHOTOS.find((x) => x[0] === decodeURIComponent(path.slice(12)));
      if (!p || !shownWidgets(await config()).some((w) => w.type === 'photos')) return json({ error: 'Unknown photo' }, 404);
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 1000"><defs><linearGradient id="g" x2="1" y2="1"><stop offset="0" stop-color="${p[1]}"/><stop offset="1" stop-color="${p[2]}"/></linearGradient></defs><rect width="1600" height="1000" fill="url(#g)"/><circle cx="1220" cy="260" r="120" fill="#fff8" /><path d="M0 820 L420 520 L760 760 L1100 480 L1600 860 V1000 H0Z" fill="#0006"/></svg>`;
      return new Response(svg, { status: 200, headers: { 'Content-Type': 'image/svg+xml' } });
    }
    // spectrum bars: like the agent, frames only for a widget on an enabled page; a made-up beat with a bass kick here
    if (path === '/api/spectrum') {
      const shown = shownWidgets(await config()).some((w) => w.type === 'spectrum');
      const s = Date.now() / 1000, kick = Math.max(0, Math.cos(s * Math.PI * 2 * 2)) ** 6;
      const b = shown ? Array.from({ length: 48 }, (_, i) => Math.round(Math.max(0, Math.min(100,
        78 - i * 0.7 + (i < 8 ? kick * 22 : 0) + 14 * Math.sin(s * (2.1 + i * 0.37) + i) + 6 * Math.sin(s * 9.7 + i * 1.3))))) : null;
      return json({ shown, active: shown, fps: 30, b });
    }
    if (path === '/api/feeds') { const c = await config(); return json({ items: DEMO_FEEDS(), updated: now(), errors: [], canOpen: feedsCanOpen(c) }); }
    if (path === '/api/feeds/open' && method === 'POST') {
      if (!feedsCanOpen(await config())) return json({ error: 'Opening articles is off: turn on "Open articles on the PC" in the News feeds widget\'s settings' }, 400);
      return DEMO_FEEDS().some((i) => i.id === q.get('id') && i.openable) ? json({ ok: true }) : json({ error: 'That headline is no longer in the list' }, 400);
    }
    if (path === '/api/apps') { const c = await config(); return json({ supported: true, canOpen: widgets(c).some((w) => w.type === 'apps'), apps: DEMO_APPS }); }
    if (path === '/api/apps/open' && method === 'POST') {
      if (!widgets(await config()).some((w) => w.type === 'apps')) return json({ error: 'Add an All apps widget to a page to open apps from the phone' }, 400);
      return DEMO_APPS.some((a) => a.id === q.get('id')) ? json({ ok: true }) : json({ error: 'Unknown app' }, 400);
    }
    // Dictation: the demo PC "types" the text if that widget allows it (Dictation.TypeAsync)
    // Camera: like CameraFeeds, a picture only for a camera widget on an enabled page with a camera chosen; the demo's
    // "camera" draws a moving sweep with the time instead of a stream
    if (path === '/api/camera') {
      const c = await config();
      const w = panelWidget(c, q.get('widget'), 'camera');
      if (!w) return json({ state: 'unknown' }, 404);
      if (!w.options?.camera || !(c.cameras?.sources || []).some((s) => s.id === w.options.camera)) return json({ state: 'pick' }, 404);
      const d = new Date(), x = (d.getTime() / 25) % 640;
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360"><rect width="640" height="360" fill="#1f2937"/><rect y="250" width="640" height="110" fill="#374151"/><rect x="${x.toFixed(0)}" y="0" width="6" height="360" fill="#22d3ee" opacity=".5"/><text x="16" y="34" fill="#fff" font-family="monospace" font-size="22">CAM 1  ${d.toTimeString().slice(0, 8)}</text></svg>`;
      return new Response(svg, { status: 200, headers: { 'Content-Type': 'image/svg+xml', 'Cache-Control': 'no-store' } });
    }
    if (path === '/api/dictate' && method === 'POST') {
      if (panelWidget(await config(), q.get('widget'), 'dictate')?.options?.control !== true)
        return json({ error: 'typing is off: turn on "Type the text on the computer" in the dictation widget\'s settings' }, 403);
      const text = String(q.get('text') || '').replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]+/g, ' ').trim();
      return text ? json({ ok: true }) : json({ error: 'nothing to type' }, 400);
    }
    // App console: the demo PC always has Photoshop in front; like AppConsole, only enabled pages count
    if (path === '/api/console') {
      const w = consoleWidget(await config(), q.get('widget'));
      if (!w) return json({ error: "this app console widget isn't on an enabled page of this phone" }, 404);
      return json({ supported: true, control: w.options?.control === true, profile: DEMO_CONSOLE });
    }
    if (path.startsWith('/api/console/') && method === 'POST') {
      if (consoleWidget(await config(), q.get('widget'))?.options?.control !== true)
        return json({ error: 'sending keys is off: turn on "Send keys to the computer" in the app console widget\'s settings' }, 403);
      const [, , , profile, slot] = path.split('/');
      const ok = profile === DEMO_CONSOLE.id && (/^dial-(left|right|press)$/.test(slot) || (/^k(0|[1-9]\d*)$/.test(slot) && +slot.slice(1) < DEMO_CONSOLE.keys.length));
      return ok ? json({ ok: true }) : json({ error: 'nothing is set for this key' }, 404);
    }
    if (path.startsWith('/api/obs/') && method === 'POST') {
      if (!allows(await config(), isObs)) return json({ error: "controlling OBS is turned off in the widget's settings" }, 400);
      return obsAct(path.slice(9), q.get('scene')) ? json({ ok: true }) : json({ error: 'unknown action' }, 400);
    }
    if (path === '/api/ha/entities') return json({ configured: true, error: null, entities: Object.keys(ha).sort().map((id) => ({ id, name: ha[id].name, switchable: /^(light|switch|lock|scene)\./.test(id) })) });
    if (path === '/api/ha/states') return json(haStates((q.get('ids') || '').split(',').filter(Boolean)));
    if (path.startsWith('/api/ha/toggle/') && method === 'POST') { haToggle(decodeURIComponent(path.slice(15))); return json({ ok: true }); }
    if (path.startsWith('/api/docker/') && method === 'POST') {
      const [, , , id, action] = path.split('/');
      if (!allows(await config(), (t) => t === 'docker')) return json({ error: "starting and stopping containers is turned off in the widget's settings" }, 400);
      if (!['start', 'stop', 'restart'].includes(action)) return json({ error: 'unknown action' }, 400);
      const c = containers.find((x) => x.id === id);
      if (!c) return json({ error: 'unknown container' }, 400);
      if (c && action === 'stop') Object.assign(c, { state: 'exited', status: 'Exited (0) 1 second ago', cpu: null, mem: null, health: null });
      if (c && (action === 'start' || action === 'restart')) Object.assign(c, { state: 'running', status: 'Up 1 second', cpu: 2, mem: '40MiB', health: null });
      return json({ ok: true });
    }
    if (path === '/api/work') return json(work());
    if (path === '/api/procs') return json(procs());
    if (path === '/api/procs/detail') return json(procDetail(q.get('name')));
    if (path.startsWith('/api/widget/')) return json({ output: String(3 + Math.floor(Math.random() * 3)), at: now() - 8000, ok: true, running: false, durationMs: 412 });
    if (path === '/api/approvals' && method === 'GET') return json({ items: approvals.filter((a) => a.expiresAt > now()) });
    if (path.startsWith('/api/approvals/') && method === 'POST') {
      const id = decodeURIComponent(path.slice(15));
      if (!approvals.some((a) => a.id === id)) return json({}, 404);
      approvals = approvals.filter((a) => a.id !== id);
      bc.postMessage({ type: 'decided', id });
      return json({ ok: true });
    }
    if (path === '/api/push' && method === 'GET') return json({ version: pushVersion, items: livePush() });
    if (path === '/api/push' && method === 'DELETE') { for (const k of Object.keys(push)) delete push[k]; pushVersion++; return json({}); }
    if (path.startsWith('/api/push/') && method === 'DELETE') { const key = decodeURIComponent(path.slice(10)); if (!push[key]) return json({}, 404); delete push[key]; pushVersion++; return json({}); }
    if (path.startsWith('/api/push/') && method === 'POST') {
      const key = decodeURIComponent(path.slice(10)), value = body == null || body === '' ? (q.has('value') ? parse(q.get('value')) : true) : parse(body), ttl = q.has('ttl') ? +q.get('ttl') : null;
      setPush(key, value, ttl);
      bc.postMessage({ type: 'push', key, value, ttl });
      return json({ ok: true, key, version: pushVersion });
    }
    if (path === '/api/rules/test' && method === 'POST') {
      // runs the rule's action once, like the agent's Test button
      const r = parse(body) || {};
      if (r.then === 'goto' && r.page) emit('goto', { page: r.page, rule: r.id || 'test', name: r.name || null, priority: r.priority ?? 5, countdown: r.countdownSec ?? null, hold: false });
      else if (r.then === 'notify') emit('notify', { title: r.name || 'PhoneBox', text: (r.text || 'test').replace('{detail}', 'test'), level: r.level || 'info' });
      else if (r.then === 'push' && r.key) {
        const value = r.pushValue ?? true;
        setPush(r.key, value);
        bc.postMessage({ type: 'push', key: r.key, value });
      }
      return json({ ok: true });
    }
    if (path === '/api/rules/pause' && method === 'POST') {
      const m = q.has('minutes') ? +q.get('minutes') : 60;
      rulesPaused = m > 0 ? now() + m * 60e3 : 0;
      return json({ pausedUntil: rulesPaused || null });
    }
    if (path === '/api/phone/battery' && method === 'POST') {
      phoneBattery = {
        level: Math.max(0, Math.min(100, +q.get('level') || 0)),
        plugged: q.get('plugged') === 'true',
        temp: q.has('temp') ? +q.get('temp') : null,
        at: now(),
        source: (q.get('source') || '').trim().toLowerCase() || null,
        usb: (q.get('usb') || '').trim().toLowerCase() || null,
      };
      return json({ ok: true });
    }
    if (path === '/api/charge') return json({ phone: phoneBattery, power: null, switchedAt: null, error: null });
    if (path === '/api/notify' && method === 'POST') {
      const d = parse(body), payload = typeof d === 'object' && d && !Array.isArray(d) ? d : { text: d == null ? '' : String(d) };
      if (!('text' in payload) && !('title' in payload)) return json({ error: 'give a text or a title' }, 400);
      return json(emit('notify', payload));
    }
    if (path.startsWith('/api/goto/') && method === 'POST')
      return json(emit('goto', { page: decodeURIComponent(path.slice(10)), countdown: q.has('countdown') ? Math.max(0, Math.min(30, +q.get('countdown') || 0)) : null }));
    if (path === '/api/refresh' && method === 'POST') return json(emit('refresh'));
    // settings page
    if (path === '/api/catalog') return json(await loadCatalog());
    if (path === '/api/settings') {
      if (method === 'PUT') {
        // a save over settings that changed since this page loaded them is refused, as by the agent (X-Config-Version)
        const loaded = headers.get('X-Config-Version');
        if (loaded && +loaded !== cfgVersion()) return json({ error: 'The settings were changed in another tab or window after this one loaded them. Reload the page to see them; the changes made here were not saved.' }, 409);
        const c = parse(body);
        if (!c || typeof c !== 'object') return json({ error: 'the settings are not valid JSON' }, 400);
        (c.pages || []).forEach((p, i) => { p.id ||= `page-${i + 1}`; p.title ||= 'Page'; const seen = {}; (p.widgets || []).forEach((w) => { if (!w.id) { const n = (seen[w.type] = (seen[w.type] || 0) + 1); w.id = `${w.type}-${Date.now() % 100000}-${n}`; } }); });
        (c.shortcuts || []).forEach((s, i) => { s.id ||= `shortcut-${i + 1}`; });
        store.set('pb-demo-config', c);
        store.set('pb-demo-version', cfgVersion() + 1);
        return json({ version: cfgVersion() });
      }
      return json(await config(), 200, { 'X-Config-Version': String(cfgVersion()) });
    }
    if (path === '/api/info') return json({ version: VERSION, os: 'windows', configPath: 'browser storage (demo)', logPath: '—', adb: 'demo', connected: true, devices: ['DEMO0001'], appMissing: [], usbProblem: null, webviewProblem: null });
    // Pairing over Wi-Fi: a placeholder pattern instead of real QR codes (the demo has no agent to pair with)
    if (path === '/api/pair/start' && method === 'POST') {
      const box = (label) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 29 29"><rect width="29" height="29" fill="#fff"/>${
        [0, 22].flatMap((x) => [0, 22].filter((y) => !(x === 22 && y === 22)).map((y) => `<rect x="${x}" y="${y}" width="7" height="7" fill="#000"/><rect x="${x + 1}" y="${y + 1}" width="5" height="5" fill="#fff"/><rect x="${x + 2}" y="${y + 2}" width="3" height="3" fill="#000"/>`)).join('')
      }<text x="14.5" y="16" font-size="3" text-anchor="middle" font-family="sans-serif">${label}</text></svg>`;
      return json({ enabled: true, pc: 'DEMO-PC', hosts: ['192.168.1.20', '100.101.102.103'], port: 8766, expiresAt: now() + 300e3, claims: 0, attempt: 1, check: 'DE40 0001',
        link: 'https://ersinkoc.github.io/phonebox/pair/#v=1&pc=DEMO-PC&fp=demo&port=8766&h=192.168.1.20&c=demo',
        qr: box('demo pairing'), app: 'https://github.com/ersinkoc/phonebox/releases/latest', appQr: box('demo app') });
    }
    if (path === '/api/pair/status') return json({ claims: 0 });
    if (path === '/api/pair/cancel' && method === 'POST') return json({});
    if (path === '/api/lan') return json({ configured: true, active: true, port: 8766, restartNeeded: false, hosts: ['192.168.1.20', '100.101.102.103'], fingerprint: 'demo' });
    if (path === '/api/update') return json({ status: { agentVersion: VERSION, appVersion: VERSION, latest: VERSION, releaseUrl: 'https://github.com/ersinkoc/phonebox/releases/latest', agentUpdate: false, appUpdate: false,
      lastCheck: now() - 3600e3, state: 'idle', error: null }, auto: (await config()).updates?.auto !== false, installed: true });
    if (path === '/api/claude/hook') return json({ installed: false, path: '~/.claude/settings.json' });
    if (method !== 'GET') return json({ ok: true });
    return json({ error: 'not in the demo' }, 404);
  }

  window.fetch = async (input, init = {}) => {
    const url = new URL(typeof input === 'string' ? input : input.url, location.href);
    const i = url.pathname.indexOf('/api/');
    if (url.origin !== location.origin || i < 0) return realFetch(input, init);
    return handle((init.method || 'GET').toUpperCase(), url.pathname.slice(i), url.searchParams, init.body, new Headers(init.headers || {}));
  };

  // Album art and radio logos are <img>, which fetch() does not see: swap their address for the demo cover.
  // App icons become a coloured tile with the app's initial.
  const appIcon = (id) => {
    const a = DEMO_APPS.find((x) => x.id === id) || { name: '?', hue: 210 };
    return 'data:image/svg+xml,' + encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96"><rect x="6" y="6" width="84" height="84" rx="20" fill="hsl(${a.hue} 65% 52%)"/>`
      + `<text x="48" y="62" font-family="system-ui,sans-serif" font-size="42" font-weight="700" text-anchor="middle" fill="#fff">${a.name.slice(0, 1)}</text></svg>`);
  };
  const swapDemoImg = (img) => {
    if (img.tagName !== 'IMG') return;
    const src = img.getAttribute('src') || '';
    if (/\/api\/(art|controls\/radio-logo)|^https:\/\/i\.scdn\.co\/image\/demo\d+$/.test(src)) img.src = ART;
    else if (/\/api\/apps\/icon/.test(src)) img.src = appIcon(new URL(src, location.href).searchParams.get('id'));
  };
  new MutationObserver((list) => {
    for (const m of list) {
      if (m.type === 'attributes') swapDemoImg(m.target);
      else for (const n of m.addedNodes) if (n.nodeType === 1) { swapDemoImg(n); n.querySelectorAll?.('img').forEach(swapDemoImg); }
    }
  }).observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['src'] });

  // A pretend Android bridge for phone-only widgets.
  if (!window.Android && document.currentScript.dataset.panel !== undefined)
    window.Android = { battery: () => JSON.stringify({ level: 82, plugged: true, temp: 29.5 }), transport: () => 'usb', version: () => VERSION,
      is24Hour: () => !new Intl.DateTimeFormat(undefined, { hour: 'numeric' }).resolvedOptions().hour12,
      haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {} };

  // Served by tools/demo-server.py: webhooks sent to that server with curl arrive here. Each tab applies them itself.
  if (document.currentScript.dataset.relay !== undefined && window.EventSource) {
    const es = new EventSource(ROOT + 'demo/events');
    const on = (kind, fn) => es.addEventListener(kind, (m) => fn(JSON.parse(m.data)));
    on('notify', (d) => emit('notify', d, false));
    on('goto', (d) => emit('goto', d, false));
    on('refresh', () => emit('refresh', null, false));
    on('push', (d) => setPush(d.key, d.value));
    on('ask', (d) => ask(d || undefined, false));
  }

  window.PhoneBoxDemo = { emit, ask,
    reset() { localStorage.removeItem('pb-demo-config'); store.set('pb-demo-version', cfgVersion() + 1); },
    // the agent restarted: PushStore keeps events in memory only, so its counter and backlog start over
    restart() { seq = 0; events.length = 0; } };
})();
