// PhoneBox desk panel: core, pages, navigation and data from the agent. The widgets live in widgets/*.js;
// they import what they need from here, so this module must not run code that needs them while it loads
// (the start section at the end runs after every module has loaded).
import { W } from './registry.js';
import './appearance.js';
import { cached } from './widgets/helpers.js';
import './widgets/basic.js';
import './widgets/stack.js';
import './widgets/time.js';
import './widgets/pc.js';
import './widgets/sky.js';
import './widgets/web.js';
import './widgets/controls.js';

// PhoneBox desk panel. Pages are 12 × 6 grids of widgets, designed in the agent's settings page.
// The panel talks to the PhoneBox agent through "/agent/*", which the Android shell proxies over USB
// (adb reverse) or Wi-Fi to the computer.

export const API = '/agent/api';
// The phone's native proxy only forwards requests that carry this header (or image GETs), so script in a sandboxed
// Custom HTML widget cannot reach the agent: adding the header to a cross-origin request needs a CORS preflight,
// which the proxy refuses. See MainActivity.fromPanel.
export const PANEL_HEADERS = { 'X-PhoneBox-Panel': '1' };
export const LOCALE = 'en-GB';
export const COLS = 12;
export const ROWS = 6;
export const $ = (id) => document.getElementById(id);
// The native bridge (window.Android) is in every frame, sandboxed widgets included, so each call carries a key that only
// the panel's own frames receive (MainActivity.bridgeKey). This wrapper adds it.
const BRIDGE = ['battery', 'buzz', 'haptic', 'pcs', 'selectPc', 'forgetPc', 'setBrightness', 'setHomeScreen', 'setOrientation',
  'setPinned', 'setVolumeKeys', 'transport', 'sensors', 'version'];
export const bridge = typeof Android === 'undefined' ? null : Object.fromEntries(BRIDGE.filter((m) => typeof Android[m] === 'function')
  .map((m) => [m, (...args) => Android[m](window.__phoneboxKey || '', ...args)]));
// Live preview inside the agent's layout editor: the layout comes from the editor (unsaved), not from the agent.
export const PREVIEW = new URLSearchParams(location.search).has('preview') && window.parent !== window;

/** Used until the agent has sent its settings once (first start without a connection). */
export const FALLBACK_PAGES = [{
  id: 'home', title: 'Home', icon: '🏠', enabled: true, widgets: [
    { id: 'clock', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: {} },
    { id: 'media', type: 'media', x: 6, y: 0, w: 6, h: 4, options: {} },
    { id: 'cpu', type: 'stat', x: 0, y: 4, w: 3, h: 2, options: { metric: 'cpu' } },
    { id: 'gpu', type: 'stat', x: 3, y: 4, w: 3, h: 2, options: { metric: 'gpu' } },
    { id: 'ram', type: 'stat', x: 6, y: 4, w: 3, h: 2, options: { metric: 'ram' } },
    { id: 'net', type: 'stat', x: 9, y: 4, w: 3, h: 2, options: { metric: 'net' } },
  ],
}];

export const DEFAULT_CFG = {
  weather: { city: 'Tallinn', lat: 59.437, lon: 24.745 },
  night: { enabled: true, start: '00:30', end: '07:30', brightness: 0.02 },
  display: { showSeconds: true, idleReturnSec: 90, vibrate: true, volumeKeys: true },
  pages: FALLBACK_PAGES,
  shortcuts: [],
};

export const store = {
  get(k, d) { try { const v = localStorage.getItem(k); return v ? JSON.parse(v) : d; } catch (_) { return d; } },
  set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (_) {} },
};

export let cfg = PREVIEW ? { ...DEFAULT_CFG, pages: [] } : { ...DEFAULT_CFG, ...store.get('cfg', {}) };
export let cfgVersion = null;
export let online = null;
export let lastTouch = Date.now();
export let lastUserTouch = 0;     // last real touch or key (rules wait a while after it)
export let wakeUntil = 0;
export let rulesPaused = null;    // unix ms until which the agent's rules are paused
export let nightOn = null;
export let evSeq = null;          // last agent event seen (notifications, page switches…)
export let pushVersion = null;

/** Latest data from the agent and the web; widgets read from here. */
export const D = { state: null, quotas: null, work: null, procs: null, push: {}, weather: null, calendar: null, notes: store.get('notes', []) };

export const HIST = 90;
export const hist = { cpu: [], gpu: [], ram: [], vram: [], down: [], up: [], ping: [], gputemp: [], cputemp: [] };
export const pushHist = (k, v) => { const a = hist[k]; a.push(v ?? 0); if (a.length > HIST) a.shift(); };

// ---------------------------------------------------------------- helpers
export async function api(path, method = 'GET', timeout = 2500) {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), timeout);
  try {
    const r = await fetch(API + path, { method, signal: ctl.signal, cache: 'no-store', headers: PANEL_HEADERS });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const ct = r.headers.get('content-type') || '';
    return ct.includes('json') ? r.json() : r.text();
  } finally { clearTimeout(t); }
}

export const haptic = () => { try { bridge && bridge.haptic(); } catch (_) {} };
export const buzz = (p) => { try { if (bridge && !nightOn && cfg.display?.vibrate !== false) bridge.buzz(p); } catch (_) {} };
export const transport = () => { try { return bridge ? bridge.transport() : ''; } catch (_) { return ''; } };

/** h('div', 'cls', child, 'text', [more]) */
export function h(tag, cls, ...kids) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  for (const k of kids.flat(3)) if (k != null && k !== false) e.append(k.nodeType ? k : String(k));
  return e;
}

export const pad = (n) => String(n).padStart(2, '0');
export const fmtTime = (ms) => { const s = Math.max(0, Math.floor(ms / 1000)); return Math.floor(s / 60) + ':' + pad(s % 60); };
export function fmtRate(bps) {
  if (bps == null) return ['—', ''];
  if (bps >= 1e9) return [(bps / 1e9).toFixed(1), 'GB/s'];
  if (bps >= 1e6) return [(bps / 1e6).toFixed(bps >= 1e8 ? 0 : 1), 'MB/s'];
  if (bps >= 1e3) return [(bps / 1e3).toFixed(0), 'KB/s'];
  return [Math.round(bps).toString(), 'B/s'];
}
export const rateStr = (b) => fmtRate(b).join(' ');
export function fmtDur(ms) {
  const m = Math.max(0, Math.round(ms / 60000));
  if (m < 60) return `${m} min`;
  const hr = Math.floor(m / 60);
  if (hr < 24) return `${hr} h ${m % 60} min`;
  const d = Math.floor(hr / 24);
  return `${d} d ${hr % 24} h`;
}
export function shortDur(ms) {
  const m = Math.max(0, Math.floor(ms / 60000));
  if (m < 1) return 'now';
  if (m < 60) return m + 'm';
  const hr = Math.floor(m / 60);
  return hr < 24 ? `${hr}h ${m % 60}m` : `${Math.floor(hr / 24)}d`;
}
export const ago = (ts) => { const d = Date.now() - ts; return d < 60_000 ? 'just now' : shortDur(d) + ' ago'; };
export const r1 = (v) => (v == null ? '—' : (Math.round(v * 10) / 10).toString());
export const fmtMem = (mb) => (mb >= 1024 ? (mb / 1024).toFixed(1) + ' GB' : Math.round(mb) + ' MB');
export const cssVar = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
export const color = (c) => (c && c.startsWith('--') ? cssVar(c) : c);

export let toastTimer = 0;
export function toast(msg, kind = '', title = '', seconds = 2.6) {
  const t = $('toast');
  t.replaceChildren(...(title ? [h('b', null, title), h('span', null, msg)] : [h('span', null, msg)]));
  t.className = 'toast on ' + kind;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.className = 'toast ' + kind), Math.max(1, seconds) * 1000);
}

// ---------------------------------------------------------------- charts
export function sizeCanvas(c) {
  const dpr = window.devicePixelRatio || 1;
  const w = Math.max(1, Math.round(c.clientWidth * dpr)), hh = Math.max(1, Math.round(c.clientHeight * dpr));
  if (c.width !== w || c.height !== hh) { c.width = w; c.height = hh; }
  return [c.getContext('2d'), w, hh, dpr];
}

/** Draws series as area charts. series: [{ data, color, max }] */
export function chart(canvas, series, opts = {}) {
  if (!canvas.clientWidth) return;
  const [g, w, hh, dpr] = sizeCanvas(canvas);
  g.clearRect(0, 0, w, hh);
  if (opts.grid) {
    g.strokeStyle = 'rgba(255,255,255,0.06)';
    g.lineWidth = 1;
    for (let i = 1; i < 4; i++) { const y = Math.round((hh * i) / 4) + 0.5; g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke(); }
  }
  const top = 4 * dpr;
  for (const s of series) {
    const d = s.data;
    if (d.length < 2) continue;
    const max = s.max || 1;
    const step = w / (HIST - 1);
    const x0 = w - (d.length - 1) * step;
    const y = (v) => hh - (Math.min(v, max) / max) * (hh - top);
    g.beginPath();
    g.moveTo(x0, y(d[0]));
    for (let i = 1; i < d.length; i++) g.lineTo(x0 + i * step, y(d[i]));
    g.lineWidth = 1.6 * dpr;
    g.strokeStyle = s.color;
    g.lineJoin = 'round';
    g.stroke();
    g.lineTo(w, hh);
    g.lineTo(x0, hh);
    g.closePath();
    const grad = g.createLinearGradient(0, 0, 0, hh);
    grad.addColorStop(0, s.color + '55');
    grad.addColorStop(1, s.color + '00');
    g.fillStyle = grad;
    g.fill();
  }
}

