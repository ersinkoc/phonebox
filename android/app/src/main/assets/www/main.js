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
import './widgets/steam-library.js';
import './widgets/lyrics.js';

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
/** A panel literal translated by its English text (i18n.js PANEL); English when the language has no entry. */
export const tp = (text) => (I18n && I18n.tp ? I18n.tp(text) : text);
/** tp() with {name} placeholders filled in after the lookup, so the English text with its placeholders is the key. */
export const tpv = (text, vars) => Object.entries(vars).reduce((s, [k, v]) => s.split(`{${k}}`).join(String(v)), tp(text));
/** A quota label or note the agent sends in English ("5-hour session", "Weekly · Opus", "Credits: 1,250"), in the panel
 *  language where it has the words; a model or group name in it stays as it is. */
export function quotaText(s) {
  if (!s || typeof s !== 'string') return s;
  const exact = tp(s);
  if (exact !== s) return exact;
  let m;
  if ((m = /^(.*?) · (.*)$/.exec(s))) return `${quotaText(m[1])} · ${m[2]}`;
  if ((m = /^(.*?) \((.*)\)$/.exec(s))) return `${quotaText(m[1])} (${m[2]})`;
  if ((m = /^(\d+(?:\.\d+)?)-hour$/.exec(s))) return tpv('{n}-hour', { n: m[1] });
  if ((m = /^(\d+)-day$/.exec(s))) return tpv('{n}-day', { n: m[1] });
  if ((m = /^Credits: (.*)$/.exec(s))) return tpv('Credits: {n}', { n: m[1] === 'unlimited' ? tp('unlimited') : m[1] });
  return s;
}
export let LOCALE = I18n ? I18n.getLocale() : 'en-GB';
export const COLS = 12;
export const ROWS = 6;
export const $ = (id) => document.getElementById(id);
// The native bridge (window.Android) is in every frame, sandboxed widgets included, so each call carries a key that only
// the panel's own frames receive (MainActivity.bridgeKey). This wrapper adds it.
const BRIDGE = ['battery', 'buzz', 'haptic', 'is24Hour', 'pcs', 'selectPc', 'forgetPc', 'setBrightness', 'setHomeScreen', 'setOrientation',
  'setPinned', 'setVolumeKeys', 'setLanguage', 'transport', 'sensors', 'version', 'canListen', 'listen', 'stopListening', 'cancelListening', 'trackpad', 'canWake', 'wakePc'];
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
  weather: { city: 'Tallinn', lat: 59.437, lon: 24.745, unit: 'celsius', model: '' },
  night: { enabled: true, start: '00:30', end: '07:30', brightness: 0.02 },
  display: { showSeconds: true, idleReturnSec: 90, vibrate: true, finishToast: true, volumeKeys: true, nightWhenUnreachable: false, unreachableNightDelayMin: 5, pageBar: 'dots', pageBarSide: 'bottom' },
  opencode: { alwaysCountdownSec: 10 },
  pages: FALLBACK_PAGES,
  shortcuts: [],
};

export const store = {
  get(k, d) { try { const v = localStorage.getItem(k); return v ? JSON.parse(v) : d; } catch (_) { return d; } },
  set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (_) {} },
};

export let cfg = PREVIEW ? { ...DEFAULT_CFG, pages: [] } : { ...DEFAULT_CFG, ...store.get('cfg', {}) };
export let cfgVersion = null;
export function setCfgVersion(v) { if (v != null) cfgVersion = v; }
let configRequest = 0;
let appliedConfigRequest = 0;
let writeBarrier = 0; // config reads numbered below this started before the panel's own last write
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
      if (!r.ok) throw Object.assign(new Error('HTTP ' + r.status), { status: r.status });
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
// One AudioContext for the whole panel: Chrome limits how many can be open, so a new one per chime would run out.
// It is suspended a second after the last tone, so a panel that runs all day doesn't keep an audio output open.
let chimeCtx = null, chimeIdle = 0;
/**
 * A short tone for timers, made with Web Audio so the app needs no sound file. Silent on the night screen, like
 * buzz(). volume is a percentage (10–100); finished plays a rising three-note pattern instead of two beeps.
 */
export const chime = (volume, finished = false) => {
  try {
    if (nightOn) return;
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    if (!chimeCtx) chimeCtx = new Ctx();
    // a context made without a tap starts suspended in a desktop browser; the phone's WebView allows playback
    if (chimeCtx.state === 'suspended') { const r = chimeCtx.resume(); if (r && r.catch) r.catch(() => {}); }
    const peak = 0.4 * Math.min(100, Math.max(10, +volume || 60)) / 100; // below 1, so two notes never clip
    const notes = finished ? [660, 880, 1100] : [880, 880];
    const t0 = chimeCtx.currentTime + 0.02;
    notes.forEach((freq, i) => {
      const at = t0 + i * 0.25, osc = chimeCtx.createOscillator(), gain = chimeCtx.createGain();
      osc.type = 'sine';
      osc.frequency.setValueAtTime(freq, at);
      gain.gain.setValueAtTime(0.0001, at);
      gain.gain.linearRampToValueAtTime(peak, at + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.0001, at + 0.22); // a quick fade-out, no click at the end
      osc.connect(gain);
      gain.connect(chimeCtx.destination);
      osc.start(at);
      osc.stop(at + 0.24);
    });
    clearTimeout(chimeIdle);
    const ctx = chimeCtx;
    chimeIdle = setTimeout(() => {
      try { if (ctx.state === 'running' && ctx.suspend) { const r = ctx.suspend(); if (r && r.catch) r.catch(() => {}); } } catch (_) {}
    }, (notes.length * 0.25 + 1) * 1000);
  } catch (_) {}
};
export const transport = () => { try { return bridge ? bridge.transport() : ''; } catch (_) { return ''; } };

/** h('div', 'cls', child, 'text', [more]) */
export function h(tag, cls, ...kids) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  for (const k of kids.flat(3)) if (k != null && k !== false) e.append(k.nodeType ? k : String(k));
  return e;
}

/** Built-in English text (a default widget title) marked lang="en": the panel's <html lang> follows the chosen
 *  language, and text-transform: uppercase would otherwise apply that language's case rules to English words
 *  (in Turkish "Notifications" becomes "NOTİFİCATİONS"). Titles the user typed are left as they are. */
