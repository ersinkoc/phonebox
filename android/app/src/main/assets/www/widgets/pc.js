import { $, API, D, PANEL_HEADERS, LOCALE, Q_STYLE, api, bridge, cfg, color, rulesPaused, toggleRulesPause, fillIcon, fmtClock, fmtDur, fmtMem, gotoPageRef, h, haptic, openSheet, paceInfo, pad, quotaProviders, store, tick, toast, useHour12 } from '../main.js';
import { W } from '../registry.js';
import { canKillAnywhere, dialEl, every } from './helpers.js';

// ---------------------------------------------------------------- phone & PC
// Your own HTML / CSS / JavaScript in a sandboxed iframe (no access to the panel, the agent or the phone). It gets
// the panel's data through window.phonebox: phonebox.on('state' | 'push' | 'weather' | 'work' | 'quotas', fn),
// phonebox.data, and may ask for phonebox.run(shortcutId) or phonebox.goto(page).
export const HTML_PRELUDE = `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="dark"><style>:root{color-scheme:dark}html,body{margin:0;height:100%;background:transparent;color:#eef2f6;font-family:Roboto,"Segoe UI",system-ui,sans-serif;overflow:hidden}</style>
<script>(function(){var subs={},data={};window.phonebox={data:data,
on:function(k,f){(subs[k]=subs[k]||[]).push(f);if(data[k]!==undefined)try{f(data[k])}catch(e){console.error(e)}},
run:function(id){parent.postMessage({phonebox:'run',id:String(id)},'*')},
goto:function(p){parent.postMessage({phonebox:'goto',page:p},'*')}};
addEventListener('message',function(e){var m=e.data;if(!m||m.phonebox!=='data')return;data[m.kind]=m.value;(subs[m.kind]||[]).forEach(function(f){try{f(m.value)}catch(err){console.error(err)}})});})();<\/script>`;
export const HTML_KINDS = ['state', 'push', 'weather', 'work', 'quotas'];

W.html = {
  needs: HTML_KINDS,
  create(box, { o }) {
    if (o.transparent) box.classList.add('bare-html');
    const frame = h('iframe', 'html-frame');
    frame.setAttribute('sandbox', 'allow-scripts');
    frame.setAttribute('referrerpolicy', 'no-referrer');
    frame.srcdoc = HTML_PRELUDE + (o.html || '');
    box.append(frame);
    const send = (kind) => { if (D[kind] != null) try { frame.contentWindow?.postMessage({ phonebox: 'data', kind, value: D[kind] }, '*'); } catch (_) {} };
    frame.addEventListener('load', () => HTML_KINDS.forEach(send));
    const onMsg = (e) => {
      if (e.source !== frame.contentWindow || !e.data) return;
      if (e.data.phonebox === 'run' && (cfg.shortcuts || []).some((s) => s.id === e.data.id)) { haptic(); api('/run/' + encodeURIComponent(e.data.id), 'POST', 5000).catch(() => toast('The PC did not answer', 'err')); }
      if (e.data.phonebox === 'goto') gotoPageRef(e.data.page);
    };
    window.addEventListener('message', onMsg);
    return {
      update(kind) { if (HTML_KINDS.includes(kind)) send(kind); },
      destroy() { window.removeEventListener('message', onMsg); },
    };
  },
};

// Battery levels of the PC's wireless devices (agent: BatteryMonitor) and, optionally, the panel phone itself.
export const BAT_ICONS = { headphones: '🎧', mouse: '🖱️', keyboard: '⌨️', controller: '🎮', laptop: '💻', pen: '🖊️', phone: '📱', other: '🔋' };
W.batteries = {
  needs: ['tick'],
  create(box, { o }) {
    const low = +o.low || 20;
    let data = null, lastKey = '';
    const list = h('div', 'bt-list');
    box.append(h('div', 'w-title', o.title || 'Batteries'), list);
    const load = async () => { try { data = await api('/batteries'); } catch (_) {} };
    const render = () => {
      const devices = [...(data?.devices || [])];
      if (o.phone !== false && bridge) {
        try { const b = JSON.parse(bridge.battery()); if (b.level >= 0) devices.push({ name: 'This phone', level: b.level, kind: 'phone', charging: b.plugged }); } catch (_) {}
      }
      const key = JSON.stringify([devices, box.clientHeight]);
      if (key === lastKey) return;
      lastKey = key;
      if (!data && !devices.length) { list.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!devices.length) { list.replaceChildren(h('div', 'empty', 'No wireless devices report a battery level')); return; }
      list.replaceChildren(...devices.map((d) => {
        const col = d.level <= low ? '#ef4444' : d.level <= low * 2 ? '#f59e0b' : '#22c55e';
        const row = h('div', 'bt-row' + (d.level <= low ? ' low' : ''),
          h('span', 'bt-ico', BAT_ICONS[d.kind] || '🔋'),
          h('span', 'bt-name', d.name),
          h('span', 'bt-bar', h('i')),
          h('b', 'bt-pct', (d.charging ? '⚡' : '') + d.level + '%'));
        row.style.setProperty('--c', col);
        row.style.setProperty('--v', d.level / 100);
        return row;
      }));
    };
    const tick = every(30_000, async () => { await load(); render(); });
    return { update: () => { tick(); render(); } };
  },
};