export function ringSvg(pct, elapsed) {
  const r = 42, c = 2 * Math.PI * r;
  const off = c * (1 - Math.max(0, Math.min(100, pct)) / 100);
  let tick = '';
  if (elapsed != null) {
    const a = elapsed * 2 * Math.PI;
    const p = (rad) => [50 + Math.cos(a) * rad, 50 + Math.sin(a) * rad].map((v) => v.toFixed(1));
    const [x1, y1] = p(35), [x2, y2] = p(49);
    tick = `<line class="pace" x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}"/>`;
  }
  return `<svg viewBox="0 0 100 100"><circle class="bg" cx="50" cy="50" r="${r}"/>` +
    `<circle class="fg" cx="50" cy="50" r="${r}" stroke-dasharray="${c.toFixed(1)}" stroke-dashoffset="${off.toFixed(1)}"/>${tick}</svg>`;
}

// ---------------------------------------------------------------- metrics (stat, gauge, chart widgets)
export const shortCpu = (n) => (n || '').replace(/^(AMD|Intel\(R\))\s*/, '').split(' ').slice(0, 4).join(' ');
export const gpuOf = (s) => s?.pc?.gpu;
export const netMax = () => Math.max(1, ...hist.down, ...hist.up) * 1.15;

export const METRICS = {
  cpu: { label: 'CPU', name: 'Processor', color: '--cpu', unit: '%', max: () => 100, val: (s) => s.pc?.cpu,
    sub: (s) => (s.pc?.cpuTemp != null ? `${Math.round(s.pc.cpuTemp)}°C` : shortCpu(s.pc?.cpuName)), title: (s) => s.pc?.cpuName },
  gpu: { label: 'GPU', name: 'Graphics', color: '--gpu', unit: '%', max: () => 100, val: (s) => gpuOf(s)?.util,
    sub: (s) => {
      const g = gpuOf(s);
      if (!g) return 'No GPU data';
      if (g.temp != null && g.power != null) return `${Math.round(g.temp)}°C · ${Math.round(g.power)} W`;
      if (g.vramUsed != null && g.vramTotal != null) return `${r1(g.vramUsed)} / ${Math.round(g.vramTotal)} GB memory`; // no sensors (Apple GPU)
      return g.name || 'Active';
    }, title: (s) => gpuOf(s)?.name },
  ram: { label: 'RAM', name: 'Memory', color: '--ram', unit: 'GB', max: (s) => s.pc?.ramTotalGb || 1, val: (s) => s.pc?.ramUsedGb, fmt: r1,
    pct: (s) => (s.pc ? (s.pc.ramUsedGb / s.pc.ramTotalGb) * 100 : null), sub: (s) => (s.pc && s.pc.ramTotalGb ? `${r1(s.pc.ramUsedGb)} / ${Math.round(s.pc.ramTotalGb)} GB` : '') },
  vram: { label: 'VRAM', name: 'Video memory', color: '--gpu', unit: '%', max: () => 100,
    val: (s) => (gpuOf(s)?.vramTotal ? (gpuOf(s).vramUsed / gpuOf(s).vramTotal) * 100 : null),
    sub: (s) => (gpuOf(s)?.vramTotal ? `${r1(gpuOf(s).vramUsed)} / ${Math.round(gpuOf(s).vramTotal)} GB` : '') },
  net: { label: 'NETWORK', name: 'Network', color: '--net', rate: true, hist: 'down', max: netMax, val: (s) => s.pc?.netDownBps,
    sub: (s) => '↑ ' + rateStr(s.pc?.netUpBps) + (s.ping?.ms != null ? ` · ${Math.round(s.ping.ms)} ms` : '') },
  down: { label: 'DOWNLOAD', name: 'Download', color: '--net', rate: true, max: netMax, val: (s) => s.pc?.netDownBps },
  up: { label: 'UPLOAD', name: 'Upload', color: '--up', rate: true, max: netMax, val: (s) => s.pc?.netUpBps },
  ping: { label: 'PING', name: 'Ping', color: '#38bdf8', unit: 'ms', max: () => Math.max(50, ...hist.ping) * 1.2, val: (s) => s.ping?.ms,
    sub: (s) => (s.ping ? `avg ${s.ping.avgMs ?? '—'} ms · loss ${s.ping.lossPct}%` : '') },
  gputemp: { label: 'GPU TEMP', name: 'GPU temperature', color: '#f97316', unit: '°C', max: () => 100, val: (s) => gpuOf(s)?.temp,
    sub: (s) => {
      const g = gpuOf(s);
      if (!g) return 'No GPU data';
      const detail = [g.fan != null ? `Fan ${Math.round(g.fan)}%` : null, g.clock != null ? `${Math.round(g.clock)} MHz` : null].filter(Boolean);
      return detail.length ? detail.join(' · ') : (g.temp != null ? 'Temperature' : 'No GPU data');
    } },
  cputemp: { label: 'CPU TEMP', name: 'CPU temperature', color: '#fb7185', unit: '°C', max: () => 100, val: (s) => s.pc?.cpuTemp,
    sub: (s) => (s.pc?.cpuTemp == null ? 'needs LibreHardwareMonitor' : shortCpu(s.pc?.cpuName)) },
};

export function metricText(m, s) {
  const v = m.val(s);
  if (m.rate) return fmtRate(v);
  if (v == null) return ['—', m.unit || ''];
  return [m.fmt ? m.fmt(v) : String(Math.round(v)), m.unit || ''];
}

// ---------------------------------------------------------------- weather (Open-Meteo, straight from the phone)
export const WX = {
  0: ['Clear', '☀️', '🌙'], 1: ['Mostly clear', '🌤️', '🌙'], 2: ['Partly cloudy', '⛅', '☁️'], 3: ['Overcast', '☁️', '☁️'],
  45: ['Fog', '🌫️'], 48: ['Rime fog', '🌫️'],
  51: ['Light drizzle', '🌦️'], 53: ['Drizzle', '🌦️'], 55: ['Heavy drizzle', '🌧️'], 56: ['Freezing drizzle', '🌧️'], 57: ['Freezing drizzle', '🌧️'],
  61: ['Light rain', '🌦️'], 63: ['Rain', '🌧️'], 65: ['Heavy rain', '🌧️'], 66: ['Freezing rain', '🌧️'], 67: ['Freezing rain', '🌧️'],
  71: ['Light snow', '🌨️'], 73: ['Snow', '🌨️'], 75: ['Heavy snow', '❄️'], 77: ['Snow grains', '🌨️'],
  80: ['Light showers', '🌦️'], 81: ['Showers', '🌧️'], 82: ['Heavy showers', '⛈️'], 85: ['Snow showers', '🌨️'], 86: ['Heavy snow showers', '❄️'],
  95: ['Thunderstorm', '⛈️'], 96: ['Thunderstorm, hail', '⛈️'], 99: ['Severe hail', '⛈️'],
};
export const wxIcon = (code, day = 1) => { const x = WX[code] || ['—', '🌡️']; return day === 0 && x[2] ? x[2] : x[1]; };

export async function loadWeather(force = false) {
  const { lat, lon } = cfg.weather || DEFAULT_CFG.weather;
  const key = `v3:${lat},${lon}`;
  const cached = store.get('wx', null);
  if (cached && cached.key === key && !D.weather) { D.weather = cached.data; emit('weather'); }
  if (!force && cached && cached.key === key && Date.now() - cached.at < 14 * 60_000) return;
  try {
    const url = `https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}` +
      '&current=temperature_2m,apparent_temperature,weather_code,is_day' +
      '&hourly=temperature_2m,weather_code,precipitation_probability,is_day' +
      '&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,daylight_duration,uv_index_max' +
      '&timezone=auto&forecast_days=7';
    const ctl = new AbortController();
    setTimeout(() => ctl.abort(), 8000);
    const data = await (await fetch(url, { signal: ctl.signal })).json();
    store.set('wx', { key, at: Date.now(), data });
    D.weather = data;
    emit('weather');
  } catch (_) { if (!D.weather) emit('weather'); }
}

export function wxLines() {
  const w = D.weather;
  if (!w || !w.current) return null;
  const c = w.current, d = w.daily || {};
  const desc = (WX[c.weather_code] || ['—'])[0];
  const hi = d.temperature_2m_max ? Math.round(d.temperature_2m_max[0]) : null;
  const lo = d.temperature_2m_min ? Math.round(d.temperature_2m_min[0]) : null;
  return {
    icon: wxIcon(c.weather_code, c.is_day),
    temp: Math.round(c.temperature_2m) + '°',
    desc: `${desc} · ${cfg.weather?.city || ''}`,
    range: (hi != null ? `↑${hi}° ↓${lo}° · ` : '') + `Feels like ${Math.round(c.apparent_temperature)}°`,
  };
}

export function wxBlock() {
  const e = h('div', 'wx', h('span', 'wx-icon', '·'), h('span', 'wx-temp', '--°'),
    h('div', 'wx-meta', h('span', 'wx-desc', 'Loading weather…'), h('span', 'wx-range muted')));
  e.update = () => {
    const l = wxLines();
    if (!l) { e.querySelector('.wx-desc').textContent = D.weather === null ? 'Loading weather…' : 'Weather unavailable'; return; }
    e.querySelector('.wx-icon').textContent = l.icon;
    e.querySelector('.wx-temp').textContent = l.temp;
    e.querySelector('.wx-desc').textContent = l.desc;
    e.querySelector('.wx-range').textContent = l.range;
  };
  return e;
}

// ---------------------------------------------------------------- status line (clock widget)
export function phoneBattery() {
  if (!bridge) return '';
  try { const b = JSON.parse(bridge.battery()); return `📱 ${b.level}%${b.plugged ? ' ⚡' : ''}`; } catch (_) { return ''; }
}