export function en(text) {
  const e = document.createElement('span');
  // a title the panel language has a translation for is that language's text, not English
  const local = tp(text);
  if (local !== text) { e.textContent = local; return e; }
  e.lang = 'en';
  e.textContent = text;
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
  monitor: 'M2 3h20v14H2zM8 21h8M12 17v4',
  pause: 'M6 4h4v16H6zM14 4h4v16h-4z',
  play: 'M6 3l14 9-14 9V3z',
  mic: 'M12 2a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3zM19 10v1a7 7 0 0 1-14 0v-1M12 18v4M8 22h8',
  'alarm-clock': 'M4 13a8 8 0 1 0 16 0a8 8 0 1 0 -16 0|M12 9v4l2 2|M5 3 2 6|m22 6-3-3|M6.38 18.7 4 21|M17.64 18.67 20 21',
  'app-window': 'M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-12a2 2 0 0 1 2 -2z|M10 4v4|M2 8h20|M6 4v4',
  'arrow-left-right': 'M8 3 4 7l4 4|M4 7h16|m16 21 4-4-4-4|M20 17H4',
  'arrow-up': 'm5 12 7-7 7 7|M12 19V5',
  'asterisk': 'M12 6v12|M17.196 9 6.804 15|m6.804 9 10.392 6',
  'battery': 'M4 7h12a2 2 0 0 1 2 2v6a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-6a2 2 0 0 1 2 -2z|M22 11L22 13',
  'bed': 'M2 4v16|M2 8h18a2 2 0 0 1 2 2v10|M2 17h20|M6 8v9',
  'bell': 'M10.268 21a2 2 0 0 0 3.464 0|M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326',
  'bell-off': 'M10.268 21a2 2 0 0 0 3.464 0|M17 17H4a1 1 0 0 1-.74-1.673C4.59 13.956 6 12.499 6 8a6 6 0 0 1 .258-1.742|m2 2 20 20|M8.668 3.01A6 6 0 0 1 18 8c0 2.687.77 4.653 1.707 6.05',
  'bot': 'M12 8V4H8|M6 8h12a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-8a2 2 0 0 1 2 -2z|M2 14h2|M20 14h2|M15 13v2|M9 13v2',
  'brush': 'm9.06 11.9 8.07-8.06a2.85 2.85 0 1 1 4.03 4.03l-8.06 8.08|M7.07 14.94c-1.66 0-3 1.35-3 3.02 0 1.33-2.5 1.52-2 2.02 1.08 1.1 2.49 2.02 4 2.02 2.2 0 4-1.8 4-4.04a3.01 3.01 0 0 0-3-3.02z',
  'calculator': 'M6 2h12a2 2 0 0 1 2 2v16a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-16a2 2 0 0 1 2 -2z|M8 6L16 6|M16 14L16 18|M16 10h.01|M12 10h.01|M8 10h.01|M12 14h.01|M8 14h.01|M12 18h.01|M8 18h.01',
  'calendar': 'M8 2v4|M16 2v4|M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z|M3 10h18',
  'calendar-days': 'M8 2v4|M16 2v4|M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z|M3 10h18|M8 14h.01|M12 14h.01|M16 14h.01|M8 18h.01|M12 18h.01|M16 18h.01',
  'camera': 'M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3l-2.5-3z|M9 13a3 3 0 1 0 6 0a3 3 0 1 0 -6 0',
  'car': 'M19 17h2c.6 0 1-.4 1-1v-3c0-.9-.7-1.7-1.5-1.9C18.7 10.6 16 10 16 10s-1.3-1.4-2.2-2.3c-.5-.4-1.1-.7-1.8-.7H5c-.6 0-1.1.4-1.4.9l-1.4 2.9A3.7 3.7 0 0 0 2 12v4c0 .6.4 1 1 1h2|M5 17a2 2 0 1 0 4 0a2 2 0 1 0 -4 0|M9 17h6|M15 17a2 2 0 1 0 4 0a2 2 0 1 0 -4 0',
  'cat': 'M12 5c.67 0 1.35.09 2 .26 1.78-2 5.03-2.84 6.42-2.26 1.4.58-.42 7-.42 7 .57 1.07 1 2.24 1 3.44C21 17.9 16.97 21 12 21s-9-3-9-7.56c0-1.25.5-2.4 1-3.44 0 0-1.89-6.42-.5-7 1.39-.58 4.72.23 6.5 2.23A9.04 9.04 0 0 1 12 5Z|M8 14v.5|M16 14v.5|M11.25 16.25h1.5L12 17l-.75-.75Z',
  'chart-column': 'M3 3v16a2 2 0 0 0 2 2h16|M18 17V9|M13 17V5|M8 17v-3',
  'circle-check': 'M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0|m9 12 2 2 4-4',
  'circle-dot': 'M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0|M11 12a1 1 0 1 0 2 0a1 1 0 1 0 -2 0',
  'circle-x': 'M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0|m15 9-6 6|m9 9 6 6',
  'clapperboard': 'M20.2 6 3 11l-.9-2.4c-.3-1.1.3-2.2 1.3-2.5l13.5-4c1.1-.3 2.2.3 2.5 1.3Z|m6.2 5.3 3.1 3.9|m12.4 3.4 3.1 4|M3 11h18v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z',
  'clock': 'M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0|M12 6L12 12L16 14',
  'clock-10': 'M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0|M12 6L12 12L8 10',
  'cloud-fog': 'M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242|M16 17H7|M17 21H9',
  'cloud-lightning': 'M6 16.326A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 .5 8.973|m13 12-3 5h4l-3 5',
  'cloud-rain': 'M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242|M16 14v6|M8 14v6|M12 16v6',
  'cloud-snow': 'M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242|M8 15h.01|M8 19h.01|M12 17h.01|M12 21h.01|M16 15h.01|M16 19h.01',
  'cloud-sun': 'M12 2v2|m4.93 4.93 1.41 1.41|M20 12h2|m19.07 4.93-1.41 1.41|M15.947 12.65a4 4 0 0 0-5.925-4.128|M13 22H7a5 5 0 1 1 4.9-6H13a3 3 0 0 1 0 6Z',
  'cloud-sun-rain': 'M12 2v2|m4.93 4.93 1.41 1.41|M20 12h2|m19.07 4.93-1.41 1.41|M15.947 12.65a4 4 0 0 0-5.925-4.128|M3 20a5 5 0 1 1 8.9-4H13a3 3 0 0 1 2 5.24|M11 20v2|M7 19v2',
  'cloudy': 'M17.5 21H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z|M22 10a3 3 0 0 0-3-3h-2.207a5.502 5.502 0 0 0-10.702.5',
  'coffee': 'M10 2v2|M14 2v2|M16 8a1 1 0 0 1 1 1v8a4 4 0 0 1-4 4H7a4 4 0 0 1-4-4V9a1 1 0 0 1 1-1h14a4 4 0 1 1 0 8h-1|M6 2v2',
  'coins': 'M2 8a6 6 0 1 0 12 0a6 6 0 1 0 -12 0|M18.09 10.37A6 6 0 1 1 10.34 18|M7 6h1v4|m16.71 13.88.7.71-2.82 2.82',
  'container': 'M22 7.7c0-.6-.4-1.2-.8-1.5l-6.3-3.9a1.72 1.72 0 0 0-1.7 0l-10.3 6c-.5.2-.9.8-.9 1.4v6.6c0 .5.4 1.2.8 1.5l6.3 3.9a1.72 1.72 0 0 0 1.7 0l10.3-6c.5-.3.9-1 .9-1.5Z|M10 21.9V14L2.1 9.1|m10 14 11.9-6.9|M14 19.8v-8.1|M18 17.5V9.4',
  'door-open': 'M13 4h3a2 2 0 0 1 2 2v14|M2 20h3|M13 20h9|M10 12v.01|M13 4.562v16.157a1 1 0 0 1-1.242.97L5 20V5.562a2 2 0 0 1 1.515-1.94l4-1A2 2 0 0 1 13 4.561Z',
  'droplet': 'M12 22a7 7 0 0 0 7-7c0-2-1-3.9-3-5.5s-3.5-4-4-6.5c-.5 2.5-2 4.9-4 6.5C6 11.1 5 13 5 15a7 7 0 0 0 7 7z',
  'egg-fried': 'M8 12.5a3.5 3.5 0 1 0 7 0a3.5 3.5 0 1 0 -7 0|M3 8c0-3.5 2.5-6 6.5-6 5 0 4.83 3 7.5 5s5 2 5 6c0 4.5-2.5 6.5-7 6.5-2.5 0-2.5 2.5-6 2.5s-7-2-7-5.5c0-3 1.5-3 1.5-5C3.5 10 3 9 3 8Z',
  'eye': 'M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0|M9 12a3 3 0 1 0 6 0a3 3 0 1 0 -6 0',
  'fan': 'M10.827 16.379a6.082 6.082 0 0 1-8.618-7.002l5.412 1.45a6.082 6.082 0 0 1 7.002-8.618l-1.45 5.412a6.082 6.082 0 0 1 8.618 7.002l-5.412-1.45a6.082 6.082 0 0 1-7.002 8.618l1.45-5.412Z|M12 12v.01',
  'flame': 'M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.072-2.143-.224-4.054 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.153.433-2.294 1-3a2.5 2.5 0 0 0 2.5 2.5z',
  'flask-conical': 'M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2|M6.453 15h11.094|M8.5 2h7',
  'flower-2': 'M12 5a3 3 0 1 1 3 3m-3-3a3 3 0 1 0-3 3m3-3v1M9 8a3 3 0 1 0 3 3M9 8h1m5 0a3 3 0 1 1-3 3m3-3h-1m-2 3v-1|M10 8a2 2 0 1 0 4 0a2 2 0 1 0 -4 0|M12 10v12|M12 22c4.2 0 7-1.667 7-5-4.2 0-7 1.667-7 5Z|M12 22c-4.2 0-7-1.667-7-5 4.2 0 7 1.667 7 5Z',
  'folder': 'M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z',
  'footprints': 'M4 16v-2.38C4 11.5 2.97 10.5 3 8c.03-2.72 1.49-6 4.5-6C9.37 2 10 3.8 10 5.5c0 3.11-2 5.66-2 8.68V16a2 2 0 1 1-4 0Z|M20 20v-2.38c0-2.12 1.03-3.12 1-5.62-.03-2.72-1.49-6-4.5-6C14.63 6 14 7.8 14 9.5c0 3.11 2 5.66 2 8.68V20a2 2 0 1 0 4 0Z|M16 17h4|M4 13h4',
  'gamepad-2': 'M6 11L10 11|M8 9L8 13|M15 12L15.01 12|M18 10L18.01 10|M17.32 5H6.68a4 4 0 0 0-3.978 3.59c-.006.052-.01.101-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258-.007-.05-.011-.1-.017-.151A4 4 0 0 0 17.32 5z',
  'github': 'M15 22v-4a4.8 4.8 0 0 0-1-3.5c3 0 6-2 6-5.5.08-1.25-.27-2.48-1-3.5.28-1.15.28-2.35 0-3.5 0 0-1 0-3 1.5-2.64-.5-5.36-.5-8 0C6 2 5 2 5 2c-.3 1.15-.3 2.35 0 3.5A5.403 5.403 0 0 0 4 9c0 3.5 3 5.5 6 5.5-.39.49-.68 1.05-.85 1.65-.17.6-.22 1.23-.15 1.85v4|M9 18c-4.51 2-5-2-7-2',
  'globe': 'M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0|M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20|M2 12h20',
  'hand': 'M18 11V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2|M14 10V4a2 2 0 0 0-2-2a2 2 0 0 0-2 2v2|M10 10.5V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2v8|M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15',
  'hard-drive': 'M22 12L2 12|M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z|M6 16L6.01 16|M10 16L10.01 16',
  'hash': 'M4 9L20 9|M4 15L20 15|M10 3L8 21|M16 3L14 21',
  'headphones': 'M3 14h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a9 9 0 0 1 18 0v7a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3',
  'house': 'M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8|M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z',
  'image': 'M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z|M7 9a2 2 0 1 0 4 0a2 2 0 1 0 -4 0|m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21',
  'keyboard': 'M10 8h.01|M12 12h.01|M14 8h.01|M16 12h.01|M18 8h.01|M6 8h.01|M7 16h10|M8 12h.01|M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-12a2 2 0 0 1 2 -2z',
  'laptop': 'M20 16V7a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v9m16 0H4m16 0 1.28 2.55a1 1 0 0 1-.9 1.45H3.62a1 1 0 0 1-.9-1.45L4 16',
  'leaf': 'M11 20A7 7 0 0 1 9.8 6.1C15.5 5 17 4.48 19 2c1 2 2 4.18 2 8 0 5.5-4.78 10-10 10Z|M2 21c0-3 1.85-5.36 5.08-6C9.5 14.52 12 13 13 12',
  'lightbulb': 'M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5|M9 18h6|M10 22h4',
  'lock': 'M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-7a2 2 0 0 1 2 -2z|M7 11V7a5 5 0 0 1 10 0v4',
  'lock-open': 'M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-7a2 2 0 0 1 2 -2z|M7 11V7a5 5 0 0 1 9.9-1',
  'mail': 'M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-12a2 2 0 0 1 2 -2z|m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7',
  'mailbox': 'M22 17a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9.5C2 7 4 5 6.5 5H18c2.2 0 4 1.8 4 4v8Z|M15 9L18 9L18 11|M6.5 5C9 5 11 7 11 9.5V17a2 2 0 0 1-2 2|M6 10L7 10',
  'message-square': 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z',
  'moon': 'M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z',
  'mouse': 'M12 2h0a7 7 0 0 1 7 7v6a7 7 0 0 1 -7 7h-0a7 7 0 0 1 -7 -7v-6a7 7 0 0 1 7 -7z|M12 6v4',
  'music': 'M9 18V5l12-2v13|M3 18a3 3 0 1 0 6 0a3 3 0 1 0 -6 0|M15 16a3 3 0 1 0 6 0a3 3 0 1 0 -6 0',
  'newspaper': 'M4 22h16a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2H8a2 2 0 0 0-2 2v16a2 2 0 0 1-2 2Zm0 0a2 2 0 0 1-2-2v-9c0-1.1.9-2 2-2h2|M18 14h-8|M15 18h-5|M10 6h8v4h-8V6Z',
  'notebook-pen': 'M13.4 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7.4|M2 6h4|M2 10h4|M2 14h4|M2 18h4|M21.378 5.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z',
  'package': 'M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z|M12 22V12|m3.3 7 7.703 4.734a2 2 0 0 0 1.994 0L20.7 7|m7.5 4.27 9 5.15',
  'pen': 'M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z',
  'person-standing': 'M11 5a1 1 0 1 0 2 0a1 1 0 1 0 -2 0|m9 20 3-6 3 6|m6 8 6 2 6-2|M12 10v4',
  'phone': 'M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z',
  'plug': 'M12 22v-5|M9 8V2|M15 8V2|M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z',
  'plus': 'M5 12h14|M12 5v14',
  'printer': 'M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2|M6 9V3a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v6|M7 14h10a1 1 0 0 1 1 1v6a1 1 0 0 1 -1 1h-10a1 1 0 0 1 -1 -1v-6a1 1 0 0 1 1 -1z',
  'radio': 'M4.9 19.1C1 15.2 1 8.8 4.9 4.9|M7.8 16.2c-2.3-2.3-2.3-6.1 0-8.5|M10 12a2 2 0 1 0 4 0a2 2 0 1 0 -4 0|M16.2 7.8c2.3 2.3 2.3 6.1 0 8.5|M19.1 4.9C23 8.8 23 15.1 19.1 19',
  'radio-tower': 'M4.9 16.1C1 12.2 1 5.8 4.9 1.9|M7.8 4.7a6.14 6.14 0 0 0-.8 7.5|M10 9a2 2 0 1 0 4 0a2 2 0 1 0 -4 0|M16.2 4.8c2 2 2.26 5.11.8 7.47|M19.1 1.9a9.96 9.96 0 0 1 0 14.1|M9.5 18h5|m8 22 4-11 4 11',
  'rocket': 'M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z|m12 15-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z|M9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0|M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5',
  'scissors': 'M3 6a3 3 0 1 0 6 0a3 3 0 1 0 -6 0|M8.12 8.12 12 12|M20 4 8.12 15.88|M3 18a3 3 0 1 0 6 0a3 3 0 1 0 -6 0|M14.8 14.8 20 20',
  'scroll-text': 'M15 12h-5|M15 8h-5|M19 17V5a2 2 0 0 0-2-2H4|M8 21h12a2 2 0 0 0 2-2v-1a1 1 0 0 0-1-1H11a1 1 0 0 0-1 1v1a2 2 0 1 1-4 0V5a2 2 0 1 0-4 0v2a1 1 0 0 0 1 1h3',
  'settings': 'M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z|M9 12a3 3 0 1 0 6 0a3 3 0 1 0 -6 0',
  'shower-head': 'm4 4 2.5 2.5|M13.5 6.5a4.95 4.95 0 0 0-7 7|M15 5 5 15|M14 17v.01|M10 16v.01|M13 13v.01|M16 10v.01|M11 20v.01|M17 14v.01|M20 11v.01',
  'signal': 'M2 20h.01|M7 20v-4|M12 20v-8|M17 20V8|M22 4v16',
  'skip-back': 'M19 20L9 12L19 4L19 20z|M5 19L5 5',
  'skip-forward': 'M5 4L15 12L5 20L5 4z|M19 5L19 19',
  'sliders-horizontal': 'M21 4L14 4|M10 4L3 4|M21 12L12 12|M8 12L3 12|M21 20L16 20|M12 20L3 20|M14 2L14 6|M8 10L8 14|M16 18L16 22',
  'smartphone': 'M7 2h10a2 2 0 0 1 2 2v16a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-16a2 2 0 0 1 2 -2z|M12 18h.01',
  'snowflake': 'M2 12L22 12|M12 2L12 22|m20 16-4-4 4-4|m4 8 4 4-4 4|m16 4-4 4-4-4|m8 20 4-4 4 4',
  'sofa': 'M20 9V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v3|M2 16a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5a2 2 0 0 0-4 0v1.5a.5.5 0 0 1-.5.5h-11a.5.5 0 0 1-.5-.5V11a2 2 0 0 0-4 0z|M4 18v2|M20 18v2|M12 4v9',
  'sprout': 'M7 20h10|M10 20c5.5-2.5.8-6.4 3-10|M9.5 9.4c1.1.8 1.8 2.2 2.3 3.7-2 .4-3.5.4-4.8-.3-1.2-.6-2.3-1.9-3-4.2 2.8-.5 4.4 0 5.5.8z|M14.1 6a7 7 0 0 0-1.1 4c1.9-.1 3.3-.6 4.3-1.4 1-1 1.6-2.3 1.7-4.6-2.7.1-4 1-4.9 2z',
  'square': 'M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z',
  'star': 'M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.123 2.123 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.123 2.123 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.122 2.122 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.122 2.122 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.122 2.122 0 0 0 1.597-1.16z',
  'sun': 'M8 12a4 4 0 1 0 8 0a4 4 0 1 0 -8 0|M12 2v2|M12 20v2|m4.93 4.93 1.41 1.41|m17.66 17.66 1.41 1.41|M2 12h2|M20 12h2|m6.34 17.66-1.41 1.41|m19.07 4.93-1.41 1.41',
  'sunrise': 'M12 2v8|m4.93 10.93 1.41 1.41|M2 18h2|M20 18h2|m19.07 10.93-1.41 1.41|M22 22H2|m8 6 4-4 4 4|M16 18a4 4 0 0 0-8 0',
  'target': 'M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0|M6 12a6 6 0 1 0 12 0a6 6 0 1 0 -12 0|M10 12a2 2 0 1 0 4 0a2 2 0 1 0 -4 0',
  'thermometer': 'M14 4v10.54a4 4 0 1 1-4 0V4a2 2 0 0 1 4 0Z',
  'timer': 'M10 2L14 2|M12 14L15 11|M4 14a8 8 0 1 0 16 0a8 8 0 1 0 -16 0',
  'trending-up': 'M22 7L13.5 15.5L8.5 10.5L2 17|M16 7L22 7L22 13',
  'tv': 'M4 7h16a2 2 0 0 1 2 2v11a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-11a2 2 0 0 1 2 -2z|M17 2L12 7L7 2',
  'type': 'M4 7L4 4L20 4L20 7|M9 20L15 20|M12 4L12 20',
  'user': 'M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2|M8 7a4 4 0 1 0 8 0a4 4 0 1 0 -8 0',
  'vibrate': 'm2 8 2 2-2 2 2 2-2 2|m22 8-2 2 2 2-2 2 2 2|M9 5h6a1 1 0 0 1 1 1v12a1 1 0 0 1 -1 1h-6a1 1 0 0 1 -1 -1v-12a1 1 0 0 1 1 -1z',
  'video': 'm16 13 5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5|M4 6h10a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-8a2 2 0 0 1 2 -2z',
  'volume-2': 'M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z|M16 9a5 5 0 0 1 0 6|M19.364 18.364a9 9 0 0 0 0-12.728',
  'volume-x': 'M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z|M22 9L16 15|M16 9L22 15',
  'washing-machine': 'M3 6h3|M17 6h.01|M5 2h14a2 2 0 0 1 2 2v16a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-16a2 2 0 0 1 2 -2z|M7 13a5 5 0 1 0 10 0a5 5 0 1 0 -10 0|M12 18a2.5 2.5 0 0 0 0-5 2.5 2.5 0 0 1 0-5',
  'wind': 'M12.8 19.6A2 2 0 1 0 14 16H2|M17.5 8a2.5 2.5 0 1 1 2 4H2|M9.8 4.4A2 2 0 1 1 11 8H2',
  'wrench': 'M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z',
  'zap': 'M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z',
  'flag': 'M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z|M4 22L4 15',
  'rotate-ccw': 'M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8|M3 3v5h5',
  'calendar-range': 'M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z|M16 2v4|M3 10h18|M8 2v4|M17 14h-6|M13 18H7|M7 14h.01|M17 18h.01',
  'compass': 'm16.24 7.76-1.804 5.411a2 2 0 0 1-1.265 1.265L7.76 16.24l1.804-5.411a2 2 0 0 1 1.265-1.265z|M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0',
  'fast-forward': 'M13 19L22 12L13 5L13 19z|M2 19L11 12L2 5L2 19z',
  'folder-open': 'm6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2',
  'folders': 'M20 17a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3.9a2 2 0 0 1-1.69-.9l-.81-1.2a2 2 0 0 0-1.67-.9H8a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2Z|M2 8v11a2 2 0 0 0 2 2h14',
  'music-2': 'M4 18a4 4 0 1 0 8 0a4 4 0 1 0 -8 0|M12 18V2l7 4',
  'palette': 'M13 6.5a0.5 0.5 0 1 0 1 0a0.5 0.5 0 1 0 -1 0|M17 10.5a0.5 0.5 0 1 0 1 0a0.5 0.5 0 1 0 -1 0|M8 7.5a0.5 0.5 0 1 0 1 0a0.5 0.5 0 1 0 -1 0|M6 12.5a0.5 0.5 0 1 0 1 0a0.5 0.5 0 1 0 -1 0|M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10c.926 0 1.648-.746 1.648-1.688 0-.437-.18-.835-.437-1.125-.29-.289-.438-.652-.438-1.125a1.64 1.64 0 0 1 1.668-1.668h1.996c3.051 0 5.555-2.503 5.555-5.554C21.965 6.012 17.461 2 12 2z',
  'piano': 'M18.5 8c-1.4 0-2.6-.8-3.2-2A6.87 6.87 0 0 0 2 9v11a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-8.5C22 9.6 20.4 8 18.5 8|M2 14h20|M6 14v4|M10 14v4|M14 14v4|M18 14v4',
  'projector': 'M5 7 3 5|M9 6V3|m13 7 2-2|M6 13a3 3 0 1 0 6 0a3 3 0 1 0 -6 0|M11.83 12H20a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2h2.17|M16 16h2',
  'puzzle': 'M15.39 4.39a1 1 0 0 0 1.68-.474 2.5 2.5 0 1 1 3.014 3.015 1 1 0 0 0-.474 1.68l1.683 1.682a2.414 2.414 0 0 1 0 3.414L19.61 15.39a1 1 0 0 1-1.68-.474 2.5 2.5 0 1 0-3.014 3.015 1 1 0 0 1 .474 1.68l-1.683 1.682a2.414 2.414 0 0 1-3.414 0L8.61 19.61a1 1 0 0 0-1.68.474 2.5 2.5 0 1 1-3.014-3.015 1 1 0 0 0 .474-1.68l-1.683-1.682a2.414 2.414 0 0 1 0-3.414L4.39 8.61a1 1 0 0 1 1.68.474 2.5 2.5 0 1 0 3.014-3.015 1 1 0 0 1-.474-1.68l1.683-1.682a2.414 2.414 0 0 1 3.414 0z',
  'shuffle': 'm18 14 4 4-4 4|m18 2 4 4-4 4|M2 18h1.973a4 4 0 0 0 3.3-1.7l5.454-8.6a4 4 0 0 1 3.3-1.7H22|M2 6h1.972a4 4 0 0 1 3.6 2.2|M22 18h-6.041a4 4 0 0 1-3.3-1.8l-.359-.45',
  'sliders-vertical': 'M4 21L4 14|M4 10L4 3|M12 21L12 12|M12 8L12 3|M20 21L20 16|M20 12L20 3|M2 14L6 14|M10 8L14 8|M18 16L22 16',
  'trash-2': 'M3 6h18|M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6|M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2|M10 11L10 17|M14 11L14 17',
  'umbrella': 'M22 12a10.06 10.06 1 0 0-20 0Z|M12 12v8a2 2 0 0 0 4 0|M12 2v1',
  'volume-1': 'M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z|M16 9a5 5 0 0 1 0 6',
};

