import { $, API, D, PANEL_HEADERS, LOCALE, Q_STYLE, api, bridge, cfg, color, cssVar, rulesPaused, toggleRulesPause, fillIcon, fmtDur, fmtHM, fmtMem, gotoPageRef, gpuMem, gpusOf, h, haptic, openSheet, paceInfo, pad, pairedPcs, quotaProviders, r1, shortGpu, store, switchPc, t, tick, toast, transport } from '../main.js';
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
      if (e.data.phonebox === 'run' && (cfg.shortcuts || []).some((s) => s.id === e.data.id)) { haptic(); api('/run/' + encodeURIComponent(e.data.id), 'POST', 5000).catch(() => toast(t('panel.pcNoAnswer'), 'err')); }
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
      if (!data && !devices.length) { list.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
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
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      setTimeout(async () => { await load(); busy = null; render(true); }, 600);
    };
    const render = (force = false) => {
      const s = data?.snapshot;
      const since = Date.now() - fetchedAt; // timers keep running between polls
      const key = JSON.stringify([data, busy, Math.floor(Date.now() / 1000), box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!data) { box.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', 'OBS'), h('div', 'empty', 'Turn on OBS in Settings › General')); return; }
      if (!s.connected) { box.replaceChildren(h('div', 'w-title', 'OBS'), h('div', 'empty', s.error || t('panel.connecting'))); return; }
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

// Discord through the desktop app on the PC: the voice channel / call you are in, who is there, mute / deafen / leave,
// voice channels to join and recent notifications. Buttons only work when the widget allows control.
W.discord = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, lastKey = '', busy = null;
    const open = new Set(); // servers whose channels are unfolded
    // a round logo: the image once it has loaded, the first letter until then (and when there is no image)
    const ico = (src, name) => {
      const w = h('span', 'dc-ico', (name || '?').trim().charAt(0).toUpperCase() || '?');
      if (src) { const im = new Image(); im.alt = ''; im.onload = () => w.replaceChildren(im); im.src = src; }
      return w;
    };
    const load = async () => { try { data = await api('/discord'); } catch (_) {} };
    const img = (kind, id, hash) => `${API}/discord/img?kind=${kind}&id=${encodeURIComponent(id)}&hash=${encodeURIComponent(hash)}`;
    const act = async (action, channel) => {
      if (busy) return; // one press at a time: a double tap must not toggle twice
      haptic();
      busy = action + (channel || ''); render(true);
      try {
        const r = await fetch(`${API}/discord/${action}` + (channel ? '?channel=' + encodeURIComponent(channel) : ''), { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || t('w.discord.failed'), 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      await new Promise((done) => setTimeout(done, 600));
      await load(); busy = null; render(true);
    };
    const render = (force = false) => {
      const key = JSON.stringify([data, busy, [...open], box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!data) { box.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', 'Discord'), h('div', 'empty', t('w.discord.turnOn'))); return; }
      const s = data.snapshot;
      if (!s.connected) { box.replaceChildren(h('div', 'w-title', 'Discord'), h('div', 'empty', s.error || t('panel.connecting'))); return; }
      const ctl = data.control;
      const btn = (cls, label, on, action) => {
        const b = h('button', 'dc-btn ' + cls + (on ? ' on' : '') + (busy === action ? ' busy' : ''), label);
        if (ctl && !busy) b.addEventListener('click', () => act(action)); else b.disabled = true;
        return b;
      };
      const kids = [h('div', 'dc-top',
        h('span', 'dc-where', s.channelName ? `${s.channelName}` : t('w.discord.notInChannel')),
        s.channelKind ? h('span', 'dc-kind', s.channelKind) : null,
        btn('mute', s.muted ? '🔇' : '🎙️', s.muted, 'mute'), btn('deaf', s.deafened ? '🔕' : '🎧', s.deafened, 'deafen'),
        s.channelId ? btn('leave', '📞', false, 'leave') : null)];
      if (s.channelId && s.members.length) {
        kids.push(h('div', 'dc-members', ...s.members.map((m) => h('span', 'dc-member' + (m.deafened ? ' deaf' : m.muted ? ' muted' : ''), ico(m.avatar && img('user', m.id, m.avatar), m.name), (m.deafened ? '🔕 ' : m.muted ? '🔇 ' : '') + m.name))));
      }
      if (o.channels !== false && s.channels.length) {
        // one block per server; tap it to unfold its voice channels (the server you are in starts unfolded)
        // grouped by server id, not by name: two servers can have the same name
        const servers = new Map();
        for (const c of s.channels) {
          const gid = c.guildId || c.guild || 'other';
          if (!servers.has(gid)) servers.set(gid, []);
          servers.get(gid).push(c);
        }
        kids.push(h('div', 'dc-servers', ...[...servers].map(([gid, chans]) => {
          const here = chans.some((c) => c.id === s.channelId);
          const isOpen = open.has(gid) || (here && !open.has('!' + gid));
          const g = chans[0];
          const name = g.guild || t('w.discord.otherServer');
          const head = h('button', 'dc-server' + (isOpen ? ' open' : '') + (here ? ' here' : ''), ico(g.guildIcon && g.guildId && img('guild', g.guildId, g.guildIcon), name),
            h('span', 'dc-stext', h('b', null, name), h('small', null, t(chans.length === 1 ? 'w.discord.channel' : 'w.discord.channels', { n: chans.length }))));
          head.addEventListener('click', () => {
            haptic();
            if (isOpen) { open.delete(gid); if (here) open.add('!' + gid); } else { open.add(gid); open.delete('!' + gid); }
            render(true);
          });
          const block = h('div', 'dc-block' + (isOpen ? ' open' : ''), head);
          if (isOpen) {
            block.append(h('div', 'dc-chans', ...chans.map((c) => {
              const b = h('button', 'dc-chan' + (c.id === s.channelId ? ' cur' : '') + (busy === 'join' + c.id ? ' busy' : ''), '🔊 ' + c.name);
              if (ctl && !busy && c.id !== s.channelId) b.addEventListener('click', () => act('join', c.id));
              return b;
            })));
          }
          return block;
        })));
      }
      if (o.notifications !== false && s.notifications.length) {
        kids.push(h('div', 'dc-notes', ...s.notifications.slice(0, 6).map((n) => h('div', 'dc-note', h('b', null, n.title), h('span', null, n.body)))));
      }
      box.replaceChildren(...kids);
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
const HA_MAX_ITEMS = 200; // keep in step with HomeAssistant.MaxItems in the agent
const HA_BATCH_CHARS = 6000; // encoded ids per /ha/states request, well under the agent's 16 KB request line
export const HA_ACTIVE = new Set(['on', 'open', 'opening', 'unlocked', 'playing', 'home', 'cleaning', 'heat', 'cool', 'heat_cool', 'auto', 'detected']);

export function haState(e) {
  const s = e.state, dom = e.id.split('.')[0], dc = e.deviceClass;
  if (s === 'unavailable' || s === 'unknown') return s === 'unknown' ? '—' : t('w.unavailable');
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
    // "items" (the entity editor) wins over the older comma-separated "entities"; an item without its own
    // "control" follows the widget's. The agent checks the same rule before it switches anything.
    const items = [];
    const seenIds = new Set();
    const listed = Array.isArray(o.items) ? o.items : [];
    for (const it of listed) {
      const id = it && typeof it.id === 'string' ? it.id.trim() : '';
      if (!id.includes('.') || seenIds.has(id)) continue;
      if (seenIds.size >= HA_MAX_ITEMS) break; // the cap counts usable rows, like the agent's
      seenIds.add(id);
      items.push({ id, name: String(it.name || '').trim(), icon: String(it.icon || '').trim(), showName: it.showName !== false, showIcon: it.showIcon !== false,
        control: typeof it.control === 'boolean' ? it.control : o.control === true });
    }
    // like the agent: a non-empty "items" list is final, even when none of its rows is usable
    if (!listed.length) for (const id of String(o.entities || '').split(/[\s,;]+/).filter((x) => x.includes('.')).slice(0, HA_MAX_ITEMS)) items.push({ id, name: '', icon: '', showName: true, showIcon: true, control: o.control === true });
    const ids = items.map((i) => i.id);
    let data = null, lastKey = '', busy = null;
    const grid = h('div', 'ha-grid');
    if (o.title) box.append(h('div', 'w-title', o.title));
    box.append(grid);
    // 200 long ids would outgrow the agent's request line, so ask in batches and merge the answers
    const batches = [];
    for (let cur = [], len = 0, i = 0; i <= ids.length; i++) {
      const n = i < ids.length ? encodeURIComponent(ids[i]).length + 3 : 0;
      if (cur.length && (i === ids.length || len + n > HA_BATCH_CHARS)) { batches.push(cur); cur = []; len = 0; }
      if (i < ids.length) { cur.push(ids[i]); len += n; }
    }
    const load = async () => {
      try {
        const parts = await Promise.all(batches.map((b) => api('/ha/states?ids=' + encodeURIComponent(b.join(',')))));
        if (parts.length) data = { ...parts[0], entities: parts.flatMap((p) => p.entities || []) };
      } catch (_) {}
    };
    const toggle = async (e) => {
      haptic();
      busy = e.id; render(true);
      try {
        const r = await fetch(`${API}/ha/toggle/${encodeURIComponent(e.id)}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || 'Could not switch it', 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      setTimeout(async () => { await load(); busy = null; render(true); }, 700);
    };
    const render = (force = false) => {
      const key = JSON.stringify([data, busy]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!ids.length) { grid.replaceChildren(h('div', 'empty', t('w.ha.empty'))); return; }
      if (!data) { grid.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!data.configured) { grid.replaceChildren(h('div', 'empty', data.error)); return; }
      const byId = new Map(data.entities.map((e) => [e.id, e]));
      grid.replaceChildren(...items.map((it) => {
        const e = byId.get(it.id) || { id: it.id, state: 'unknown', name: it.id };
        const dom = e.id.split('.')[0];
        const active = HA_ACTIVE.has(e.state);
        const icon = it.icon || (dom === 'lock' ? (e.state === 'unlocked' ? '🔓' : '🔒') : HA_CLASS_ICONS[e.deviceClass] || HA_ICONS[dom] || '◻️');
        const can = it.control && HA_SWITCHABLE.has(dom) && e.state !== 'unavailable';
        const tile = h('div', 'ha-tile' + (active ? ' on' : '') + (can ? ' can' : '') + (busy === e.id ? ' busy' : '') + (e.state === 'unavailable' ? ' gone' : ''),
          ...(it.showIcon ? [h('span', 'ha-ico', icon)] : []), ...(it.showName ? [h('span', 'ha-name', it.name || e.name)] : []), h('b', 'ha-state', haState(e)));
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
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      busy = null; open = null;
      await load(); render(true);
    };
    const load = async () => { try { data = await api('/docker'); } catch (_) {} };
    const render = (force = false) => {
      const snap = data?.snapshot;
      const matched = (snap?.containers || []).filter((c) => !filter
        || c.name.toLowerCase().includes(filter) || (c.project || '').toLowerCase().includes(filter));
      const items = o.stopped === false ? matched.filter((c) => c.state === 'running') : matched;
      const key = JSON.stringify([!!data, snap?.available, snap?.error, snap?.updated === 0, matched.length, items, data?.control, open, busy, box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      const running = items.filter((c) => c.state === 'running').length;
      head.querySelector('.dk-count').textContent = snap?.available && (!snap.error || matched.length) ? ` ${running}/${matched.length}` : '';
      if (!data || (!snap?.updated && !snap?.error)) { list.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!snap.available || (snap.error && !items.length)) { list.replaceChildren(h('div', 'empty', snap.error || 'Docker is not running')); return; }
      if (!items.length) {
        const msg = matched.length
          ? `No running containers (${matched.length} stopped)`
          : filter ? `No containers match "${o.filter}"` : 'No containers';
        list.replaceChildren(h('div', 'empty', msg));
        return;
      }
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

// The computer's installed applications (macOS): search, tap one to open it (or bring it forward) on the computer.
// The phone only sends the app's id; the agent opens apps from its own scan.
W.apps = {
  needs: ['tick'],
  create(box, { o }) {
    const pinned = String(o.pinned || '').split(',').map((s) => s.trim().toLowerCase()).filter(Boolean);
    const confirm = o.confirm !== false; // first tap selects, a second tap within a few seconds opens
    let data = null, q = '', busy = null, armed = null, armTimer = 0, lastKey = '';
    const disarm = () => { clearTimeout(armTimer); if (armed) { armed = null; render(true); } };
    const head = h('div', 'w-title', o.title || 'Apps', h('span', 'ap-count'));
    const search = h('input', 'ap-search');
    search.type = 'search';
    search.placeholder = 'Search apps';
    search.autocomplete = 'off';
    search.spellcheck = false;
    search.addEventListener('input', () => { q = search.value.trim().toLowerCase(); render(true); });
    const grid = h('div', 'ap-grid');
    box.append(head, ...(o.search === false ? [] : [search]), grid);

    const load = async () => { try { data = await api('/apps', 'GET', 10_000); } catch (_) { return false; } };
    const open = async (a) => {
      if (!data?.canOpen) { toast('Opening apps needs an All apps widget saved on a page'); return; }
      haptic();
      busy = a.id; render(true);
      try {
        const r = await fetch(`${API}/apps/open?id=${encodeURIComponent(a.id)}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        const j = await r.json().catch(() => ({}));
        if (r.ok) toast(`Opening ${a.name}`, 'ok'); else toast(j.error || `Could not open ${a.name}`, 'err');
      } catch (_) { toast('The PC did not answer', 'err'); }
      busy = null; render(true);
    };
    const rank = (a) => { const i = pinned.indexOf(a.name.toLowerCase()); return i < 0 ? pinned.length : i; };
    const tile = (a) => {
      const img = h('img', 'ap-ico');
      img.loading = 'lazy';
      img.decoding = 'async';
      img.alt = '';
      const src = `${API}/apps/icon?id=${encodeURIComponent(a.id)}`;
      img.src = src;
      // The first conversion of an icon can outlast the request on a busy computer; show the initial, try once more later.
      img.addEventListener('error', () => {
        const letter = h('span', 'ap-ico ap-letter', a.name.slice(0, 1).toUpperCase());
        img.replaceWith(letter);
        setTimeout(() => {
          if (!letter.isConnected) return;
          const again = h('img', 'ap-ico');
          again.alt = '';
          again.addEventListener('load', () => { if (letter.isConnected) letter.replaceWith(again); }, { once: true });
          again.src = src + '&retry=1';
        }, 15_000);
      }, { once: true });
      const isArmed = armed === a.id;
      const t = h('button', 'ap-tile' + (busy === a.id ? ' busy' : '') + (isArmed ? ' armed' : ''), img,
        h('span', 'ap-name', isArmed ? 'Tap again to open' : a.name));
      t.title = a.name;
      t.addEventListener('click', () => {
        if (!confirm || armed === a.id) { clearTimeout(armTimer); armed = null; open(a); return; }
        haptic();
        armed = a.id;
        clearTimeout(armTimer);
        armTimer = setTimeout(disarm, 4000);
        render(true);
      });
      return t;
    };
    const render = (force = false) => {
      const all = data?.apps || [];
      const items = all.filter((a) => !q || a.name.toLowerCase().includes(q))
        .sort((x, y) => rank(x) - rank(y) || x.name.localeCompare(y.name));
      const key = JSON.stringify([items.map((a) => a.id), busy, armed, data?.supported, data?.canOpen]);
      if (!force && key === lastKey) return;
      lastKey = key;
      head.querySelector('.ap-count').textContent = all.length ? ` ${q ? items.length + '/' : ''}${all.length}` : '';
      if (!data) { grid.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!data.supported) { grid.replaceChildren(h('div', 'empty', 'The apps list is available on macOS for now')); return; }
      if (!items.length) { grid.replaceChildren(h('div', 'empty', q ? `No apps match "${search.value.trim()}"` : 'No apps found')); return; }
      grid.replaceChildren(...items.map(tile));
    };
    const tick = every(60_000, async () => { const ok = await load(); render(); return ok; }, 5000);
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
    const hm = (ms) => fmtHM(new Date(ms));
    const dayKey = (ms) => new Date(ms).toDateString();
    const dayName = (ms) => {
      const d = new Date(ms), now = new Date();
      const diff = Math.round((new Date(d.toDateString()) - new Date(now.toDateString())) / 864e5);
      return diff === 0 ? t('time.today') : diff === 1 ? t('time.tomorrow') : d.toLocaleDateString(LOCALE, { weekday: 'short', day: 'numeric', month: 'short' });
    };
    const inMin = (ms) => {
      const m = Math.round((ms - Date.now()) / 60000);
      return m < 1 ? t('time.now') : m < 60 ? t('time.inDur', { dur: t('time.min', { m }) }) : m < 24 * 60 ? t('time.inDur', { dur: fmtDur(ms - Date.now()) }) : '';
    };
    const render = () => {
      const cal = D.calendar;
      const now = Date.now();
      const today = new Date(now);
      // Whole days follow the local calendar even when the clocks change, also where a day starts at 01:00 because
      // its midnight is skipped (Santiago): the end is built from the date, not moved on from today's start.
      const wholeDays = Math.floor(days);
      const until = new Date(today.getFullYear(), today.getMonth(), today.getDate() + wholeDays).getTime() + (days - wholeDays) * 864e5;
      const list = (cal?.events || []).filter((e) => e.end > now && e.start < until && (!only.length || only.includes(e.calendar.toLowerCase())));
      const small = box.clientHeight < 110;
      const key = JSON.stringify([list.map((e) => e.start + e.title), Math.floor(now / 30000), small, box.clientHeight]);
      if (key === lastKey) return;
      lastKey = key;
      box.classList.toggle('small', small);
      if (!cal) { box.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!cal.updated && !list.length) { box.replaceChildren(h('div', 'w-title', o.title || 'Agenda'), h('div', 'empty', 'Add a calendar in Settings › General')); return; }
      const timed = list.filter((e) => !e.allDay);
      const cur = timed.find((e) => e.start <= now);
      const next = timed.find((e) => e.start > now);
      if (small) {
        const e = cur || next || list[0];
        if (!e) { box.replaceChildren(h('div', 'ag-big-t', 'No events'), h('div', 'ag-big-s', `next ${days} days`)); return; }
        box.style.setProperty('--c', e.color);
        box.replaceChildren(
          h('div', 'ag-big-s', e === cur ? `Now · until ${hm(e.end)}` : e.allDay ? dayName(e.start) + ' · all day' : `${dayName(e.start) === t('time.today') ? inMin(e.start) || t('time.today') : dayName(e.start)} · ${hm(e.start)}`),
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
    box.classList.add('w-onair');
    const sign = h('div', 'oa-sign', o.text || o.title || 'ON AIR'), sub = h('div', 'oa-sub');
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
W['meeting-status'] = W.onair;

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

// The computers this phone is paired with, one tap to switch (as in the page overview). A USB cable always wins, so
// the buttons wait while one is plugged in.
W.pcswitch = {
  needs: ['state'],
  create(box, { o }) {
    const row = h('div', 'pcs-row'), sub = h('div', 'pcs-sub');
    box.append(o.title === '' ? '' : h('div', 'w-title', o.title || 'Computers'), row, sub);
    let shown = '';
    const render = () => {
      const pcs = pairedPcs(), usb = transport() === 'usb';
      const key = JSON.stringify([pcs, usb, !!bridge]);
      if (key === shown) return;
      shown = key;
      if (!bridge) { row.replaceChildren(h('div', 'empty', 'Only on the phone')); sub.textContent = ''; return; }
      if (pcs.length < 2) {
        row.replaceChildren(h('div', 'empty', pcs.length ? 'Pair another computer over Wi-Fi to switch here' : 'No computer paired over Wi-Fi'));
        sub.textContent = '';
        return;
      }
      row.replaceChildren(...pcs.map((pc) => {
        const b = h('button', 'ov-pc' + (pc.active && !usb ? ' on' : ''), '🖥️ ' + (pc.name || t('panel.computer')));
        b.disabled = usb;
        b.addEventListener('click', (e) => { e.stopPropagation(); switchPc(pc); });
        return b;
      }));
      sub.textContent = usb ? 'USB cable connected: the panel uses that computer' : '';
    };
    render();
    return { update: render };
  },
};

W.topprocs = {
  needs: ['procs', 'state'],
  create(box, { o }) {
    const byMem = o.sort === 'mem';
    const list = h('div', 'tp-list');
    box.style.setProperty('--c', '#f472b6');
    const title = h('div', 'w-title', byMem ? 'Top memory' : 'Top CPU');
    box.append(title, list);
    const rowMap = new Map();
    return {
      update() {
        const pc = D.state?.pc;
        if (byMem && pc?.ramTotalGb) {
          const pct = Math.round((pc.ramUsedGb / pc.ramTotalGb) * 100);
          title.textContent = `Top memory (${pct}% · ${Math.round(pc.ramUsedGb)}/${Math.round(pc.ramTotalGb)} GB)`;
        } else if (!byMem && pc?.cpu != null) {
          title.textContent = `Top CPU (${Math.round(pc.cpu)}%)`;
        }
        const data = D.procs;
        if (!data?.groups?.length) { rowMap.clear(); list.replaceChildren(h('div', 'empty', t('w.measuring'))); return; }
        if (list.querySelector('.empty')) list.replaceChildren();
        const n = Math.max(1, Math.floor((box.clientHeight - 30) / 24));
        const top = [...data.groups].sort((a, b) => (byMem ? b.memMb - a.memMb : b.cpu - a.cpu)).slice(0, n);
        const keep = new Set(top.map((g) => g.name));
        for (const [k, el] of rowMap) if (!keep.has(k)) { el.remove(); rowMap.delete(k); }
        for (const g of top) {
          let row = rowMap.get(g.name);
          if (!row) {
            const ico = h('div', 'p-ico');
            fillIcon(ico, g);
            row = h('div', 'tp-i', ico, h('span', 'tp-n'), h('b'));
            row.addEventListener('click', () => { haptic(); openSheet(row._g, canKillAnywhere()); });
            rowMap.set(g.name, row);
          }
          row._g = g;
          row.querySelector('.tp-n').textContent = g.title + (g.count > 1 ? ` ×${g.count}` : '');
          row.querySelector('b').textContent = byMem ? fmtMem(g.memMb) : g.cpu.toFixed(1) + '%';
          list.appendChild(row);
        }
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
    let dragging = false, timer = 0, dragEndTimer = 0, level = 0;
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
    bar.addEventListener('pointerdown', (e) => { clearTimeout(dragEndTimer); dragEndTimer = 0; dragging = true; try { bar.setPointerCapture(e.pointerId); } catch (_) {} fromPointer(e); });
    bar.addEventListener('pointermove', (e) => { if (dragging) fromPointer(e); });
    bar.addEventListener('pointerup', () => { clearTimeout(dragEndTimer); dragEndTimer = setTimeout(() => { dragEndTimer = 0; dragging = false; }, 400); });
    const cancelDrag = () => { clearTimeout(dragEndTimer); dragEndTimer = 0; clearTimeout(timer); dragging = false; };
    bar.addEventListener('pointercancel', cancelDrag);
    bar.addEventListener('lostpointercapture', () => { if (dragging && !dragEndTimer) dragging = false; });
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
    box.append(h('div', 'w-title', o.title || t('s.nav.rules'), btn), list);
    btn.addEventListener('click', (e) => { e.stopPropagation(); toggleRulesPause(); });
    let key = '';
    return {
      update() {
        const rules = (cfg.rules || []).filter((r) => r.enabled !== false);
        const active = D.state?.rules || [];
        const paused = rulesPaused && rulesPaused > Date.now();
        const k = JSON.stringify([rules, active, paused]);
        if (k === key) return;
        key = k;
        btn.textContent = paused ? '▶ Resume' : '⏸ Pause';
        btn.hidden = !D.state;
        box.classList.toggle('paused', !!paused);
        if (!rules.length) { list.replaceChildren(h('div', 'empty', 'Add rules in Settings › Rules')); return; }
        const sorted = [...rules].sort((a, b) => active.includes(b.id) - active.includes(a.id));
        list.replaceChildren(...(paused ? [h('div', 'ru-note', `Paused until ${fmtHM(new Date(rulesPaused))}`)] : []),
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
    const hm = (ms) => fmtHM(new Date(ms));
    return {
      update() {
        const cal = D.calendar, now = Date.now();
        const evs = (cal?.events || []).filter((e) => !e.allDay && e.end > now && (!only.length || only.includes(e.calendar.toLowerCase()))).sort((a, b) => a.start - b.start);
        const cur = evs.find((e) => e.start <= now), next = evs.find((e) => e.start > now);
        const ev = cur || next;
        const k = JSON.stringify([ev?.start, ev?.title, Math.floor(now / 15000), !!cal]);
        if (k === key) return;
        key = k;
        box.classList.toggle('now', !!cur);
        track.hidden = !cur;
        if (!ev) {
          when.textContent = ''; meta.textContent = '';
          title.textContent = !cal ? t('w.loading') : cal.updated || evs.length ? t('w.noEvents') : 'Add a calendar in Settings › General';
          return;
        }
        const left = Math.round(((cur ? ev.end : ev.start) - now) / 60000);
        const dur = left < 60 ? t('time.min', { m: Math.max(1, left) }) : fmtDur((cur ? ev.end : ev.start) - now);
        const sameDay = new Date(ev.start).toDateString() === new Date().toDateString();
        when.textContent = cur ? t('w.nowLeft', { dur }) : sameDay || left < 12 * 60 ? t('time.inDur', { dur }) : new Date(ev.start).toLocaleDateString(LOCALE, { weekday: 'long' });
        title.textContent = ev.title;
        meta.textContent = [`${hm(ev.start)}–${hm(ev.end)}`, ev.location, cur && next ? `then ${next.title} at ${hm(next.start)}` : ''].filter(Boolean).join(' · ');
        if (cur) bar.style.width = Math.min(100, ((now - ev.start) / (ev.end - ev.start)) * 100) + '%';
        box.style.setProperty('--nu', ev.color || 'rgb(var(--ambient))');
      },
    };
  },
};

// One GPU in detail: load, memory, temperatures, power, clocks and fan. NVIDIA comes from nvidia-smi; AMD and Intel
// from Windows' own GPU counters (temperature, VRAM, load), richer with LibreHardwareMonitor (hot spot, watts, fan, clocks).
const GPU_VENDOR = { nvidia: 'NVIDIA', amd: 'AMD', intel: 'Intel', apple: 'Apple', qualcomm: 'Qualcomm' };
const GPU_SOURCE = { 'nvidia-smi': 'nvidia-smi', lhm: 'LibreHardwareMonitor', windows: 'Windows', sysfs: 'sysfs', ioreg: 'ioreg' };

W.gpu = {
  needs: ['state'],
  create(box, { o }) {
    const idx = Math.max(0, Math.min(3, Number(o.adapter) || 0));
    box.style.setProperty('--c', cssVar('--gpu'));
    const label = h('span', 's-label', 'GPU'), name = h('span', 's-name');
    const big = h('div', 'gd-big'), rows = h('div', 'meters gd-rows'), chips = h('div', 'chips gd-chips');
    box.append(h('div', 's-head', label, name), big, rows, chips);
    const meter = (title, txt, pct, c) => {
      const e = h('div', 'meter', h('div', 'm-row', h('span', null, title), h('span', null, txt)), h('div', 'track', h('i')));
      e.querySelector('i').style.width = Math.max(0, Math.min(100, pct || 0)) + '%';
      if (c) e.style.setProperty('--c', c);
      return e;
    };
    const heat = (t, max) => (t >= max - 5 ? '#ef4444' : t >= max - 15 ? '#f59e0b' : '#f97316');
    return {
      update() {
        const g = gpusOf(D.state)[idx];
        if (!g) {
          label.textContent = 'GPU';
          name.textContent = '';
          big.replaceChildren(h('div', 'empty', !D.state ? 'Waiting…' : idx ? 'No second GPU' : 'No GPU data'));
          rows.replaceChildren();
          chips.replaceChildren();
          return;
        }
        label.textContent = (GPU_VENDOR[g.vendor] || 'GPU') + (g.integrated ? ' · iGPU' : '');
        name.textContent = shortGpu(g.name);
        name.title = g.name;

        const temps = [g.temp != null ? `${Math.round(g.temp)}°C` : null, g.hotSpot != null ? `hot spot ${Math.round(g.hotSpot)}°` : null,
          g.memTemp != null ? `memory ${Math.round(g.memTemp)}°` : null].filter(Boolean).join(' · ');
        big.replaceChildren(h('b', null, g.util != null ? String(Math.round(g.util)) : '—'), h('small', null, '% load'), h('span', 'gd-temp', temps));

        const list = [];
        const mem = gpuMem(g);
        if (mem) list.push(meter(mem.label === 'VRAM' ? 'VRAM' : 'GPU memory', `${r1(mem.used)} / ${Math.round(mem.total)} GB`, (mem.used / mem.total) * 100));
        if (!g.integrated && g.sharedTotal && g.sharedUsed != null) list.push(meter('Shared memory', `${r1(g.sharedUsed)} GB`, (g.sharedUsed / g.sharedTotal) * 100, '#64748b'));
        if (g.power != null) {
          list.push(meter('Power', g.powerLimit ? `${Math.round(g.power)} / ${Math.round(g.powerLimit)} W` : `${Math.round(g.power)} W`,
            g.powerLimit ? (g.power / g.powerLimit) * 100 : g.powerPct, '#facc15'));
        } else if (g.powerPct != null) list.push(meter('Power', `${Math.round(g.powerPct)}% of limit`, g.powerPct, '#facc15'));
        const hottest = Math.max(g.temp ?? -1, g.hotSpot ?? -1);
        if (hottest >= 0) {
          const max = g.tempMax || (g.hotSpot != null ? 110 : 100);
          list.push(meter('Temperature', `${Math.round(hottest)} / ${Math.round(max)}°C`, (hottest / max) * 100, heat(hottest, max)));
        }
        rows.replaceChildren(...list);

        chips.replaceChildren(...[
          g.clock != null ? `Core ${Math.round(g.clock)} MHz` : null,
          g.memClock != null ? `Memory ${Math.round(g.memClock)} MHz` : null,
          g.fan != null || g.fanRpm != null ? `Fan ${[g.fan != null ? Math.round(g.fan) + '%' : null, g.fanRpm != null ? Math.round(g.fanRpm) + ' rpm' : null].filter(Boolean).join(' · ')}` : null,
          g.driver ? `Driver ${g.driver}` : null,
          g.source ? g.source.split('+').map((x) => GPU_SOURCE[x] || x).join(' + ') : null,
        ].filter(Boolean).map((t) => h('span', 'chip', t)));
      },
    };
  },
};