export function quotaProviders() { return D.quotas?.providers || []; }

export function renderStatus(el) {
  const via = transport();
  const name = online ? (D.state?.host || 'PC') + (via === 'wifi' ? ' · Wi-Fi' : via === 'usb' ? ' · USB' : '') : online === false ? 'PC not connected' : 'Connecting…';
  const kids = [h('span', 'dot ' + (online ? 'on' : online === false ? 'off' : '')), h('span', null, name)];
  const bat = phoneBattery();
  if (bat) kids.push(h('span', 'sep', '·'), h('span', null, bat));
  for (const p of quotaProviders()) {
    const l = p.limits?.[0];
    if (!l) continue;
    const s = h('span', 'qm', h('span', 'sep', '·'), p.name + ' ', h('b', null, Math.round(l.percent) + '%'));
    if (l.percent >= 95) s.style.color = '#f87171'; else if (l.percent >= 80) s.style.color = '#fbbf24';
    kids.push(s);
  }
  const waiting = (D.work?.sessions || []).filter((s) => s.status === 'waiting').length;
  if (waiting) kids.push(h('span', 'sep', '·'), h('span', 'smini', `${waiting} waiting`));
  const u = D.state?.update;
  if (u) kids.push(h('span', 'sep', '·'), h('span', 'upd', u.app && via !== 'usb' ? `⬆ ${u.latest}: plug in USB to update` : `⬆ ${u.latest} available`));
  el.replaceChildren(...kids);
  // Too long for the widget: drop the quota summaries, the most dispensable part.
  while (el.scrollWidth > el.clientWidth + 1 && el.querySelector('.qm')) [...el.querySelectorAll('.qm')].pop().remove();
}

// ---------------------------------------------------------------- shortcuts
export async function runShortcut(btn, id) {
  haptic();
  btn.classList.remove('ok', 'err');
  btn.classList.add('busy');
  let ok = false;
  try { await api('/run/' + encodeURIComponent(id), 'POST', 5000); ok = true; } catch (_) {}
  btn.classList.remove('busy');
  btn.classList.add(ok ? 'ok' : 'err');
  setTimeout(() => btn.classList.remove('ok', 'err'), 900);
}

export function keyButton(s) {
  const b = h('button', 'key' + (s.toggle ? ' toggle' : ''), h('span', 'k-icon', s.icon || '•'), h('span', 'k-label', s.label));
  b.style.setProperty('--k', s.color || '#6b7280');
  b.dataset.id = s.id;
  b.addEventListener('click', () => runShortcut(b, s.id));
  return b;
}