// Lucide shapes with several elements are stored as one path per element, joined with '|'.
export function icon(name, cls = '') {
  const t = document.createElement('template');
  const paths = (ICONS[name] || '').split('|').map((d) => `<path d="${d}"/>`).join('');
  t.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths}</svg>`;
  const s = t.content.firstElementChild;
  s.setAttribute('class', 'ico' + (cls ? ' ' + cls : ''));
  s.dataset.icon = name;
  return s;
}

/* Emoji → Lucide icon. Saved configs (shortcut icons, Home Assistant icons, widget catalog) and
   translations still hold emoji as plain text; every place that shows one goes through emo()/rich(),
   so the panel draws the same line icons everywhere and unknown characters (a custom emoji or text) stay as typed. */
const EMOJI_ICON = { '🏠': 'house', '☀': 'sun', '🌙': 'moon', '🌤': 'cloud-sun', '⛅': 'cloud-sun', '☁': 'cloudy', '🌫': 'cloud-fog', '🌦': 'cloud-sun-rain', '🌧': 'cloud-rain', '🌨': 'cloud-snow', '❄': 'snowflake', '⛈': 'cloud-lightning', '🌡': 'thermometer', '📱': 'smartphone', '⚡': 'zap', '📹': 'video', '🎙': 'mic', '📶': 'signal', '🎛': 'sliders-horizontal', '💬': 'message-square', '🔋': 'battery', '⏭': 'skip-forward', '⏮': 'skip-back', '⏯': 'play', '🔤': 'type', '🕒': 'clock', '🎵': 'music', '📈': 'trending-up', '⏲': 'timer', '⏱': 'timer', '📊': 'chart-column', '💾': 'hard-drive', '🎮': 'gamepad-2', '✳': 'asterisk', '🤖': 'bot', '🐙': 'github', '⚙': 'settings', '🔘': 'circle-dot', '🌍': 'globe', '🌐': 'globe', '📅': 'calendar', '🍅': 'timer', '⏳': 'hourglass', '📝': 'notebook-pen', '💻': 'laptop', '📡': 'radio-tower', '📬': 'mailbox', '🕰': 'alarm-clock', '📆': 'calendar-days', '🔢': 'hash', '🫁': 'wind', '✅': 'circle-check', '🔥': 'flame', '🎯': 'target', '🔊': 'volume-2', '🌅': 'sunrise', '🍃': 'leaf', '📰': 'newspaper', '🗞': 'newspaper', '🖼': 'image', '💱': 'arrow-left-right', '🪙': 'coins', '🖥': 'monitor', '🎧': 'headphones', '🖱': 'mouse', '⌨': 'keyboard', '🖊': 'pen', '🔇': 'volume-x', '🔕': 'bell-off', '📞': 'phone', '💡': 'lightbulb', '🔌': 'plug', '🌀': 'fan', '🪟': 'app-window', '📜': 'scroll-text', '🎬': 'clapperboard', '🧑': 'user', '🧹': 'brush', '📷': 'camera', '💧': 'droplet', '🚪': 'door-open', '🏃': 'footprints', '🧍': 'person-standing', '🫧': 'cloudy', '🔒': 'lock', '📳': 'vibrate', '🔓': 'lock-open', '⚠': 'warn', '🔴': 'radio', '🕙': 'clock-10', '👀': 'eye', '❌': 'circle-x', '⏸': 'pause', '🔔': 'bell', '📧': 'mail', '🧮': 'calculator', '✂': 'scissors', '🚀': 'rocket', '🐳': 'container', '🧪': 'flask-conical', '🛠': 'wrench', '📦': 'package', '⭐': 'star', '🦊': 'cat', '📁': 'folder', '📺': 'tv', '🚿': 'shower-head', '🛋': 'sofa', '🛏': 'bed', '🍳': 'egg-fried', '☕': 'coffee', '🌱': 'sprout', '🪴': 'flower-2', '🚗': 'car', '🧺': 'washing-machine', '🖨': 'printer', '📻': 'radio', '➕': 'plus', '⬆': 'arrow-up', '◻': 'square', '👋': 'hand', '🧭': 'compass', '🗓': 'calendar-range', '🏖': 'umbrella', '🧩': 'puzzle', '🗂': 'folders', '🎚': 'sliders-vertical', '🔉': 'volume-1', '⏩': 'fast-forward', '🔀': 'shuffle', '📂': 'folder-open', '📽': 'projector', '🎶': 'music-2', '🎹': 'piano', '🎨': 'palette', '🗑': 'trash-2' };
// A mapped emoji only counts when it stands alone: not part of a longer sequence (skin tone, joined with U+200D, or a combining mark: keycap, either variation selector); a selector that is not ours to take must not be given back.
const EMOJI_RE = new RegExp('(?<!\\u200D)(' + Object.keys(EMOJI_ICON).map((k) => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|') + ')\uFE0F?(?![\\u{1F3FB}-\\u{1F3FF}\\u200D\\p{M}])', 'gu');
export const emojiIcon = (e) => EMOJI_ICON[String(e == null ? '' : e).replace(/\uFE0F/g, '').trim()];
/** A node for one emoji: its icon when we have one, otherwise the text as typed. */
/** Names a control that holds only an icon (the icons are aria-hidden), and gives it a tooltip. */
export function labelled(el, text) { el.setAttribute('aria-label', text); el.title = text; return el; }
export function emo(e, cls = '') { const n = emojiIcon(e); return n ? icon(n, cls) : document.createTextNode(e == null ? '' : String(e)); }
/** A text with every known emoji swapped for its inline icon (a DocumentFragment, ready for append/replaceChildren). */
export function rich(text) {
  const frag = document.createDocumentFragment(), s = String(text == null ? '' : text);
  let last = 0, m;
  EMOJI_RE.lastIndex = 0;
  while ((m = EMOJI_RE.exec(s))) {
    if (m.index > last) frag.append(s.slice(last, m.index));
    frag.append(icon(EMOJI_ICON[m[1]], 'm'));
    last = m.index + m[0].length;
  }
  if (last < s.length) frag.append(s.slice(last));
  return frag;
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
  t.replaceChildren(...(title ? [h('b', null, rich(title)), msgSpan] : [msgSpan]));
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
export const gpuPower = (g) => (g?.power != null ? `${Math.round(g.power)} W` : g?.powerPct != null ? tpv('{n}% power', { n: Math.round(g.powerPct) }) : null);
export const netMax = () => Math.max(1, ...hist.down, ...hist.up) * 1.15;

export const METRICS = {
  cpu: { label: 'CPU', name: 'Processor', color: '--cpu', unit: '%', max: () => 100, val: (s) => s.pc?.cpu,
    sub: (s) => (s.pc?.cpuTemp != null ? `${Math.round(s.pc.cpuTemp)}°C` : shortCpu(s.pc?.cpuName)), title: (s) => s.pc?.cpuName },
  gpu: { label: 'GPU', name: 'Graphics', color: '--gpu', unit: '%', max: () => 100, val: (s) => gpuOf(s)?.util,
    sub: (s) => {
      const g = gpuOf(s);
      if (!g) return tp('No GPU data');
      const parts = [g.temp != null ? `${Math.round(g.temp)}°C` : null, gpuPower(g)].filter(Boolean);
      if (parts.length) return parts.join(' · ');
      const mem = gpuMem(g);
      if (mem) return tpv('{mem} memory', { mem: memText(mem) }); // no sensors (Apple GPU)
      return shortGpu(g.name) || tp('Active');
    }, title: (s) => gpuOf(s)?.name },
  ram: { label: 'RAM', name: 'Memory', color: '--ram', unit: 'GB', max: (s) => s.pc?.ramTotalGb || 1, val: (s) => s.pc?.ramUsedGb, fmt: r1,
    pct: (s) => (s.pc ? (s.pc.ramUsedGb / s.pc.ramTotalGb) * 100 : null), sub: (s) => (s.pc && s.pc.ramTotalGb ? `${r1(s.pc.ramUsedGb)} / ${Math.round(s.pc.ramTotalGb)} GB` : '') },
  vram: { label: 'VRAM', name: 'Video memory', color: '--gpu', unit: '%', max: () => 100,
    val: (s) => { const m = gpuMem(gpuOf(s)); return m ? (m.used / m.total) * 100 : null; },
    sub: (s) => { const m = gpuMem(gpuOf(s)); return m ? memText(m) : tp('No GPU data'); } },
  net: { label: 'NETWORK', name: 'Network', color: '--net', rate: true, hist: 'down', max: netMax, val: (s) => s.pc?.netDownBps,
    sub: (s) => '↑ ' + rateStr(s.pc?.netUpBps) + (s.ping?.ms != null ? ` · ${Math.round(s.ping.ms)} ms` : '') },
  down: { label: 'DOWNLOAD', name: 'Download', color: '--net', rate: true, max: netMax, val: (s) => s.pc?.netDownBps },
  up: { label: 'UPLOAD', name: 'Upload', color: '--up', rate: true, max: netMax, val: (s) => s.pc?.netUpBps },
  ping: { label: 'PING', name: 'Ping', color: '#38bdf8', unit: 'ms', max: () => Math.max(50, ...hist.ping) * 1.2, val: (s) => s.ping?.ms,
    sub: (s) => (s.ping ? tpv('avg {avg} ms · loss {loss}%', { avg: s.ping.avgMs ?? '—', loss: s.ping.lossPct }) : '') },
  gputemp: { label: 'GPU TEMP', name: 'GPU temperature', color: '#f97316', unit: '°C', max: () => 100, hot: [75, 85], val: (s) => gpuOf(s)?.temp,
    sub: (s) => {
      const g = gpuOf(s);
      if (!g) return tp('No GPU data');
      const detail = [g.hotSpot != null ? tpv('Hot spot {n}°', { n: Math.round(g.hotSpot) }) : null,
        g.fan != null ? tpv('Fan {n}%', { n: Math.round(g.fan) }) : g.fanRpm != null ? tpv('Fan {n} rpm', { n: Math.round(g.fanRpm) }) : null,
        g.clock != null ? `${Math.round(g.clock)} MHz` : null].filter(Boolean);
      return detail.length ? detail.join(' · ') : (g.temp != null ? tp('Temperature') : tp('No GPU data'));
    } },
  hotspot: { label: 'HOT SPOT', name: 'GPU hot spot', color: '#ef4444', unit: '°C', max: () => 110, hot: [90, 100], val: (s) => gpuOf(s)?.hotSpot,
    sub: (s) => {
      const g = gpuOf(s);
      if (g?.hotSpot == null) return tp('needs LibreHardwareMonitor');
      return [g.temp != null ? tpv('Edge {n}°', { n: Math.round(g.temp) }) : null, g.memTemp != null ? tpv('Memory {n}°', { n: Math.round(g.memTemp) }) : null].filter(Boolean).join(' · ');
    } },
  gpupower: { label: 'GPU POWER', name: 'GPU power', color: '#facc15', unit: (s) => (gpuOf(s)?.power != null ? 'W' : '%'),
    max: (s) => { const g = gpuOf(s); return g?.power != null ? Math.max(g.powerLimit || 0, ...hist.gpupower, 50) : 100; },
    val: (s) => { const g = gpuOf(s); return g?.power ?? g?.powerPct; },
    sub: (s) => {
      const g = gpuOf(s);
      if (!g || (g.power == null && g.powerPct == null)) return tp('No power data');
      if (g.power != null && g.powerLimit) return tpv('of {n} W limit', { n: Math.round(g.powerLimit) });
      return g.power != null ? shortGpu(g.name) : tp('of the power limit');
    } },
  gpuclock: { label: 'GPU CLOCK', name: 'GPU clock', color: '#a78bfa', unit: 'MHz', max: () => Math.max(1000, ...hist.gpuclock) * 1.1, val: (s) => gpuOf(s)?.clock,
    sub: (s) => { const g = gpuOf(s); return g?.memClock != null ? tpv('Memory {n} MHz', { n: Math.round(g.memClock) }) : g?.clock == null ? tp('No clock data') : ''; } },
  cputemp: { label: 'CPU TEMP', name: 'CPU temperature', color: '#fb7185', unit: '°C', max: () => 100, hot: [75, 85], val: (s) => s.pc?.cpuTemp,
    sub: (s) => (s.pc?.cpuTemp == null ? tp('needs LibreHardwareMonitor') : shortCpu(s.pc?.cpuName)) },
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
/** A temperature from Open-Meteo (always °C) rounded in the configured unit; the raw forecast stays in °C. */
export const wxDeg = (c) => Math.round(cfg.weather?.unit === 'fahrenheit' ? c * 9 / 5 + 32 : c);

let weatherRetry = 0;
let weatherFor = ''; // the location the forecast in D.weather belongs to
let unitShown = ''; // the unit the widgets last drew: a settings change redraws the cached forecast, no new request
// the model is part of the key: another model is another forecast, fetched now rather than in up to 14 minutes
const weatherKey = () => { const { lat, lon } = cfg.weather || DEFAULT_CFG.weather; const model = wxModel(); return `v3:${lat},${lon}${model ? ':' + model : ''}`; };
// the agent passes only known model names; this guard keeps a hand-edited cached config out of the URL as well
const wxModel = () => (/^[a-z0-9_]{1,40}$/.test(cfg.weather?.model || '') ? cfg.weather.model : '');
export async function loadWeather(force = false) {
  const { lat, lon, unit } = cfg.weather || DEFAULT_CFG.weather;
  const key = weatherKey();
  const cached = store.get('wx', null);
  // a new place starts from its own cache or from "Loading…", never from the previous place's forecast
  if (D.weather && weatherFor !== key) { D.weather = null; emit('weather'); }
  if (cached && cached.key === key && !D.weather) { D.weather = cached.data; weatherFor = key; emit('weather'); }
  // buildLayout keeps the widgets when the pages did not change, so a new unit must be drawn from here
  else if (D.weather && unit !== unitShown) emit('weather');
  unitShown = unit;
  if (!force && cached && cached.key === key && Date.now() - cached.at < 14 * 60_000) return;
  // only while a page shows the weather (PRIVACY.md): no widget, no request with the location;
  // also wait for the first agent config when no cached config exists, so FALLBACK_PAGES never leaks default coordinates
  if (!anyNeeds('weather') || (!PREVIEW && cfgVersion === null && !store.get('cfg', null))) return;
  try {
    const url = `https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}` +
      '&current=temperature_2m,apparent_temperature,weather_code,is_day' +
      '&hourly=temperature_2m,weather_code,precipitation_probability,is_day' +
      '&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,daylight_duration,uv_index_max' +
      '&timezone=auto&forecast_days=7' + (wxModel() ? '&models=' + wxModel() : '');
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
  const hi = d.temperature_2m_max ? wxDeg(d.temperature_2m_max[0]) : null;
  const lo = d.temperature_2m_min ? wxDeg(d.temperature_2m_min[0]) : null;
  return {
    icon: wxIcon(c.weather_code, c.is_day),
    temp: wxDeg(c.temperature_2m) + '°',
    desc: `${desc} · ${cfg.weather?.city || ''}`,
    range: (hi != null ? `↑${hi}° ↓${lo}° · ` : '') + t('wx.feelsLike', { deg: wxDeg(c.apparent_temperature) }),
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
    e.querySelector('.wx-icon').replaceChildren(emo(l.icon));
    e.querySelector('.wx-temp').textContent = l.temp;
    e.querySelector('.wx-desc').textContent = l.desc;
    e.querySelector('.wx-range').textContent = l.range;
  };
  return e;
}

// ---------------------------------------------------------------- status line (clock widget)
export function phoneBattery() {
  if (!bridge) return '';
  try { const b = JSON.parse(bridge.battery()); return rich(`📱 ${b.level}%${b.plugged ? ' ⚡' : ''}`); } catch (_) { return ''; }
}

export function quotaProviders() { return D.quotas?.providers || []; }

export function renderStatus(el, o = {}) {
  const via = transport();
  const name = online ? (D.state?.host || 'PC') + (via === 'wifi' ? ' · ' + t('status.wiFi') : via === 'usb' ? ' · ' + t('status.usb') : '') : online === false ? t('panel.pcNotConnected') : t('panel.connecting');
  const kids = [h('span', 'dot ' + (online ? 'on' : online === false ? 'off' : '')), h('span', null, name)];
  const bat = phoneBattery();
  if (bat) kids.push(h('span', 'sep', '·'), h('span', null, bat));
  for (const p of quotaProviders()) {
    const l = p.limits?.[0];
    // Clocks saved before the Providers option have none: show every provider like before.
    if (!l || (o.providers != null && !o.providers.includes(p.id))) continue;
    const s = h('span', 'qm', h('span', 'sep', '·'), p.name + ' ', h('b', null, Math.round(l.percent) + '%'));
    if (l.percent >= 95) s.style.color = '#f87171'; else if (l.percent >= 80) s.style.color = '#fbbf24';
    kids.push(s);
  }
  const waiting = (D.work?.sessions || []).filter((s) => s.status === 'waiting').length;
  if (waiting) kids.push(h('span', 'sep', '·'), h('span', 'smini', t('status.waitingCount', { n: waiting })));
  const u = D.state?.update;
  if (u) kids.push(h('span', 'sep', '·'), h('span', 'upd', rich(u.app && via !== 'usb' ? t('status.updateUsb', { version: u.latest }) : t('status.updateAvailable', { version: u.latest }))));
  el.replaceChildren(...kids);
  // Too long for the widget: drop the quota summaries, the most dispensable part.
  while (el.scrollWidth > el.clientWidth + 1 && el.querySelector('.qm')) [...el.querySelectorAll('.qm')].pop().remove();
}

// ---------------------------------------------------------------- shortcuts
export async function runShortcut(btn, id, hold = false) {
  haptic();
  btn.classList.remove('ok', 'err');
  btn.classList.add('busy');
  let ok = false;
  // hold: the agent runs the key's own long-press shortcut; the phone still sends only this key's id
  try { await api('/run/' + encodeURIComponent(id) + (hold ? '?hold=true' : ''), 'POST', 5000); ok = true; } catch (_) {}
  btn.classList.remove('busy');
  btn.classList.add(ok ? 'ok' : 'err');
  setTimeout(() => btn.classList.remove('ok', 'err'), 900);
}

/** A shortcut key. The Big button's options: with `cover`, a picture the user uploaded becomes the key's background
 *  instead of the centred icon; without `label`, the key shows no name (screen readers still get it). */
export function keyButton(s, { cover = false, label = true } = {}) {
  const icon = h('span', 'k-icon', s.icon ? rich(s.icon) : '•');
  const b = h('button', 'key' + (s.toggle ? ' toggle' : '') + (s.cycle ? ' cycle' : '') + (label ? '' : ' k-nolabel'), icon, h('span', 'k-label', s.label));
  // The agent accepts a blank label, and a filled key hides its icon, so the id keeps the button named when no text
  // is shown; visible text names it otherwise.
  const name = String(s.label || '').trim();
  if (!label || !name) b.setAttribute('aria-label', name || s.id);
  b.style.setProperty('--k', s.color || '#6b7280');
  b.dataset.id = s.id;
  b._cover = cover;
  if (s.hold) holdToRun(b, s.id);
  else b.addEventListener('click', () => runShortcut(b, s.id));
  // Only an upload fills the key: a program's own icon is 32 px on Windows, 64 px on macOS, and on Linux whatever logo
  // the icon theme has, none of them a picture the user chose for the button. The agent says which is which, since the
  // picture's size cannot tell (an SVG reports a default size, often 24 px from an icon set).
  if (s.iconRev != null) applyShortcutIcon(b, icon, s.id, cover && s.iconUpload === true);
  return b;
}

const HOLD_MS = 500, HOLD_SLOP = 12;
/** A key with a long press: a tap runs the key, holding it for half a second runs its long-press shortcut instead (and
 *  the tap that ends the hold does nothing). Moving the finger more than a few pixels turns it into a scroll, not a hold.
 *  Only the pointer that started the press counts (a second finger, or a mouse's right button, never starts or ends one). */
function holdToRun(b, id) {
  let timer = 0, held = false, owner = null, x0 = 0, y0 = 0;
  const cancel = () => { clearTimeout(timer); timer = 0; };
  b.addEventListener('pointerdown', (e) => {
    if (owner !== null || (e.pointerType === 'mouse' && e.button !== 0)) return;
    owner = e.pointerId; held = false; x0 = e.clientX; y0 = e.clientY; cancel();
    timer = setTimeout(() => { timer = 0; held = true; runShortcut(b, id, true); }, HOLD_MS);
  });
  b.addEventListener('pointermove', (e) => { if (timer && e.pointerId === owner && Math.hypot(e.clientX - x0, e.clientY - y0) > HOLD_SLOP) cancel(); });
  // the press is over when its own pointer lifts or is taken over (a scroll); `held` stays set until the click that follows
  for (const ev of ['pointerup', 'pointercancel', 'pointerleave']) b.addEventListener(ev, (e) => { if (e.pointerId === owner) { cancel(); owner = null; } });
  b.addEventListener('contextmenu', (e) => e.preventDefault()); // Android's WebView opens a menu on a long press
  b.addEventListener('click', () => { if (held) held = false; else runShortcut(b, id); });
}

/** Shows another shortcut's face on a key: a cycle key shows the key that ran last, with its icon (emoji, program icon or
 *  upload) and name; the key's own picture is taken off. Shown with the key's own shortcut it restores the key's face. */
export function setKeyFace(b, s, cover = false) {
  b.dataset.face = s.id === b.dataset.id ? '' : s.id;
  b.querySelectorAll('.k-bg, .k-shade').forEach((n) => n.remove());
  b.classList.remove('k-cover');
  const icon = b.querySelector('.k-icon');
  icon.classList.remove('k-icon-img');
  icon.replaceChildren(s.icon ? rich(s.icon) : '•');
  b.querySelector('.k-label').textContent = s.label;
  // a key that had no visible name is named by its id; the name follows the face
  if (b.hasAttribute('aria-label')) b.setAttribute('aria-label', String(s.label || '').trim() || s.id);
  if (s.iconRev != null) applyShortcutIcon(b, icon, s.id, cover && s.iconUpload === true);
}

function applyShortcutIcon(b, icon, id, cover) {
  // a picture that arrives after the key changed face (a cycle key) is not the face any more: it is dropped
  const current = () => (b.dataset.face || b.dataset.id) === id;
  const url = `${API}/shortcuts/icon?id=${encodeURIComponent(id)}`;
  const img = new Image();
  if (cover) {
    // the upload only: if it was removed since the config was read, the key falls back to the centred icon (the
    // program's own, or the emoji) instead of the agent's next choice stretched across it
    img.onload = () => {
      if (!current()) return;
      img.className = 'k-bg';
      img.alt = '';
      b.prepend(img, h('span', 'k-shade'));
      b.classList.add('k-cover');
    };
    img.onerror = () => applyShortcutIcon(b, icon, id, false);
    img.src = url + '&upload=true'; // "true", not "1": the agent binds it as a bool and answers 400 to "1"
    return;
  }
  img.onload = () => { if (!current()) return; icon.replaceChildren(img); icon.classList.add('k-icon-img'); };
  img.src = url;
}

// ---------------------------------------------------------------- media (shared state, any number of widgets)
export let media = null;          // last media state + the time it was received
export let volDragging = null;
export function setVolDragging(control, dragging) {
  if (dragging) volDragging = control;
  else if (volDragging === control) volDragging = null;
}
// the agent says how old the position was when it sent it (it was read up to a second before the 1 s tick sent it);
// counting from "that long before it arrived" keeps the bar from running behind. Capped: a bogus age must not jump it.
const mediaSampleAt = (m) => performance.now() - Math.min(Math.max(Number(m?.ageMs) || 0, 0), 5000);
// The next sample of the same track is not trusted blindly: the bar is counted on the phone between samples, and each
// sample is a second old and read at a slightly different moment, so taking it as is makes the time stutter. A bar
// within 0.3 s of the sample stays as it is, up to 1.5 s off it moves 30 % of the way, and anything bigger (a seek, a
// pause, a new track, a different source) snaps.
export function mediaFrom(prev, m) {
  if (!m) return null;
  const next = { ...m, at: mediaSampleAt(m) };
  const now = performance.now();
  // just after the user moved the position bar the agent's samples still hold the old position for a moment: keep the
  // chosen one until a sample agrees with it (or the hold runs out)
  if (prev?.hold > now && prev.title === m.title && prev.app === m.app) {
    const target = prev.posMs + (prev.playing ? now - prev.at : 0);
    if (Math.abs(next.posMs + (m.playing ? now - next.at : 0) - target) >= 1500) return { ...next, posMs: prev.posMs, at: prev.at, hold: prev.hold };
  }
  if (!prev || !prev.playing || !m.playing || prev.title !== m.title || prev.app !== m.app || prev.durMs !== m.durMs) return next;
  const shown = prev.posMs + (now - prev.at);
  const off = next.posMs + (now - next.at) - shown;
  if (Math.abs(off) < 300) return { ...next, posMs: prev.posMs, at: prev.at };
  if (Math.abs(off) < 1500) return { ...next, posMs: shown + off * 0.3, at: now };
  return next;
}
export const setMediaState = (m) => {
  if (D.state) D.state.media = m || null;
  media = mediaFrom(media, m);
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
  <div class="m-progress"><div class="bar" role="slider" tabindex="0" aria-label="Position" aria-valuemin="0"><div class="fill"></div><div class="scrub-tip" aria-hidden="true">0:00</div></div><div class="times"><span class="m-pos">0:00</span><span class="m-dur">0:00</span></div></div>
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
export const Q_STYLE = { antigravity: { color: '#38bdf8', logo: '✦' }, claude: { color: '#d97757', logo: '✳' }, codex: { color: '#e5e7eb', logo: '◎' }, cursor: { color: '#67e8f9', logo: '◆' }, grok: { color: '#f8fafc', logo: '✶' } };

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

/** Vibrates and shows a toast when a session finishes or starts waiting for input; the finish toast can be turned off. */
export function notifyTransitions(sessions) {
  for (const s of sessions) {
    const before = prevStatus.get(s.id);
    if (workLoaded && before && before !== s.status) {
      if (s.status === 'waiting') {
        buzz('0,90,90,90,90,160');
        toast(s.waitingFor || t('panel.needsInput'), 'err', s.name);
      } else if (s.status === 'idle' && (before === 'busy' || before === 'shell')) {
        buzz('0,70,110,70');
        if (cfg.display?.finishToast !== false) toast(t('panel.sessionFinished', { name: s.name }), 'ok');
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
  if (typeof x === 'boolean') return x ? tp('On') : tp('Off');
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

// ---------------------------------------------------------------- permission prompts (AI coding-agent hook → phone)
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
  if (!a) { box.hidden = true; approvalShown = null; stopAlwaysCountdown(); return; }
  if (approvalShown !== a.id) {
    approvalShown = a.id;
    lastTouch = Date.now();
    undim();
    if (nightOn) { wakeUntil = Date.now() + 60_000; setNight(false); }
    buzz('0,120,80,120,80,240');
    // A prompt whose answer is still in flight comes back locked: it can be shown again while its own post runs.
    // The Send button and the typed draft belong to the previous question: this one starts clean.
    $('apAllow').disabled = $('apDeny').disabled = $('apAlways').disabled = $('apSkip').disabled = $('apCustomSend').disabled = submitting.has(a.id);
    $('apCustomText').value = '';
    renderChoices(a);
    // Allow always saves a pattern for the whole project: give the thumb ten seconds (as configured) before it can
    // be pressed by accident.
    const countdown = Math.max(0, Math.min(60, cfg.opencode?.alwaysCountdownSec ?? 10));
    if (countdown > 0 && a.kind !== 'question' && a.always) startAlwaysCountdown(countdown); else stopAlwaysCountdown();
  }
  // A question from Claude is answered at the PC (the terminal shows it); the phone only tells you it is waiting.
  // OpenCode questions carry their answers, so the phone can send the pick — or a typed answer when allowed.
  const asking = a.kind === 'question';
  const options = asking && Array.isArray(a.options) ? a.options : [];
  $('apAllow').hidden = $('apDeny').hidden = asking;
  // "Got it" is for questions answered at the PC (Claude's, or OpenCode forms with nothing to tap); a card that
  // carries its own answers answers them right here, so the extra button would only crowd the row.
  $('apSkip').hidden = !asking || options.length > 0 || !!a.custom;
  // "Always allow" comes with OpenCode prompts (OpenCode saves the pattern for the project); the other agents have no
  // such answer and keep their two buttons. Three buttons are tighter: the countdown steps aside while the third shows.
  $('apAlways').hidden = asking || !a.always;
  box.classList.toggle('with-always', !asking && !!a.always);
  box.classList.toggle('with-choices', options.length > 0 || (asking && !!a.custom));
  $('apChoices').hidden = options.length === 0;
  $('apCustom').hidden = !(asking && a.custom);
  if (asking && a.custom) $('apCustomText').placeholder = t('panel.answerCustom');
  const source = a.source || 'AI agent';
  const sourceKind = /opencode/i.test(source) ? 'opencode' : /codex/i.test(source) ? 'codex' : /claude/i.test(source) ? 'claude' : 'agent';
  // the card wears the agent's own glyph: OpenCode's diamond (as on the sessions widget), Codex's circle, Claude's asterisk
  $('apLogo').textContent = sourceKind === 'opencode' ? '◈' : sourceKind === 'codex' ? '◎' : sourceKind === 'claude' ? '✳' : '◆';
  $('apLogo').className = `ap-logo ${sourceKind}`;
  $('apSource').textContent = source;
  $('apProject').textContent = a.project || a.session || '';
  // a long path keeps its end, which tells projects apart; the whole path is in the title
  $('apCwd').textContent = (a.cwd || '').length > 140 ? '…' + a.cwd.slice(-139) : (a.cwd || '');
  $('apCwd').title = a.cwd || '';
  $('apContextSep').hidden = !(a.cwd && (a.project || a.session));
  $('apCount').textContent = approvals.length > 1 ? t('panel.oneOfN', { n: approvals.length }) : '';
  $('apTool').textContent = a.tool;
  $('apDesc').textContent = a.description || '';
  $('apDetail').textContent = a.detail || '';
  $('apDetail').hidden = !a.detail;
  // what Allow always would save for the project, so the card says what it agrees to from then on
  const pattern = !asking && a.always && a.alwaysPattern ? t('panel.alwaysSaves', { pattern: a.alwaysPattern }) : '';
  $('apPattern').textContent = pattern;
  $('apPattern').hidden = !pattern;
  box.hidden = false;
  tickApproval();
}

// Agent questions (OpenCode forms): one button per answer. Built only when the card changes, so the five-second list
// poll never rebuilds the buttons mid-tap or disturbs the typed answer.
function renderChoices(a) {
  const wrap = $('apChoices');
  const options = a.kind === 'question' && Array.isArray(a.options) ? a.options : [];
  wrap.replaceChildren(...options.map((o) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'ap-opt';
    b.disabled = submitting.has(a.id);
    const label = document.createElement('b');
    label.textContent = o.label || o.value || '';
    b.appendChild(label);
    if (o.description) { const d = document.createElement('small'); d.textContent = o.description; b.appendChild(d); }
    b.addEventListener('click', () => decide('answer', o.value, o.label || o.value));
    return b;
  }));
}

// Allow always arms after the configured seconds (Settings › General › OpenCode prompts); the seconds show on the
// button itself. A stale countdown never unlocks a newer card (or one whose answer is on its way): every step
// re-checks which card is current.
let alwaysCountdown = null;

function startAlwaysCountdown(seconds) {
  stopAlwaysCountdown();
  const b = $('apAlways');
  const id = approvalShown;
  let left = seconds;
  b.disabled = true;
  b.textContent = `${t('panel.allowAlways')} · ${left}`;
  alwaysCountdown = setInterval(() => {
    left -= 1;
    if (left > 0 && approvalShown === id) { b.textContent = `${t('panel.allowAlways')} · ${left}`; return; }
    stopAlwaysCountdown();
    if (approvalShown === id && approvals[0] && approvals[0].id === id && !submitting.has(id)) {
      b.disabled = false;
      b.textContent = t('panel.allowAlways');
    }
  }, 1000);
}

function stopAlwaysCountdown() {
  if (alwaysCountdown !== null) { clearInterval(alwaysCountdown); alwaysCountdown = null; }
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
setInterval(() => { if (!PREVIEW && !idle()) loadApprovals(); }, 5000);

export async function decide(decision, value, label) {
  const a = approvals[0];
  if (!a || submitting.has(a.id)) return;
  haptic();
  submitting.add(a.id);
  $('apAllow').disabled = $('apDeny').disabled = $('apAlways').disabled = $('apSkip').disabled = $('apCustomSend').disabled = true;
  document.querySelectorAll('#apChoices .ap-opt').forEach((b) => { b.disabled = true; });
  let accepted = false;
  try {
    // an answer carries its value as a second parameter; allow / always / deny / skip are plain flags
    const q = value === undefined ? `decision=${decision}` : `decision=answer&value=${encodeURIComponent(value)}`;
    await api(`/approvals/${encodeURIComponent(a.id)}?${q}`, 'POST', 4000);
    accepted = true;
    if (decision === 'skip') toast(t('panel.questionWaiting'), 'warn');
    else if (decision === 'answer') toast(t('panel.answered', { answer: label || value || '' }), 'ok');
    else toast(t(decision === 'allow' ? 'panel.allowedTool' : decision === 'always' ? 'panel.allowedAlways' : 'panel.deniedTool', { tool: a.tool }), decision === 'deny' ? 'err' : 'ok');
  } catch (_) { toast(t('panel.alreadyAnswered'), 'warn'); }
  finally { submitting.delete(a.id); }
  if (!accepted) {
    // The agent never took the answer, so the prompt is still live and the command is still blocked. Hiding it
    // here would leave nothing on screen while the computer waits; the buttons come back and the next list
    // request settles whether the prompt really is gone. Only for this prompt though: a list request may have
    // moved on to another approval while this one was in flight, and unlocking that one would let a second
    // answer go out for a prompt whose own post is still running. Allow always stays locked while its own
    // countdown for this card is still running — a failed post does not arm it early.
    if (approvals[0] && approvals[0].id === a.id) {
      const stillCounting = approvalShown === a.id && alwaysCountdown !== null;
      $('apAllow').disabled = $('apDeny').disabled = $('apSkip').disabled = $('apCustomSend').disabled = false;
      $('apAlways').disabled = stillCounting;
      document.querySelectorAll('#apChoices .ap-opt').forEach((b) => { b.disabled = false; });
    }
    return;
  }
  // The typed draft is cleared only while this card is still the one on screen: a late success (the post landed
  // after the user had moved on) must not wipe the next question's draft. A card change clears it in renderApproval.
  if (decision === 'answer' && approvals[0] && approvals[0].id === a.id) $('apCustomText').value = '';
  answered.add(a.id);
  if (answered.size > answeredCap) answered.delete(answered.values().next().value);
  approvals = approvals.filter((x) => x.id !== a.id);
  renderApproval();
}
$('apAllow').addEventListener('click', () => decide('allow'));
$('apAlways').addEventListener('click', () => decide('always'));
$('apDeny').addEventListener('click', () => decide('deny'));
$('apSkip').addEventListener('click', () => decide('skip'));

function sendCustomAnswer() {
  const v = $('apCustomText').value.trim();
  if (v) decide('answer', v, v);
}
$('apCustomSend').addEventListener('click', sendCustomAnswer);
$('apCustomText').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); sendCustomAnswer(); } });

export function handleEvent(e) {
  if (PREVIEW && (e.kind === 'goto' || e.kind === 'back' || e.kind === 'reload')) return; // the editor picks the page
  if (e.kind === 'approval' || e.kind === 'approval-done') loadApprovals();
  else if (e.kind === 'notify' && e.data) handleNotify(e.data);
  else if (e.kind === 'goto' && e.data) requestPage(e.data);
  else if (e.kind === 'back' && e.data) requestBack(e.data.rule);
  else if (e.kind === 'refresh') { loadConfig(); loadQuotas(); loadWork(); loadPush(); loadWeather(true); loadCalendar(); }
  else if (e.kind === 'reload') location.reload();
}

export const WIDGET_ICONS = { camera: '📹', dictate: '🎙️', spectrum: '📶', 'app-console': '🎛️', discord: '💬', steam: '👥', 'steam-activity': '🔔', rules: '⚡', chargecare: '🔋', nextup: '⏭️', moon: '🌙', wordclock: '🔤', clock: '🕒', weather: '⛅', forecast: '🌦️', media: '🎵', lyrics: '🎤', stat: '📈', gauge: '⏲️', chart: '📊', meters: '💾', gpu: '🎮', quotas: '✳️', sessions: '🤖',
  github: '🐙', processes: '⚙️', shortcuts: '🎛️', button: '🔘', worldclock: '🌍', calendar: '📅', pomodoro: '🍅', countdown: '⏳', note: '📝', command: '💻',
  push: '📡', inbox: '📬', label: '🔤', analog: '🕰️', progress: '📆', stopwatch: '⏱️', timer: '⏲️', counter: '🔢', breathe: '🫁', quote: '💬',
  checklist: '✅', battery: '🔋', topprocs: '🔥', quotaring: '🎯', volume: '🔊', sun: '🌅', air: '🍃', hackernews: '📰', feeds: '🗞️', photos: '🖼️', exchange: '💱', crypto: '🪙', image: '🖼️', pcswitch: '🖥️', pagebuttons: '🧭', 'plex-queue': '🎛️', 'plex-playlists': '🎶' };

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
let pageAllBtn = null;
let scrubFrom = null, scrubbed = false; // { x, id, at }: a press on the dots that may turn into a slide along them
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
    pagePauseBtn = pageAllBtn = null;
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
    if (scrubbed) { scrubbed = false; return; } // the touch that opened the page bar
    togglePagePaused();
  });
  // each dot is a finger-sized button that goes to its page; the dot itself stays the small <i> inside
  const dot = (p, i) => {
    const b = h('button', 'dot', h('i'), p.icon ? h('span', 'dot-ic', rich(p.icon)) : null, h('span', 'dot-nm', p.title || `${i + 1}`));
    b.type = 'button';
    b.dataset.i = i;
    b.setAttribute('aria-label', `${i + 1} · ${`${p.icon || ''} ${p.title || ''}`.trim()}`);
    return b;
  };
  pageAllBtn = h('button', 'page-all', makeSvg('i-all', 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z'));
  pageAllBtn.type = 'button';
  pageAllBtn.title = t('panel.allPages');
  pageAllBtn.setAttribute('aria-label', t('panel.allPages'));
  pageAllBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (scrubbed) { scrubbed = false; return; } // the touch that opened the page bar
    openOverview();
  });
  dots.style.setProperty('--n', pages.length);
  dots.replaceChildren(...pages.map(dot), pageAllBtn, pagePauseBtn);
  dots.hidden = false;
  syncPagePauseUi();
}

export function updateDots() {
  $('dots').querySelectorAll('i').forEach((d, i) => d.classList.toggle('on', i === curPage));
  $('dots').querySelectorAll('.dot').forEach((d, i) => d.classList.toggle('on', i === curPage));
}

export let snapTimer = 0;
/** The width of one page: the pager's, which a docked side page bar makes narrower than the window. */
const pageWidth = () => pager.clientWidth || window.innerWidth;
export function goPage(i, instant = false) {
  i = Math.max(0, Math.min(pages.length - 1, i));
  if (curPage >= 0 && i !== curPage && pagePaused) setPagePaused(false, { feedback: false, resume: false });
  if (!instant) { pager.scrollTo({ left: i * pageWidth(), behavior: 'smooth' }); return; }
  // scroll-snap-stop: always would stop a programmatic jump at the next page, so snapping is paused for the jump.
  pager.style.scrollSnapType = 'none';
  pager.scrollLeft = i * pageWidth();
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
    $('ncText').replaceChildren(rich(`${name} → ${target}${more}`));
    $('ncWhy').textContent = why;
    $('ncGo').onclick = () => { haptic(); perform(req); };
    $('ncX').onclick = () => dismiss(req);
    return;
  }
  chip.classList.remove('on');
  if (navShown !== navCount) {
    navShown = navCount;
    $('nvName').textContent = name;
    $('nvLine').replaceChildren(rich(req.kind === 'goto' ? t('panel.switchingTo', { target }) : t('panel.backTo', { target })));
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
    if (rulesPaused) navQueue = navQueue.filter((x) => x.kind !== 'goto' || !x.rule);
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
/** Shows page i's number, icon and title at the top: until hidden (hold) or for 900 ms. -1 hides it. */
function showPageName(i, hold = false) {
  const el = $('ptitle');
  clearTimeout(titleTimer);
  const p = pages[i];
  if (!p || pages.length < 2) { el.classList.remove('on'); return; }
  el.replaceChildren(rich(`${p.icon || ''} ${p.title}`.trim()));
  el.classList.add('on');
  if (!hold) titleTimer = setTimeout(() => el.classList.remove('on'), 900);
}
export function flashTitle() {
  if (scrubFrom?.at != null) return; // the slide along the dots names the page under the finger
  showPageName(curPage);
}

export function onScroll() {
  const p = Math.round(pager.scrollLeft / pageWidth());
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
      const b = h('span', null, emo(WIDGET_ICONS[w.type] || '◻'));
      b.style.gridArea = `${w.y + 1} / ${w.x + 1} / span ${w.h} / span ${w.w}`;
      return b;
    }));
    const t = h('button', 'ov-tile' + (i === curPage ? ' on' : ''), map, h('div', 'ov-name', h('b', null, `${i + 1}`), rich(` ${p.icon || ''} ${p.title}`)));
    t.addEventListener('click', (e) => { e.stopPropagation(); haptic(); closeOverview(); goPage(i, true); });
    return t;
  };
  $('ovGrid').replaceChildren(...pages.map(tile));
  renderRulesBtn();
  // several paired computers: switch between them here (a USB cable always wins)
  const pcs = pairedPcs();
  $('ovPcs').replaceChildren(...(pcs.length > 1 ? pcs.map((pc) => {
    const b = h('button', 'ov-pc' + (pc.active ? ' on' : ''), icon('monitor', 'm'), pc.name || t('panel.computer'));
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
// ---- the page bar: the dots shown as buttons with each page's icon and name (Settings › Display › Page navigation)
/** dots (default), auto (a bar while the dots are being used), bar (always, with its own space) or hidden (none). */
export const pageBarMode = () => (['auto', 'bar', 'hidden'].includes(cfg.display?.pageBar) ? cfg.display.pageBar : 'dots');
const pageBarSide = () => (['left', 'right'].includes(cfg.display?.pageBarSide) ? cfg.display.pageBarSide : 'bottom');
let pageBarKey = '', pageBarTimer = 0, pageBarOpen = false;
/** In "auto" mode, opens the bar and closes it again 5 s after the last touch on it. */
function showPageBar() {
  pageBarOpen = true;
  applyPageBar();
  clearTimeout(pageBarTimer);
  const close = () => {
    if (scrubFrom) { pageBarTimer = setTimeout(close, 1000); return; } // not while a finger slides along it
    pageBarOpen = false;
    applyPageBar();
  };
  pageBarTimer = setTimeout(close, 5000);
}
export function applyPageBar() {
  const mode = pageBarMode(), side = pageBarSide();
  const docked = mode === 'bar' && pages.length > 1;
  const shown = docked || (mode === 'auto' && pageBarOpen);
  const key = `${mode}|${side}|${docked}|${shown}`;
  if (key === pageBarKey) return;
  const [wasMode, wasSide, wasDocked] = pageBarKey.split('|');
  const reflow = (wasDocked === 'true') !== docked || (docked && (wasMode !== mode || wasSide !== side));
  pageBarKey = key;
  const b = document.body.classList;
  b.toggle('pagebar-docked', docked);
  b.toggle('dots-hidden', mode === 'hidden'); // swipes, the volume keys and a long press on an empty spot still work (#475)
  b.toggle('pagebar-left', side === 'left');
  b.toggle('pagebar-right', side === 'right');
  $('dots').classList.toggle('pagebar', shown);
  $('dots').classList.toggle('pb-vertical', shown && side !== 'bottom');
  // a docked bar changes the size of the pages: keep the current one in place
  if (reflow) requestAnimationFrame(() => goPage(curPage, true));
}
/** Whether the dots show as a page bar down the left or right edge (slides then go up and down). */
const barVertical = () => $('dots').classList.contains('pagebar') && $('dots').classList.contains('pb-vertical');
/** Where a pointer is along the bar, and across it. */
const along = (e) => (barVertical() ? e.clientY : e.clientX);
const across = (e) => (barVertical() ? e.clientX : e.clientY);
/** The page whose dot is nearest to position a along the bar. */
function dotAt(a) {
  const v = barVertical();
  let best = 0, gap = Infinity;
  $('dots').querySelectorAll('.dot').forEach((d, i) => {
    const r = d.getBoundingClientRect();
    const dist = v ? Math.abs(a - (r.top + r.height / 2)) : Math.abs(a - (r.left + r.width / 2));
    if (dist < gap) { gap = dist; best = i; }
  });
  return best;
}
/** Ends a slide along the dots: no dot is aimed at any more, and the page name fades. */
function endScrub() {
  if (!scrubFrom) return;
  const was = scrubFrom.at;
  scrubFrom = null;
  $('dots').querySelectorAll('.dot.aim').forEach((d) => d.classList.remove('aim'));
  if (was !== null) showPageName(-1);
}
/** Cancels any pending long-press hold timer on the #dots bar. */
function clearDotsHold() {
  if (dotsHoldTimer) { clearTimeout(dotsHoldTimer); dotsHoldTimer = 0; }
  dotsHoldStart = null;
}
$('dots').addEventListener('pointerdown', (e) => {
  heldDots = false;
  scrubbed = false;
  clearDotsHold();
  if (pages.length < 2 || dimmed || document.body.classList.contains('waking')) return;
  if (pageBarMode() === 'auto') {
    const opening = !$('dots').classList.contains('pagebar');
    showPageBar();
    if (opening) { scrubbed = true; return; } // this touch only opens the bar
  }
  if (e.pointerType === 'mouse' && e.button !== 0) return;
  if (e.target.closest('.page-pause, .page-all')) return;
  dotsHoldStart = [e.clientX, e.clientY];
  scrubFrom = { a: along(e), c: across(e), id: e.pointerId, at: null };
  dotsHoldTimer = setTimeout(() => {
    dotsHoldTimer = 0;
    dotsHoldStart = null;
    heldDots = true;
    togglePagePaused();
  }, 450);
});
$('dots').addEventListener('pointermove', (e) => {
  if (dotsHoldStart && Math.hypot(e.clientX - dotsHoldStart[0], e.clientY - dotsHoldStart[1]) > 10) clearDotsHold();
  // sliding along the dots scrubs through the pages: the page under the finger is named, and opened on release
  if (!scrubFrom || e.pointerId !== scrubFrom.id) return;
  if (e.pointerType === 'mouse' && e.buttons === 0) { endScrub(); return; } // released outside the bar
  if (scrubFrom.at === null) {
    // a touch keeps its target while it moves, so a press that went up or down off the bar is ended here
    if (Math.abs(across(e) - scrubFrom.c) > 24) { scrubFrom = null; return; }
    if (Math.abs(along(e) - scrubFrom.a) <= 10) return;
    try { $('dots').setPointerCapture(e.pointerId); } catch (_) {}
  }
  // a slide that wanders far up or down off the bar is called off: nothing opens when the finger lifts
  if (Math.abs(across(e) - scrubFrom.c) > 60) { endScrub(); scrubbed = true; return; }
  const at = dotAt(along(e));
  if (at === scrubFrom.at) return;
  scrubFrom.at = at;
  $('dots').querySelectorAll('.dot').forEach((d, i) => d.classList.toggle('aim', i === at));
  $('dots').querySelectorAll('.dot')[at]?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }); // a bar with more pages than fit
  showPageName(at, true);
  haptic();
});
$('dots').addEventListener('pointerup', (e) => {
  if (!scrubFrom || e.pointerId !== scrubFrom.id) return;
  const at = scrubFrom.at;
  endScrub();
  if (at === null) return;
  scrubbed = true; // the click that follows a scrub opens nothing
  setTimeout(() => { scrubbed = false; }, 0);
  if (at !== curPage) goPage(at, true);
  else flashTitle();
});
$('dots').addEventListener('pointercancel', endScrub);
// a press that leaves the bar before it became a slide is over (its release happens elsewhere)
$('dots').addEventListener('pointerleave', (e) => { if (scrubFrom && e.pointerId === scrubFrom.id && scrubFrom.at === null) scrubFrom = null; });
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
  if (scrubbed) { scrubbed = false; e.preventDefault(); e.stopPropagation(); return; }
  if (e.target.closest('.page-pause, .page-all')) return;
  const dot = e.target.closest('.dot');
  const i = dot ? +dot.dataset.i : -1;
  if (i >= 0 && i !== curPage && i < pages.length) { haptic(); goPage(i, Math.abs(i - curPage) > 1); return; }
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

/**
 * Whether a sideways swipe that starts on el turns the page, as the browser decides it from touch-action: the wrap at
 * either end follows the same rule as an ordinary page turn. A button or a card turns the page; the page dots, a
 * slider or anything else that keeps sideways moves for itself does not (#487).
 */
function swipeTurnsPage(el) {
  for (let n = el; n && n !== pager; n = n.parentElement) {
    const ta = getComputedStyle(n).touchAction;
    if (ta !== 'auto' && ta !== 'manipulation' && !/pan-(x|left|right)/.test(ta)) return false;
  }
  return true;
}

pager.addEventListener('touchstart', (e) => {
  swipeTracking = false;
  if (e.touches.length !== 1 || pages.length < 2) return;
  // Dragging a slider, the volume bar, a list or the station carousel must never flip (or wrap) the page.
  if (volDragging || e.target.closest?.('input, textarea, select, [contenteditable], .s-list, .vb, .bar, .radio-carousel-track, .ac-dial')
    || !swipeTurnsPage(e.target)) return;
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
  if (e.key === 'Escape') {
    const noteEdit = document.querySelector('.note-edit');
    if (noteEdit) { e.preventDefault(); noteEdit.remove(); return; }
    if (closeSessionDetails()) { e.preventDefault(); return; }
    if (sheet) { e.preventDefault(); closeSheet(); return; }
    $('overview').hidden ? openOverview() : closeOverview();
    return;
  }
  if (e.altKey || e.ctrlKey || e.metaKey) return;
  if (e.target?.closest?.('input, textarea, select, [contenteditable=""], [contenteditable="true"]') || document.querySelector('.note-edit')) return;
  if (e.key === 'ArrowRight') window.onAndroidKey('next');
  else if (e.key === 'ArrowLeft') window.onAndroidKey('prev');
  else if (e.key === 'o') ($('overview').hidden ? openOverview() : closeOverview());
});
window.onAndroidBack = () => {
  if (!$('approval').hidden) return; // answer it (or let it time out); Back must not dismiss a permission prompt
  const noteEdit = document.querySelector('.note-edit');
  if (noteEdit) { noteEdit.remove(); return; }
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
let unreachableSince = 0;

function reconcileUnreachableNight(now = new Date()) {
  const enabled = !PREVIEW && cfg.display?.nightWhenUnreachable === true;
  if (!enabled) unreachableSince = 0;
  else if (online === false && unreachableSince === 0) unreachableSince = Date.now();
  const delayMin = Math.max(1, Math.min(30, +cfg.display?.unreachableNightDelayMin || 5));
  const unreachable = enabled && unreachableSince > 0
    && Date.now() - unreachableSince >= delayMin * 60_000;
  const dark = darkRoom && cfg.display?.nightWhenDark === true;
  setNight((isNight(now) || dark || unreachable) && Date.now() > wakeUntil);
}
/**
 * Before widgets are built: a timer that ran out while the panel was closed rings during the build, so the night
 * screen must already be on if the schedule says so (nightOn is null at start, and new settings may move the night).
 * Only ever turns night on; tick() keeps the state up to date as before, so the day is unchanged.
 */
function nightBeforeBuild() { if (isNight(new Date()) && Date.now() > wakeUntil) setNight(true); }

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
  reconcileUnreachableNight(now);
  if (!PREVIEW && idleDue() && !navQueue.some((r) => r.kind === 'home')) {
    if (curPage === homeIndex()) { closeSheet(); closeOverview(); lastNav = Date.now(); } // only a sheet to close
    else { navQueue.push({ kind: 'home', at: Date.now(), priority: -1 }); pump(); }
  }
  applyBurnIn();
  document.body.classList.toggle('controls-top', cfg.display?.controlsTop === true);
  applyPageBar();
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
  try { bridge.setLanguage?.(I18n?.getPref() || 'auto'); } catch (_) {}
  try { bridge.setVolumeKeys && bridge.setVolumeKeys(d.volumeKeys !== false); } catch (_) {}
  try { bridge.setPinned && bridge.setPinned(d.pin === true); } catch (_) {}
  try { bridge.setOrientation && bridge.setOrientation(['portrait', 'auto'].includes(d.orientation) ? d.orientation : 'landscape'); } catch (_) {}
  try { bridge.setHomeScreen && bridge.setHomeScreen(d.homeScreen === true); } catch (_) {}
  applyBrightness(true);
}

/** A mark taken before the panel writes the config itself (a note save). */
export function configMark() { return configRequest; }
/** Whether a config read that started after `mark` has been applied: it already shows the write, or a later one. */
export function configReadSince(mark) { return appliedConfigRequest > mark; }
/** The panel's own write was stored as version `v`: a config read that started before it must not apply over it. */
export function localWriteStored(v) {
  if (v != null) cfgVersion = v;
  writeBarrier = configRequest + 1;
}

export async function loadConfig() {
  if (PREVIEW) return;
  const request = ++configRequest;
  try {
    const r = await api('/config');
    if (request < appliedConfigRequest || request < writeBarrier) return;
    const before = JSON.stringify(cfg);
    const first = cfgVersion === null;
    cfg = { ...DEFAULT_CFG, ...r.config };
    cfgVersion = r.version;
    appliedConfigRequest = request;
    if (first || JSON.stringify(cfg) !== before) {
      store.set('cfg', cfg);
      if (JSON.stringify(cfg) !== before) {
        nightBeforeBuild();
        buildLayout();
        applyDisplay();
      }
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

let workEpoch = 0;
export function applyWork(w) {
  workEpoch++;
  D.work = w;
  notifyTransitions(w.sessions || []);
  emit('work');
}

export async function loadWork() {
  const stamp = ++workEpoch;
  try {
    const w = await api('/work');
    if (stamp !== workEpoch) return;
    D.work = w;
    notifyTransitions(w.sessions || []);
    emit('work');
  } catch (_) {}
}

export async function loadProcs() {
  try { D.procs = await api('/procs?top=150'); emit('procs'); } catch (_) {}
}

let pushRequest = 0, appliedPushRequest = 0;
export async function loadPush() {
  const req = ++pushRequest;
  try {
    const r = await api('/push');
    if (req < appliedPushRequest) return;
    appliedPushRequest = req;
    D.push = r.items || {};
    pushVersion = r.version;
    emit('push');
  } catch (_) {}
}

// ---------------------------------------------------------------- Wake-on-LAN
// While the PC is unreachable the app (which holds the PC's MAC addresses) can wake it. The page only ever passes the
// computer's id to the bridge. One tap sends one burst; the button waits 10 s before it can send again and reports
// "didn't wake" after 3 minutes. The agent answering again (setOnline(true)) resets everything.
const WAKE_COOLDOWN_MS = 10_000, WAKE_WINDOW_MS = 180_000;
let wakeAt = 0, wakeTicker = 0, canWakeAt = 0, canWakeNow = false;
// canWake() looks at the phone's network interfaces: asked at most every 5 s, since a PC that sleeps all night keeps
// this panel offline (and calling it on every failed poll) for hours
function wakeAvailable() {
  if (Date.now() - canWakeAt > 5000) { canWakeAt = Date.now(); canWakeNow = !!bridge?.canWake?.(); }
  return canWakeNow;
}
function wakeRender() {
  const box = $('wake');
  if (!box) return;
  const waited = wakeAt ? Date.now() - wakeAt : Infinity;
  const waking = waited < WAKE_WINDOW_MS;
  // the 1 s timer only runs while a wake is in progress (the cooldown and the 3 minutes), not for the whole outage;
  // stopped before the hidden check below, because the phone may have left the network and hidden the button meanwhile
  if (!waking && wakeTicker) { clearInterval(wakeTicker); wakeTicker = 0; }
  const offline = document.body.classList.contains('offline');
  box.hidden = !(offline && wakeAvailable());
  if (box.hidden) return;
  const btn = box.querySelector('button');
  btn.textContent = t(waking ? 'panel.waking' : 'panel.wake');
  btn.disabled = waited < WAKE_COOLDOWN_MS;
  box.querySelector('.note').textContent = wakeAt && !waking ? t('panel.wakeFailed') : '';
}
$('wake')?.querySelector('button').addEventListener('click', () => {
  if (Date.now() - wakeAt < WAKE_COOLDOWN_MS) return;
  const pc = pairedPcs().find((p) => p.active);
  if (!pc || !bridge?.wakePc) return;
  haptic();
  if (bridge.wakePc(pc.id)) {
    wakeAt = Date.now();
    if (!wakeTicker) wakeTicker = setInterval(wakeRender, 1000);
  } else toast(t('panel.wakeFailed'), 'err');
  wakeRender();
});

export function setOnline(on) {
  document.body.classList.toggle('offline', !on);
  if (on) { wakeAt = 0; clearInterval(wakeTicker); wakeTicker = 0; }
  wakeRender();
  if (on && online !== true) { loadConfig(); loadQuotas(); loadWork(); loadPush(); loadApprovals(); loadCalendar(); }
  const changed = online !== on;
  if (!on && changed) unreachableSince = unreachableSince || Date.now();
  if (on && changed) unreachableSince = 0;
  online = on;
  reconcileUnreachableNight();
  if (changed) emit('state');
}

export function applyState(s, replayGen = null) {
  lastStateAt = Date.now();
  // compares the agent's wall clock across responses: if the PC's clock steps back (NTP), polled events are
  // ignored until it catches up — the stream still delivers them meanwhile
  // a late answer is at most a few seconds old (the request deadline), so a bigger step back is the clock itself
  const fresh = s.ts == null || s.ts >= stateTs || stateTs - s.ts > 10_000;
  // the answer to a ?ev=0 replay can be overtaken by streamed states on the way; its events are still the backlog.
  // they go through evOffer like live ones: while a replay is armed the queue merges them before dispatch
  if (fresh || replayGen === evGen) for (const e of s.events || []) evOffer(e);
  if (!fresh) return replayGen === evGen;
  D.state = s;
  media = mediaFrom(media, s.media);
  setOnline(true);
  if (s.cfgVersion !== undefined && cfgVersion !== null && s.cfgVersion !== cfgVersion) loadConfig();
  if (s.pushVersion !== undefined && s.pushVersion !== pushVersion) loadPush();
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

// Nobody can see the panel: the screen is off or another app is in front (the shell tells us — WebView.onPause() does not
// stop JavaScript — and a browser tab reports it as hidden). The shell closes the stream then, and the polling that
// would take over, the 250 ms refresh and the periodic loads below all wait. Clocks, timers and alarms keep running.
let appPaused = false;
export const idle = () => appPaused || document.hidden;
window.onAppPause = (paused) => {
  const was = idle();
  appPaused = !!paused;
  if (was && !idle()) wokeUp();
};
document.addEventListener('visibilitychange', () => { if (!document.hidden && !appPaused) wokeUp(); });
// what the paused panel skipped is fetched at once, not at the next interval
function wokeUp() {
  if (!online) return;
  loadConfig(); loadPush(); loadApprovals();
  if (anyNeeds('quotas')) loadQuotas();
  if (anyNeeds('calendar')) loadCalendar();
  if (anyNeeds('work') && !streaming()) loadWork();
}

export async function poll() {
  // a pending replay must go through even while the stream is live: the stream only sends events newer than
  // its own connect, so the poll is the only way to get the ones the restarted agent queued before it
  if (idle()) { setTimeout(poll, 2000); return; }
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

// The status pulses (session dots, OBS LED, on-air sign, dictation mic, radio equalizer) are stepped here, not by CSS animations:
// an animation that never ends makes the browser draw a frame on every display refresh while it is on screen, and for these
// that cost the panel 50 to 100 percentage points of a core each on a 120 Hz phone. beat-N is on during every other run of N
// ticks of 250 ms (N = 2 is a half-second on, a half-second off). It only moves while something that pulses is on screen
// (every page is mounted, side by side, and a stack keeps its other cards in place at opacity 0, so being in the page is not enough), and the
// tick above is skipped when the panel is not visible or the night screen is up.
const PULSING = '.sess.busy, .sess.shell, .sess.waiting, .obs-pill.on, .w-onair.on, .dc-mic.on, .radio-card.is-playing';
const pulseShown = (el) => { if (el.closest('.stack-item:not(.active)')) return false; const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.right > 0 && r.left < innerWidth && r.bottom > 0 && r.top < innerHeight; };
let beatN = 0;
function beat() {
  if (![...document.querySelectorAll(PULSING)].some(pulseShown)) return;
  beatN++;
  const cl = document.documentElement.classList;
  for (const n of [2, 3, 4]) cl.toggle('beat-' + n, Math.floor(beatN / n) % 2 === 1);
}

// ---------------------------------------------------------------- start
nightBeforeBuild();
buildLayout();
applyDisplay();
tick();
loadWeather();
setInterval(loadWeather, 15 * 60_000);
setInterval(() => { if (!nightOn && !idle()) { emit('fast'); beat(); } }, 250);
setInterval(() => { if (online && !idle() && anyNeeds('quotas')) loadQuotas(); }, 30_000);
setInterval(() => { if (online && !idle() && anyNeeds('calendar')) loadCalendar(); }, 120_000);
setInterval(() => { if (online && !idle() && anyNeeds('work') && !streaming()) loadWork(); }, 3000);
setInterval(() => { if (idle()) return; if (online && pageOf('procs')) loadProcs(); if (online && sheet) loadSheet(); }, 2000);
setInterval(() => { if (online && !idle()) loadConfig(); }, 60_000);
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
