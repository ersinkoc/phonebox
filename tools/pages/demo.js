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
      weather: { city: 'Tallinn', lat: 59.437, lon: 24.745 },
      night: { enabled: false, start: '00:30', end: '07:30', brightness: 0.02 },
      display: { appearance: null, showSeconds: true, idleReturnSec: 0, vibrate: true, volumeKeys: true, orientation: 'landscape', theme: 'default', accent: '#2dd4bf',
        artTint: true, autoBrightness: false, nightWhenDark: false, proximityWake: true, pin: false, homeScreen: false, keepAwake: true, followComputerLock: false,
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
      cloudPhotos: { cacheMaxMb: 250 },
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

  // What the agent sends to a phone (AgentConfig.ToPublic): no commands, keys, tokens or what a rule matches on.
  function publicConfig(c) {
    const scrubPhotos = (opts) => opts ? Object.fromEntries(Object.entries(opts).filter(([k]) => !['shareUrl', 'sharePassword', 'folder', 'albumId'].includes(k))) : null;
    const options = (w) => {
      if (w.type === 'command') return Object.fromEntries(Object.entries(w.options || {}).filter(([k]) => k === 'title' || k === 'interval'));
      if (w.type === 'photos') return scrubPhotos(w.options);
      if (w.type === 'stack' && Array.isArray(w.options?.widgets)) {
        return { ...w.options, widgets: w.options.widgets.map((sub) => sub?.type === 'photos' ? { ...sub, options: scrubPhotos(sub.options) } : sub) };
      }
      return w.options ?? null;
    };
    return {
      weather: c.weather, codexBar: c.codexBar, night: c.night, display: c.display,
      pages: (c.pages || []).map((p) => ({ id: p.id, title: p.title, icon: p.icon, enabled: p.enabled !== false, idleSec: p.idleSec ?? null, appearance: p.appearance ?? null,
        widgets: (p.widgets || []).map((w) => ({ id: w.id, type: w.type, x: w.x, y: w.y, w: w.w, h: w.h, appearance: w.appearance ?? null, options: options(w) })) })),
      shortcuts: (c.shortcuts || []).map(({ id, label, icon, color, toggle, cmd }) => ({ id, label, icon, color, toggle: !!toggle || cmd === 'builtin:mute' })),
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
        gpu: { name: 'NVIDIA GeForce RTX 4080', util: Math.max(0, wave(29, 4, 38, 2, 4)), temp: wave(50, 46, 61), power: wave(29, 38, 140, 2),
          powerLimit: 320, clock: 2520, fan: 34, vramUsed: 5.8, vramTotal: 16 },
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
        s(4, 'claude', 'infra', 'shell', 1), s(5, 'claude', 'blog-engine', 'idle', 38), s(6, 'codex', 'data-pipeline', 'idle', 74), s(7, 'claude', 'design-system', 'idle', 125)],
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

  const DEMO_PHOTOS = [
    {
      id: 'demo-alpine',
      caption: 'Alpine ridge at sunrise',
      album: 'Travel Favorites',
      takenAt: Date.UTC(2025, 6, 14, 10, 30),
      svg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 500"><defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#1e293b"/><stop offset=".6" stop-color="#fb923c"/><stop offset="1" stop-color="#fde047"/></linearGradient></defs><rect width="800" height="500" fill="url(#sky)"/><circle cx="560" cy="210" r="54" fill="#fef08a"/><polygon points="0,420 220,180 430,420" fill="#0f172a"/><polygon points="260,440 520,160 800,440" fill="#1e293b"/><rect y="400" width="800" height="100" fill="#090d16"/></svg>',
    },
    {
      id: 'demo-coast',
      caption: 'Cliffside ocean trail',
      album: 'Travel Favorites',
      takenAt: Date.UTC(2025, 7, 2, 10, 30),
      svg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 500"><defs><linearGradient id="sea" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0ea5e9"/><stop offset="1" stop-color="#0f766e"/></linearGradient></defs><rect width="800" height="320" fill="#bae6fd"/><rect y="280" width="800" height="220" fill="url(#sea)"/><polygon points="0,240 280,295 210,500 0,500" fill="#1e293b"/><circle cx="180" cy="110" r="40" fill="#fef9c3"/></svg>',
    },
    {
      id: 'demo-aurora',
      caption: 'Midnight fjord glow',
      album: 'Nordic Winter',
      takenAt: Date.UTC(2025, 10, 19, 10, 30),
      svg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 500"><defs><linearGradient id="aur" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2dd4bf"/><stop offset=".5" stop-color="#22c55e"/><stop offset="1" stop-color="#6366f1"/></linearGradient></defs><rect width="800" height="500" fill="#050816"/><path d="M0 180 Q 220 70 460 170 T 800 120 L 800 280 L 0 280 Z" fill="url(#aur)" opacity=".55"/><polygon points="40,500 260,250 490,500" fill="#0b1120"/><polygon points="360,500 620,230 800,450 800,500" fill="#111827"/></svg>',
    },
  ];
  const DEMO_PHOTO_ROOTS = [
    { id: 'pictures', name: 'Pictures', providerHint: 'local', path: '~/Pictures/Favorites' },
    { id: 'icloud-pics', name: 'iCloud Photos', providerHint: 'icloud', path: '~/Pictures/iCloud Photos' },
    { id: 'od-pics', name: 'OneDrive › Pictures', providerHint: 'onedrive', path: '~/OneDrive/Pictures' },
    { id: 'od-cloudonly', name: 'OneDrive › CloudOnly', providerHint: 'onedrive', path: '~/OneDrive/CloudOnly' },
    { id: 'gd-drive', name: 'Google Drive › My Drive', providerHint: 'google', path: '~/Google Drive/My Drive' },
    { id: 'db-cam', name: 'Dropbox › Camera Uploads', providerHint: 'dropbox', path: '~/Dropbox/Camera Uploads' },
  ];
  function findDemoWidgetOptions(c, widgetId) {
    if (!widgetId) return {};
    for (const p of c.pages || []) {
      for (const w of p.widgets || []) {
        if (w.type === 'photos' && w.id === widgetId) return w.options || {};
      }
    }
    const idx = widgetId.indexOf(':');
    if (idx > 0) {
      const sid = widgetId.slice(0, idx), cid = widgetId.slice(idx + 1);
      for (const p of c.pages || []) {
        for (const w of p.widgets || []) {
          if (w.type === 'stack' && w.id === sid) {
            const sub = (w.options?.widgets || []).find((x) => x.id === cid && x.type === 'photos');
            if (sub) return sub.options || {};
          }
        }
      }
    }
    return {};
  }
  function demoPhotoFeed(opts = {}) {
    const provider = opts.provider || 'local';
    const hasCloudTarget = !!(opts.shareUrl || opts.folder || opts.albumId);
    const configured = (provider === 'onedrive' || provider === 'google' || provider === 'dropbox') ? true
      : hasCloudTarget;
    if (!configured) {
      return { configured: false, provider, sourceLabel: provider, title: opts.title || null, updatedAt: now(), stale: false,
        error: provider === 'icloud' ? 'Paste an iCloud Shared Album link or pick a synced folder in Settings' : provider === 'nextcloud' ? 'Paste a Nextcloud public share link in Settings' : 'Choose a photo album or folder in PhoneBox Settings',
        photos: [] };
    }
    const sourceLabel = opts.folder || (provider === 'icloud' ? 'iCloud Shared Album' : provider === 'nextcloud' ? 'Nextcloud Share' : provider);
    if (/CloudOnly/i.test(opts.folder || '') && opts.downloadCloudFiles !== true) {
      return {
        configured: true, provider, sourceLabel, title: opts.title || null, updatedAt: now(), stale: false,
        error: '3 cloud-only photos were not downloaded. Enable "Download cloud-only files on demand" in PhoneBox Settings to allow cloud downloads.',
        photos: [], cloudOnlyCount: 3,
      };
    }
    const items = DEMO_PHOTOS.map((p) => ({
      id: p.id, url: `/api/photos/image/${p.id}`, caption: p.caption, takenAt: p.takenAt,
      album: p.album, source: sourceLabel, width: 800, height: 500,
    }));
    if (opts.order === 'newest') items.sort((a, b) => b.takenAt - a.takenAt);
    else if (opts.order === 'oldest') items.sort((a, b) => a.takenAt - b.takenAt);
    else if (opts.order === 'name') items.sort((a, b) => a.caption.localeCompare(b.caption));
    const cloudOnlyCount = /CloudOnly/i.test(opts.folder || '') ? 3 : 0;
    return { configured: true, provider, sourceLabel, title: opts.title || null, updatedAt: now(), stale: false, error: null, photos: items, cloudOnlyCount };
  }

  // ---- router
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
      actions: (controls.actions || []).map((a) => ({ id: a.id, name: a.label, kind: a.kind, state: actionState[a.id] ?? (/^(app|project|shortcut|focus)$/.test(a.kind) ? (c.shortcuts.some((x) => x.id === a.target) ? 'Ready' : 'Shortcut is missing') : null),
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
  const spotifyTrack = (i) => ({ id: spotifyId(i), name: TRACKS[i][0], artist: TRACKS[i][1] });
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
      if (a.kind === 'server') actionState[id] = op === 'stop' ? 'Stopped' : 'Running';
      else if (a.kind === 'task') { actionState[id] = 'Running'; setTimeout(() => { actionState[id] = 'Finished'; }, 4000); }
      else if (a.kind === 'sound' || a.kind === 'radio') actionState[id] = 'Playing';
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
    if (path === '/api/calendar') return json(calendar());
    if (path === '/api/docker') return json({ ...docker(), control: allows(await config(), (t) => t === 'docker') });
    if (path === '/api/obs') { const c = await config(); return json({ ...obsState(), enabled: c.obs?.enabled !== false, control: widgets(c).some((w) => w.type === 'obs' && w.options?.control === true) }); }
    if (path === '/api/phones') return json([
      { id: '3f9a1c0d7b2e', model: 'UMIDIGI BISON', lastSeen: now() - 2000, via: 'USB', name: null, pages: null },
      { id: 'c81e728d9d4c', model: 'samsung SM-X200', lastSeen: now() - 140000, via: 'Wi-Fi', name: null, pages: null },
    ]);
    if (path === '/api/batteries') return json({ devices: [
      { name: 'WH-1000XM5', level: 64, kind: 'headphones', charging: null },
      { name: 'MX Master 3S', level: 85, kind: 'mouse', charging: null },
      { name: 'MX Keys', level: 18, kind: 'keyboard', charging: null },
      { name: 'Xbox Wireless Controller', level: 40, kind: 'controller', charging: null },
    ] });
    if (path.startsWith('/api/obs/') && method === 'POST') {
      if (!allows(await config(), isObs)) return json({ error: "controlling OBS is turned off in the widget's settings" }, 400);
      return obsAct(path.slice(9), q.get('scene')) ? json({ ok: true }) : json({ error: 'unknown action' }, 400);
    }
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
    if (path === '/api/photos/status') return json({
      providers: ['onedrive', 'google', 'dropbox'].map((p) => ({
        provider: p, clientConfigured: false, connected: false, account: null,
        redirectUri: `http://127.0.0.1:8765/api/photos/oauth/callback/${p}`, localFolderDetected: true,
      })),
      localRoots: DEMO_PHOTO_ROOTS,
    });
    if (path === '/api/photos/local-roots') return json(DEMO_PHOTO_ROOTS);
    if (path === '/api/photos/feed') {
      const c = await config();
      return json(demoPhotoFeed(findDemoWidgetOptions(c, q.get('widgetId'))));
    }
    if (path === '/api/photos/preview-feed' && method === 'POST') return json(demoPhotoFeed((parse(body) || {}).options || {}));
    if (path === '/api/photos/browse' && method === 'POST') {
      const req = parse(body) || {}, prov = req.provider || 'local';
      if (prov === 'local') {
        const list = req.path
          ? [{ id: req.path + '/2025', name: '2025', kind: 'folder', path: req.path + '/2025' }, { id: req.path + '/Travel', name: 'Travel', kind: 'folder', path: req.path + '/Travel' }]
          : DEMO_PHOTO_ROOTS.map((r) => ({ id: r.path, name: r.name, kind: 'folder', path: r.path }));
        return json({ ok: true, provider: prov, currentPath: req.path || null, parentPath: null, entries: list });
      }
      if (prov === 'google') {
        return json({
          ok: true, provider: prov, currentPath: req.path || 'My Drive', parentPath: null,
          entries: [
            { id: 'google-fav', name: 'Travel Favorites', kind: 'folder', itemCount: 42, path: 'My Drive/Travel Favorites' },
            { id: 'google-cam', name: 'Camera Roll', kind: 'folder', itemCount: 120, path: 'My Drive/Camera Roll' },
          ],
        });
      }
      if (prov === 'onedrive') {
        if (req.parentId === 'albums') {
          return json({
            ok: true, provider: prov, currentPath: 'Photo Albums', parentPath: '/',
            entries: [
              { id: 'od-alb-1', name: 'Summer Trips', kind: 'album', itemCount: 24, path: 'Summer Trips' },
            ],
          });
        }
        return json({
          ok: true, provider: prov, currentPath: req.path || '/', parentPath: null,
          entries: [
            { id: 'onedrive-cam', name: 'Pictures / Photos', kind: 'folder', itemCount: 120, path: '/Pictures/Photos' },
            { id: 'albums', name: 'Photo Albums', kind: 'collection', itemCount: 3, path: 'Photo Albums' },
          ],
        });
      }
      return json({
        ok: true, provider: prov, currentPath: req.path || '/', parentPath: null,
        entries: [
          { id: '/Camera Uploads', name: 'Camera Uploads', kind: 'folder', itemCount: 120, path: '/Camera Uploads' },
          { id: '/Travel Favorites', name: 'Travel Favorites', kind: 'folder', itemCount: 42, path: '/Travel Favorites' },
        ],
      });
    }
    if (path === '/api/photos/test' && method === 'POST') {
      const opts = (parse(body) || {}).options || {};
      const feed = demoPhotoFeed(opts);
      const cloudCount = feed.cloudOnlyCount || 0;
      const prov = opts.provider || 'local';
      const f = (opts.folder || '').trim();
      const isLocalDir = (p) => /^\/(?:Users|home|Volumes|mnt|media|var|private|tmp)(?:\/|$)/.test(p)
        || p === '~' || p.startsWith('~/') || p.startsWith('~\\')
        || /^[A-Za-z]:[\\/]/.test(p) || p.startsWith('\\\\');
      const remoteStream = prov === 'nextcloud'
        || (prov === 'icloud' && !!(opts.shareUrl || '').trim())
        || (['onedrive', 'google', 'dropbox'].includes(prov) && !!(opts.albumId || (!isLocalDir(f) && f)));
      if (!feed.configured) return json({ ok: false, message: feed.error, count: 0, sourceLabel: '', cloudOnlyCount: 0, remoteStream });
      if (feed.error) return json({ ok: false, message: feed.error, count: 0, sourceLabel: feed.sourceLabel, cloudOnlyCount: cloudCount, remoteStream });
      const msg = cloudCount > 0 && opts.downloadCloudFiles
        ? `Found ${feed.photos.length} photos in ${feed.sourceLabel} (${cloudCount} in cloud — photos will be downloaded onto this computer as they are shown)`
        : `Found ${feed.photos.length} photos in ${feed.sourceLabel}`;
      return json({ ok: true, message: msg, count: feed.photos.length, sourceLabel: feed.sourceLabel, cloudOnlyCount: cloudCount, remoteStream });
    }
    if (path.startsWith('/api/photos/image/')) {
      const id = decodeURIComponent(path.slice('/api/photos/image/'.length));
      const item = DEMO_PHOTOS.find((p) => p.id === id) || DEMO_PHOTOS[0];
      return new Response(item.svg, { status: 200, headers: { 'Content-Type': 'image/svg+xml; charset=utf-8' } });
    }
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
    if (path === '/api/charge') return json({ phone: { level: 72, plugged: true, temp: 29, at: now() - 20e3, source: 'usb', usb: 'configured' }, power: null, switchedAt: null, error: null });
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
    if (path === '/api/info') return json({ version: VERSION, os: 'windows', configPath: 'browser storage (demo)', logPath: '—', adb: 'demo', connected: true, devices: ['DEMO0001'], appMissing: [], usbProblem: null });
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

  // Album art is an <img>, which fetch() does not see: swap its address for the demo cover.
  new MutationObserver((list) => {
    for (const m of list) {
      const img = m.target;
      if (img.tagName === 'IMG' && /\/api\/art/.test(img.getAttribute('src') || '')) img.src = ART;
    }
  }).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ['src'] });

  // A pretend Android bridge for phone-only widgets.
  if (!window.Android && document.currentScript.dataset.panel !== undefined)
    window.Android = { battery: () => JSON.stringify({ level: 82, plugged: true, temp: 29.5 }), transport: () => 'usb', version: () => VERSION,
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

  window.PhoneBoxDemo = { emit, ask, reset() { localStorage.removeItem('pb-demo-config'); store.set('pb-demo-version', cfgVersion() + 1); } };
})();
