import { $, LOCALE, cfg, h, ringSvg, safeFetch, sizeCanvas, store } from '../main.js';

// ---------------------------------------------------------------- more widgets: helpers
export const inflight = {};
/** fetch() + JSON with a localStorage cache; returns stale data when offline. */
export function cached(key, url, maxAgeMs) {
  const c = store.get('nc:' + key, null);
  if (c && Date.now() - c.at < maxAgeMs) return Promise.resolve(c.data);
  if (inflight[key]) return inflight[key];
  const ctl = new AbortController();
  setTimeout(() => ctl.abort(), 10000);
  inflight[key] = safeFetch(url, { signal: ctl.signal })
    .then((r) => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
    .then((data) => { store.set('nc:' + key, { at: Date.now(), data }); return data; })
    .catch((e) => { if (c) return c.data; throw e; })
    .finally(() => delete inflight[key]);
  return inflight[key];
}

/** Calls load() now and then every intervalMs while the widget is on a visible page. */
export function every(intervalMs, load, retryMs = 60_000) {
  let next = 0;
  return () => {
    if (Date.now() < next) return;
    next = Date.now() + intervalMs;
    // a load that returns false failed (offline, service down): try again soon, not after the whole interval
    Promise.resolve(load()).then((ok) => { if (ok === false) next = Math.min(next, Date.now() + retryMs); }, () => {});
  };
}

export function sparkLine(canvas, data, col) {
  if (!canvas.clientWidth || !data || data.length < 2) return;
  const [g, w, hh, dpr] = sizeCanvas(canvas);
  g.clearRect(0, 0, w, hh);
  const min = Math.min(...data), max = Math.max(...data), span = max - min || 1;
  g.beginPath();
  data.forEach((v, i) => { const x = (i / (data.length - 1)) * w, y = hh - 2 * dpr - ((v - min) / span) * (hh - 4 * dpr); i ? g.lineTo(x, y) : g.moveTo(x, y); });
  g.strokeStyle = col; g.lineWidth = 1.5 * dpr; g.lineJoin = 'round'; g.stroke();
}

export const fmtNum = (v) => (v == null ? '—' : v >= 1000 ? v.toLocaleString(LOCALE, { maximumFractionDigits: 0 }) : v >= 10 ? v.toFixed(2) : v.toPrecision(4));
export const pctSpan = (p) => { const e = h('span', 'chg ' + (p >= 0 ? 'up' : 'down'), `${p >= 0 ? '▲' : '▼'} ${Math.abs(p).toFixed(2)}%`); return e; };
export const lines = (v, d) => (Array.isArray(v) ? v : typeof v === 'string' ? v.split('\n') : d).map((x) => String(x).trim()).filter(Boolean);

/** Gauge-style dial used by several widgets. */
export function dialEl() {
  const dial = h('div', 'g-dial'), val = h('div', 'g-val');
  dial.append(val);
  let svg = null;
  dial.set = (pct, text, unit, elapsed) => {
    if (svg) svg.remove();
    dial.insertAdjacentHTML('afterbegin', ringSvg(pct, elapsed)); // numbers only
    svg = dial.firstElementChild;
    val.replaceChildren(String(text), unit ? h('small', null, unit) : '');
  };
  return dial;
}

export function canKillAnywhere() {
  return (cfg.pages || []).some((p) => p.enabled !== false && (p.widgets || []).some((w) => w.type === 'processes' && w.options?.allowKill !== false));
}
