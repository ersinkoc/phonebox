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
import './widgets/photos.js';
import './widgets/controls.js';
import './widgets/console.js';
import './widgets/dictate.js';
import './widgets/camera.js';
import './widgets/spectrum.js';

// PhoneBox desk panel. Pages are 12 × 6 grids of widgets, designed in the agent's settings page.
// The panel talks to the PhoneBox agent through "/agent/*", which the Android shell proxies over USB
// (adb reverse) or Wi-Fi to the computer.

export const API = '/agent/api';
// The phone's native proxy only forwards requests that carry this header (or image GETs), so script in a sandboxed
// Custom HTML widget cannot reach the agent: adding the header to a cross-origin request needs a CORS preflight,
// which the proxy refuses. See MainActivity.fromPanel.
export const PANEL_HEADERS = { 'X-PhoneBox-Panel': '1' };
export const I18n = window.PhoneBoxI18n;
export const t = (key, vars) => (I18n ? I18n.t(key, vars) : key);
export let LOCALE = I18n ? I18n.getLocale() : 'en-GB';
export const COLS = 12;
export const ROWS = 6;
export const $ = (id) => document.getElementById(id);
// The native bridge (window.Android) is in every frame, sandboxed widgets included, so each call carries a key that only
// the panel's own frames receive (MainActivity.bridgeKey). This wrapper adds it.
const BRIDGE = ['battery', 'buzz', 'haptic', 'is24Hour', 'pcs', 'selectPc', 'forgetPc', 'setBrightness', 'setHomeScreen', 'setOrientation',
  'setPinned', 'setVolumeKeys', 'transport', 'sensors', 'version', 'canListen', 'listen', 'stopListening', 'cancelListening'];
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
export let evSeq = null;          // last event seq the agent reported in a state (notifications, page switches…)
let evReplay = false;             // the agent restarted: the next poll replays the events it kept (from 0)
let evGen = 0;                    // bumped when a restart arms evReplay, so an answer already in flight cannot disarm a newer one
let stateTs = 0;                  // server ts of the newest state applied (a late response must not reset the events)
const evSeen = new Set();         // event seqs already handled in this agent run (the stream and a poll can repeat one)
const evMark = (s) => { evSeen.add(s); if (evSeen.size > 400) evSeen.delete(evSeen.values().next().value); };
let evQueue = [];                 // live events held while a replay is armed — the backlog must apply before them
let evDrop = 0;                   // highest seq pushed out of a full evQueue (0 = none lost) — replay covers them or retries
const evDrain = () => {           // the backlog applied: release the held events in seq order
  for (const e of evQueue.sort((a, b) => a.seq - b.seq)) if (!evSeen.has(e.seq)) { evMark(e.seq); handleEvent(e); }
  evQueue = [];
};
let evPre = null;                 // stream events before the first state of a connection — that state picks the run first
const evOffer = (e) => {          // one event from the stream or a state answer: dedupe, then hold or dispatch
  if (typeof e.seq !== 'number' || evSeen.has(e.seq)) return;
  // a replay is armed: hold it so the backlog applies first — the drain sorts held and replayed events together
  if (evReplay) {
    if (!evQueue.some((q) => q.seq === e.seq)) evQueue.push(e);
    if (evQueue.length > 100) evDrop = Math.max(evDrop, evQueue.shift().seq);
  } else { evMark(e.seq); handleEvent(e); }
};
export let pushVersion = null;

/** Latest data from the agent and the web; widgets read from here. */
export const D = { state: null, quotas: null, work: null, procs: null, push: {}, weather: null, calendar: null, notes: store.get('notes', []) };

export const HIST = 90;
export const hist = { cpu: [], gpu: [], ram: [], vram: [], down: [], up: [], ping: [], gputemp: [], cputemp: [], hotspot: [], gpupower: [], gpuclock: [] };
export const pushHist = (k, v) => { const a = hist[k]; a.push(v ?? 0); if (a.length > HIST) a.shift(); };

// ---------------------------------------------------------------- helpers
/** fetch() to the panel's local/USB feed proxy when the phone cannot reach an allowed public HTTPS feed directly. */
export async function safeFetch(url, opts = {}) {
  try {
    return await fetch(url, opts);
  } catch (e) {
    if (!opts.signal?.aborted && typeof url === 'string' && url.startsWith('https://')) {
      return await fetch(`${API}/proxy?url=${encodeURIComponent(url)}`, {
        ...opts,
        headers: { ...(opts.headers || {}), ...PANEL_HEADERS },
      });
    }
    throw e;
  }
}

export async function api(path, method = 'GET', timeout = 2500) {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), timeout);
  // The deadline rejects the caller itself instead of only aborting the request. Aborting is not enough: a body
  // that started arriving can leave the read pending after the signal fires, and a caller waiting on it never
  // reaches its own finally - that is what left the approval poll switched off for good.
  const deadline = new Promise((_, fail) => setTimeout(() => fail(new Error('timeout')), timeout));
  try {
    const work = (async () => {
      const r = await fetch(API + path, { method, signal: ctl.signal, cache: 'no-store', headers: PANEL_HEADERS });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const ct = r.headers.get('content-type') || '';
      // Awaited on purpose: the timer is cleared in finally, so returning the promise unawaited let a response that
      // sent its headers and then stalled hold the caller for good, past its own deadline.
      return ct.includes('json') ? await r.json() : await r.text();
    })();
    work.catch(() => {}); // whichever side loses the race must not surface as an unhandled rejection
    return await Promise.race([work, deadline]);
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

/* Interface icons: Lucide (https://lucide.dev), ISC license — a small inline set keeps the
   panel's chrome consistent across platforms and works fully offline. Icons are decorative
   (aria-hidden); the controls they sit in carry the accessible names.
   Lucide licence (ISC): Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT).
   All other copyright (c) for Lucide are held by Lucide Contributors 2022.
   Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby
   granted, provided that the above copyright notice and this permission notice appear in all copies. */
const ICONS = {
  x: 'M18 6 6 18M6 6l12 12',
  hourglass: 'M5 22h14M5 2h14M17 22v-4.172a2 2 0 0 0-.586-1.414L12 12l-4.414 4.414A2 2 0 0 0 7 17.828V22M7 2v4.172a2 2 0 0 0 .586 1.414L12 12l4.414-4.414A2 2 0 0 0 17 6.172V2',
  wifiOff: 'M8.5 16.5a5 5 0 0 1 7 0M2 8.82a15 15 0 0 1 4.17-2.65M10.66 5c4.01-.36 8.14.9 11.34 3.76M16.85 11.25a10 10 0 0 1 2.22 1.68M5 13a10 10 0 0 1 5.24-2.76M12 20h.01M2 2l20 20',
  warn: 'M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0zM12 9v4M12 17h.01',
  check: 'M20 6 9 17l-5-5',
  err: 'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 8v4M12 16h.01',
  info: 'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 16v-4M12 8h.01',
  pause: 'M6 4h4v16H6zM14 4h4v16h-4z',
  play: 'M6 3l14 9-14 9V3z',
  mic: 'M12 2a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3zM19 10v1a7 7 0 0 1-14 0v-1M12 18v4M8 22h8',
};
export function icon(name, cls = '') {
  const t = document.createElement('template');
  t.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${ICONS[name] || ''}"/></svg>`;
  const s = t.content.firstElementChild;
  s.setAttribute('class', 'ico' + (cls ? ' ' + cls : ''));
  return s;
}

export const pad = (n) => String(n).padStart(2, '0');
// Clock times follow display.hour12: false ("14:05", default), true ("2:05 PM") or "auto" (follow the phone). Pass hours 0-23.
export function hour12() {
  const pref = cfg.display?.hour12;
  if (pref === true || pref === 'true') return true;
  if (pref === 'auto') {
    try { if (bridge?.is24Hour) return !bridge.is24Hour(); } catch (_) {}
    try { return Boolean(new Intl.DateTimeFormat(undefined, { hour: 'numeric' }).resolvedOptions().hour12); } catch (_) {}
  }
  return false;
}
export const fmtClock = (H, M) => (hour12() ? `${H % 12 || 12}:${pad(M)} ${H < 12 ? 'AM' : 'PM'}` : `${pad(H)}:${pad(M)}`);
export const fmtHM = (d) => fmtClock(d.getHours(), d.getMinutes());
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
  if (m < 60) return t('time.min', { m });
  const hr = Math.floor(m / 60);
  if (hr < 24) return t('time.hMin', { h: hr, m: m % 60 });
  const d = Math.floor(hr / 24);
  return t('time.dH', { d, h: hr % 24 });
}
export function shortDur(ms) {
  const m = Math.max(0, Math.floor(ms / 60000));
  if (m < 1) return t('time.now');
  if (m < 60) return m + 'm';
  const hr = Math.floor(m / 60);
  return hr < 24 ? `${hr}h ${m % 60}m` : `${Math.floor(hr / 24)}d`;
}
export const ago = (ts) => { const d = Date.now() - ts; return d < 60_000 ? t('time.justNow') : t('time.ago', { dur: shortDur(d) }); };
export const r1 = (v) => (v == null ? '—' : (Math.round(v * 10) / 10).toString());
export const fmtMem = (mb) => (mb >= 1024 ? (mb / 1024).toFixed(1) + ' GB' : Math.round(mb) + ' MB');
export const cssVar = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
export const color = (c) => (c && c.startsWith('--') ? cssVar(c) : c);