// OBS Studio: live / recording state with timers, the current scene, and (when allowed) buttons and scene switching.
W.obs = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, lastKey = '', fetchedAt = 0, busy = null;
    const load = async () => { try { data = await api('/obs'); fetchedAt = Date.now(); } catch (_) {} };
    const dur = (ms) => { const s = Math.floor(ms / 1000); return (s >= 3600 ? Math.floor(s / 3600) + ':' : '') + pad(Math.floor(s / 60) % 60) + ':' + pad(s % 60); };
    const act = async (action, scene) => {
      haptic();
      busy = action + (scene || ''); render(true);
      try {
        const r = await fetch(`${API}/obs/${action}` + (scene ? '?scene=' + encodeURIComponent(scene) : ''), { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || 'OBS did not do it', 'err');
      } catch (_) { toast('The PC did not answer', 'err'); }
      setTimeout(async () => { await load(); busy = null; render(true); }, 600);
    };
    const render = (force = false) => {
      const s = data?.snapshot;
      const since = Date.now() - fetchedAt; // timers keep running between polls
      const key = JSON.stringify([data, busy, Math.floor(Date.now() / 1000), box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!data) { box.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', 'OBS'), h('div', 'empty', 'Turn on OBS in Settings › General')); return; }
      if (!s.connected) { box.replaceChildren(h('div', 'w-title', 'OBS'), h('div', 'empty', s.error || 'Connecting…')); return; }
      const ctl = data.control;
      const pill = (on, label, cls, time, action) => {
        const el = h('div', 'obs-pill ' + cls + (on ? ' on' : '') + (ctl ? ' can' : '') + (busy === action ? ' busy' : ''), h('span', 'obs-led'), h('b', null, label), h('span', 'obs-time', on ? dur(time + since) : 'off'));
        if (ctl) el.addEventListener('click', () => act(action));
        return el;
      };
      const recLabel = s.recordPaused ? 'PAUSED' : 'REC';
      const kids = [
        h('div', 'obs-top', pill(s.streaming, 'LIVE', 'live', s.streamMs, 'stream'), pill(s.recording, recLabel, 'rec', s.recordPaused ? s.recordMs - since : s.recordMs, 'record')),
        h('div', 'obs-scene', h('span', null, 'Scene'), h('b', null, s.scene || '—'),
          s.micMuted != null ? h('span', 'obs-mic' + (s.micMuted ? ' muted' : ''), s.micMuted ? '🔇' : '🎙️') : null),
      ];
      if (o.scenes !== false && s.scenes.length) {
        kids.push(h('div', 'obs-scenes', ...s.scenes.map((name) => {
          const b = h('button', 'obs-sc' + (name === s.scene ? ' cur' : '') + (busy === 'scene' + name ? ' busy' : ''), name);
          if (ctl && name !== s.scene) b.addEventListener('click', () => act('scene', name));
          return b;
        })));
      }
      box.replaceChildren(...kids);
      const mic = box.querySelector('.obs-mic');
      if (mic && ctl) mic.addEventListener('click', (e) => { e.stopPropagation(); act('mic'); });
    };
    const tick = every(2000, async () => { await load(); render(); });
    return { update: () => { tick(); render(); } };
  },
};

// Home Assistant entities through the agent (which holds the token). Tap a light, switch, scene… to switch it,
// when the widget allows it.
export const HA_ICONS = {
  light: '💡', switch: '🔌', fan: '🌀', cover: '🪟', media_player: '🎵', scene: '🎬', script: '📜', automation: '🤖',
  climate: '🌡️', person: '🧑', vacuum: '🧹', camera: '📷', input_boolean: '🔘', button: '🔘', input_button: '🔘', sun: '☀️', weather: '⛅',
};
export const HA_CLASS_ICONS = {
  temperature: '🌡️', humidity: '💧', power: '⚡', energy: '⚡', battery: '🔋', illuminance: '☀️', door: '🚪', garage_door: '🚪',
  window: '🪟', motion: '🏃', occupancy: '🧍', presence: '🧍', moisture: '💧', smoke: '🔥', gas: '🔥', co2: '🫧', carbon_dioxide: '🫧',
  pm25: '🌫️', connectivity: '📶', plug: '🔌', outlet: '🔌', lock: '🔒', sound: '🔊', vibration: '📳',
};
export const HA_SWITCHABLE = new Set(['light', 'switch', 'fan', 'input_boolean', 'automation', 'siren', 'humidifier', 'group', 'cover', 'media_player',
  'scene', 'script', 'button', 'input_button', 'lock', 'vacuum']);
export const HA_ACTIVE = new Set(['on', 'open', 'opening', 'unlocked', 'playing', 'home', 'cleaning', 'heat', 'cool', 'heat_cool', 'auto', 'detected']);

export function haState(e) {
  const s = e.state, dom = e.id.split('.')[0], dc = e.deviceClass;
  if (s === 'unavailable' || s === 'unknown') return s === 'unknown' ? '—' : 'Unavailable';
  if (dom === 'binary_sensor') {
    const on = s === 'on';
    if (['door', 'window', 'garage_door', 'opening'].includes(dc)) return on ? 'Open' : 'Closed';
    if (['motion', 'occupancy', 'presence'].includes(dc)) return on ? 'Detected' : 'Clear';
    if (dc === 'moisture') return on ? 'Wet' : 'Dry';
    return on ? 'On' : 'Off';
  }
  if (dom === 'scene' || dom === 'script' || dom === 'button' || dom === 'input_button') return 'Run';
  if (/^-?\d+(\.\d+)?$/.test(s)) { const n = +s; return (Math.abs(n) >= 100 ? Math.round(n) : Math.round(n * 10) / 10) + (e.unit ? ' ' + e.unit : ''); }
  return s.replace(/_/g, ' ').replace(/^./, (c) => c.toUpperCase());
}

W.ha = {
  needs: ['tick'],
  create(box, { o }) {
    const ids = String(o.entities || '').split(/[\s,;]+/).filter((x) => x.includes('.'));
    let data = null, lastKey = '', busy = null;
    const grid = h('div', 'ha-grid');
    if (o.title) box.append(h('div', 'w-title', o.title));
    box.append(grid);
    const load = async () => { try { data = await api('/ha/states?ids=' + encodeURIComponent(ids.join(','))); } catch (_) {} };
    const toggle = async (e) => {
      haptic();
      busy = e.id; render(true);
      try {
        const r = await fetch(`${API}/ha/toggle/${encodeURIComponent(e.id)}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || 'Could not switch it', 'err');
      } catch (_) { toast('The PC did not answer', 'err'); }
      setTimeout(async () => { await load(); busy = null; render(true); }, 700);
    };
    const render = (force = false) => {
      const key = JSON.stringify([data, busy]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!ids.length) { grid.replaceChildren(h('div', 'empty', 'List entity ids in the widget settings, e.g. light.desk')); return; }
      if (!data) { grid.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!data.configured) { grid.replaceChildren(h('div', 'empty', data.error)); return; }
      grid.replaceChildren(...data.entities.map((e) => {
        const dom = e.id.split('.')[0];
        const active = HA_ACTIVE.has(e.state);
        const icon = dom === 'lock' ? (e.state === 'unlocked' ? '🔓' : '🔒') : HA_CLASS_ICONS[e.deviceClass] || HA_ICONS[dom] || '◻️';
        const can = o.control === true && HA_SWITCHABLE.has(dom) && e.state !== 'unavailable';
        const tile = h('div', 'ha-tile' + (active ? ' on' : '') + (can ? ' can' : '') + (busy === e.id ? ' busy' : '') + (e.state === 'unavailable' ? ' gone' : ''),
          h('span', 'ha-ico', icon), h('span', 'ha-name', e.name), h('b', 'ha-state', haState(e)));
        if (can) tile.addEventListener('click', () => toggle(e));
        return tile;
      }), ...(data.error ? [h('div', 'ha-err', '⚠ ' + data.error)] : []));
    };
    const tick = every(4000, async () => { await load(); render(); });
    return { update: () => { tick(); render(); } };
  },
};

// Docker / Podman containers on the PC. Tap one for start / stop / restart (when the widget allows it).
W.docker = {
  needs: ['tick'],
  create(box, { o }) {
    const filter = String(o.filter || '').toLowerCase();
    let data = null, open = null, busy = null, lastKey = '';
    const list = h('div', 'dk-list'), head = h('div', 'w-title', o.title || 'Docker', h('span', 'dk-count'));
    box.append(head, list);
    const act = async (c, action) => {
      haptic();
      busy = c.id; render(true);
      try {
        const r = await fetch(`${API}/docker/${encodeURIComponent(c.id)}/${action}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) toast(j.error || `Could not ${action} ${c.name}`, 'err');
        else toast(`${c.name}: ${action === 'stop' ? 'stopped' : action === 'start' ? 'started' : 'restarted'}`, 'ok');
      } catch (_) { toast('The PC did not answer', 'err'); }
      busy = null; open = null;
      await load(); render(true);
    };
    const load = async () => { try { data = await api('/docker'); } catch (_) {} };
    const render = (force = false) => {
      const snap = data?.snapshot;
      const items = (snap?.containers || []).filter((c) => (o.stopped !== false || c.state === 'running')
        && (!filter || c.name.toLowerCase().includes(filter) || (c.project || '').toLowerCase().includes(filter)));
      const key = JSON.stringify([items, open, busy, box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      const running = items.filter((c) => c.state === 'running').length;
      head.querySelector('.dk-count').textContent = snap?.available ? ` ${running}/${items.length}` : '';
      if (!data) { list.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!snap.available || (snap.error && !items.length)) { list.replaceChildren(h('div', 'empty', snap.error || 'Docker is not running')); return; }
      if (!items.length) { list.replaceChildren(h('div', 'empty', filter ? `No containers match "${o.filter}"` : 'No containers')); return; }
      list.replaceChildren(...items.map((c) => {
        const st = c.health === 'unhealthy' ? 'bad' : c.state === 'running' ? (c.health === 'starting' ? 'warn' : 'ok') : c.state === 'restarting' || c.state === 'paused' ? 'warn' : 'off';
        const row = h('div', 'dk-row ' + st + (open === c.id ? ' open' : ''),
          h('span', 'dk-dot'),
          h('span', 'dk-name', c.name, h('small', null, ' ' + (c.project ? c.project + ' · ' : '') + c.image.split('@')[0])),
          h('span', 'dk-stat', busy === c.id ? '…' : c.state === 'running' ? (c.cpu != null ? `${c.cpu.toFixed(1)}%` + (c.mem ? ` · ${c.mem}` : '') : c.status) : c.status.replace(/ ago$/, '')));
        row.addEventListener('click', () => {
          if (!data.control) { toast('Turn on "Allow start / stop" in the widget settings on the PC'); return; }
          haptic(); open = open === c.id ? null : c.id; render(true);
        });
        if (open !== c.id) return row;
        const btn = (label, action, cls = '') => {
          const b = h('button', 'dk-btn ' + cls, label);
          b.addEventListener('click', (e) => { e.stopPropagation(); act(c, action); });
          return b;
        };
        return h('div', 'dk-wrap', row, h('div', 'dk-acts', ...(c.state === 'running' ? [btn('↻ Restart', 'restart'), btn('■ Stop', 'stop', 'danger')] : [btn('▶ Start', 'start', 'go')])));
      }));
    };
    const tick = every(4000, async () => { await load(); render(); });
    return { update: () => { tick(); render(); } };
  },
};

// Upcoming events from the calendars set up in the agent (ICS feeds). Small: the current / next event; larger: a
// list grouped by day, with the event that is happening now highlighted.
W.agenda = {
  needs: ['calendar', 'tick'],
  create(box, { o }) {
    const days = Math.max(1, Math.min(14, +o.days || 7));
    const only = String(o.calendars || '').split(',').map((s) => s.trim().toLowerCase()).filter(Boolean);
    let lastKey = '';
    const hm = (ms) => fmtClock(ms, { hour12: o.hour12 });
    const dayKey = (ms) => new Date(ms).toDateString();
    const dayName = (ms) => {
      const d = new Date(ms), t = new Date();
      const diff = Math.round((new Date(d.toDateString()) - new Date(t.toDateString())) / 864e5);
      return diff === 0 ? 'Today' : diff === 1 ? 'Tomorrow' : d.toLocaleDateString(LOCALE, { weekday: 'short', day: 'numeric', month: 'short' });
    };
    const inMin = (ms) => {
      const m = Math.round((ms - Date.now()) / 60000);
      return m < 1 ? 'now' : m < 60 ? `in ${m} min` : m < 24 * 60 ? `in ${Math.floor(m / 60)} h ${m % 60 ? (m % 60) + ' min' : ''}`.trim() : '';
    };
    const render = () => {
      const cal = D.calendar;
      const now = Date.now();
      const until = new Date(new Date().toDateString()).getTime() + days * 864e5;
      const list = (cal?.events || []).filter((e) => e.end > now && e.start < until && (!only.length || only.includes(e.calendar.toLowerCase())));
      const small = box.clientHeight < 110;
      const is12 = useHour12(o.hour12);
      const key = JSON.stringify([list.map((e) => e.start + e.title), Math.floor(now / 30000), small, box.clientHeight, is12]);
      if (key === lastKey) return;
      lastKey = key;
      box.classList.toggle('small', small);
      box.classList.toggle('h12', is12);
      if (!cal) { box.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!cal.updated && !list.length) { box.replaceChildren(h('div', 'w-title', o.title || 'Agenda'), h('div', 'empty', 'Add a calendar in Settings › General')); return; }
      const timed = list.filter((e) => !e.allDay);
      const cur = timed.find((e) => e.start <= now);
      const next = timed.find((e) => e.start > now);
      if (small) {
        const e = cur || next || list[0];
        if (!e) { box.replaceChildren(h('div', 'ag-big-t', 'No events'), h('div', 'ag-big-s', `next ${days} days`)); return; }
        box.style.setProperty('--c', e.color);
        box.replaceChildren(
          h('div', 'ag-big-s', e === cur ? `Now · until ${hm(e.end)}` : e.allDay ? dayName(e.start) + ' · all day' : `${dayName(e.start) === 'Today' ? inMin(e.start) || 'Today' : dayName(e.start)} · ${hm(e.start)}`),
          h('div', 'ag-big-t', e.title));
        return;
      }
      const rows = [];
      let lastDay = '';
      for (const e of list) {
        // events that have already started (including today's all-day ones) belong to today
        const at = e.start < now ? now : e.start;
        const dk = dayKey(at);
        if (dk !== lastDay) { rows.push(h('div', 'ag-day', dayName(at))); lastDay = dk; }
        const isNow = e === cur;
        const row = h('div', 'ag-row' + (isNow ? ' now' : ''),
          h('span', 'ag-dot'),
          h('span', 'ag-time', e.allDay ? 'all day' : hm(e.start)),
          h('span', 'ag-title', e.title, o.location !== false && e.location ? h('small', null, ' · ' + e.location) : null),
          h('span', 'ag-when', isNow ? `until ${hm(e.end)}` : e === next ? inMin(e.start) : ''));
        row.style.setProperty('--c', e.color);
        if (isNow) row.style.setProperty('--p', Math.min(1, (now - e.start) / Math.max(1, e.end - e.start)));
        rows.push(row);
      }
      const body = h('div', 'ag-list', ...(rows.length ? rows : [h('div', 'empty', `Nothing in the next ${days} days`)]));
      box.replaceChildren(h('div', 'w-title', o.title || 'Agenda', cal.errors?.length ? h('span', 'ag-err', ' ⚠') : null), body);
      // drop rows that do not fit, keeping the day headers tidy
      while (body.scrollHeight > body.clientHeight + 2 && body.childElementCount > 1) {
        body.lastElementChild.remove();
        if (body.lastElementChild?.classList.contains('ag-day')) body.lastElementChild.remove();
      }
    };
    return { update: render };
  },
};

// Lights up while the PC's microphone or camera is in use (agent: PrivacyMonitor) or while a pushed value is on.
W.onair = {
  needs: ['state', 'push'],
  create(box, { o }) {
    const sign = h('div', 'oa-sign', o.text || 'ON AIR'), sub = h('div', 'oa-sub');
    box.append(sign, sub);
    const src = o.source || 'both';
    return {
      update() {
        const p = D.state?.privacy;
        const mic = (src === 'both' || src === 'mic') && !!p?.mic;
        const cam = (src === 'both' || src === 'camera') && !!p?.camera;
        const obs = D.state?.obs;
        const obsLive = (src === 'both' || src === 'obs') && !!obs && (obs.streaming || (obs.recording && !obs.recordPaused));
        const pushKey = o.key ?? 'onair'; // the catalog's default; an emptied field means no pushed value
        let pv = pushKey ? D.push?.[pushKey]?.data : null;
        if (pv && typeof pv === 'object' && 'value' in pv) pv = pv.value;
        const pushed = pv != null && pv !== false && pv !== 0 && !/^(|0|false|off|no)$/i.test(String(pv));
        const on = mic || cam || obsLive || pushed;
        box.classList.toggle('on', on);
        const apps = [...new Set([...(mic ? p.micApps || [] : []), ...(cam ? p.cameraApps || [] : [])])];
        sub.textContent = on
          ? [mic ? '🎙️' : '', cam ? '📷' : '', obsLive ? (obs.streaming ? 'OBS live' : 'OBS recording') : '', apps.join(', ') || (pushed && typeof pv === 'string' ? pv : '')].filter(Boolean).join(' ')
          : (src === 'mic' || src === 'camera') && p && p.mic == null ? 'Not detectable on this computer' : 'Off air';
      },
    };
  },
};

W.battery = {
  needs: ['tick'],
  create(box) {
    const dial = dialEl(), sub = h('div', 'g-sub');
    box.append(dial, h('div', 'g-label', 'PHONE'), sub);
    const load = every(20_000, () => {
      if (!bridge) { dial.set(0, '—'); sub.textContent = 'Only on the phone'; return; }
      try {
        const b = JSON.parse(bridge.battery());
        box.style.setProperty('--q', b.plugged ? '#22c55e' : b.level <= 20 ? '#ef4444' : b.level <= 40 ? '#f59e0b' : '#84cc16');
        dial.set(b.level, b.level, '%');
        sub.textContent = (b.plugged ? '⚡ Charging' : 'On battery') + (b.temp ? ` · ${b.temp.toFixed(0)}°C` : '');
      } catch (_) {}
    });
    return { update: load };
  },
};

W.topprocs = {
  needs: ['procs'],
  create(box, { o }) {
    const byMem = o.sort === 'mem';
    const list = h('div', 'tp-list');
    box.style.setProperty('--c', '#f472b6');
    box.append(h('div', 'w-title', byMem ? 'Top memory' : 'Top CPU'), list);
    return {
      update() {
        const data = D.procs;
        if (!data?.groups?.length) { list.replaceChildren(h('div', 'empty', 'Measuring…')); return; }
        const n = Math.max(1, Math.floor((box.clientHeight - 30) / 24));
        const top = [...data.groups].sort((a, b) => (byMem ? b.memMb - a.memMb : b.cpu - a.cpu)).slice(0, n);
        list.replaceChildren(...top.map((g) => {
          const ico = h('div', 'p-ico');
          fillIcon(ico, g);
          const row = h('div', 'tp-i', ico, h('span', 'tp-n', g.title + (g.count > 1 ? ` ×${g.count}` : '')), h('b', null, byMem ? fmtMem(g.memMb) : g.cpu.toFixed(1) + '%'));
          row.addEventListener('click', () => { haptic(); openSheet(g, canKillAnywhere()); });
          return row;
        }));
      },
    };
  },
};

W.quotaring = {
  needs: ['quotas', 'tick'],
  create(box, { o }) {
    const prov = o.provider || 'claude', idx = Number(o.limit) || 0;
    const st = Q_STYLE[prov] || { color: '#94a3b8', logo: '●' };
    box.style.setProperty('--q', st.color);
    const dial = dialEl(), label = h('div', 'g-label'), sub = h('div', 'g-sub');
    box.append(dial, label, sub);
    let last = 0;
    const render = () => {
      const p = quotaProviders().find((x) => x.id === prov);
      const l = p?.limits?.[idx];
      if (!l) { dial.set(0, '—'); label.textContent = `${st.logo} ${p?.name || prov}`; sub.textContent = D.quotas ? 'No data' : 'Waiting…'; return; }
      const pace = paceInfo(l);
      dial.style.setProperty('--qc', l.percent >= 95 ? '#ef4444' : l.percent >= 80 ? '#f59e0b' : 'var(--q)');
      dial.set(l.percent, Math.round(l.percent), '%', pace?.elapsed);
      label.textContent = `${st.logo} ${p.name} · ${l.label}`;
      sub.textContent = l.resetsAt ? 'resets in ' + fmtDur(l.resetsAt - Date.now()) : '';
      sub.className = 'g-sub ' + (pace?.cls === 'over' ? 'hot' : '');
    };
    return { update(kind) { if (kind === 'tick' && Date.now() - last < 30_000) return; last = Date.now(); render(); } };
  },
};

W.volume = {
  needs: ['state'],
  create(box) {
    const fill = h('i'), bar = h('div', 'vb', fill), pct = h('div', 'vb-v', '--');
    const mute = h('button', 'pb', '🔇'), down = h('button', 'pb', '−'), up = h('button', 'pb', '+');
    box.append(h('div', 'w-title', 'Volume'), h('div', 'vb-row', bar, pct), h('div', 'pomo-btns', mute, down, up));
    let dragging = false, timer = 0, level = 0;
    const vertical = () => box.clientHeight > box.clientWidth * 1.15;
    const paint = (v, muted) => {
      level = v;
      box.classList.toggle('vert', vertical());
      fill.style[vertical() ? 'height' : 'width'] = v + '%';
      fill.style[vertical() ? 'width' : 'height'] = '100%';
      pct.textContent = muted ? 'muted' : v + '%';
      box.classList.toggle('muted', !!muted);
    };
    const fromPointer = (e) => {
      if ((!D.state?.audio || D.state.audio.canControl === false)) return;
      const r = bar.getBoundingClientRect();
      const f = vertical() ? 1 - (e.clientY - r.top) / r.height : (e.clientX - r.left) / r.width;
      const v = Math.round(Math.max(0, Math.min(1, f)) * 100);
      paint(v, false);
      clearTimeout(timer);
      timer = setTimeout(() => api('/volume?level=' + v, 'POST').catch(() => {}), 80);
    };
    bar.addEventListener('pointerdown', (e) => { dragging = true; bar.setPointerCapture(e.pointerId); fromPointer(e); });
    bar.addEventListener('pointermove', (e) => { if (dragging) fromPointer(e); });
    bar.addEventListener('pointerup', () => setTimeout(() => (dragging = false), 400));
    const op = (q) => async () => { haptic(); try { const a = await api('/volume?' + q, 'POST'); paint(a.volume, a.muted); } catch (_) {} };
    mute.addEventListener('click', op('op=mute'));
    down.addEventListener('click', op('op=down'));
    up.addEventListener('click', op('op=up'));
    return { update() {
      const a = D.state?.audio;
      box.classList.toggle('unavailable', (!a || a.canControl === false));
      mute.disabled = down.disabled = up.disabled = (!a || a.canControl === false);
      if (a && !dragging) paint(a.volume, a.muted);
      else if (!a) { pct.textContent = 'n/a'; fill.style.width = fill.style.height = '0'; }
    } };
  },
};

// The agent's automation rules: which hold right now, and a button to pause them all for an hour.
const RULE_WHEN = { 'session-waiting': '✳️', 'session-busy': '⚙️', 'media-playing': '🎵', 'process-running': '🪟', 'mic-in-use': '🎙️', 'camera-in-use': '📷',
  'obs-live': '🔴', 'calendar-event': '📆', 'cpu-above': '🔥', idle: '🌙', time: '🕙', push: '📡', 'quota-above': '🎯', 'review-requested': '👀',
  'ci-failed': '❌', 'battery-low': '🔋', 'ping-high': '📶', always: '📅' };
W.rules = {
  needs: ['state'],
  create(box, { o }) {
    const list = h('div', 'ru-list'), btn = h('button', 'ru-pause');
    box.append(h('div', 'w-title', o.title || 'Rules', btn), list);
    btn.addEventListener('click', (e) => { e.stopPropagation(); toggleRulesPause(); });
    let key = '';
    return {
      update() {
        const rules = (cfg.rules || []).filter((r) => r.enabled !== false);
        const active = D.state?.rules || [];
        const paused = rulesPaused && rulesPaused > Date.now();
        const is12 = useHour12(o.hour12);
        const k = JSON.stringify([rules, active, paused, is12]);
        if (k === key) return;
        key = k;
        btn.textContent = paused ? '▶ Resume' : '⏸ Pause';
        btn.hidden = !D.state;
        box.classList.toggle('paused', !!paused);
        if (!rules.length) { list.replaceChildren(h('div', 'empty', 'Add rules in Settings › Rules')); return; }
        const sorted = [...rules].sort((a, b) => active.includes(b.id) - active.includes(a.id));
        list.replaceChildren(...(paused ? [h('div', 'ru-note', `Paused until ${fmtClock(rulesPaused, { hour12: is12 })}`)] : []),
          ...sorted.map((r) => h('div', 'ru-row' + (active.includes(r.id) && !paused ? ' on' : ''),
            h('span', 'ru-ico', RULE_WHEN[r.when] || '⚡'), h('span', 'ru-name', r.name || r.when), h('i', 'ru-dot'))));
      },
    };
  },
};

// The phone's battery with the charge guard's band (Settings › General › Battery care).
W.chargecare = {
  needs: ['tick'],
  create(box) {
    const C = 2 * Math.PI * 42;
    box.innerHTML = '<div class="w-title">Battery care</div><div class="cc-body"><div class="cc-wrap"><svg viewBox="0 0 100 100" class="cc-ring">' +
      '<circle class="cc-bg" cx="50" cy="50" r="42"/><circle class="cc-band" cx="50" cy="50" r="42"/><circle class="cc-fg" cx="50" cy="50" r="42"/></svg>' +
      '<b class="cc-pct">—</b></div></div><div class="cc-sub"><span class="cc-state"></span> <span class="cc-text"></span></div>';
    const q = (s) => box.querySelector(s);
    const load = every(15_000, () => {
      const ch = cfg.charge || {};
      const min = ch.min ?? 50, max = ch.max ?? 80;
      const band = q('.cc-band');
      band.style.strokeDasharray = `${(C * (max - min)) / 100} ${C}`;
      band.style.strokeDashoffset = String((-C * min) / 100);
      band.style.opacity = ch.enabled ? '' : '0';
      if (!bridge?.battery) { q('.cc-pct').textContent = '—'; q('.cc-text').textContent = 'Only on the phone'; return; }
      let b;
      try { b = JSON.parse(bridge.battery()); } catch (_) { return; }
      if (!(b.level >= 0)) return;
      q('.cc-fg').style.strokeDasharray = `${(C * b.level) / 100} ${C}`;
      q('.cc-pct').textContent = b.level + '%';
      q('.cc-state').textContent = b.plugged ? '⚡' : '🔋';
      const col = b.level <= 20 ? '#ef4444' : ch.enabled && b.level > max ? '#f59e0b' : b.plugged ? '#22c55e' : 'rgb(var(--ambient))';
      box.style.setProperty('--cc', col);
      q('.cc-text').textContent = !ch.enabled ? `Battery care is off${b.temp ? ` · ${b.temp.toFixed(0)} °C` : ''}`
        : b.plugged ? `Charges up to ${max}%` + (b.level >= max ? ' · power off soon' : '') : `Charges again at ${min}%`;
    });
    return { update: load };
  },
};

// The next calendar event, big, with a countdown (or how much of the current one is left).
W.nextup = {
  needs: ['calendar', 'tick'],
  create(box, { o }) {
    const only = String(o.calendars || '').split(',').map((s) => s.trim().toLowerCase()).filter(Boolean);
    const when = h('div', 'nu-when'), title = h('div', 'nu-title'), meta = h('div', 'nu-meta'), bar = h('i'), track = h('div', 'track nu-track', bar);
    box.append(h('div', 'w-title', o.title || 'Next up'), when, title, meta, track);
    let key = '';
    const hm = (ms) => fmtClock(ms, { hour12: o.hour12 });
    return {
      update() {
        const cal = D.calendar, now = Date.now();
        const evs = (cal?.events || []).filter((e) => !e.allDay && e.end > now && (!only.length || only.includes(e.calendar.toLowerCase()))).sort((a, b) => a.start - b.start);
        const cur = evs.find((e) => e.start <= now), next = evs.find((e) => e.start > now);
        const ev = cur || next;
        const is12 = useHour12(o.hour12);
        const k = JSON.stringify([ev?.start, ev?.title, Math.floor(now / 15000), !!cal, is12]);
        if (k === key) return;
        key = k;
        box.classList.toggle('now', !!cur);
        track.hidden = !cur;
        if (!ev) {
          when.textContent = ''; meta.textContent = '';
          title.textContent = !cal ? 'Loading…' : cal.updated || evs.length ? 'Nothing else today' : 'Add a calendar in Settings › General';
          return;
        }
        const left = Math.round(((cur ? ev.end : ev.start) - now) / 60000);
        const dur = left < 60 ? `${Math.max(1, left)} min` : `${Math.floor(left / 60)} h${left % 60 ? ' ' + (left % 60) + ' min' : ''}`;
        const sameDay = new Date(ev.start).toDateString() === new Date().toDateString();
        when.textContent = cur ? `now · ${dur} left` : sameDay || left < 12 * 60 ? `in ${dur}` : new Date(ev.start).toLocaleDateString(LOCALE, { weekday: 'long' });
        title.textContent = ev.title;
        meta.textContent = [`${hm(ev.start)}–${hm(ev.end)}`, ev.location, cur && next ? `then ${next.title} at ${hm(next.start)}` : ''].filter(Boolean).join(' · ');
        if (cur) bar.style.width = Math.min(100, ((now - ev.start) / (ev.end - ev.start)) * 100) + '%';
        box.style.setProperty('--nu', ev.color || 'rgb(var(--ambient))');
      },
    };
  },
};
