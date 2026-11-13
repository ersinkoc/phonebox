import { API, LOCALE, PANEL_HEADERS, api, h, haptic, lastPluginEvent, toast } from '../main.js';
import { W } from '../registry.js';
import { dialEl, every } from './helpers.js';

// Declarative and sandboxed HTML widget renderer for community plugins (widget types "plugin:<id>[.<subType>]").
const PLUGIN_HTML_PRELUDE = `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="dark"><style>:root{color-scheme:dark}html,body{margin:0;height:100%;background:transparent;color:#eef2f6;font-family:Roboto,"Segoe UI",system-ui,sans-serif;overflow:hidden}</style>
<script>(function(){var subs={},data={};window.phonebox={data:data,
on:function(k,f){(subs[k]=subs[k]||[]).push(f);if(data[k]!==undefined)try{f(data[k])}catch(e){console.error(e)}},
act:function(op,arg){parent.postMessage({phonebox:'plugin-act',op:String(op),arg:arg==null?'':String(arg)},'*')}};
addEventListener('message',function(e){var m=e.data;if(!m||m.phonebox!=='plugin-data')return;data[m.kind]=m.value;(subs[m.kind]||[]).forEach(function(f){try{f(m.value)}catch(err){console.error(err)}})});})();<\/script>`;

const toneClass = (tone) => {
  if (tone === 'ok' || tone === 'success' || tone === 'green') return 'ok';
  if (tone === 'warn' || tone === 'warning') return 'warn';
  if (tone === 'err' || tone === 'error' || tone === 'danger' || tone === 'fail') return 'err';
  return '';
};

const fmtVal = (v) => {
  if (v == null) return '—';
  if (typeof v === 'number' && Number.isFinite(v)) return v.toLocaleString(LOCALE);
  return String(v);
};

function pluginIdOf(type) {
  if (typeof type !== 'string' || !type.startsWith('plugin:')) return '';
  return type.slice(7).split('.')[0];
}

function emptyTextFor(state) {
  if (!state) return 'Loading plugin data…';
  if (state.status === 'missing') return 'Plugin not installed — open Settings › Plugins';
  if (state.status === 'disabled') return 'Plugin disabled — enable in Settings › Plugins';
  if (state.status === 'not_found') return 'Widget configuration missing — save in Settings';
  if (state.status === 'error') return 'Plugin error — ' + String(state.error || 'Unavailable');
  if (state.status === 'loading') return 'Loading plugin data…';
  return 'Waiting for plugin data…';
}