// ---------------------------------------------------------------- media (shared state, any number of widgets)
export let media = null;          // last media state + the time it was received
export let volDragging = false;
export const setVolDragging = (v) => { volDragging = v; };
// The background glow: the accent colour (Settings › Display), or the album art's colour while music plays.
export let DEFAULT_AMBIENT = '45, 212, 191';
export let artTint = true;
export let currentArtColor = null;
export const setArtColor = (c) => { currentArtColor = c; setAmbient(c); };
export const hexToRgb = (hex) => { const m = /^#?([0-9a-f]{6})$/i.exec(hex || ''); if (!m) return null; const n = parseInt(m[1], 16); return `${n >> 16}, ${(n >> 8) & 255}, ${n & 255}`; };

export function averageColor(img) {
  try {
    const c = document.createElement('canvas');
    c.width = c.height = 16;
    const g = c.getContext('2d');
    g.drawImage(img, 0, 0, 16, 16);
    const d = g.getImageData(0, 0, 16, 16).data;
    let r = 0, gg = 0, b = 0, n = 0;
    for (let i = 0; i < d.length; i += 4) {
      const mx = Math.max(d[i], d[i + 1], d[i + 2]), mn = Math.min(d[i], d[i + 1], d[i + 2]);
      if (mx < 30 || mx - mn < 20) continue; // skip very dark / grey pixels
      r += d[i]; gg += d[i + 1]; b += d[i + 2]; n++;
    }
    if (!n) return null;
    const [R, G, B] = [r / n, gg / n, b / n];
    const k = 220 / (Math.max(R, G, B) || 1); // normalise brightness
    return [R, G, B].map((v) => Math.round(Math.min(255, v * k))).join(', ');
  } catch (_) { return null; }
}
export const setAmbient = (rgb) => document.documentElement.style.setProperty('--ambient', (artTint && rgb) || DEFAULT_AMBIENT);

export const MEDIA_HTML = `
  <div class="m-top">
    <div class="art-wrap"><img class="art" alt=""><div class="art-ph">♪</div></div>
    <div class="m-text"><div class="m-app"><span class="m-source-icon" aria-hidden="true"></span><span class="m-app-name">Media</span></div><div class="m-title">Nothing playing</div><div class="m-artist">Start something on the PC</div></div>
  </div>
  <div class="m-progress"><div class="bar"><div class="fill"></div></div><div class="times"><span class="m-pos">0:00</span><span class="m-dur">0:00</span></div></div>
  <div class="m-controls">
    <button class="ic" data-media="prev" aria-label="Previous"><svg viewBox="0 0 24 24"><path d="M6 5h2v14H6zM20 5v14L9.5 12z"/></svg></button>
    <button class="ic play" data-media="playpause" aria-label="Play/Pause">
      <svg class="i-play" viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
      <svg class="i-pause" viewBox="0 0 24 24"><path d="M7 5h4v14H7zM13 5h4v14h-4z"/></svg>
    </button>
    <button class="ic" data-media="next" aria-label="Next"><svg viewBox="0 0 24 24"><path d="M16 5h2v14h-2zM4 5v14l10.5-7z"/></svg></button>
    <div class="vol">
      <button class="ic sm mute" aria-label="Mute">
        <svg class="i-vol" viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9zm11.5 3a4.5 4.5 0 0 0-2.5-4v8a4.5 4.5 0 0 0 2.5-4z"/></svg>
        <svg class="i-mute" viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9zm12.6 3 2.2-2.2-1.4-1.4-2.2 2.2-2.2-2.2-1.4 1.4 2.2 2.2-2.2 2.2 1.4 1.4 2.2-2.2 2.2 2.2 1.4-1.4z"/></svg>
      </button>
      <input type="range" class="volr" min="0" max="100" step="1" value="50"><span class="vol-val">--</span>
    </div>
  </div>
  <div class="offline-msg"><b>Computer not connected</b>
    <span>Is the PhoneBox agent running? Plug in the USB cable, or turn on Wi-Fi in the PhoneBox settings and pair once over USB.</span></div>`;

// ---------------------------------------------------------------- quotas
export const Q_STYLE = { antigravity: { color: '#38bdf8', logo: '✦' }, claude: { color: '#d97757', logo: '✳' }, codex: { color: '#e5e7eb', logo: '◎' } };

export function fmtReset(ts) {
  if (!ts) return '';
  const d = new Date(ts);
  const hm = d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
  const day = d.toDateString() === new Date().toDateString() ? hm : d.toLocaleDateString(LOCALE, { weekday: 'short' }) + ' ' + hm;
  return `${day} · in ${fmtDur(ts - Date.now())}`;
}

/** Compares the share of the quota used with the share of the window elapsed. */
export function paceInfo(l) {
  if (!l.resetsAt || !l.windowSec) return null;
  const win = l.windowSec * 1000;
  const left = l.resetsAt - Date.now();
  const elapsed = Math.min(1, Math.max(0, 1 - left / win));
  if (l.percent >= 100) return { elapsed, cls: 'over', text: 'Limit reached' };
  if (elapsed < 0.04 || l.percent < 1) return { elapsed, cls: 'calm', text: 'Plenty left' };
  const rate = l.percent / (elapsed * win);          // % per ms
  const msTo100 = (100 - l.percent) / rate;
  if (msTo100 < left) return { elapsed, cls: 'over', text: `At this pace: full in ${fmtDur(msTo100)}` };
  const diff = l.percent - elapsed * 100;
  if (diff > 10) return { elapsed, cls: 'fast', text: 'Ahead of pace' };
  if (diff < -10) return { elapsed, cls: 'calm', text: 'Plenty left' };
  return { elapsed, cls: '', text: 'On pace' };
}

// ---------------------------------------------------------------- sessions & GitHub
export const S_ORDER = { waiting: 0, busy: 1, shell: 2, idle: 3 };
export const S_TOOLS = {
  claude: { icon: '✳', label: 'Claude Code' },
  codex: { icon: '◎', label: 'Codex' },
  antigravity: { icon: '✦', label: 'Antigravity' },
  openclaw: { icon: '🦀', label: 'OpenClaw' },
};
export const prevStatus = new Map();
export let workLoaded = false;

/** Vibrates and shows a toast when a session finishes or starts waiting for input. */
export function notifyTransitions(sessions) {
  for (const s of sessions) {
    const before = prevStatus.get(s.id);
    if (workLoaded && before && before !== s.status) {
      if (s.status === 'waiting') {
        buzz('0,90,90,90,90,160');
        toast(s.waitingFor || 'needs input', 'err', s.name);
      } else if (s.status === 'idle' && (before === 'busy' || before === 'shell')) {
        buzz('0,70,110,70');
        toast(`${s.name} finished`, 'ok');
      }
    }
    prevStatus.set(s.id, s.status);
  }
  workLoaded = true;
}

export function prRow(p, mine) {
  const row = h('div', 'gh-pr' + (p.draft ? ' draft' : ''));
  if (mine) {
    const ciMap = { SUCCESS: '✓', FAILURE: '✗', ERROR: '✗', PENDING: '●', EXPECTED: '●' };
    row.appendChild(h('span', 'ci ' + (p.ci || 'none'), ciMap[p.ci] || '–'));
  } else {
    row.appendChild(h('span', 'ci none', '◇'));
  }
  row.append(h('span', 'rp', `${p.repo.split('/')[1]}#${p.number}`), h('span', 'tt', p.title));
  if (mine && (p.review === 'APPROVED' || p.review === 'CHANGES_REQUESTED'))
    row.appendChild(h('span', 'rv ' + p.review, p.review === 'APPROVED' ? 'approved' : 'changes'));
  return row;
}

// ---------------------------------------------------------------- processes: instance sheet
export function monogramColor(name) {
  let x = 0;
  for (const ch of name) x = (x * 31 + ch.charCodeAt(0)) % 360;
  return `hsl(${x} 70% 65%)`;
}

/** Fills an icon box with the app icon, falling back to a coloured monogram. */
export function fillIcon(box, g) {
  box.replaceChildren();
  box.style.background = monogramColor(g.name);
  box.textContent = (g.title || g.name).charAt(0).toUpperCase();
  if (g.hasIcon !== false) {
    const img = new Image();
    img.onload = () => { box.textContent = ''; box.style.background = 'transparent'; box.appendChild(img); };
    img.src = `${API}/procs/icon?name=${encodeURIComponent(g.name)}`;
  }
}

export let sheet = null;   // { group, detail, canKill }

/** Two-tap confirmation: the first tap arms the button, the second one runs the action. */
export function armButton(btn, armedText, action) {
  btn.addEventListener('click', (ev) => {
    ev.stopPropagation();
    if (btn.disabled) return;
    if (!btn.classList.contains('armed')) {
      haptic();
      btn.dataset.text = btn.textContent;
      btn.textContent = armedText;
      btn.classList.add('armed');
      clearTimeout(btn._t);
      btn._t = setTimeout(() => { btn.classList.remove('armed'); btn.textContent = btn.dataset.text; }, 3500);
      return;
    }
    clearTimeout(btn._t);
    btn.classList.remove('armed');
    action();
  });
}

export function openSheet(g, canKill) {
  sheet = { group: g, detail: null, canKill };
  fillIcon($('psIco'), g);
  $('psTitle').textContent = g.title;
  $('psSub').textContent = 'Loading…';
  $('psList').replaceChildren();
  $('pSheet').hidden = false;
  loadSheet();
}

export function closeSheet() {
  sheet = null;
  $('pSheet').hidden = true;
  closeSessionDetails();
}

/** The session details dialog (Sessions widget) closes like the other overlays: Back, idle return, page change. */
function closeSessionDetails() {
  const backdrop = document.querySelector('.session-backdrop');
  if (backdrop) backdrop.click(); // a click on the backdrop itself runs the dialog's own dismiss
  return !!backdrop;
}

export async function loadSheet() {
  if (!sheet) return;
  try {
    const d = await api('/procs/detail?name=' + encodeURIComponent(sheet.group.name));
    if (!sheet || d.name !== sheet.group.name) return;
    sheet.detail = d;
    renderSheet();
  } catch (_) {}
}

export function renderSheet() {
  const d = sheet.detail, canKill = sheet.canKill;
  const totalCpu = d.instances.reduce((a, i) => a + i.cpu, 0), totalMem = d.instances.reduce((a, i) => a + i.memMb, 0);
  $('psSub').textContent = `${d.instances.length} instance${d.instances.length === 1 ? '' : 's'} · CPU ${totalCpu.toFixed(1)}% · ${fmtMem(totalMem)}` +
    (d.locked ? ' · system process' : !canKill ? ' · ending disabled in settings' : '');
  const all = $('psAll');
  if (!all.classList.contains('armed')) all.textContent = d.instances.length > 1 ? `End all ${d.instances.length}` : 'End';
  all.disabled = d.locked || !canKill || !d.instances.length;

  const list = $('psList');
  list.replaceChildren();
  if (!d.instances.length) { list.appendChild(h('div', 'empty', 'No running instances')); return; }
  for (const i of d.instances) {
    const primary = i.session ? '✳ ' + i.session : i.window || i.args || d.title;
    const secondary = [i.session ? i.args : i.window ? i.args : null,
      i.startedAt ? (Date.now() - i.startedAt < 60_000 ? 'just started' : 'started ' + shortDur(Date.now() - i.startedAt) + ' ago') : null]
      .filter(Boolean).join(' · ');
    const btn = h('button', 'ps-btn danger', 'End');
    btn.disabled = i.locked || !canKill;
    armButton(btn, 'Confirm', () => killInstance(i, primary));
    list.appendChild(h('div', 'pi', h('span', 'pid', 'PID ' + i.pid),
      h('div', 'lbl', h('b', i.session ? 'sess' : '', primary), h('small', null, secondary)),
      h('div', 'num', h('b', null, i.cpu.toFixed(1) + '%'), h('br'), fmtMem(i.memMb)), btn));
  }
}

export async function postKill(query) {
  const r = await fetch(`${API}/procs/kill?${query}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
  return { ok: r.ok, body: await r.json().catch(() => ({})) };
}

export async function killInstance(i, label) {
  try {
    const { ok, body } = await postKill('pid=' + i.pid);
    ok ? toast(`Ended ${label} (PID ${i.pid})`, 'ok') : toast(body.error || 'Could not end it', 'err');
  } catch (_) { toast('Computer unreachable', 'err'); }
  setTimeout(() => { loadSheet(); loadProcs(); }, 500);
}

armButton($('psAll'), 'Tap again to confirm', async () => {
  const g = sheet?.group;
  if (!g) return;
  try {
    const { ok, body } = await postKill('name=' + encodeURIComponent(g.name));
    if (ok) { toast(`Ended ${body.killed} × ${g.title}${body.failed ? ` (${body.failed} failed)` : ''}`, 'ok'); closeSheet(); }
    else toast(body.error || 'Could not end them', 'err');
  } catch (_) { toast('Computer unreachable', 'err'); }
  setTimeout(loadProcs, 500);
});
$('psClose').addEventListener('click', () => { haptic(); closeSheet(); });

// ---------------------------------------------------------------- pushed data (webhook API)
/** Normalises whatever was pushed into { value, title, sub, unit, color, icon, progress, items, level }. */
export function pushView(item) {
  const d = item.data;
  const v = { at: item.at };
  if (Array.isArray(d)) v.items = d;
  else if (d !== null && typeof d === 'object') {
    v.value = d.value ?? d.state ?? d.status;
    v.title = d.title ?? d.label ?? d.name;
    v.sub = d.sub ?? d.subtitle ?? d.text ?? d.message;
    v.unit = d.unit;
    v.color = d.color;
    v.icon = d.icon;
    v.items = Array.isArray(d.items) ? d.items : undefined;
    v.level = d.level;
    if (typeof d.progress === 'number') v.progress = d.progress > 1 ? d.progress / 100 : d.progress;
    if (typeof d.min === 'number') v.min = d.min;
    if (typeof d.max === 'number') v.max = d.max;
    if (v.value === undefined && !v.items && v.sub === undefined) v.value = JSON.stringify(d);
  } else v.value = d;
  return v;
}

export function fmtValue(x) {
  if (typeof x === 'number') return Math.abs(x) >= 100 ? String(Math.round(x)) : String(Math.round(x * 10) / 10);
  if (typeof x === 'boolean') return x ? 'On' : 'Off';
  return x == null ? '—' : String(x);
}
export const LEVEL_COLOR = { ok: '#22c55e', success: '#22c55e', warn: '#f59e0b', warning: '#f59e0b', error: '#ef4444', fail: '#ef4444', failure: '#ef4444', info: '#60a5fa' };
export const levelOf = (v) => v.level || (typeof v.value === 'string' ? (/^(ok|pass(ed)?|success|green|up|online)$/i.test(v.value) ? 'ok'
  : /^(fail(ed|ure)?|error|red|down|offline|broken)$/i.test(v.value) ? 'error' : /^(running|pending|building|warn(ing)?|yellow)$/i.test(v.value) ? 'warn' : '') : '');

// ---------------------------------------------------------------- notifications (POST /api/notify)
export function handleNotify(d) {
  const level = d.level === 'error' ? 'err' : d.level === 'ok' || d.level === 'success' ? 'ok' : d.level === 'warn' ? 'warn' : '';
  if (d.wake && nightOn) { wakeUntil = Date.now() + 30_000; setNight(false); }
  toast(d.text || '', level, d.title || '', d.seconds || 5);
  if (d.vibrate !== false) buzz(level === 'err' ? '0,90,90,90,90,160' : '0,70,110,70');
  D.notes.unshift({ title: d.title || '', text: d.text || '', level: d.level || 'info', at: Date.now() });
  D.notes = D.notes.slice(0, 30);
  store.set('notes', D.notes);
  emit('notes');
  if (d.page != null) requestPage({ page: d.page, name: d.title || null, countdown: d.countdown });
}

// ---------------------------------------------------------------- permission prompts (Claude Code hook → phone)
export let approvals = [];
export let approvalShown = null;

export async function loadApprovals() {
  if (PREVIEW) return; // permission prompts are answered on the phone, not in the preview
  try { approvals = (await api('/approvals')).items || []; } catch (_) { return; }
  renderApproval();
}

export function renderApproval() {
  const a = approvals[0];
  const box = $('approval');
  if (!a) { box.hidden = true; approvalShown = null; return; }
  if (approvalShown !== a.id) {
    approvalShown = a.id;
    lastTouch = Date.now();
    if (nightOn) { wakeUntil = Date.now() + 60_000; setNight(false); }
    buzz('0,120,80,120,80,240');
    $('apAllow').disabled = $('apDeny').disabled = $('apSkip').disabled = false;
  }
  // A question from Claude is answered at the PC (the terminal shows it); the phone only tells you it is waiting.
  const asking = a.kind === 'question';
  $('apAllow').hidden = $('apDeny').hidden = asking;
  $('apSkip').hidden = !asking;
  $('apSource').textContent = a.source || 'Claude Code';
  $('apProject').textContent = a.project || a.session || '';
  $('apCount').textContent = approvals.length > 1 ? `1 of ${approvals.length}` : '';
  $('apTool').textContent = a.tool;
  $('apDesc').textContent = a.description || '';
  $('apDetail').textContent = a.detail || '';
  $('apDetail').hidden = !a.detail;
  box.hidden = false;
  tickApproval();
}

export function tickApproval() {
  const a = approvals[0];
  if (!a || $('approval').hidden) return;
  const left = a.expiresAt - Date.now();
  $('apTime').textContent = a.kind === 'question' ? 'Answer it in the terminal on your PC'
    : left > 0 ? `otherwise asked in the terminal in ${fmtTime(left)}` : 'asking in the terminal…';
  if (left < -3000) { approvals.shift(); renderApproval(); }
}
setInterval(tickApproval, 1000);

export async function decide(decision) {
  const a = approvals[0];
  if (!a) return;
  haptic();
  $('apAllow').disabled = $('apDeny').disabled = $('apSkip').disabled = true;
  try {
    await api(`/approvals/${encodeURIComponent(a.id)}?decision=${decision}`, 'POST', 4000);
    if (decision === 'skip') toast('The question is waiting in the terminal', 'warn');
    else toast(`${decision === 'allow' ? 'Allowed' : 'Denied'}: ${a.tool}`, decision === 'allow' ? 'ok' : 'err');
  } catch (_) { toast('Already answered in the terminal', 'warn'); }
  approvals = approvals.filter((x) => x.id !== a.id);
  renderApproval();
}
$('apAllow').addEventListener('click', () => decide('allow'));
$('apDeny').addEventListener('click', () => decide('deny'));
$('apSkip').addEventListener('click', () => decide('skip'));

export function handleEvent(e) {
  if (PREVIEW && (e.kind === 'goto' || e.kind === 'back' || e.kind === 'reload')) return; // the editor picks the page
  if (e.kind === 'approval' || e.kind === 'approval-done') loadApprovals();
  else if (e.kind === 'notify' && e.data) handleNotify(e.data);
  else if (e.kind === 'goto' && e.data) requestPage(e.data);
  else if (e.kind === 'back' && e.data) requestBack(e.data.rule);
  else if (e.kind === 'refresh') { loadConfig(); loadQuotas(); loadWork(); loadPush(); loadWeather(true); loadCalendar(); }
  else if (e.kind === 'reload') location.reload();
}

export const WIDGET_ICONS = { rules: '⚡', chargecare: '🔋', nextup: '⏭️', moon: '🌙', wordclock: '🔤', clock: '🕒', weather: '⛅', forecast: '🌦️', media: '🎵', stat: '📈', gauge: '⏲️', chart: '📊', meters: '💾', quotas: '✳️', sessions: '🤖',
  github: '🐙', processes: '⚙️', shortcuts: '🎛️', button: '🔘', worldclock: '🌍', calendar: '📅', pomodoro: '🍅', countdown: '⏳', note: '📝', command: '💻',
  push: '📡', inbox: '📬', label: '🔤', analog: '🕰️', progress: '📆', stopwatch: '⏱️', timer: '⏲️', counter: '🔢', breathe: '🫁', quote: '💬',
  checklist: '✅', battery: '🔋', topprocs: '🔥', quotaring: '🎯', volume: '🔊', sun: '🌅', air: '🍃', hackernews: '📰', exchange: '💱', crypto: '🪙', image: '🖼️' };

// ================================================================= pages
export const pager = $('pager');
export let pages = [];       // enabled pages
export let mounted = [];     // { pi, def, inst, ctx, dirty }
export let layoutKey = '';
export let curPage = 0;

export const isPortrait = () => window.innerHeight > window.innerWidth * 1.05;

// Portrait: the same widgets packed into a 6-column grid in reading order. Their sizes in cells stay the same
// (portrait cells are about as square as landscape ones); wider widgets shrink to 6 columns and the rows grow to fit.
export function portraitLayout(widgets) {
  const cols = 6, occ = [], pos = new Map();
  const free = (x, y, w, h) => { for (let r = y; r < y + h; r++) for (let c = x; c < x + w; c++) if (occ[r]?.[c]) return false; return true; };
  const take = (x, y, w, h) => { for (let r = y; r < y + h; r++) { occ[r] ||= []; for (let c = x; c < x + w; c++) occ[r][c] = true; } };
  for (const w of [...widgets].sort((a, b) => a.y - b.y || a.x - b.x)) {
    const ww = Math.min(cols, w.w), hh = w.h;
    search: for (let y = 0; y < 200; y++) for (let x = 0; x + ww <= cols; x++) if (free(x, y, ww, hh)) { take(x, y, ww, hh); pos.set(w, { x, y, w: ww, h: hh }); break search; }
  }
  return { pos, rows: Math.max(12, occ.length) };
}

export function buildLayout() {
  const enabled = (cfg.pages || []).filter((p) => p.enabled !== false && Array.isArray(p.widgets));
  const next = enabled.length ? enabled : FALLBACK_PAGES;
  const portrait = isPortrait();
  const key = JSON.stringify([next, cfg.shortcuts, cfg.display?.showSeconds, cfg.display?.appearance, portrait]);
  if (key === layoutKey) return;
  layoutKey = key;
  const prevId = pages[curPage]?.id;
  pages = next;
  for (const m of mounted) { try { m.inst.destroy?.(); } catch (_) {} }
  mounted = [];
  closeSheet();
  pager.replaceChildren();
  pages.forEach((p, pi) => {
    const grid = h('div', 'grid');
    const pageAppearance = PhoneBoxAppearance.resolve(cfg.display?.appearance, p.appearance);
    const pl = portrait ? portraitLayout(p.widgets) : null;
    if (pl) { grid.style.gridTemplateColumns = 'repeat(6, minmax(0, 1fr))'; grid.style.gridTemplateRows = `repeat(${pl.rows}, minmax(0, 1fr))`; }
    for (const w of p.widgets) {
      const def = W[w.type];
      if (!def) continue;
      const box = h('div', `w w-${w.type}${def.bare ? ' bare' : ' card'}`);
      const appearance = PhoneBoxAppearance.resolve(cfg.display?.appearance, p.appearance, w.appearance);
      const at = pl?.pos.get(w) || w;
      box.style.gridArea = `${at.y + 1} / ${at.x + 1} / span ${at.h} / span ${at.w}`;
      grid.appendChild(box);
      const ctx = { page: p, w, o: w.options || {}, box, appearance };
      let inst;
      try { inst = def.create(box, ctx) || {}; } catch (e) { box.replaceChildren(h('div', 'empty', `${w.type}: ${e.message}`)); inst = {}; }
      PhoneBoxAppearance.widget(box, appearance);
      mounted.push({ pi, def: inst.needs ? { ...def, needs: inst.needs, always: inst.always ?? def.always } : def, inst, ctx, dirty: true });
    }
    const sec = h('section', 'page', grid);
    PhoneBoxAppearance.page(sec, grid, pageAppearance);
    sec.dataset.id = p.id;
    pager.appendChild(sec);
  });
  buildDots();
  const idx = Math.max(0, pages.findIndex((p) => p.id === prevId));
  curPage = -1;
  goPage(idx, true);
  requestAnimationFrame(() => setActive(true));
}

export function activeSet() { return new Set([curPage - 1, curPage, curPage + 1]); }
export const pageOf = (kind) => mounted.filter((m) => m.pi === curPage && m.def.needs.includes(kind)).length > 0;
export const anyNeeds = (kind) => mounted.some((m) => m.def.needs.includes(kind));

export function runUpdate(m, kind) {
  try { m.inst.update?.(kind); } catch (e) { console.warn(m.ctx.w.type, e); }
}

/** Delivers fresh data to the widgets on the visible pages; the others catch up when they come into view. */
export function emit(kind) {
  const act = activeSet();
  for (const m of mounted) {
    if (!m.def.needs.includes(kind)) continue;
    // alarms (timer, pomodoro) must go off on whatever page is showing, so those widgets get every tick
    if (act.has(m.pi) || (m.def.always && kind === 'tick')) runUpdate(m, kind); else m.dirty = true;
  }
}

export function setActive(all = false) {
  const act = activeSet();
  for (const m of mounted) {
    if ((m.dirty || all) && act.has(m.pi)) {
      m.dirty = false;
      for (const k of m.def.needs) runUpdate(m, k);
    }
  }
}

// ---------------------------------------------------------------- navigation: swipe, dots, overview, volume keys, agent events
export function buildDots() {
  $('dots').replaceChildren(...pages.map(() => h('i')));
  $('dots').hidden = pages.length < 2;
}

export function updateDots() {
  [...$('dots').children].forEach((d, i) => d.classList.toggle('on', i === curPage));
}

export let snapTimer = 0;
export function goPage(i, instant = false) {
  i = Math.max(0, Math.min(pages.length - 1, i));
  if (!instant) { pager.scrollTo({ left: i * window.innerWidth, behavior: 'smooth' }); return; }
  // scroll-snap-stop: always would stop a programmatic jump at the next page, so snapping is paused for the jump.
  pager.style.scrollSnapType = 'none';
  pager.scrollLeft = i * window.innerWidth;
  onScroll();
  clearTimeout(snapTimer);
  snapTimer = setTimeout(() => (pager.style.scrollSnapType = ''), 120);
}

export function gotoPageRef(ref) {
  const n = resolvePage(ref);
  if (n < 0) return -1;
  lastTouch = Date.now();
  goPage(n, Math.abs(n - curPage) > 1);
  return n;
}

export const resolvePage = (ref) => {
  const n = typeof ref === 'number' ? ref - 1 : /^\d+$/.test(String(ref)) ? +ref - 1 : pages.findIndex((p) => p.id === ref || p.title.toLowerCase() === String(ref).toLowerCase());
  return n >= 0 && n < pages.length ? n : -1;
};
export const homeIndex = () => { const id = cfg.display?.homePage; const i = id ? pages.findIndex((p) => p.id === id) : -1; return i >= 0 ? i : 0; };

// ================================================================= calm page switching
// Rules, webhooks and the idle return ask for a page instead of jumping to it. A request waits while you are using the
// panel and while the last automatic switch is recent, then counts down on screen ("Switching to AI in 3…") so it can be
// stopped, and fades over. Requests that come one after another are merged: the highest priority goes first, a rule
// that ends before its turn is dropped, and a page that a rule holds is not taken over by a lower rule while it lasts.
// A rule that holds its page "while active" keeps it (no idle return, up to display.holdMaxMin); when the rule ends the
// panel goes back to the page it came from, unless you moved on yourself in the meantime.
export let ruleHold = null;   // { rule, name, page, from, at, priority }
export let navQueue = [];     // { kind: 'goto' | 'back' | 'home', rule, name, detail, page, hold, priority, countdown, at }
let navCount = null;          // { req, until, total }
let navTimer = 0;
let lastAutoSwitch = 0;
let lastNav = 0;              // last automatic switch (for the idle timer)
const dsp = () => cfg.display || {};
const pageLabel = (i) => { const p = pages[i]; return p ? `${p.icon || ''} ${p.title}`.trim() : ''; };

export function requestPage(d) {
  if (PREVIEW) return;
  const page = resolvePage(d.page);
  if (page < 0) return;
  const rule = d.rule || null;
  if (rule) {
    // the rule ended and came back before its "back" ran: just stay
    const back = navQueue.find((r) => r.kind === 'back' && r.rule === rule);
    navQueue = navQueue.filter((r) => r.rule !== rule);
    if (back && ruleHold?.rule === rule && curPage === ruleHold.page) { ruleHold.at = Date.now(); return pump(); }
  }
  navQueue.push({ kind: 'goto', rule, name: d.name || null, detail: d.detail || '', page, hold: !!(d.hold && rule),
    priority: d.priority ?? 5, countdown: d.countdown, at: Date.now() });
  soon();
}

// events that arrive together (several rules at once) are looked at together, a moment later
function soon() { if (!navTimer) navTimer = setInterval(pump, 250); }

export function requestBack(rule) {
  if (PREVIEW || !rule) return;
  // the rule ended before its turn: nothing to undo
  const queued = navQueue.findIndex((r) => r.kind === 'goto' && r.rule === rule);
  if (queued >= 0) { navQueue.splice(queued, 1); return pump(); }
  if (!ruleHold || ruleHold.rule !== rule || navQueue.some((r) => r.kind === 'back' && r.rule === rule)) return;
  navQueue.push({ kind: 'back', rule, name: ruleHold.name, page: ruleHold.from, priority: ruleHold.priority, at: Date.now() });
  soon();
}

/** Seconds of no touch after which this page goes home (0 = stay). */
const idleLimit = () => pages[curPage]?.idleSec ?? dsp().idleReturnSec ?? 90;
function idleDue() {
  const idle = idleLimit();
  if (!(idle > 0)) return false;
  const maxHold = (dsp().holdMaxMin ?? 30) * 60_000;
  const held = ruleHold && curPage === ruleHold.page && (!maxHold || Date.now() - ruleHold.at < maxHold);
  return !held && Date.now() - Math.max(lastTouch, lastNav) > idle * 1000
    && (curPage !== homeIndex() || !!sheet || !$('overview').hidden);
}

function softGo(i) {
  if (i === curPage) return;
  if (Math.abs(i - curPage) === 1) { goPage(i); return; }
  pager.classList.add('fade');
  setTimeout(() => { goPage(i, true); pager.classList.remove('fade'); }, 320);
}

function perform(req) {
  navQueue = navQueue.filter((r) => r !== req);
  navCount = null;
  if (req.kind === 'goto') {
    // a rule taking over another rule's page inherits where that one came from
    const from = ruleHold && curPage === ruleHold.page ? ruleHold.from : curPage;
    navQueue = navQueue.filter((r) => r.kind !== 'back'); // whatever was going back is replaced by this
    if (req.hold) ruleHold = { rule: req.rule, name: req.name, page: req.page, from, at: Date.now(), priority: req.priority };
    else if (ruleHold && req.page !== ruleHold.page) ruleHold = null;
    softGo(req.page);
  } else if (req.kind === 'back') {
    const h = ruleHold;
    ruleHold = null;
    if (h && curPage === h.page && h.from !== h.page) softGo(h.from);
  } else {
    ruleHold = null;
    closeSheet(); closeOverview();
    softGo(homeIndex());
  }
  if (req.kind !== 'home') lastAutoSwitch = Date.now();
  lastNav = Date.now();
  renderNav();
  if (navQueue.length) setTimeout(pump, 0);
}

/** Drops a request (the Stay button). A dropped rule page also gives up its "back". */
function dismiss(req) {
  haptic();
  navQueue = navQueue.filter((r) => r !== req);
  if (req.kind === 'home') lastTouch = Date.now();
  navCount = null;
  pump();
}

function nextRequest() {
  const hold = ruleHold && curPage === ruleHold.page ? ruleHold : null;
  navQueue = navQueue.filter((r) => (r.kind === 'back' ? hold && hold.rule === r.rule : r.kind === 'home' ? idleDue() : true));
  // already there: a rule just takes the page over
  for (const r of navQueue.filter((r) => r.kind === 'goto' && r.page === curPage)) {
    if (r.hold && (!hold || r.priority >= hold.priority)) ruleHold = { rule: r.rule, name: r.name, page: r.page, from: hold ? hold.from : curPage, at: Date.now(), priority: r.priority };
    navQueue = navQueue.filter((x) => x !== r);
  }
  const ending = hold && navQueue.some((r) => r.kind === 'back' && r.rule === hold.rule);
  const blocked = (r) => r.kind === 'goto' && hold && !ending && r.priority < hold.priority;
  const order = { goto: 0, back: 1, home: 2 };
  return navQueue.filter((r) => !blocked(r)).sort((a, b) => order[a.kind] - order[b.kind] || b.priority - a.priority || a.at - b.at)[0] || null;
}

/** Why a request has to wait, or '' when it may go. */
function waitReason(req) {
  if (!$('approval').hidden) return 'after the permission prompt';
  if (req.kind === 'home') return '';
  const grace = (dsp().touchGraceSec ?? 20) * 1000;
  if (Date.now() - lastUserTouch < grace) return 'waits until you are done';
  if (sheet || !$('overview').hidden) return 'waits until you are done';
  const dwell = (dsp().minDwellSec ?? 15) * 1000 - (Date.now() - lastAutoSwitch);
  if (dwell > 0) return `in ${Math.ceil(dwell / 1000)} s`;
  return '';
}

export function pump() {
  const req = nextRequest();
  if (!req) { navCount = null; renderNav(); clearInterval(navTimer); navTimer = 0; return; }
  if (!navTimer) navTimer = setInterval(pump, 250);
  const why = waitReason(req);
  if (why) { navCount = null; renderNav(req, why); return; }
  const seconds = nightOn ? 0 : req.countdown ?? dsp().switchCountdownSec ?? 3;
  if (!navCount || navCount.req !== req) {
    if (seconds <= 0) return perform(req);
    navCount = { req, until: Date.now() + seconds * 1000, total: seconds };
    buzz('0,40');
  }
  if (Date.now() >= navCount.until) return perform(req);
  renderNav(req);
}

let navShown = null;
function renderNav(req = null, why = '') {
  const card = $('navCard'), chip = $('navChip');
  if (!req) { card.classList.remove('on'); chip.classList.remove('on'); navShown = null; return; }
  const name = req.name || (req.kind === 'home' ? 'Nobody around' : req.kind === 'back' ? 'Done' : 'Page switch');
  const target = req.kind === 'home' ? pageLabel(homeIndex()) : pageLabel(req.page);
  const more = navQueue.length > 1 ? ` +${navQueue.length - 1}` : '';
  if (why) {
    card.classList.remove('on');
    chip.classList.add('on');
    navShown = null;
    $('ncText').textContent = `${name} → ${target}${more}`;
    $('ncWhy').textContent = why;
    $('ncGo').onclick = () => { haptic(); perform(req); };
    $('ncX').onclick = () => dismiss(req);
    return;
  }
  chip.classList.remove('on');
  if (navShown !== navCount) {
    navShown = navCount;
    $('nvName').textContent = name;
    $('nvLine').textContent = req.kind === 'goto' ? `Switching to ${target}` : `Back to ${target}`;
    $('nvDetail').textContent = req.detail && req.detail !== 'test' ? req.detail : more ? `${navQueue.length - 1} more waiting` : '';
    const ring = $('nvRing');
    ring.style.animation = 'none';
    void ring.getBoundingClientRect();
    ring.style.animation = `nav-ring ${Math.max(0.1, (navCount.until - Date.now()) / 1000)}s linear forwards`;
    $('nvStay').onclick = () => dismiss(req);
    $('nvGo').onclick = () => { haptic(); perform(req); };
    card.classList.add('on');
  }
  $('nvNum').textContent = Math.max(1, Math.ceil((navCount.until - Date.now()) / 1000));
}

/** Overview: pause the agent's rules for an hour, or resume them. */
export async function toggleRulesPause() {
  haptic();
  const paused = rulesPaused && rulesPaused > Date.now();
  try {
    const r = await api(`/rules/pause?minutes=${paused ? 0 : 60}`, 'POST', 4000);
    rulesPaused = r.pausedUntil || null;
    if (!rulesPaused) navQueue = navQueue.filter((x) => x.kind === 'home');
    toast(rulesPaused ? 'Rules paused for an hour' : 'Rules are back on', 'ok');
  } catch (_) { toast('The PC did not answer', 'warn'); }
  renderRulesBtn();
}
export function renderRulesBtn() {
  const b = $('ovRules');
  if (!b) return;
  const paused = rulesPaused && rulesPaused > Date.now();
  b.hidden = PREVIEW || !online;
  b.classList.toggle('on', !!paused);
  b.textContent = paused ? `⏸ Rules paused until ${new Date(rulesPaused).toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' })} · resume` : '⏸ Pause rules 1 h';
}

export let titleTimer = 0;
export function flashTitle() {
  const p = pages[curPage];
  if (!p || pages.length < 2) return;
  const t = $('ptitle');
  t.textContent = `${p.icon || ''} ${p.title}`.trim();
  t.classList.add('on');
  clearTimeout(titleTimer);
  titleTimer = setTimeout(() => t.classList.remove('on'), 900);
}

export function onScroll() {
  const p = Math.round(pager.scrollLeft / window.innerWidth);
  if (p === curPage) return;
  curPage = p;
  updateDots();
  flashTitle();
  setActive();
  if (sheet) closeSheet();
  if (pageOf('procs')) loadProcs();
}
pager.addEventListener('scroll', onScroll, { passive: true });

export function openOverview() {
  haptic();
  const ov = $('overview');
  const tile = (p, i) => {
    const map = h('div', 'ov-map', p.widgets.map((w) => {
      const b = h('span', null, WIDGET_ICONS[w.type] || '▫️');
      b.style.gridArea = `${w.y + 1} / ${w.x + 1} / span ${w.h} / span ${w.w}`;
      return b;
    }));
    const t = h('button', 'ov-tile' + (i === curPage ? ' on' : ''), map, h('div', 'ov-name', h('b', null, `${i + 1}`), ` ${p.icon || ''} ${p.title}`));
    t.addEventListener('click', (e) => { e.stopPropagation(); haptic(); closeOverview(); goPage(i, true); });
    return t;
  };
  $('ovGrid').replaceChildren(...pages.map(tile));
  renderRulesBtn();
  // several paired computers: switch between them here (a USB cable always wins)
  let pcs = [];
  try { pcs = bridge?.pcs ? JSON.parse(bridge.pcs()) : []; } catch (_) {}
  $('ovPcs').replaceChildren(...(pcs.length > 1 ? pcs.map((pc) => {
    const b = h('button', 'ov-pc' + (pc.active ? ' on' : ''), '🖥️ ' + (pc.name || 'Computer'));
    b.addEventListener('click', (e) => {
      e.stopPropagation();
      if (pc.active) return;
      haptic(); toast(`Switching to ${pc.name || 'the other computer'}…`);
      setTimeout(() => bridge.selectPc(pc.id), 300);
    });
    return b;
  }) : []));
  $('ovGrid').style.setProperty('--n', Math.min(4, Math.ceil(Math.sqrt(pages.length * 1.6))));
  ov.hidden = false;
}
window.openOverview = openOverview;
export function closeOverview() { $('overview').hidden = true; }
$('overview').addEventListener('click', closeOverview);
$('ovRules').addEventListener('click', (e) => { e.stopPropagation(); toggleRulesPause(); });
$('dots').addEventListener('click', openOverview);

// Long press on an empty part of a page opens the overview too.
export let pressTimer = 0;
export let pressAt = null;
pager.addEventListener('pointerdown', (e) => {
  if (!e.target.classList.contains('grid') && !e.target.classList.contains('page')) return;
  pressAt = [e.clientX, e.clientY];
  pressTimer = setTimeout(() => { pressAt = null; openOverview(); }, 550);
});
pager.addEventListener('pointermove', (e) => { if (pressAt && Math.hypot(e.clientX - pressAt[0], e.clientY - pressAt[1]) > 12) { clearTimeout(pressTimer); pressAt = null; } });
['pointerup', 'pointercancel'].forEach((ev) => pager.addEventListener(ev, () => { clearTimeout(pressTimer); pressAt = null; }));

// Edge swipe wrap-around: swiping left on the last page wraps to the first page,
// and swiping right on the first page wraps to the last page.
let swipeStartX = 0;
let swipeStartY = 0;
let swipeStartPage = 0;
let swipeStartTime = 0;
let swipeTracking = false;

pager.addEventListener('touchstart', (e) => {
  swipeTracking = false;
  if (e.touches.length !== 1 || pages.length < 2) return;
  // Dragging a slider, the volume bar or a list must never flip the page.
  if (volDragging || e.target.closest?.('button, input, textarea, select, a, [contenteditable], .s-list, .vb')) return;
  swipeStartX = e.touches[0].clientX;
  swipeStartY = e.touches[0].clientY;
  swipeStartPage = curPage;
  swipeStartTime = Date.now();
  swipeTracking = true;
}, { passive: true });

pager.addEventListener('touchend', (e) => {
  if (!swipeTracking || !e.changedTouches || e.changedTouches.length !== 1 || pages.length < 2) {
    swipeTracking = false;
    return;
  }
  swipeTracking = false;
  const dx = e.changedTouches[0].clientX - swipeStartX;
  const dy = e.changedTouches[0].clientY - swipeStartY;
  const dt = Date.now() - swipeStartTime;

  if (dt < 700 && Math.abs(dx) >= 40 && Math.abs(dx) > Math.abs(dy) * 1.3) {
    if (dx < 0 && swipeStartPage === pages.length - 1 && curPage === pages.length - 1) {
      haptic();
      goPage(0, true);
    } else if (dx > 0 && swipeStartPage === 0 && curPage === 0) {
      haptic();
      goPage(pages.length - 1, true);
    }
  }
}, { passive: true });

pager.addEventListener('touchcancel', () => { swipeTracking = false; }, { passive: true });

/** Called by the Android shell (volume keys) and by arrow keys in a desktop browser. */
window.onAndroidKey = (k) => {
  lastTouch = lastUserTouch = Date.now();
  if (nightOn) { wakeUntil = Date.now() + 30_000; setNight(false); return; }
  if (!$('overview').hidden) closeOverview();
  if (k === 'next') goPage(curPage + 1 >= pages.length ? 0 : curPage + 1, true);
  else if (k === 'prev') goPage(curPage - 1 < 0 ? pages.length - 1 : curPage - 1, true);
};
document.addEventListener('keydown', (e) => {
  if (e.key === 'ArrowRight') window.onAndroidKey('next');
  else if (e.key === 'ArrowLeft') window.onAndroidKey('prev');
  else if (e.key === 'Escape' || e.key === 'o') ($('overview').hidden ? openOverview() : closeOverview());
});
window.onAndroidBack = () => {
  if (!$('approval').hidden) return; // answer it (or let it time out); Back must not dismiss a permission prompt
  if (closeSessionDetails()) return;
  if (!$('overview').hidden) closeOverview();
  else if (sheet) closeSheet();
  else if (curPage !== homeIndex()) goPage(homeIndex(), true);
  else openOverview();
};
window.addEventListener('resize', () => { buildLayout(); goPage(curPage, true); });

['pointerdown', 'touchstart'].forEach((ev) => document.addEventListener(ev, (e) => {
  if (e.target?.closest?.('#navCard, #navChip')) return;
  lastTouch = lastUserTouch = Date.now();
  if (navQueue.length) { navQueue = navQueue.filter((r) => r.kind !== 'home'); pump(); }
  if (nightOn) { wakeUntil = Date.now() + 30_000; setNight(false); }
}, { passive: true, capture: true }));

// ---------------------------------------------------------------- clock tick & night mode
export function parseHM(s) { const [hh, mm] = (s || '0:0').split(':').map(Number); return hh * 60 + (mm || 0); }
export function isNight(now) {
  const n = cfg.night || {};
  if (!n.enabled) return false;
  const t = now.getHours() * 60 + now.getMinutes();
  const a = parseHM(n.start), b = parseHM(n.end);
  return a <= b ? t >= a && t < b : t >= a || t < b;
}

export function setNight(on) {
  if (on === nightOn) return;
  nightOn = on;
  document.body.classList.toggle('night', on);
  applyBrightness(true);
}

// ---------------------------------------------------------------- phone sensors
// Room light (lux) and proximity come from the Android shell. Brightness can follow the room, the night screen can
// come on when the room goes dark, and a hand near the phone wakes the night screen.
export let lux = null;
export let darkSince = 0;
export let darkRoom = false;
export let lastBrightness = null;

/** 1 lux → 5 %, 10 → 37 %, 100 → 68 %, 1000+ lux → 100 % (perceived brightness is roughly logarithmic). */
export const luxToBrightness = (l) => Math.max(0.05, Math.min(1, 0.05 + (0.95 * Math.log10(l + 1)) / 3));

export function applyBrightness(force = false) {
  if (!bridge) return;
  let v = -1; // the phone's own setting
  if (nightOn) v = cfg.night?.brightness ?? 0.02;
  else if (cfg.display?.autoBrightness && lux != null) v = luxToBrightness(lux);
  if (!force && lastBrightness != null && v >= 0 && lastBrightness >= 0 && Math.abs(v - lastBrightness) < 0.04) return;
  lastBrightness = v;
  try { bridge.setBrightness(v); } catch (_) {}
}

window.onAndroidSensor = (kind, value) => {
  if (kind === 'light') {
    lux = +value;
    // dark room: below 3 lux for 30 s; light again above 15 lux
    if (lux < 3) { darkSince ||= Date.now(); } else darkSince = 0;
    if (darkRoom && lux > 15) darkRoom = false;
    applyBrightness();
  } else if (kind === 'proximity' && value === true && cfg.display?.proximityWake !== false && nightOn) {
    wakeUntil = Date.now() + 30_000;
    setNight(false);
  }
};

export function tick() {
  const now = new Date();
  $('nClock').textContent = pad(now.getHours()) + ':' + pad(now.getMinutes());
  $('nDate').textContent = now.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' });
  if (darkSince && Date.now() - darkSince > 30_000) darkRoom = true;
  const dark = darkRoom && cfg.display?.nightWhenDark === true;
  setNight((isNight(now) || dark) && Date.now() > wakeUntil);
  if (!PREVIEW && idleDue() && !navQueue.some((r) => r.kind === 'home')) {
    if (curPage === homeIndex()) { closeSheet(); closeOverview(); lastNav = Date.now(); } // only a sheet to close
    else { navQueue.push({ kind: 'home', at: Date.now(), priority: -1 }); pump(); }
  }
  if (!nightOn) emit('tick');
  setTimeout(tick, 1000 - (Date.now() % 1000) + 5);
}

// ---------------------------------------------------------------- data from the agent
export function applyDisplay() {
  const d = cfg.display || {};
  // theme and accent apply in the preview and the browser too
  document.body.dataset.theme = ['amoled', 'midnight', 'warm', 'graphite'].includes(d.theme) ? d.theme : 'default';
  const accent = hexToRgb(d.accent);
  const before = DEFAULT_AMBIENT;
  DEFAULT_AMBIENT = accent || '45, 212, 191';
  document.documentElement.style.setProperty('--accent', `rgb(${DEFAULT_AMBIENT})`);
  artTint = d.artTint !== false;
  if (before !== DEFAULT_AMBIENT || !artTint) setAmbient(artTint ? currentArtColor : null);
  if (PREVIEW || !bridge) return;
  try { bridge.setVolumeKeys && bridge.setVolumeKeys(d.volumeKeys !== false); } catch (_) {}
  try { bridge.setPinned && bridge.setPinned(d.pin === true); } catch (_) {}
  try { bridge.setOrientation && bridge.setOrientation(['portrait', 'auto'].includes(d.orientation) ? d.orientation : 'landscape'); } catch (_) {}
  try { bridge.setHomeScreen && bridge.setHomeScreen(d.homeScreen === true); } catch (_) {}
  applyBrightness(true);
}

export async function loadConfig() {
  if (PREVIEW) return;
  try {
    const r = await api('/config');
    const before = JSON.stringify(cfg);
    cfg = { ...DEFAULT_CFG, ...r.config };
    cfgVersion = r.version;
    if (JSON.stringify(cfg) !== before) {
      store.set('cfg', cfg);
      buildLayout();
      applyDisplay();
      loadWeather();
    }
  } catch (_) {}
}

// All-day events have a date but no time zone: the agent sends the UTC midnight of that date. Show them on that date in
// the phone's own zone (west of UTC they would land a day early, east of UTC run into the next day).
const localDay = (ms) => { const d = new Date(ms); return new Date(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()).getTime(); };
export async function loadCalendar() {
  try {
    const cal = await api('/calendar');
    cal.events = (cal.events || []).map((e) => (e.allDay ? { ...e, start: localDay(e.start), end: localDay(e.end) } : e)).sort((a, b) => a.start - b.start);
    D.calendar = cal;
    emit('calendar');
  } catch (_) {}
}

export async function loadQuotas() {
  try { D.quotas = await api('/quotas'); emit('quotas'); } catch (_) {}
}

export function applyWork(w) {
  D.work = w;
  notifyTransitions(w.sessions || []);
  emit('work');
}

export async function loadWork() {
  try { applyWork(await api('/work')); } catch (_) {}
}

export async function loadProcs() {
  try { D.procs = await api('/procs?top=150'); emit('procs'); } catch (_) {}
}

export async function loadPush() {
  try {
    const r = await api('/push');
    D.push = r.items || {};
    pushVersion = r.version;
    emit('push');
  } catch (_) {}
}

export function setOnline(on) {
  document.body.classList.toggle('offline', !on);
  if (on && online !== true) { loadConfig(); loadQuotas(); loadWork(); loadPush(); loadApprovals(); loadCalendar(); }
  const changed = online !== on;
  online = on;
  if (changed) emit('state');
}

export function applyState(s) {
  lastStateAt = Date.now();
  D.state = s;
  media = s.media ? { ...s.media, at: performance.now() } : null;
  setOnline(true);
  if (s.cfgVersion !== undefined && cfgVersion !== null && s.cfgVersion !== cfgVersion) loadConfig();
  if (s.pushVersion !== undefined && s.pushVersion !== pushVersion) loadPush();
  if (evSeq != null && s.evSeq != null && s.evSeq < evSeq) evSeq = s.evSeq; // the agent restarted: it counts from 0 again
  if (evSeq != null) for (const e of s.events || []) if (e.seq > evSeq) handleEvent(e);
  evSeq = Math.max(evSeq ?? 0, s.evSeq ?? 0);
  // the "back" event can be missed while offline; the list of active rules settles it
  if (ruleHold && Array.isArray(s.rules) && !s.rules.includes(ruleHold.rule) && Date.now() - ruleHold.at > 8000) requestBack(ruleHold.rule);
  rulesPaused = s.rulesPaused || null;
  if (!$('overview').hidden) renderRulesBtn();
  {
    const pc = s.pc;
    if (pc) {
      pushHist('cpu', pc.cpu);
      pushHist('gpu', pc.gpu?.util);
      pushHist('ram', pc.ramUsedGb);
      pushHist('vram', pc.gpu?.vramTotal ? (pc.gpu.vramUsed / pc.gpu.vramTotal) * 100 : 0);
      pushHist('down', pc.netDownBps);
      pushHist('up', pc.netUpBps);
      pushHist('ping', s.ping?.ms);
      pushHist('gputemp', pc.gpu?.temp);
      pushHist('cputemp', pc.cpuTemp);
    }
  }
  emit('state');
}

// Live stream from the Android shell (see AgentStream.kt); polling below is the fallback when it is not running.
export let streamOn = false;
export let lastStateAt = 0;
window.onAgentStream = (event, data) => {
  if (event === 'open') { streamOn = true; return; }
  if (event === 'close') { streamOn = false; return; }
  let d;
  try { d = JSON.parse(data); } catch (_) { return; }
  if (event === 'state') applyState(d);
  else if (event === 'work') applyWork(d);
  else if (event === 'event' && d.seq > (evSeq ?? 0)) { evSeq = d.seq; handleEvent(d); }
};
export const streaming = () => streamOn && Date.now() - lastStateAt < 3000;

export async function poll() {
  if (!streaming()) {
    try { applyState(await api('/state' + (evSeq != null ? '?ev=' + evSeq : ''))); }
    catch (_) { if (!streaming()) setOnline(false); }
  }
  setTimeout(poll, 1000);
}

// ---------------------------------------------------------------- start
buildLayout();
applyDisplay();
tick();
loadWeather();
setInterval(loadWeather, 15 * 60_000);
setInterval(() => { if (!nightOn) emit('fast'); }, 250);
setInterval(() => { if (online && anyNeeds('quotas')) loadQuotas(); }, 30_000);
setInterval(() => { if (online && anyNeeds('calendar')) loadCalendar(); }, 120_000);
setInterval(() => { if (online && anyNeeds('work') && !streaming()) loadWork(); }, 3000);
setInterval(() => { if (online && pageOf('procs')) loadProcs(); if (online && sheet) loadSheet(); }, 2000);
setInterval(() => { if (online) loadConfig(); }, 60_000);
poll();

if (PREVIEW) {
  let weatherKey = '';
  window.addEventListener('message', (e) => {
    if (e.origin !== location.origin || e.data?.type !== 'phonebox-preview') return;
    const c = e.data.config || {};
    // no night screen and no idle return in the preview
    cfg = { ...DEFAULT_CFG, ...c, night: { ...(c.night || {}), enabled: false }, display: { ...(c.display || {}), idleReturnSec: 0 } };
    buildLayout();
    applyDisplay();
    const wk = JSON.stringify(cfg.weather);
    if (wk !== weatherKey) { weatherKey = wk; loadWeather(); }
    // the editor's page index counts hidden pages too; the panel only has the shown ones
    const list = cfg.pages || [], sel = list[e.data.page];
    const shown = (p) => p.enabled !== false && Array.isArray(p.widgets);
    const i = sel && shown(sel) ? list.slice(0, e.data.page).filter(shown).length : -1;
    if (i >= 0 && i !== curPage) goPage(i, true);
  });
  window.parent.postMessage({ type: 'phonebox-preview-ready' }, location.origin);
}

// ---------------------------------------------------------------- battery report (the agent's charge guard)
function reportBattery() {
  if (PREVIEW || !bridge?.battery || !online) return;
  try {
    const b = JSON.parse(bridge.battery());
    // the phone's view of its USB link lets the agent explain a cable that only charges (see UsbDoctor.cs)
    const usb = b.usb ? ['connected', 'configured', 'adb'].filter((k) => b.usb[k]).join(',') || 'none' : '';
    if (b.level >= 0) api(`/phone/battery?level=${b.level}&plugged=${!!b.plugged}&temp=${b.temp || 0}&source=${b.source || ''}&usb=${usb}`, 'POST', 4000).catch(() => {});
  } catch (_) {}
}
setInterval(reportBattery, 30_000);
setTimeout(reportBattery, 5000);