export let toastTimer = 0;
export function toast(msg, kind = '', title = '', seconds = 2.6) {
  const t = $('toast');
  const ic = { ok: 'check', err: 'err', warn: 'warn' }[kind] || 'info';
  const msgSpan = h('span', null, icon(ic, 'm'), msg);
  t.replaceChildren(...(title ? [h('b', null, title), msgSpan] : [msgSpan]));
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
/** All GPUs, main one first (older agents only send pc.gpu). */
export const gpusOf = (s) => (s?.pc?.gpus?.length ? s.pc.gpus : s?.pc?.gpu ? [s.pc.gpu] : []);
export const shortGpu = (n) => (n || 'GPU').replace(/^(NVIDIA|AMD|Intel\(R\))\s+/i, '').replace(/\((TM|R)\)/gi, '').replace(/\s+/g, ' ').trim();
/**
 * The memory a GPU works with. A graphics card: its own VRAM. An integrated GPU has only a small carve-out and lives
 * on shared system memory, so dedicated + shared is what counts (Task Manager's "GPU memory").
 */
export function gpuMem(g) {
  if (!g) return null;
  if (g.integrated && g.sharedTotal) {
    return { label: 'GPU memory', used: (g.vramUsed || 0) + (g.sharedUsed || 0), total: (g.vramTotal || 0) + g.sharedTotal };
  }
  return g.vramTotal ? { label: 'VRAM', used: g.vramUsed || 0, total: g.vramTotal } : null;
}
const memText = (m) => `${r1(m.used)} / ${Math.round(m.total)} GB`;
/** Board power: watts when a sensor gives them, otherwise the share of the power limit Windows reports. */
export const gpuPower = (g) => (g?.power != null ? `${Math.round(g.power)} W` : g?.powerPct != null ? `${Math.round(g.powerPct)}% power` : null);
export const netMax = () => Math.max(1, ...hist.down, ...hist.up) * 1.15;

export const METRICS = {
  cpu: { label: 'CPU', name: 'Processor', color: '--cpu', unit: '%', max: () => 100, val: (s) => s.pc?.cpu,
    sub: (s) => (s.pc?.cpuTemp != null ? `${Math.round(s.pc.cpuTemp)}°C` : shortCpu(s.pc?.cpuName)), title: (s) => s.pc?.cpuName },
  gpu: { label: 'GPU', name: 'Graphics', color: '--gpu', unit: '%', max: () => 100, val: (s) => gpuOf(s)?.util,
    sub: (s) => {
      const g = gpuOf(s);
      if (!g) return 'No GPU data';
      const parts = [g.temp != null ? `${Math.round(g.temp)}°C` : null, gpuPower(g)].filter(Boolean);
      if (parts.length) return parts.join(' · ');
      const mem = gpuMem(g);
      if (mem) return `${memText(mem)} memory`; // no sensors (Apple GPU)
      return shortGpu(g.name) || 'Active';
    }, title: (s) => gpuOf(s)?.name },
  ram: { label: 'RAM', name: 'Memory', color: '--ram', unit: 'GB', max: (s) => s.pc?.ramTotalGb || 1, val: (s) => s.pc?.ramUsedGb, fmt: r1,
    pct: (s) => (s.pc ? (s.pc.ramUsedGb / s.pc.ramTotalGb) * 100 : null), sub: (s) => (s.pc && s.pc.ramTotalGb ? `${r1(s.pc.ramUsedGb)} / ${Math.round(s.pc.ramTotalGb)} GB` : '') },
  vram: { label: 'VRAM', name: 'Video memory', color: '--gpu', unit: '%', max: () => 100,
    val: (s) => { const m = gpuMem(gpuOf(s)); return m ? (m.used / m.total) * 100 : null; },
    sub: (s) => { const m = gpuMem(gpuOf(s)); return m ? memText(m) : 'No GPU data'; } },
  net: { label: 'NETWORK', name: 'Network', color: '--net', rate: true, hist: 'down', max: netMax, val: (s) => s.pc?.netDownBps,
    sub: (s) => '↑ ' + rateStr(s.pc?.netUpBps) + (s.ping?.ms != null ? ` · ${Math.round(s.ping.ms)} ms` : '') },
  down: { label: 'DOWNLOAD', name: 'Download', color: '--net', rate: true, max: netMax, val: (s) => s.pc?.netDownBps },
  up: { label: 'UPLOAD', name: 'Upload', color: '--up', rate: true, max: netMax, val: (s) => s.pc?.netUpBps },
  ping: { label: 'PING', name: 'Ping', color: '#38bdf8', unit: 'ms', max: () => Math.max(50, ...hist.ping) * 1.2, val: (s) => s.ping?.ms,
    sub: (s) => (s.ping ? `avg ${s.ping.avgMs ?? '—'} ms · loss ${s.ping.lossPct}%` : '') },
  gputemp: { label: 'GPU TEMP', name: 'GPU temperature', color: '#f97316', unit: '°C', max: () => 100, hot: [75, 85], val: (s) => gpuOf(s)?.temp,
    sub: (s) => {
      const g = gpuOf(s);
      if (!g) return 'No GPU data';
      const detail = [g.hotSpot != null ? `Hot spot ${Math.round(g.hotSpot)}°` : null,
        g.fan != null ? `Fan ${Math.round(g.fan)}%` : g.fanRpm != null ? `Fan ${Math.round(g.fanRpm)} rpm` : null,
        g.clock != null ? `${Math.round(g.clock)} MHz` : null].filter(Boolean);
      return detail.length ? detail.join(' · ') : (g.temp != null ? 'Temperature' : 'No GPU data');
    } },
  hotspot: { label: 'HOT SPOT', name: 'GPU hot spot', color: '#ef4444', unit: '°C', max: () => 110, hot: [90, 100], val: (s) => gpuOf(s)?.hotSpot,
    sub: (s) => {
      const g = gpuOf(s);
      if (g?.hotSpot == null) return 'needs LibreHardwareMonitor';
      return [g.temp != null ? `Edge ${Math.round(g.temp)}°` : null, g.memTemp != null ? `Memory ${Math.round(g.memTemp)}°` : null].filter(Boolean).join(' · ');
    } },
  gpupower: { label: 'GPU POWER', name: 'GPU power', color: '#facc15', unit: (s) => (gpuOf(s)?.power != null ? 'W' : '%'),
    max: (s) => { const g = gpuOf(s); return g?.power != null ? Math.max(g.powerLimit || 0, ...hist.gpupower, 50) : 100; },
    val: (s) => { const g = gpuOf(s); return g?.power ?? g?.powerPct; },
    sub: (s) => {
      const g = gpuOf(s);
      if (!g || (g.power == null && g.powerPct == null)) return 'No power data';
      if (g.power != null && g.powerLimit) return `of ${Math.round(g.powerLimit)} W limit`;
      return g.power != null ? shortGpu(g.name) : 'of the power limit';
    } },
  gpuclock: { label: 'GPU CLOCK', name: 'GPU clock', color: '#a78bfa', unit: 'MHz', max: () => Math.max(1000, ...hist.gpuclock) * 1.1, val: (s) => gpuOf(s)?.clock,
    sub: (s) => { const g = gpuOf(s); return g?.memClock != null ? `Memory ${Math.round(g.memClock)} MHz` : g?.clock == null ? 'No clock data' : ''; } },
  cputemp: { label: 'CPU TEMP', name: 'CPU temperature', color: '#fb7185', unit: '°C', max: () => 100, hot: [75, 85], val: (s) => s.pc?.cpuTemp,
    sub: (s) => (s.pc?.cpuTemp == null ? 'needs LibreHardwareMonitor' : shortCpu(s.pc?.cpuName)) },
};

export function metricText(m, s) {
  const v = m.val(s);
  if (m.rate) return fmtRate(v);
  const unit = (typeof m.unit === 'function' ? m.unit(s) : m.unit) || '';
  if (v == null) return ['—', unit];
  return [m.fmt ? m.fmt(v) : String(Math.round(v)), unit];
}

// ---------------------------------------------------------------- weather (Open-Meteo, straight from the phone)
// The condition names are i18n keys: they are translated when the line is drawn, not when this table is built, so a
// language change applies without reloading the panel.
export const WX = {
  0: ['wx.clear', '☀️', '🌙'], 1: ['wx.mostlyClear', '🌤️', '🌙'], 2: ['wx.partlyCloudy', '⛅', '☁️'], 3: ['wx.overcast', '☁️', '☁️'],
  45: ['wx.fog', '🌫️'], 48: ['wx.rimeFog', '🌫️'],
  51: ['wx.lightDrizzle', '🌦️'], 53: ['wx.drizzle', '🌦️'], 55: ['wx.heavyDrizzle', '🌧️'], 56: ['wx.freezingDrizzle', '🌧️'], 57: ['wx.freezingDrizzle', '🌧️'],
  61: ['wx.lightRain', '🌦️'], 63: ['wx.rain', '🌧️'], 65: ['wx.heavyRain', '🌧️'], 66: ['wx.freezingRain', '🌧️'], 67: ['wx.freezingRain', '🌧️'],
  71: ['wx.lightSnow', '🌨️'], 73: ['wx.snow', '🌨️'], 75: ['wx.heavySnow', '❄️'], 77: ['wx.snowGrains', '🌨️'],
  80: ['wx.lightShowers', '🌦️'], 81: ['wx.showers', '🌧️'], 82: ['wx.heavyShowers', '⛈️'], 85: ['wx.snowShowers', '🌨️'], 86: ['wx.heavySnowShowers', '❄️'],
  95: ['wx.thunderstorm', '⛈️'], 96: ['wx.thunderstormHail', '⛈️'], 99: ['wx.severeHail', '⛈️'],
};
export const wxIcon = (code, day = 1) => { const x = WX[code] || [null, '🌡️']; return day === 0 && x[2] ? x[2] : x[1]; };
/** The condition in the panel's language, or an em dash for a code Open-Meteo does not list. */
export const wxText = (code) => { const x = WX[code]; return x ? t(x[0]) : '—'; };

let weatherRetry = 0;
let weatherFor = ''; // the location the forecast in D.weather belongs to
const weatherKey = () => { const { lat, lon } = cfg.weather || DEFAULT_CFG.weather; return `v3:${lat},${lon}`; };
export async function loadWeather(force = false) {
  const { lat, lon } = cfg.weather || DEFAULT_CFG.weather;
  const key = weatherKey();
  const cached = store.get('wx', null);
  // a new place starts from its own cache or from "Loading…", never from the previous place's forecast
  if (D.weather && weatherFor !== key) { D.weather = null; emit('weather'); }
  if (cached && cached.key === key && !D.weather) { D.weather = cached.data; weatherFor = key; emit('weather'); }
  if (!force && cached && cached.key === key && Date.now() - cached.at < 14 * 60_000) return;
  // only while a page shows the weather (PRIVACY.md): no widget, no request with the location
  if (!anyNeeds('weather')) return;
  try {
    const url = `https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}` +
      '&current=temperature_2m,apparent_temperature,weather_code,is_day' +
      '&hourly=temperature_2m,weather_code,precipitation_probability,is_day' +
      '&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,daylight_duration,uv_index_max' +
      '&timezone=auto&forecast_days=7';
    const ctl = new AbortController();
    setTimeout(() => ctl.abort(), 8000);
    const r = await safeFetch(url, { signal: ctl.signal });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const data = await r.json();
    // an error answer (from Open-Meteo or the agent's fallback) must not be kept as the forecast for 14 minutes
    if (!data || !data.current) throw new Error('no forecast');
    if (weatherKey() !== key) return; // the place changed while this was on its way
    store.set('wx', { key, at: Date.now(), data });
    D.weather = data;
    weatherFor = key;
    emit('weather');
  } catch (_) {
    // false, not null: the widgets say "Weather unavailable" instead of "Loading…" for good, and try again soon
    if (!D.weather) { D.weather = false; emit('weather'); }
    if (!weatherRetry) weatherRetry = setTimeout(() => { weatherRetry = 0; loadWeather(); }, 2 * 60_000);
  }
}

export function wxLines() {
  const w = D.weather;
  if (!w || !w.current) return null;
  const c = w.current, d = w.daily || {};
  const desc = wxText(c.weather_code);
  const hi = d.temperature_2m_max ? Math.round(d.temperature_2m_max[0]) : null;
  const lo = d.temperature_2m_min ? Math.round(d.temperature_2m_min[0]) : null;
  return {
    icon: wxIcon(c.weather_code, c.is_day),
    temp: Math.round(c.temperature_2m) + '°',
    desc: `${desc} · ${cfg.weather?.city || ''}`,
    range: (hi != null ? `↑${hi}° ↓${lo}° · ` : '') + t('wx.feelsLike', { deg: Math.round(c.apparent_temperature) }),
  };
}

export function wxBlock() {
  const e = h('div', 'wx', h('span', 'wx-icon', '·'), h('span', 'wx-temp', '--°'),
    h('div', 'wx-meta', h('span', 'wx-desc', t('w.loading')), h('span', 'wx-range muted')));
  e.update = () => {
    const l = wxLines();
    if (!l) {
      // no forecast: clear the last one too, so an old temperature never sits next to the new message
      e.querySelector('.wx-icon').textContent = '·';
      e.querySelector('.wx-temp').textContent = '--°';
      e.querySelector('.wx-range').textContent = '';
      e.querySelector('.wx-desc').textContent = D.weather === null ? t('w.loading') : t('w.weatherUnavailable');
      return;
    }
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
  const name = online ? (D.state?.host || 'PC') + (via === 'wifi' ? ' · ' + t('status.wiFi') : via === 'usb' ? ' · ' + t('status.usb') : '') : online === false ? t('panel.pcNotConnected') : t('panel.connecting');
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
  if (waiting) kids.push(h('span', 'sep', '·'), h('span', 'smini', t('status.waitingCount', { n: waiting })));
  const u = D.state?.update;
  if (u) kids.push(h('span', 'sep', '·'), h('span', 'upd', u.app && via !== 'usb' ? t('status.updateUsb', { version: u.latest }) : t('status.updateAvailable', { version: u.latest })));
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
  const icon = h('span', 'k-icon', s.icon || '•');
  const b = h('button', 'key' + (s.toggle ? ' toggle' : ''), icon, h('span', 'k-label', s.label));
  b.style.setProperty('--k', s.color || '#6b7280');
  b.dataset.id = s.id;
  b.addEventListener('click', () => runShortcut(b, s.id));
  if (s.iconRev != null) applyShortcutIcon(icon, s.id);
  return b;
}

function applyShortcutIcon(icon, id) {
  const img = new Image();
  img.onload = () => { icon.replaceChildren(img); icon.classList.add('k-icon-img'); };
  img.src = `${API}/shortcuts/icon?id=${encodeURIComponent(id)}`;
}

// ---------------------------------------------------------------- media (shared state, any number of widgets)
export let media = null;          // last media state + the time it was received
export let volDragging = null;
export function setVolDragging(control, dragging) {
  if (dragging) volDragging = control;
  else if (volDragging === control) volDragging = null;
}
export const setMediaState = (m) => {
  if (D.state) D.state.media = m || null;
  media = m ? { ...m, at: performance.now() } : null;
  emit('state');
};
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

/** The empty media card. A function, not a constant: its texts are translated when the widget is built. */
export const mediaHtml = () => `
  <div class="m-top">
    <div class="art-wrap"><img class="art" alt=""><div class="art-ph">♪</div></div>
    <div class="m-text"><div class="m-app"><span class="m-source-icon" aria-hidden="true"></span><span class="m-app-name">Media</span></div><div class="m-title">${t('w.nothingPlaying')}</div><div class="m-artist">${t('w.startOnPc')}</div></div>
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
  <div class="offline-msg"><b data-i18n="panel.computerNotConnected">${t('panel.computerNotConnected')}</b>
    <span data-i18n="panel.offlineHelp">${t('panel.offlineHelp')}</span></div>`;

// ---------------------------------------------------------------- quotas
export const Q_STYLE = { antigravity: { color: '#38bdf8', logo: '✦' }, claude: { color: '#d97757', logo: '✳' }, codex: { color: '#e5e7eb', logo: '◎' } };

export function fmtReset(ts) {
  if (!ts) return '';
  const d = new Date(ts);
  const hm = fmtHM(d);
  const day = d.toDateString() === new Date().toDateString() ? hm : d.toLocaleDateString(LOCALE, { weekday: 'short' }) + ' ' + hm;
  return `${day} · ${t('time.inDur', { dur: fmtDur(ts - Date.now()) })}`;
}

/** Compares the share of the quota used with the share of the window elapsed. */
export function paceInfo(l) {
  if (!l.resetsAt || !l.windowSec) return null;
  const win = l.windowSec * 1000;
  const left = l.resetsAt - Date.now();
  const elapsed = Math.min(1, Math.max(0, 1 - left / win));
  if (l.percent >= 100) return { elapsed, cls: 'over', text: t('pace.limitReached') };
  if (elapsed < 0.04 || l.percent < 1) return { elapsed, cls: 'calm', text: t('pace.plentyLeft') };
  const rate = l.percent / (elapsed * win);          // % per ms
  const msTo100 = (100 - l.percent) / rate;
  if (msTo100 < left) return { elapsed, cls: 'over', text: t('pace.fullIn', { dur: fmtDur(msTo100) }) };
  const diff = l.percent - elapsed * 100;
  if (diff > 10) return { elapsed, cls: 'fast', text: t('pace.ahead') };
  if (diff < -10) return { elapsed, cls: 'calm', text: t('pace.plentyLeft') };
  return { elapsed, cls: '', text: t('pace.onPace') };
}

// ---------------------------------------------------------------- sessions & GitHub
export const S_ORDER = { waiting: 0, busy: 1, shell: 2, idle: 3 };
export const prevStatus = new Map();
export let workLoaded = false;

/** Vibrates and shows a toast when a session finishes or starts waiting for input. */
export function notifyTransitions(sessions) {
  for (const s of sessions) {
    const before = prevStatus.get(s.id);
    if (workLoaded && before && before !== s.status) {
      if (s.status === 'waiting') {
        buzz('0,90,90,90,90,160');
        toast(s.waitingFor || t('panel.needsInput'), 'err', s.name);
      } else if (s.status === 'idle' && (before === 'busy' || before === 'shell')) {
        buzz('0,70,110,70');
        toast(t('panel.sessionFinished', { name: s.name }), 'ok');
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

const ICON_CACHE = new Map();
const MAX_ICON_CACHE = 200;

/** Fills an icon box with the app icon, falling back to a coloured monogram. */
export function fillIcon(box, g) {
  box.replaceChildren();
  const cached = ICON_CACHE.get(g.name);
  if (cached) {
    box.textContent = '';
    box.style.background = 'transparent';
    box.appendChild(cached.cloneNode(true));
    return;
  }
  box.style.background = monogramColor(g.name);
  box.textContent = (g.title || g.name).charAt(0).toUpperCase();
  if (g.hasIcon !== false) {
    const img = new Image();
    img.onload = () => {
      if (ICON_CACHE.size >= MAX_ICON_CACHE) {
        const oldest = ICON_CACHE.keys().next().value;
        if (oldest !== undefined) ICON_CACHE.delete(oldest);
      }
      ICON_CACHE.set(g.name, img);
      box.textContent = '';
      box.style.background = 'transparent';
      box.appendChild(img);
    };
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
  $('psSub').textContent = t('w.loading');
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
  const instLabel = t(d.instances.length === 1 ? 'panel.instanceOne' : 'panel.instanceMany', { n: d.instances.length });
  $('psSub').textContent = `${instLabel} · CPU ${totalCpu.toFixed(1)}% · ${fmtMem(totalMem)}` +
    (d.locked ? ` · ${t('panel.systemProcess')}` : !canKill ? ` · ${t('panel.endingDisabled')}` : '');
  const all = $('psAll');
  if (!all.classList.contains('armed')) all.textContent = d.instances.length > 1 ? t('panel.endAllN', { n: d.instances.length }) : t('panel.end');
  all.disabled = d.locked || !canKill || !d.instances.length;

  const list = $('psList');
  list.replaceChildren();
  if (!d.instances.length) { list.appendChild(h('div', 'empty', t('panel.noInstances'))); return; }
  for (const i of d.instances) {
    const primary = i.session ? '✳ ' + i.session : i.window || i.args || d.title;
    const secondary = [i.session ? i.args : i.window ? i.args : null,
      i.startedAt ? (Date.now() - i.startedAt < 60_000 ? t('panel.justStarted') : t('panel.startedAgo', { dur: shortDur(Date.now() - i.startedAt) })) : null]
      .filter(Boolean).join(' · ');
    const btn = h('button', 'ps-btn danger', t('panel.end'));
    btn.disabled = i.locked || !canKill;
    armButton(btn, t('panel.confirm'), () => killInstance(i, primary));
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
    ok ? toast(t('panel.endedPid', { label, pid: i.pid }), 'ok') : toast(body.error || t('panel.couldNotEnd'), 'err');
  } catch (_) { toast(t('panel.pcUnreachable'), 'err'); }
  setTimeout(() => { loadSheet(); loadProcs(); }, 500);
}

armButton($('psAll'), t('panel.tapToConfirm'), async () => {
  const g = sheet?.group;
  if (!g) return;
  try {
    const { ok, body } = await postKill('name=' + encodeURIComponent(g.name));
    if (ok) { toast(t('panel.endedGroup', { killed: body.killed, title: g.title }) + (body.failed ? t('panel.failedCount', { n: body.failed }) : ''), 'ok'); closeSheet(); }
    else toast(body.error || t('panel.couldNotEndThem'), 'err');
  } catch (_) { toast(t('panel.pcUnreachable'), 'err'); }
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
  undim();
  toast(d.text || '', level, d.title || '', d.seconds || 5);
  if (d.vibrate !== false) buzz(level === 'err' ? '0,90,90,90,90,160' : '0,70,110,70');
  D.notes.unshift({ title: d.title || '', text: d.text || '', level: d.level || 'info', at: Date.now() });
  D.notes = D.notes.slice(0, 30);
  store.set('notes', D.notes);
  emit('notes');
  // the webhook body is passed through as sent; like every other countdown, this one is 0-30 s (or the default)
  // only a number or a numeric string: Number() of an object such as {"toString":null} throws, which would stop the
  // events after this one from being handled
  const raw = d.countdown;
  const countdown = typeof raw === 'number' ? raw : typeof raw === 'string' && raw.trim() !== '' ? Number(raw) : NaN;
  if (d.page != null) requestPage({ page: d.page, name: d.title || null, countdown: Number.isFinite(countdown) ? Math.min(30, Math.max(0, countdown)) : undefined });
}

// ---------------------------------------------------------------- permission prompts (Claude Code hook → phone)
export let approvals = [];
export let approvalShown = null;

// A list request sent before the answer can land after it and hand back the already answered prompt, which would
// pop the card up again on its own and buzz. Ids answered from this phone are dropped from every later list.
const answered = new Set();
const answeredCap = 50;

// Ids whose answer is on its way to the agent. Tracked per id rather than as one flag because a list request can
// put a different prompt on screen while this one is still posting, and neither may unlock the other's buttons.
const submitting = new Set();

function stillOpen(items) {
  return items.filter((x) => !answered.has(x.id));
}

let approvalBusy = false;

export async function loadApprovals() {
  if (PREVIEW || approvalBusy) return; // a slow answer must not stack up with the next tick
  approvalBusy = true;
  try {
    // api() gives up after 2.5s by default: plenty for a tap, too little for a list request on a phone that
    // dozes, and the abort then looked exactly like a broken connection.
    approvals = stillOpen((await api('/approvals', 'GET', 8000)).items || []);
  } catch (e) {
    const msg = (e && e.message) || String(e);
    // Logged, never shown. The boot panel is for faults that need a new build; a list request that fails is
    // routine, and an agent that goes away stays unreachable until it is back, so a banner here would outlive the
    // outage it describes and teach the reader to ignore the box. The next tick and the live stream both retry.
    if (!/abort|timeout/i.test(msg)) console.warn('approvals: list request failed:', msg);
    return;
  } finally { approvalBusy = false; }
  renderApproval();
}

export function renderApproval() {
  const a = approvals[0];
  const box = $('approval');
  if (!a) { box.hidden = true; approvalShown = null; return; }
  if (approvalShown !== a.id) {
    approvalShown = a.id;
    lastTouch = Date.now();
    undim();
    if (nightOn) { wakeUntil = Date.now() + 60_000; setNight(false); }
    buzz('0,120,80,120,80,240');
    // A prompt whose answer is still in flight comes back locked: it can be shown again while its own post runs.
    $('apAllow').disabled = $('apDeny').disabled = $('apSkip').disabled = submitting.has(a.id);
  }
  // A question from Claude is answered at the PC (the terminal shows it); the phone only tells you it is waiting.
  const asking = a.kind === 'question';
  $('apAllow').hidden = $('apDeny').hidden = asking;
  $('apSkip').hidden = !asking;
  $('apSource').textContent = a.source || 'Claude Code';
  $('apProject').textContent = a.project || a.session || '';
  $('apCount').textContent = approvals.length > 1 ? t('panel.oneOfN', { n: approvals.length }) : '';
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
  $('apTime').textContent = a.kind === 'question' ? t('panel.answerInTerminal')
    : left > 0 ? t('panel.askedInTerminalIn', { time: fmtTime(left) }) : t('panel.askingInTerminal');
  if (left < -3000) { approvals.shift(); renderApproval(); }
}
setInterval(tickApproval, 1000);

// The live stream reconnects every minute on Wi-Fi and starts at the agent's current sequence, so anything emitted
// while it was down is never replayed. An approval lost that way leaves the command waiting in the dark with nothing
// on screen, so ask for the list outright as well instead of relying on the event alone. Only while the panel is on
// screen: the answer is needed when someone is looking, and a phone in a pocket should not poll all night.
setInterval(() => { if (!PREVIEW && !document.hidden) loadApprovals(); }, 5000);

export async function decide(decision) {
  const a = approvals[0];
  if (!a || submitting.has(a.id)) return;
  haptic();
  submitting.add(a.id);
  $('apAllow').disabled = $('apDeny').disabled = $('apSkip').disabled = true;
  let accepted = false;
  try {
    await api(`/approvals/${encodeURIComponent(a.id)}?decision=${decision}`, 'POST', 4000);
    accepted = true;
    if (decision === 'skip') toast(t('panel.questionWaiting'), 'warn');
    else toast(t(decision === 'allow' ? 'panel.allowedTool' : 'panel.deniedTool', { tool: a.tool }), decision === 'allow' ? 'ok' : 'err');
  } catch (_) { toast(t('panel.alreadyAnswered'), 'warn'); }
  finally { submitting.delete(a.id); }
  if (!accepted) {
    // The agent never took the answer, so the prompt is still live and the command is still blocked. Hiding it
    // here would leave nothing on screen while the computer waits; the buttons come back and the next list
    // request settles whether the prompt really is gone. Only for this prompt though: a list request may have
    // moved on to another approval while this one was in flight, and unlocking that one would let a second
    // answer go out for a prompt whose own post is still running.
    if (approvals[0] && approvals[0].id === a.id) {
      $('apAllow').disabled = $('apDeny').disabled = $('apSkip').disabled = false;
    }
    return;
  }
  answered.add(a.id);
  if (answered.size > answeredCap) answered.delete(answered.values().next().value);
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

export const WIDGET_ICONS = { camera: '📹', dictate: '🎙️', spectrum: '📶', 'app-console': '🎛️',discord: '💬', rules: '⚡', chargecare: '🔋', nextup: '⏭️', moon: '🌙', wordclock: '🔤', clock: '🕒', weather: '⛅', forecast: '🌦️', media: '🎵', stat: '📈', gauge: '⏲️', chart: '📊', meters: '💾', gpu: '🎮', quotas: '✳️', sessions: '🤖',
  github: '🐙', processes: '⚙️', shortcuts: '🎛️', button: '🔘', worldclock: '🌍', calendar: '📅', pomodoro: '🍅', countdown: '⏳', note: '📝', command: '💻',
  push: '📡', inbox: '📬', label: '🔤', analog: '🕰️', progress: '📆', stopwatch: '⏱️', timer: '⏲️', counter: '🔢', breathe: '🫁', quote: '💬',
  checklist: '✅', battery: '🔋', topprocs: '🔥', quotaring: '🎯', volume: '🔊', sun: '🌅', air: '🍃', hackernews: '📰', feeds: '🗞️', photos: '🖼️', exchange: '💱', crypto: '🪙', image: '🖼️', pcswitch: '🖥️' };

// ================================================================= pages
export const pager = $('pager');
export let pages = [];       // enabled pages
export let mounted = [];     // { pi, def, inst, ctx, dirty }
export let layoutKey = '';
export let curPage = 0;
let lastHour12 = null;

export const isPortrait = () => window.innerHeight > window.innerWidth * 1.05;

// Portrait: the same widgets packed into a 6-column grid in reading order. Their sizes in cells stay the same
// (portrait cells are about as square as landscape ones); wider widgets shrink to 6 columns and the rows grow to fit.
export function portraitLayout(widgets) {
  const cols = 6, occ = [], pos = new Map();
  const free = (x, y, w, h) => { for (let r = y; r < y + h; r++) for (let c = x; c < x + w; c++) if (occ[r]?.[c]) return false; return true; };
  const take = (x, y, w, h) => { for (let r = y; r < y + h; r++) { occ[r] = occ[r] || []; for (let c = x; c < x + w; c++) occ[r][c] = true; } };
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
  const h12 = hour12();
  // the active language belongs in the key: the widgets render their texts once, when they are created
  const key = JSON.stringify([next, cfg.shortcuts, cfg.display?.showSeconds, cfg.display?.hour12, h12, cfg.display?.appearance, portrait, I18n?.getLang()]);
  if (key === layoutKey) return;
  layoutKey = key;
  lastHour12 = h12;
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
  const idx = Math.max(0, pages.findIndex((p) => p.id === prevId));
  if (pagePaused && (pages.length < 2 || (pausedPageId && pages[idx]?.id !== pausedPageId))) {
    pagePaused = false;
    pausedPageId = null;
  }
  buildDots();
  curPage = -1;
  goPage(idx, true);
  requestAnimationFrame(() => setActive(true));
}

export function activeSet() { return new Set([curPage - 1, curPage, curPage + 1]); }
export const pageOf = (kind) => mounted.filter((m) => m.pi === curPage && m.def.needs.includes(kind)).length > 0;
export const anyNeeds = (kind) => mounted.some((m) => m.def.needs.includes(kind));

export function runUpdate(m, kind, background = false) {
  try { m.inst.update?.(kind, background); } catch (e) { console.warn(m.ctx.w.type, e); }
}

/** Delivers fresh data to the widgets on the visible pages; the others catch up when they come into view. */
export function emit(kind) {
  const act = activeSet();
  for (const m of mounted) {
    if (!m.def.needs.includes(kind)) continue;
    // alarms (timer, pomodoro) must go off on whatever page is showing, so those widgets get every tick
    if (act.has(m.pi)) runUpdate(m, kind);
    else if (m.def.always && kind === 'tick') runUpdate(m, kind, true);
    else m.dirty = true;
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
export let pagePaused = false;
let pausedPageId = null;
let pagePauseBtn = null;
let pageOverlayTimer = 0;

/** Builds an inline SVG element for the page pause toggle and center overlay. */
function makeSvg(cls, d) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  if (cls) svg.setAttribute('class', cls);
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(ns, 'path');
  path.setAttribute('d', d);
  svg.appendChild(path);
  return svg;
}

/** Shows a 2-second centered pause or play overlay when page switching is toggled. */
export function showPagePauseFeedback(paused) {
  const overlay = $('pagePauseOverlay');
  if (!overlay) return;
  if (pageOverlayTimer) { clearTimeout(pageOverlayTimer); pageOverlayTimer = 0; }
  overlay.replaceChildren(h('div', 'rot-overlay-icon',
    makeSvg('', paused ? 'M6 5h4v14H6zM14 5h4v14h-4z' : 'M8 5v14l11-7z')));
  overlay.classList.remove('show');
  void overlay.offsetWidth;
  overlay.classList.add('show');
  pageOverlayTimer = setTimeout(() => {
    overlay.classList.remove('show');
    pageOverlayTimer = 0;
  }, 2000);
}

/** Syncs the #dots bar and .page-pause button state with `pagePaused`. */
export function syncPagePauseUi() {
  const dots = $('dots');
  if (dots) dots.classList.toggle('paused', pagePaused);
  if (pagePauseBtn) {
    pagePauseBtn.classList.toggle('on', pagePaused);
    pagePauseBtn.setAttribute('aria-label', t('panel.pagePause'));
    pagePauseBtn.setAttribute('aria-pressed', String(pagePaused));
    pagePauseBtn.title = pagePaused ? t('panel.pagePaused') : t('panel.pagePause');
  }
}

/** Sets whether automatic page switching (idle return and rules) is paused. */
export function setPagePaused(next, { feedback = false, resume = true } = {}) {
  const val = !!(next && pages.length >= 2);
  if (val === pagePaused) return pagePaused;
  pagePaused = val;
  pausedPageId = pagePaused ? (pages[curPage]?.id ?? null) : null;
  lastTouch = Date.now();
  syncPagePauseUi();
  if (feedback) {
    haptic();
    showPagePauseFeedback(pagePaused);
  }
  if (pagePaused && navQueue.length) navQueue = navQueue.filter((r) => r.kind !== 'home');
  // leaving the page by hand ends the pause, but a waiting rule moves only after that page change has landed: the
  // queue's own 250 ms timer picks it up, so a held rule records the page the user went to as the one to return to
  if (resume) pump();
  return pagePaused;
}

/** Toggles page-level switching pause with haptic and visual feedback. */
export function togglePagePaused() {
  if (pages.length < 2) return false;
  return setPagePaused(!pagePaused, { feedback: true });
}

export function buildDots() {
  const dots = $('dots');
  if (pages.length < 2) {
    pagePauseBtn = null;
    dots.replaceChildren();
    dots.classList.remove('paused');
    dots.hidden = true;
    return;
  }
  pagePauseBtn = h('button', 'page-pause',
    makeSvg('i-pause', 'M7 5h4v14H7zM13 5h4v14h-4z'),
    makeSvg('i-play', 'M8 5v14l11-7z'));
  pagePauseBtn.type = 'button';
  pagePauseBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    togglePagePaused();
  });
  dots.replaceChildren(...pages.map(() => h('i')), pagePauseBtn);
  dots.hidden = false;
  syncPagePauseUi();
}

export function updateDots() {
  $('dots').querySelectorAll('i').forEach((d, i) => d.classList.toggle('on', i === curPage));
}

export let snapTimer = 0;
export function goPage(i, instant = false) {
  i = Math.max(0, Math.min(pages.length - 1, i));
  if (curPage >= 0 && i !== curPage && pagePaused) setPagePaused(false, { feedback: false, resume: false });
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

let procsSort = 'cpu';
let procsSorter = null;

export function registerProcessesSorter(fn) {
  procsSorter = typeof fn === 'function' ? fn : null;
  if (procsSorter) procsSorter(procsSort);
}

export function currentProcessesSort() {
  return procsSort;
}

export function hasProcessesPage() {
  return resolvePage('procs') >= 0 || resolvePage('processes') >= 0
    || pages.some((p) => (p.widgets || []).some((w) => w.type === 'processes'));
}

export function gotoProcesses(sortTarget = 'cpu') {
  procsSort = sortTarget === 'mem' ? 'mem' : 'cpu';
  let idx = resolvePage('procs');
  if (idx < 0) idx = resolvePage('processes');
  if (idx < 0) idx = pages.findIndex((p) => (p.widgets || []).some((w) => w.type === 'processes'));
  if (idx >= 0) gotoPageRef(idx + 1);
  if (procsSorter) procsSorter(procsSort);
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
  if (pagePaused) return false;
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
  const pausedBefore = pagePaused; // the Back key goes home through here while paused, and that must still work
  setTimeout(() => {
    pager.classList.remove('fade');
    if (pagePaused && !pausedBefore) return; // paused during the fade: this automatic switch is dropped
    goPage(i, true);
  }, 320);
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
  // staying after a rule ended ends its hold, or the list of active rules (applyState) would ask to go back again
  if (req.kind === 'back' && ruleHold?.rule === req.rule) ruleHold = null;
  navCount = null;
  pump();
}

function nextRequest() {
  const hold = ruleHold && curPage === ruleHold.page ? ruleHold : null;
  // the rule ended after you moved on: its hold is over, so coming back to that page later does not undo it
  if (ruleHold && !hold && navQueue.some((r) => r.kind === 'back' && r.rule === ruleHold.rule)) ruleHold = null;
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
  if (pagePaused) return t('panel.pagePausedWait');
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
let navWoken = null; // the waiting request or the countdown that last brightened a dimmed panel
function renderNav(req = null, why = '') {
  const card = $('navCard'), chip = $('navChip');
  if (!req) { card.classList.remove('on'); chip.classList.remove('on'); navShown = null; navWoken = null; return; }
  // a new waiting request or countdown must not be hidden under the dark layer; this runs every 250 ms while one
  // waits, so only its first appearance counts, or the panel would never dim behind a request that keeps waiting
  const wakeKey = why ? req : navCount;
  if (wakeKey !== navWoken) { navWoken = wakeKey; undim(); }
  const name = req.name || (req.kind === 'home' ? t('panel.nobodyAround') : req.kind === 'back' ? t('panel.done') : t('panel.pageSwitch'));
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
    $('nvLine').textContent = req.kind === 'goto' ? t('panel.switchingTo', { target }) : t('panel.backTo', { target });
    $('nvDetail').textContent = req.detail && req.detail !== 'test' ? req.detail : more ? t('panel.moreWaiting', { n: navQueue.length - 1 }) : '';
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
    toast(rulesPaused ? t('panel.rulesPausedToast') : t('panel.rulesResumedToast'), 'ok');
  } catch (_) { toast(t('panel.pcNoAnswer'), 'warn'); }
  renderRulesBtn();
}
export function renderRulesBtn() {
  const b = $('ovRules');
  if (!b) return;
  const paused = rulesPaused && rulesPaused > Date.now();
  b.hidden = PREVIEW || !online;
  b.classList.toggle('on', !!paused);
  b.replaceChildren(icon(paused ? 'play' : 'pause', 'm'), paused ? t('panel.rulesPausedUntil', { time: fmtHM(new Date(rulesPaused)) }) : t('panel.pauseRules1h'));
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
  const prev = curPage;
  curPage = p;
  if (prev >= 0 && pagePaused) setPagePaused(false, { feedback: false, resume: false });
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
  const pcs = pairedPcs();
  $('ovPcs').replaceChildren(...(pcs.length > 1 ? pcs.map((pc) => {
    const b = h('button', 'ov-pc' + (pc.active ? ' on' : ''), '🖥️ ' + (pc.name || t('panel.computer')));
    b.addEventListener('click', (e) => { e.stopPropagation(); switchPc(pc); });
    return b;
  }) : []));
  $('ovGrid').style.setProperty('--n', Math.min(4, Math.ceil(Math.sqrt(pages.length * 1.6))));
  ov.hidden = false;
}
window.openOverview = openOverview;

/** The computers this phone is paired with over Wi-Fi: [{ id, name, active }] (empty outside the app). */
export function pairedPcs() {
  try { return bridge?.pcs ? JSON.parse(bridge.pcs()) : []; } catch (_) { return []; }
}
/** Makes another paired computer the one used over Wi-Fi; the app then reloads the panel from it. */
export function switchPc(pc) {
  if (!pc || pc.active || !bridge?.selectPc) return;
  haptic(); toast(t('panel.switchingPc', { name: pc.name || t('panel.otherComputer') }));
  setTimeout(() => bridge.selectPc(pc.id), 300);
}
export function closeOverview() { $('overview').hidden = true; }
$('overview').addEventListener('click', closeOverview);
$('ovRules').addEventListener('click', (e) => { e.stopPropagation(); toggleRulesPause(); });

let dotsHoldTimer = 0, dotsHoldStart = null, heldDots = false;
/** Cancels any pending long-press hold timer on the #dots bar. */
function clearDotsHold() {
  if (dotsHoldTimer) { clearTimeout(dotsHoldTimer); dotsHoldTimer = 0; }
  dotsHoldStart = null;
}
$('dots').addEventListener('pointerdown', (e) => {
  heldDots = false;
  clearDotsHold();
  if (pages.length < 2 || dimmed || document.body.classList.contains('waking')) return;
  if (e.pointerType === 'mouse' && e.button !== 0) return;
  if (e.target.closest('.page-pause')) return;
  dotsHoldStart = [e.clientX, e.clientY];
  dotsHoldTimer = setTimeout(() => {
    dotsHoldTimer = 0;
    dotsHoldStart = null;
    heldDots = true;
    togglePagePaused();
  }, 450);
});
$('dots').addEventListener('pointermove', (e) => {
  if (dotsHoldStart && Math.hypot(e.clientX - dotsHoldStart[0], e.clientY - dotsHoldStart[1]) > 10) clearDotsHold();
});
['pointerup', 'pointercancel', 'pointerleave'].forEach((ev) => $('dots').addEventListener(ev, clearDotsHold));
window.addEventListener('blur', clearDotsHold);
$('dots').addEventListener('click', (e) => {
  if (heldDots) {
    heldDots = false;
    if (!e.target.closest('.page-pause')) {
      e.preventDefault();
      e.stopPropagation();
      return;
    }
  }
  if (e.target.closest('.page-pause')) return;
  openOverview();
});

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
  // Dragging a slider, the volume bar, a list or the station carousel must never flip (or wrap) the page.
  if (volDragging || e.target.closest?.('button, input, textarea, select, a, [contenteditable], .s-list, .vb, .radio-carousel-track, .ac-dial')) return;
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
  if (dimmed) wakeFromDim();
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
    if (lux < 3) { darkSince = darkSince || Date.now(); } else darkSince = 0;
    if (darkRoom && lux > 15) darkRoom = false;
    applyBrightness();
  } else if (kind === 'proximity' && value === true && cfg.display?.proximityWake !== false && (nightOn || dimmed)) {
    undim();
    if (!nightOn) return;
    wakeUntil = Date.now() + 30_000;
    setNight(false);
  }
};

// ---------------------------------------------------------------- OLED burn-in protection
// Pixel shift (display.pixelShift): every 2 minutes the panel steps along a small loop, at most 2 px from its place, so
// edges and text don't sit on the same pixels all day; the night clock, the most static thing on screen, drifts further.
// Idle dim (display.idleDimMin): after that many minutes without a touch, page switch, notification or prompt, a dark
// layer covers the panel. Like waking the night screen, the touch that brightens it does nothing else.
const SHIFT = [[0, 0], [1, 0], [2, 1], [1, 2], [0, 2], [-1, 1], [-2, 0], [-1, -1], [0, -2], [1, -1]];
const NIGHT_SHIFT = [[0, 0], [4, -3], [-4, 3], [3, 4], [-4, -2], [2, -4], [-2, 2], [4, 1], [-3, -4], [1, 3]]; // vmin
const SHIFT_MS = 120_000;
let shiftStep = -1;
let lastWake = Date.now();
let dimmed = false;

/** Something happened that the user should see: brighten a dimmed panel and restart its idle time. */
export function undim() {
  lastWake = Date.now();
  if (!dimmed) return;
  dimmed = false;
  document.body.classList.remove('dimmed');
}

// The dark layer keeps catching this touch until the finger lifts, so the widget under it is not pressed.
function wakeFromDim() {
  const body = document.body.classList;
  body.add('waking');
  undim();
  const done = () => {
    clearTimeout(fallback);
    ['pointerup', 'pointercancel'].forEach((t) => document.removeEventListener(t, done, true));
    setTimeout(() => body.remove('waking'), 300);
  };
  const fallback = setTimeout(done, 3000);
  ['pointerup', 'pointercancel'].forEach((t) => document.addEventListener(t, done, true));
}

export function applyBurnIn() {
  const d = cfg.display || {};
  const shift = d.pixelShift === true && !PREVIEW;
  document.body.classList.toggle('shifted', shift);
  const step = shift ? Math.floor(Date.now() / SHIFT_MS) % SHIFT.length : -1;
  if (step !== shiftStep) {
    shiftStep = step;
    const [x, y] = step < 0 ? [0, 0] : SHIFT[step];
    const [nx, ny] = step < 0 ? [0, 0] : NIGHT_SHIFT[step];
    const root = document.documentElement.style;
    root.setProperty('--shift-x', `${x}px`);
    root.setProperty('--shift-y', `${y}px`);
    root.setProperty('--night-x', `${nx}vmin`);
    root.setProperty('--night-y', `${ny}vmin`);
  }
  const after = Math.max(0, Math.floor(+d.idleDimMin || 0)) * 60_000;
  const dim = after > 0 && !PREVIEW && !nightOn && $('approval').hidden
    && Date.now() - Math.max(lastTouch, lastNav, lastWake) > after;
  if (dim !== dimmed) {
    dimmed = dim;
    document.body.classList.toggle('dimmed', dim);
  }
}

document.addEventListener('visibilitychange', () => { if (!document.hidden) undim(); }); // woken on plug-in, back from another app

export function tick() {
  if (lastHour12 !== null && hour12() !== lastHour12) { buildLayout(); renderRulesBtn(); }
  const now = new Date();
  $('nClock').textContent = fmtHM(now);
  $('nDate').textContent = now.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' });
  if (darkSince && Date.now() - darkSince > 30_000) darkRoom = true;
  const dark = darkRoom && cfg.display?.nightWhenDark === true;
  setNight((isNight(now) || dark) && Date.now() > wakeUntil);
  if (!PREVIEW && idleDue() && !navQueue.some((r) => r.kind === 'home')) {
    if (curPage === homeIndex()) { closeSheet(); closeOverview(); lastNav = Date.now(); } // only a sheet to close
    else { navQueue.push({ kind: 'home', at: Date.now(), priority: -1 }); pump(); }
  }
  applyBurnIn();
  document.body.classList.toggle('controls-top', cfg.display?.controlsTop === true);
  if (!nightOn) emit('tick');
  setTimeout(tick, 1000 - (Date.now() % 1000) + 5);
}

// ---------------------------------------------------------------- data from the agent
export function applyDisplay() {
  const d = cfg.display || {};
  const langChanged = I18n ? I18n.setLang(d.language || 'auto') : false;
  LOCALE = I18n ? I18n.getLocale() : 'en-GB';
  I18n?.applyDom();
  // theme and accent apply in the preview and the browser too
  document.body.dataset.theme = ['amoled', 'midnight', 'warm', 'graphite'].includes(d.theme) ? d.theme : 'default';
  const accent = hexToRgb(d.accent);
  const before = DEFAULT_AMBIENT;
  DEFAULT_AMBIENT = accent || '45, 212, 191';
  document.documentElement.style.setProperty('--accent', `rgb(${DEFAULT_AMBIENT})`);
  artTint = d.artTint !== false;
  if (before !== DEFAULT_AMBIENT || !artTint) setAmbient(artTint ? currentArtColor : null);
  if (langChanged) { buildLayout(); renderApproval(); }
  renderRulesBtn();
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

export function applyState(s, replayGen = null) {
  lastStateAt = Date.now();
  D.state = s;
  media = s.media ? { ...s.media, at: performance.now() } : null;
  setOnline(true);
  if (s.cfgVersion !== undefined && cfgVersion !== null && s.cfgVersion !== cfgVersion) loadConfig();
  if (s.pushVersion !== undefined && s.pushVersion !== pushVersion) loadPush();
  // compares the agent's wall clock across responses: if the PC's clock steps back (NTP), polled events are
  // ignored until it catches up — the stream still delivers them meanwhile
  const fresh = s.ts == null || s.ts >= stateTs;
  // the answer to a ?ev=0 replay can be overtaken by streamed states on the way; its events are still the backlog.
  // they go through evOffer like live ones: while a replay is armed the queue merges them before dispatch
  if (fresh || replayGen === evGen) for (const e of s.events || []) evOffer(e);
  if (fresh) {
    stateTs = s.ts ?? stateTs;
    // the agent restarted: it counts events from 0 again — forget what was handled and replay its backlog once
    if (evSeq != null && s.evSeq != null && s.evSeq < evSeq) { evSeen.clear(); evQueue = []; evDrop = 0; evReplay = true; evGen++; evSeq = 0; }
    evSeq = Math.max(evSeq ?? 0, s.evSeq ?? 0);
  }
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
      const mem = gpuMem(pc.gpu);
      pushHist('vram', mem ? (mem.used / mem.total) * 100 : 0);
      pushHist('down', pc.netDownBps);
      pushHist('up', pc.netUpBps);
      pushHist('ping', s.ping?.ms);
      pushHist('gputemp', pc.gpu?.temp);
      pushHist('cputemp', pc.cpuTemp);
      pushHist('hotspot', pc.gpu?.hotSpot);
      pushHist('gpupower', pc.gpu?.power ?? pc.gpu?.powerPct);
      pushHist('gpuclock', pc.gpu?.clock);
    }
  }
  emit('state');
  return fresh || replayGen === evGen;
}

// Live stream from the Android shell (see AgentStream.kt); polling below is the fallback when it is not running.
export let streamOn = false;
export let lastStateAt = 0;
window.onAgentStream = (event, data) => {
  // the stream can send events before its first state (Program.cs sends the backlog first): an event that is only
  // new to this run would dispatch now and again when the replay delivers it — hold them until the state decides
  if (event === 'open') { streamOn = true; evPre = []; return; }
  if (event === 'close') { streamOn = false; return; }
  let d;
  try { d = JSON.parse(data); } catch (_) { return; }
  if (event === 'state') { applyState(d); for (const e of evPre || []) evOffer(e); evPre = null; }
  else if (event === 'work') applyWork(d);
  else if (event === 'spectrum') window.dispatchEvent(new CustomEvent('phonebox-spectrum', { detail: d }));
  else if (event === 'event') { if (evPre) { evPre.push(d); if (evPre.length > 100) evPre.shift(); } else evOffer(d); }
};
export const streaming = () => streamOn && Date.now() - lastStateAt < 3000;

export async function poll() {
  // a pending replay must go through even while the stream is live: the stream only sends events newer than
  // its own connect, so the poll is the only way to get the ones the restarted agent queued before it
  if (!streaming() || evReplay) {
    const replay = evReplay, gen = evGen;
    try {
      const s = await api('/state' + (replay ? '?ev=0' : evSeq != null ? '?ev=' + evSeq : ''));
      // disarm only when this replay's own answer applied: a stale answer or a restart armed meanwhile keeps it
      // on, and so does an answer that ends before the seqs pushed out of the full buffer — the next pass gets them
      if (applyState(s, replay ? gen : null) && replay && gen === evGen) { evReplay = s.evSeq != null && s.evSeq < evDrop; evDrop = 0; if (!evReplay) evDrain(); }
    }
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
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && lastHour12 !== null && hour12() !== lastHour12) {
    buildLayout();
    renderRulesBtn();
    $('nClock').textContent = fmtHM(new Date());
  }
});
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

// index.html's boot watchdog waits for this: reaching the end of the module means the panel got through its first
// render, so a throw anywhere above shows up as a message there instead of a black screen.
window.__pbUp = true;
// A slow module graph can still arrive after the watchdog spoke: with the boot warning left in place the panel would
// report a failure while working normally.
if (window.__pbClear) window.__pbClear('the panel did not finish booting');