W['plugin:*'] = {
  needs: ['tick', 'plugin'],
  create(box, { page, w, o }) {
    const pluginId = pluginIdOf(w.type);
    const dial = dialEl();
    let state = null;
    let loading = false;
    let destroyed = false;
    let lastKey = '';
    let frame = null;
    let renderedHtml = null;

    const pushFrameData = () => {
      if (!frame?.contentWindow || !state) return;
      try {
        frame.contentWindow.postMessage({ phonebox: 'plugin-data', kind: 'data', value: state.data ?? null }, '*');
        frame.contentWindow.postMessage({
          phonebox: 'plugin-data',
          kind: 'state',
          value: { data: state.data ?? null, controlAllowed: state.controlAllowed !== false && o.control !== false, options: o },
        }, '*');
      } catch (_) {}
    };

    const runAct = async (op, arg, btn = null) => {
      if (!pluginId || !op) return;
      if (state?.controlAllowed === false || o.control === false) {
        toast('Phone control is turned off in widget settings', 'err');
        return;
      }
      haptic();
      if (btn) { btn.disabled = true; btn.classList.add('busy'); }
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 6000);
      try {
        const qs = `page=${encodeURIComponent(page.id)}&widget=${encodeURIComponent(w.id)}&op=${encodeURIComponent(op)}${arg != null && arg !== '' ? `&arg=${encodeURIComponent(arg)}` : ''}`;
        const r = await fetch(`${API}/plugin/${encodeURIComponent(pluginId)}/act?${qs}`, {
          method: 'POST',
          signal: ctl.signal,
          cache: 'no-store',
          headers: PANEL_HEADERS,
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok || j.ok === false) {
          toast(j.error || `HTTP ${r.status}`, 'err');
          return;
        }
        if (j.data !== undefined && state) {
          state = { ...state, status: 'ready', error: null, data: j.data };
          render();
        } else {
          await load(true);
        }
      } catch (_) {
        toast('Plugin action failed', 'err');
      } finally {
        clearTimeout(timer);
        if (btn) { btn.disabled = false; btn.classList.remove('busy'); }
      }
    };

    const onFrameMsg = (e) => {
      if (!frame || e.source !== frame.contentWindow || !e.data) return;
      if (e.data.phonebox === 'plugin-act') runAct(String(e.data.op || ''), e.data.arg ?? '');
    };
    window.addEventListener('message', onFrameMsg);

    const buildHeader = (d, ui) => {
      const titleText = o.title || d?.title || ui?.name || pluginId;
      const head = h('div', 'pl-head', h('div', 'w-title', titleText));
      const badgeText = d?.badge || (state?.error ? 'Stale' : '');
      const badgeTone = state?.error ? 'warn' : toneClass(d?.tone || d?.level);
      if (badgeText) head.append(h('span', `pl-badge ${badgeTone}`.trim(), String(badgeText)));
      return head;
    };

    const buildActionButtons = (actions, full = false) => {
      const list = Array.isArray(actions) ? actions.filter((a) => a && a.op) : [];
      if (!list.length) return null;
      const grid = h('div', 'pl-btns' + (full ? ' full' : ''));
      for (const a of list) {
        const cls = ['pl-btn'];
        if (a.active) cls.push('on');
        const t = toneClass(a.tone);
        if (t) cls.push(t);
        const label = [a.icon, a.label || a.name || a.op].filter(Boolean).join(' ');
        const btn = h('button', cls.join(' '), label);
        btn.type = 'button';
        btn.setAttribute('aria-pressed', String(!!a.active));
        btn.onclick = (e) => { e.stopPropagation(); runAct(a.op, a.arg ?? '', btn); };
        grid.append(btn);
      }
      return grid;
    };

    function render() {
      if (destroyed) return;
      const ui = state?.ui;
      if (ui?.kind === 'html' && ui.html && state.status !== 'missing' && state.status !== 'disabled') {
        box.classList.add('bare-html');
        if (!frame || renderedHtml !== ui.html) {
          renderedHtml = ui.html;
          frame = h('iframe', 'html-frame');
          frame.setAttribute('sandbox', 'allow-scripts');
          frame.setAttribute('referrerpolicy', 'no-referrer');
          frame.srcdoc = PLUGIN_HTML_PRELUDE + ui.html;
          frame.addEventListener('load', pushFrameData);
          box.replaceChildren(frame);
        } else {
          pushFrameData();
        }
        return;
      }

      box.classList.remove('bare-html');
      frame = null;
      renderedHtml = null;

      const d = state?.data && typeof state.data === 'object' ? state.data : null;
      if (!state || !d || state.status === 'missing' || state.status === 'disabled' || state.status === 'not_found') {
        const msg = emptyTextFor(state);
        if (lastKey === 'empty:' + msg) return;
        lastKey = 'empty:' + msg;
        box.replaceChildren(h('div', 'empty', msg));
        return;
      }

      const key = JSON.stringify([state.status, state.error, ui?.kind, d, o]);
      if (key === lastKey) return;
      lastKey = key;

      const kind = ui?.kind && ui.kind !== 'auto'
        ? ui.kind
        : (Array.isArray(d.items) || Array.isArray(d.rows) ? 'list'
          : (d.pct != null || d.progress != null ? 'gauge'
            : (d.value != null || d.status != null ? 'stat'
              : (Array.isArray(d.actions) ? 'buttons' : 'stat'))));

      const kids = [buildHeader(d, ui)];

      if (kind === 'gauge') {
        const raw = Number(d.pct ?? d.progress ?? d.value ?? 0);
        const pct = Math.max(0, Math.min(100, Number.isFinite(raw) ? raw : 0));
        dial.set(pct, d.value != null ? fmtVal(d.value) : Math.round(pct), d.unit ?? '%');
        const wrap = h('div', 'pl-gauge', dial);
        if (d.sub || d.label) wrap.append(h('div', 'pl-sub', String(d.sub || d.label)));
        kids.push(wrap);
      } else if (kind === 'buttons') {
        const btns = buildActionButtons(d.actions, true);
        if (d.sub) kids.push(h('div', 'pl-sub', String(d.sub)));
        if (btns) kids.push(btns);
        else kids.push(h('div', 'empty', 'No plugin actions defined'));
      } else if (kind === 'list') {
        const items = Array.isArray(d.items) ? d.items : (Array.isArray(d.rows) ? d.rows : []);
        const list = h('div', 'pl-list');
        if (!items.length) {
          list.append(h('div', 'empty', d.empty || 'No items'));
        } else {
          for (const item of items) {
            if (!item || typeof item !== 'object') continue;
            const title = String(item.title ?? item.name ?? item.label ?? '');
            const sub = item.sub ?? item.detail ?? item.meta ?? '';
            const val = item.value ?? item.badge ?? item.status ?? '';
            const main = h('div', 'pl-row-main', h('div', 'pl-row-t', title));
            if (sub !== '') main.append(h('div', 'pl-row-s', String(sub)));
            const row = h(item.op ? 'button' : 'div', 'pl-row', main);
            if (item.op) {
              row.type = 'button';
              row.onclick = (e) => { e.stopPropagation(); runAct(item.op, item.arg ?? item.id ?? '', row); };
            }
            if (val !== '') row.append(h('span', `pl-row-v ${toneClass(item.tone || item.level)}`.trim(), String(val)));
            list.append(row);
          }
        }
        kids.push(list);
      } else {
        const stat = h('div', 'pl-stat');
        const mainVal = d.value !== undefined ? fmtVal(d.value) : (d.status !== undefined ? String(d.status) : '—');
        const valRow = h('div', 'pl-val-row', h('div', 'pl-val', mainVal));
        if (d.unit) valRow.append(h('span', 'pl-unit', String(d.unit)));
        stat.append(valRow);
        if (d.sub || d.detail || d.label) stat.append(h('div', 'pl-sub', String(d.sub || d.detail || d.label)));
        if (Array.isArray(d.metrics) && d.metrics.length) {
          const kv = h('div', 'pl-kv');
          for (const m of d.metrics.slice(0, 6)) {
            if (!m) continue;
            kv.append(h('div', 'pl-kv-i', h('span', null, String(m.label || '')), fmtVal(m.value)));
          }
          stat.append(kv);
        }
        kids.push(stat);
      }

      if (kind !== 'buttons' && Array.isArray(d.actions) && d.actions.length) {
        const footer = buildActionButtons(d.actions, false);
        if (footer) kids.push(footer);
      }

      box.replaceChildren(...kids);
    }

    async function load(force = false) {
      if (destroyed || loading || !pluginId) return false;
      loading = true;
      try {
        const qs = `page=${encodeURIComponent(page.id)}&widget=${encodeURIComponent(w.id)}${force ? '&force=1' : ''}`;
        state = await api(`/plugin/${encodeURIComponent(pluginId)}/state?${qs}`, 'GET', 5000);
        render();
        return state?.ok !== false;
      } catch (_) {
        render();
        return false;
      } finally {
        loading = false;
      }
    }

    const periodic = every(5_000, () => load(false), 10_000);
    render();

    return {
      update(kind) {
        if (destroyed) return;
        if (kind === 'tick') periodic();
        else if (kind === 'plugin' && (!lastPluginEvent?.plugin || lastPluginEvent.plugin === pluginId)) load(false);
      },
      destroy() {
        destroyed = true;
        window.removeEventListener('message', onFrameMsg);
      },
    };
  },
};
