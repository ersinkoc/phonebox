import { $, API, D, curPage, pages, PANEL_HEADERS, PREVIEW, LOCALE, Q_STYLE, api, bridge, cfg, cfgVersion, configMark, configReadSince, localWriteStored, loadConfig, color, cssVar, rulesPaused, toggleRulesPause, fillIcon, fmtDur, fmtHM, fmtMem, gotoPageRef, gpuMem, gpusOf, h, en, haptic, icon, openSheet, paceInfo, pad, pairedPcs, quotaProviders, r1, shortDur, shortGpu, store, switchPc, t, tick, toast, transport, emo, labelled, rich, tp, tpv, quotaText } from '../main.js';
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
    box.append(h('div', 'w-title', o.title || en('Batteries')), list);
    const load = async () => { try { data = await api('/batteries'); } catch (_) {} };
    const render = () => {
      const devices = [...(data?.devices || [])];
      if (o.phone !== false && bridge) {
        try { const b = JSON.parse(bridge.battery()); if (b.level >= 0) devices.push({ name: tp('This phone'), level: b.level, kind: 'phone', charging: b.plugged }); } catch (_) {}
      }
      const key = JSON.stringify([devices, box.clientHeight]);
      if (key === lastKey) return;
      lastKey = key;
      if (!data && !devices.length) { list.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!devices.length) { list.replaceChildren(h('div', 'empty', tp('No wireless devices report a battery level'))); return; }
      list.replaceChildren(...devices.map((d) => {
        const col = d.level <= low ? '#ef4444' : d.level <= low * 2 ? '#f59e0b' : '#22c55e';
        const row = h('div', 'bt-row' + (d.level <= low ? ' low' : ''),
          h('span', 'bt-ico', emo(BAT_ICONS[d.kind] || '🔋')),
          h('span', 'bt-name', d.name),
          h('span', 'bt-bar', h('i')),
          h('b', 'bt-pct', d.charging ? emo('⚡') : '', d.level + '%'));
        row.style.setProperty('--c', col);
        row.style.setProperty('--v', d.level / 100);
        return row;
      }));
    };
    const tick = every(20_000, async () => { await load(); render(); });
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
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || tp('OBS did not do it'), 'err');
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
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', en('OBS')), h('div', 'empty', tp('Turn on OBS in Settings › General'))); return; }
      if (!s.connected) { box.replaceChildren(h('div', 'w-title', en('OBS')), h('div', 'empty', s.error || t('panel.connecting'))); return; }
      const ctl = data.control;
      const pill = (on, label, cls, time, action) => {
        const el = h('div', 'obs-pill ' + cls + (on ? ' on' : '') + (ctl ? ' can' : '') + (busy === action ? ' busy' : ''), h('span', 'obs-led'), h('b', null, label), h('span', 'obs-time', on ? dur(time + since) : 'off'));
        if (ctl) el.addEventListener('click', () => act(action));
        return el;
      };
      const recLabel = s.recordPaused ? tp('PAUSED') : 'REC';
      const kids = [
        h('div', 'obs-top', pill(s.streaming, 'LIVE', 'live', s.streamMs, 'stream'), pill(s.recording, recLabel, 'rec', s.recordPaused ? s.recordMs - since : s.recordMs, 'record')),
        h('div', 'obs-scene', h('span', null, tp('Scene')), h('b', null, s.scene || '—'),
          s.micMuted != null ? h('span', 'obs-mic' + (s.micMuted ? ' muted' : ''), emo(s.micMuted ? '🔇' : '🎙️')) : null),
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

// ---- Discord "side by side" layout ----------------------------------------------------------------------------
const dcImg = (kind, id, hash) => `${API}/discord/img?kind=${kind}&id=${encodeURIComponent(id)}&hash=${encodeURIComponent(hash)}`;
// a round logo: the image once it has loaded, the first letter until then (and when there is no image)
const dcIco = (src, name, cls) => {
  const w = h('span', cls, (name || '?').trim().charAt(0).toUpperCase() || '?');
  if (src) { const im = new Image(); im.alt = ''; im.onload = () => w.replaceChildren(im); im.src = src; }
  return w;
};
const dcGid = (c) => c.guildId || c.guild || 'other';
// servers picked or joined lately, newest first: the focus layout shows them next to the server switcher
const dcRecent = (gid) => { if (gid) store.set('dc-recent', [gid, ...store.get('dc-recent', []).filter((g) => g !== gid)].slice(0, 6)); return store.get('dc-recent', []); };
// Discord mutes the microphone while you are deafened, so the mic shows off then too
const dcCall = (kind, s) => {
  const on = kind === 'mute' ? s.muted || s.deafened : kind === 'deafen' ? s.deafened : false;
  const ic = kind === 'mute' ? (on ? 'mic-off' : 'mic') : kind === 'deafen' ? (on ? 'headphone-off' : 'headphones') : 'log-out';
  const label = t(kind === 'mute' ? (on ? 'w.discord.micOff' : 'w.discord.mic') : kind === 'deafen' ? (on ? 'w.discord.soundOff' : 'w.discord.sound') : 'w.discord.leave');
  return { on, ic, label };
};
// A voice action, with a deadline over the request and its answer: a stalled one must not leave the buttons busy. It is
// never sent again by itself (it toggles); the caller reads the state afresh instead.
const dcPost = async (path) => {
  const ctl = new AbortController();
  let timer;
  const deadline = new Promise((_, fail) => { timer = setTimeout(() => { ctl.abort(); fail(new Error('timeout')); }, 8000); });
  try {
    await Promise.race([deadline, (async () => {
      const r = await fetch(`${API}/discord/${path}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
      if (!r.ok) toast((await r.json().catch(() => ({}))).error || t('w.discord.failed'), 'err');
    })()]);
  } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); } finally { clearTimeout(timer); }
};
// what the agent's share error codes mean (DiscordShareError.Code)
const DC_SHARE_ERRORS = { off: 'w.discord.shareOff', notAllowed: 'w.discord.shareApprove', unknown: 'w.discord.shareUnknown', noCall: 'w.discord.shareJoin',
  sharing: 'w.discord.shareAlready', gone: 'w.discord.shareGone', notDirect: 'w.discord.shareNotDirect' };
// Start ("app" with a pid, "pick" for Discord's own picker on the computer) or stop a share. Never sent twice by
// itself: the same command starts and stops.
const dcShareAct = async (action, pid) => {
  const ctl = new AbortController();
  let timer;
  const deadline = new Promise((_, fail) => { timer = setTimeout(() => { ctl.abort(); fail(new Error('timeout')); }, 12000); });
  try {
    await Promise.race([deadline, (async () => {
      const r = await fetch(`${API}/discord/share/${action}` + (pid ? '?pid=' + pid : ''), { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
      if (r.ok) return;
      const { code } = await r.json().catch(() => ({}));
      toast(t(DC_SHARE_ERRORS[code] || 'w.discord.failed'), 'err');
    })()]);
  } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); } finally { clearTimeout(timer); }
};
const dcLive = () => h('span', 'dcf-badge', t('w.discord.live'));
const dcVisible = (box, page) => box.isConnected && !document.hidden && pages[curPage] === page && !box.closest('.stack-item:not(.active)');

// The Discord widget's "side by side" layout (layout: focus), sized for a tablet or a large widget: on the left the server switcher (with the
// servers used lately), the channel you are in with who is there, and round call buttons (mic, sound, screen share,
// chat, leave); on the right tabs for the chosen server's voice channels, the chat and the screen share. Uses the same
// agent endpoints as the list layout.
const dcFocus = {
  create(box, { o, page }) {
    box.classList.add('dcf');
    let data = null, lastKey = '', busy = null;
    let sel = null, picking = false, seenChannel = null, lastView = '';
    let tab = 'chan', share = null, shareBusy = null, shareWaiting = 0, shareNext = 0;
    let chat = null, chatPick = false, pickGuild = null, chatAsked = 0, chatWaiting = 0, chatNext = 0;
    const load = async () => { try { data = await api('/discord'); } catch (_) {} };
    const act = async (action, channel) => {
      if (busy) return; // one press at a time: a double tap must not toggle twice
      haptic();
      busy = action + (channel || ''); render(true);
      await dcPost(action + (channel ? '?channel=' + encodeURIComponent(channel) : ''));
      await new Promise((done) => setTimeout(done, 600));
      await load(); busy = null; render(true);
    };
    // The state is asked for beside the window list: an answer older than one already used is dropped, and a state-only
    // answer keeps the list it doesn't carry.
    let shareAsked = 0, shareUsed = 0;
    const loadShare = async (apps) => {
      const n = ++shareAsked;
      shareWaiting++;
      try {
        const r = await api('/discord/share' + (apps ? '?apps=true' : ''), undefined, 8000);
        if (n > shareUsed) {
          shareUsed = n;
          // an app list that comes back after the share tab was left keeps only the state: no name, no apps
          share = apps && tab === 'share' ? r : { ...r, name: apps ? null : r.name, apps: apps ? null : share?.apps ?? null };
        }
      } catch (_) {} finally { shareWaiting--; }
    };
    const loadChat = async () => {
      const n = ++chatAsked; // only the newest answer counts: an older one may be for another server or channel
      const forGuild = chatPick ? pickGuild : null;
      chatWaiting++;
      try {
        const c = await api('/discord/chat' + (chatPick ? '?picker=true' + (pickGuild ? '&guild=' + encodeURIComponent(pickGuild) : '') : ''), undefined, 8000);
        if (n === chatAsked) chat = { ...c, forGuild };
      } catch (_) {} finally { chatWaiting--; }
    };
    const openTab = (to) => {
      // leaving the share view: the shared app's name and the window list are not kept around
      if (tab === 'share' && to !== 'share' && share) share = { ...share, name: null, apps: null };
      tab = to; picking = false; chatPick = false; pickGuild = null;
      if (to === 'chat') { chat = null; loadChat().then(() => render(true)); }
      if (to === 'share') loadShare(true).then(() => render(true));
      render(true);
    };
    const shareAct = async (action, pid) => {
      if (shareBusy) return;
      haptic();
      shareBusy = action + (pid || ''); render(true);
      await dcShareAct(action, pid);
      shareBusy = null;
      // the window list only while the share tab is on screen; off screen the next visit asks for it
      await loadShare(tab === 'share' && visible());
      render(true);
    };
    const chooseChat = async (channel) => {
      haptic();
      chatPick = false; pickGuild = null;
      try { await api('/discord/chat/choose?channel=' + encodeURIComponent(channel), 'POST', 5000); } catch (_) { toast(t('w.discord.failed'), 'err'); }
      // reading marks the messages seen: only while the chat is on screen; otherwise the poll reads it on return
      if (tab === 'chat' && visible()) await loadChat();
      render(true);
    };
    const when = (ms) => {
      if (!ms) return '';
      const d = new Date(ms), now = new Date();
      const time = d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
      return d.toDateString() === now.toDateString() ? time : d.toLocaleDateString(LOCALE, { day: 'numeric', month: 'short' }) + ' ' + time;
    };
    const note = (text) => h('div', 'dcf-note', text);
    const visible = () => dcVisible(box, page);
    // Who is speaking changes several times a second: that only moves the ring on the chips already drawn.
    const rings = new Map();
    const ring = () => { for (const m of data?.snapshot?.members || []) rings.get(m.id)?.classList.toggle('speaking', !!m.speaking); };

    const chatPanel = () => {
      const c = chat;
      if (!c) return [note(t('w.loading'))];
      if (!c.enabled) return [note(t('w.discord.chatOff'))];
      if (!c.ready) return [note(c.error || t('w.discord.chatApprove'))];
      const head = h('button', 'dcf-pick' + (chatPick ? ' open' : ''), icon(c.voice ? 'volume-2' : 'hash'),
        h('span', null, c.channel ? (c.voice ? t('w.discord.voiceChat') + ' · ' : '') + (c.channel.name || '') : t('w.discord.pickChannel')), icon('chevron-down'));
      head.addEventListener('click', () => { haptic(); chatPick = !chatPick; pickGuild = null; loadChat().then(() => render(true)); render(true); });
      const bar = h('div', 'dcf-chatbar', head, h('span', 'dcf-ro', t('w.discord.readOnly')));
      if (data?.widgetControl && c.channel) {
        const open = labelled(h('button', 'dcf-open', icon('external-link'), h('span', null, t('w.discord.openInDiscord'))), t('w.discord.openInDiscord'));
        open.addEventListener('click', async () => { haptic(); try { await api('/discord/chat/open', 'POST', 5000); } catch (_) { toast(t('w.discord.failed'), 'err'); } });
        bar.append(open);
      }
      if (chatPick) {
        const rows = [];
        const dot = (on) => (on ? h('span', 'dcf-dot') : null);
        if (!pickGuild) {
          const v = h('button', 'dcf-prow' + (c.voice ? ' cur' : ''), icon('volume-2'), h('span', null, t('w.discord.voiceChat') + (c.callName ? ': ' + c.callName : '')));
          v.addEventListener('click', () => chooseChat('voice'));
          rows.push(v);
          for (const g of c.servers || []) {
            const b = h('button', 'dcf-prow', dcIco(g.icon && dcImg('guild', g.id, g.icon), g.name, 'dcf-av'), h('span', null, g.name), dot(g.unread), icon('chevron-right'));
            b.addEventListener('click', () => { haptic(); pickGuild = g.id; loadChat().then(() => render(true)); render(true); });
            rows.push(b);
          }
        } else {
          const back = h('button', 'dcf-prow back', icon('arrow-left'), h('span', null, (c.servers || []).find((g) => g.id === pickGuild)?.name || ''));
          back.addEventListener('click', () => { haptic(); pickGuild = null; render(true); });
          rows.push(back);
          // until the answer for this server is in, another server's channels must not show under its name
          const channels = c.forGuild === pickGuild ? c.channels : null;
          if (!channels) rows.push(note(t('w.loading')));
          for (const ch of channels || []) {
            const b = h('button', 'dcf-prow' + (!c.voice && c.channel?.id === ch.id ? ' cur' : ''), icon('hash'), h('span', null, ch.name), dot(ch.unread));
            b.addEventListener('click', () => chooseChat(ch.id));
            rows.push(b);
          }
        }
        return [bar, h('div', 'dcf-plist', ...rows)];
      }
      if (!c.channel) return [bar, note(t('w.discord.pickChannel'))];
      if (c.picker) return [bar, note(t('w.loading'))]; // the picker just closed: its answer holds no messages
      const msgs = c.messages || [];
      const list = h('div', 'dcf-msgs', ...msgs.map((m, i) => {
        const same = i > 0 && msgs[i - 1].authorId === m.authorId && m.ts - msgs[i - 1].ts < 5 * 60000;
        const name = h('b', null, m.author + (m.bot ? ' 🤖' : ''));
        if (m.color) name.style.color = m.color;
        const extra = [m.attachments ? '📎 ' + m.attachments : '', !m.text && m.embed ? '🔗 ' + m.embed : ''].filter(Boolean).join(' · ');
        return h('div', 'dcf-msg' + (same ? ' cont' : ''), same ? h('span', 'dcf-mgap') : dcIco(m.avatar && m.authorId && dcImg('user', m.authorId, m.avatar), m.author, 'dcf-mav'),
          h('div', 'dcf-mb', same ? null : h('div', 'dcf-who', name, h('small', null, when(m.ts))), m.text ? h('div', 'dcf-mt', m.text) : null, extra ? h('div', 'dcf-mx', extra) : null));
      }));
      if (!msgs.length) list.append(note(t('w.discord.noMessages')));
      return [bar, list];
    };

    const sharePanel = () => {
      const c = share;
      if (!c) return [note(t('w.loading'))];
      if (!c.enabled) return [note(t('w.discord.shareOff'))];
      if (!c.ready) return [note(c.refused ? t('w.discord.shareRefused') : c.error || t('w.discord.shareApprove'))];
      if (c.active === null) return [note(t('w.discord.shareUnknown'))];
      if (c.active) {
        const stop = h('button', 'dcf-stop' + (shareBusy === 'stop' ? ' busy' : ''), icon('screen-share-off'), h('span', null, t('w.discord.shareStop')));
        if (!shareBusy) stop.addEventListener('click', () => shareAct('stop')); else stop.disabled = true;
        return [h('div', 'dcf-livebox', dcLive(), h('div', 'dcf-lt', h('b', null, c.name ? t('w.discord.sharing', { name: c.name }) : t('w.discord.sharingNow'))), stop)];
      }
      const out = [];
      if (!c.inCall) out.push(note(t('w.discord.shareJoin')));
      const pick = h('button', 'dcf-pickpc' + (shareBusy === 'pick' ? ' busy' : ''), icon('monitor'), h('span', null, t('w.discord.sharePick')));
      if (c.inCall && !shareBusy) pick.addEventListener('click', () => shareAct('pick')); else pick.disabled = true;
      out.push(pick);
      if (c.apps?.length) {
        out.push(h('div', 'dcf-shint', t('w.discord.shareHint')));
        out.push(h('div', 'dcf-grid', ...c.apps.map((a) => {
          const b = h('button', 'dcf-app' + (shareBusy === 'app' + a.pid ? ' busy' : ''), h('span', 'dcf-appi', (a.name || '?').trim().charAt(0).toUpperCase()), h('span', null, a.name));
          if (c.inCall && !shareBusy) b.addEventListener('click', () => shareAct('app', a.pid)); else b.disabled = true;
          return b;
        })));
      }
      return out;
    };

    const renderFocus = (s, ctl) => {
      const joinControl = data.joinControl ?? ctl;
      // screen share switched off (or the Discord widget's Control): nothing of it is kept, the shared app's name included
      if (!data.share) share = null;
      const servers = new Map();
      for (const c of s.channels) { const g = dcGid(c); if (!servers.has(g)) servers.set(g, []); servers.get(g).push(c); }
      const here = s.channelId ? s.channels.find((c) => c.id === s.channelId) : null;
      // Discord lists the channels one server at a time: until the one you are in has come in, it is looked for again
      if (s.channelId !== seenChannel && (here || !s.channelId)) {
        seenChannel = s.channelId;
        if (here) { sel = dcGid(here); dcRecent(sel); store.set('dc-last', { id: here.id }); }
      }
      if (!sel || !servers.has(sel)) sel = dcRecent().find((g) => servers.has(g)) || servers.keys().next().value || null;
      const sname = (gid) => servers.get(gid)?.[0]?.guild || t('w.discord.otherServer');
      const gico = (gid) => { const c = servers.get(gid)?.[0]; return dcIco(c?.guildIcon && c?.guildId && dcImg('guild', c.guildId, c.guildIcon), sname(gid), 'dcf-sico'); };
      const pick = (gid) => { haptic(); sel = gid; picking = false; dcRecent(gid); render(true); };
      const switcher = h('button', 'dcf-switch' + (picking ? ' open' : ''), sel ? gico(sel) : null, h('span', 'dcf-sname', sel ? sname(sel) : t('w.discord.servers')), icon('chevron-down'));
      // through openTab, so leaving the share tab for the server list drops the shared app's name and the app list too
      switcher.addEventListener('click', () => { haptic(); const open = !picking; openTab('chan'); picking = open; render(true); });
      const recents = dcRecent().filter((g) => g !== sel && servers.has(g)).slice(0, 3).map((g) => {
        const b = labelled(h('button', 'dcf-recent', gico(g)), sname(g));
        b.addEventListener('click', () => pick(g));
        return b;
      });
      const live = !!(s.channelId && share?.active);
      // Push to talk without a key in Discord: nobody hears you. Say so, as the list layout does, with the way back to
      // voice activity when the widget may switch the input mode.
      const ptt = () => {
        if (s.inputMode !== 'PUSH_TO_TALK' || s.pttKey) return null;
        const fix = joinControl ? h('button', 'dcf-warnbtn' + (busy === 'inputmode' ? ' busy' : ''), t('w.a.useVoiceActivity')) : null;
        if (fix) { if (!busy) fix.addEventListener('click', () => act('inputmode')); else fix.disabled = true; }
        return h('div', 'dcf-warn', h('span', null, t('w.discord.pttNoKey')), fix);
      };
      let mid;
      if (s.channelId) {
        mid = h('div', 'dcf-now', h('span', 'dcf-label', t('w.discord.voiceChannel'), live ? dcLive() : null), h('div', 'dcf-name', s.channelName || ''),
          h('div', 'dcf-chips', ...s.members.map((m) => {
            const chip = h('span', 'dcf-chip' + (m.speaking ? ' speaking' : '') + (m.deafened ? ' deaf' : m.muted ? ' muted' : ''),
              dcIco(m.avatar && dcImg('user', m.id, m.avatar), m.name, 'dcf-av'), h('span', null, m.name));
            rings.set(m.id, chip);
            return chip;
          })),
          live ? h('div', 'dcf-hint', share.name ? t('w.discord.sharing', { name: share.name }) : t('w.discord.sharingNow')) : null,
          ptt());
      } else {
        // only the id is kept on the phone; the name comes from the list Discord sends. Like the channel tiles, the way
        // back follows the Channels option.
        const lastId = store.get('dc-last', null)?.id;
        const last = o.channels !== false && lastId ? s.channels.find((c) => c.id === lastId) : null;
        const back = last ? h('button', 'dcf-back', icon('rotate-ccw'), h('span', null, t('w.discord.backTo', { name: last.name + (last.guild ? ' · ' + last.guild : '') }))) : null;
        if (back) { if (joinControl && !busy) back.addEventListener('click', () => act('join', last.id)); else back.disabled = true; }
        mid = h('div', 'dcf-empty', h('span', 'dcf-eico', icon('volume-x')), h('div', 'dcf-name', t('w.discord.notInChannel')),
          h('div', 'dcf-hint', t('w.discord.tapToJoin')), back);
      }
      const call = (kind) => {
        const { on, ic, label } = dcCall(kind, s);
        const b = labelled(h('button', `dcf-call ${kind}` + (on ? ' on' : '') + (busy === kind ? ' busy' : ''), h('span', 'dcf-disc', icon(ic)), h('span', 'dcf-cl', label)), label);
        if (ctl && !busy && (kind !== 'leave' || s.channelId)) b.addEventListener('click', () => act(kind)); else b.disabled = true;
        return b;
      };
      // Screen and Chat open their tab on the right; a second tap goes back to the channels
      const tabCall = (kind) => {
        const on = kind === 'share' && live;
        const label = t(kind === 'share' ? (on ? 'w.discord.live' : 'w.discord.screen') : 'w.discord.chat');
        const b = labelled(h('button', `dcf-call ${kind}` + (tab === kind ? ' sel' : '') + (on ? ' live' : ''),
          h('span', 'dcf-disc', icon(kind === 'share' ? 'screen-share' : 'message-square')), h('span', 'dcf-cl', label),
          kind === 'chat' && data.chatUnread && tab !== 'chat' ? h('span', 'dcf-dot') : null), kind === 'share' ? t('w.discord.share') : label);
        b.addEventListener('click', () => { haptic(); openTab(tab === kind ? 'chan' : kind); });
        return b;
      };
      const hasShare = !!data.share, hasChat = !!data.chat;
      if ((tab === 'share' && !hasShare) || (tab === 'chat' && !hasChat)) tab = 'chan';
      const calls = [call('mute'), call('deafen'), hasShare ? tabCall('share') : null, hasChat ? tabCall('chat') : null, call('leave')].filter(Boolean);
      // with the Channels option off there are no servers to pick, as in the list layout
      const showChannels = o.channels !== false;
      if (!showChannels) picking = false;
      const left = h('div', 'dcf-left', showChannels ? h('div', 'dcf-top', switcher, ...recents) : null, mid,
        h('div', 'dcf-calls' + (calls.length > 3 ? ' many' : ''), ...calls));
      const tabs = hasShare || hasChat ? h('div', 'dcf-tabs', ...[['chan', 'w.discord.channelsTab'], hasChat && ['chat', 'w.discord.chat'], hasShare && ['share', 'w.discord.share']]
        .filter(Boolean).map(([k, key]) => {
          const b = h('button', 'dcf-tab' + (tab === k ? ' on' : ''), t(key),
            (k === 'chat' && data.chatUnread && tab !== 'chat') || (k === 'share' && live) ? h('span', 'dcf-dot') : null);
          b.addEventListener('click', () => { haptic(); openTab(k); });
          return b;
        })) : null;
      let right;
      if (tab === 'chat') right = h('div', 'dcf-right chat', tabs, ...chatPanel());
      else if (tab === 'share') right = h('div', 'dcf-right', tabs, ...sharePanel());
      else if (picking) {
        right = h('div', 'dcf-right', tabs, h('div', 'dcf-cap', t('w.discord.servers')), h('div', 'dcf-grid', ...[...servers.keys()].map((g) => {
          const b = h('button', 'dcf-srv' + (g === sel ? ' cur' : ''), gico(g), h('span', null, sname(g)));
          b.addEventListener('click', () => pick(g));
          return b;
        })));
      } else {
        // the channel you are in comes first
        const chans = showChannels ? [...(servers.get(sel) || [])].sort((a, b) => (b.id === s.channelId) - (a.id === s.channelId)) : [];
        right = h('div', 'dcf-right', tabs, showChannels ? h('div', 'dcf-cap', t('w.discord.channelsOf', { name: sel ? sname(sel) : '' })) : null,
          showChannels ? h('div', 'dcf-grid', ...chans.map((c) => {
            const b = h('button', 'dcf-chan' + (c.id === s.channelId ? ' cur' : '') + (busy === 'join' + c.id ? ' busy' : ''), icon('volume-2'), h('span', null, c.name));
            if (joinControl && !busy && c.id !== s.channelId) b.addEventListener('click', () => act('join', c.id));
            return b;
          })) : null);
        if (o.notifications !== false && s.notifications?.length) {
          right.append(h('div', 'dcf-notes', ...s.notifications.slice(0, 4).map((n) => h('div', 'dcf-ntf', h('b', null, n.title), h('span', null, n.body)))));
        }
      }
      // keep the chat's place: follow new messages only when it was showing the newest ones
      const oldList = box.querySelector('.dcf-msgs');
      const stick = !oldList || oldList.scrollHeight - oldList.scrollTop - oldList.clientHeight < 40;
      const oldTop = oldList?.scrollTop ?? 0;
      // Almost any change redraws (a member muting, a notification): the scrolled places stay where they were, as long
      // as the same view is shown (another tab, or the server list, starts at the top).
      const view = tab + (picking ? ':servers' : '') + (chatPick ? ':picker:' + (pickGuild || '') : '');
      const places = view === lastView ? ['.dcf-wrap', '.dcf-right', '.dcf-plist'].map((q) => box.querySelector(q)?.scrollTop ?? 0) : [0, 0, 0];
      lastView = view;
      const wrap = h('div', 'dcf-wrap', left, right);
      box.replaceChildren(wrap);
      ['.dcf-wrap', '.dcf-right', '.dcf-plist'].forEach((q, i) => { const el = box.querySelector(q); if (el) el.scrollTop = places[i]; });
      const list = box.querySelector('.dcf-msgs');
      if (list) list.scrollTop = stick ? list.scrollHeight : oldTop;
    };

    const render = (force = false) => {
      const s0 = data?.snapshot;
      const quiet = s0?.members ? { ...data, snapshot: { ...s0, members: s0.members.map(({ speaking, ...m }) => m) } } : data;
      const key = JSON.stringify([quiet, busy, box.clientHeight, box.clientWidth, sel, picking, tab, share, shareBusy, tab === 'chat' ? chat : null, chatPick, pickGuild]);
      if (!force && key === lastKey) { ring(); return; }
      lastKey = key;
      rings.clear();
      if (!data) { box.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'empty', t('w.discord.turnOn'))); return; }
      const s = data.snapshot;
      if (!s.connected) { box.replaceChildren(h('div', 'empty', s.error || t('panel.connecting'))); return; }
      renderFocus(s, data.control);
    };
    // In a channel the widget asks every 500 ms so the speaking ring follows the voice; otherwise every 2 s. Off screen
    // it doesn't ask at all, and catches up within 250 ms of showing.
    let next = 0, asking = false, shown = false;
    const poll = () => {
      const now = visible();
      if (now && !shown) next = 0;
      shown = now;
      if (asking || !now || Date.now() < next) return;
      asking = true;
      load().then(() => { asking = false; next = Date.now() + (data?.snapshot?.channelId ? 500 : 2000); render(); });
    };
    // the share state every 4 s while in a call (the Live badge), every 2 s with the window list while its tab is open
    const pollShare = () => {
      if (!data?.share || shareWaiting || shareBusy || !visible() || Date.now() < shareNext) return;
      if (tab !== 'share' && !data.snapshot?.channelId) { if (share?.active) { share = null; render(); } return; }
      shareNext = Date.now() + (tab === 'share' ? 2000 : 4000);
      loadShare(tab === 'share').then(() => render());
    };
    // the open chat every 2 s, only while its tab is on screen
    const pollChat = () => {
      if (tab !== 'chat' || chatWaiting || !visible() || Date.now() < chatNext) return;
      chatNext = Date.now() + 2000;
      loadChat().then(() => render());
    };
    // 'fast' (every 250 ms) drives the asking; 'tick' (every second) also redraws when the widget's size changes
    return { update: (kind) => {
      poll(); pollShare(); pollChat();
      if (kind !== 'fast') render();
      if (!data?.snapshot?.connected) return;
      // the request is for a Discord widget on the page itself, not a Smart Stack card (as in the list layout)
      const to = box.closest('.stack-item') ? null : dcTake(page, visible());
      if ((to === 'share' && data.share) || (to === 'chat' && data.chat)) openTab(to);
    } };

  },
};

// ---- Discord current-channel bar ---------------------------------------------------------------------------------
// The bar asks the Discord widget to open a tab: go to the first page that holds one; only a Discord widget on that
// page picks the request up, once it is on screen. A Discord widget inside a Smart Stack is not looked for: the stack
// shows one card at a time, so the request could not say which.
let dcWant = null;
const dcOpen = (tab) => {
  // the first page whose own Discord widget is built on this screen: one on a tablet-only row is not built on a phone
  const sections = [...document.querySelectorAll('#pager > .page')];
  const target = pages[sections.findIndex((sec) => [...sec.querySelectorAll('.w-discord')].some((el) => !el.closest('.stack-item')))];
  if (!target) { toast(t('w.discord.addWidget'), 'err'); return; }
  dcWant = { tab, page: target };
  gotoPageRef(pages.indexOf(target) + 1);
};
/** The tab the bar asked this widget to open, taken once: only on the page the request was for, and only once it shows. */
const dcTake = (page, shown) => {
  if (!dcWant || dcWant.page !== page || !shown) return null;
  const { tab } = dcWant;
  dcWant = null;
  return tab;
};

// The voice channel you are in as a bar any page can hold: the channel, who is there and mic / sound / screen / chat /
// leave. Its layout follows its size in the grid (style.css, container queries). Outside a voice channel it hides and
// the widgets next to it take its cells (reflowPage in main.js), unless "autoHide" is off. Screen and Chat open those
// tabs of the Discord widget; while you share, Screen stops the share.
W['discord-bar'] = {
  needs: ['tick', 'fast'],
  create(box, { o, page, reflow }) {
    let data = null, lastKey = '', busy = null;
    let share = null, shareBusy = false, shareWaiting = false, shareNext = 0;
    const rings = new Map();
    const load = async () => { try { data = await api('/discord'); } catch (_) {} };
    const loadShare = async () => {
      shareWaiting = true;
      try { share = await api('/discord/share', undefined, 8000); } catch (_) {} finally { shareWaiting = false; }
    };
    const act = async (action) => {
      if (busy) return;
      haptic();
      busy = action; render(true);
      await dcPost(action);
      await new Promise((done) => setTimeout(done, 600));
      await load(); busy = null; render(true);
    };
    // after a rebuild (a rotation, a settings save) a late answer must not reflow the old page
    let destroyed = false;
    const hide = (v) => { if (!destroyed && box.hidden !== v) { box.hidden = v; reflow?.(); } };
    // It hides only on a page grid that can give its rows away (a Smart Stack card has no reflow), never in the
    // settings' live preview, and only once the agent has answered, so it doesn't blink away while the page loads.
    const canHide = o.autoHide !== false && !!reflow && !PREVIEW;
    // Out of a channel it was showing (or Discord reconnecting for a moment) it waits a little before it hides, so the
    // widgets around it don't jump for a blip.
    const GRACE = 3000;
    let shownIn = false, awaySince = 0;
    // Start hidden where it may hide, so the widgets around it don't jump when the first answer says you are not in a
    // channel; the page gives its rows away once every widget is in place.
    if (canHide) { box.hidden = true; queueMicrotask(() => { if (!destroyed) reflow(); }); }
    const render = (force = false) => {
      const s = data?.snapshot;
      if (!s?.channelId || !data.enabled || !s.connected) {
        if (canHide) {
          if (!data) return; // still hidden until the agent answers
          if (!shownIn) { lastKey = ''; hide(true); return; }
          awaySince = awaySince || Date.now(); // not ||= : that is Chromium 85, and the panel runs from 80
          if (Date.now() - awaySince < GRACE) return; // keep what is drawn for now
          shownIn = false; lastKey = ''; hide(true); return;
        }
        lastKey = '';
        hide(false);
        box.replaceChildren(h('div', 'empty', !data ? t('w.loading') : !data.enabled ? t('w.discord.turnOn') : !s.connected ? (s.error || t('panel.connecting')) : t('w.discord.notInChannel')));
        return;
      }
      shownIn = true; awaySince = 0;
      hide(false);
      const live = !!share?.active;
      const quiet = { ...s, members: s.members.map(({ speaking, ...m }) => m), notifications: null };
      const key = JSON.stringify([quiet, data.control, data.share, data.shareState, data.chat, data.chatUnread, busy, live, share?.name, shareBusy]);
      if (!force && key === lastKey) { for (const m of s.members) rings.get(m.id)?.classList.toggle('speaking', !!m.speaking); return; }
      lastKey = key;
      rings.clear();
      const ctl = data.control;
      const here = s.channels.find((c) => c.id === s.channelId);
      const av = (m) => dcIco(m.avatar && dcImg('user', m.id, m.avatar), m.name, 'dcb-av');
      const chips = s.members.map((m) => h('span', 'dcb-chip' + (m.speaking ? ' speaking' : ''), av(m), h('span', null, m.name)));
      const stack = s.members.slice(0, 5).map((m) => { const a = av(m); if (m.speaking) a.classList.add('speaking'); return a; });
      // one ring entry per member: the chip (names shown) or the stacked avatar, whichever the size shows
      s.members.forEach((m, i) => rings.set(m.id, { classList: { toggle: (c, v) => { chips[i].classList.toggle(c, v); stack[i]?.classList.toggle(c, v); } } }));
      const btn = (kind) => {
        const { on, ic, label } = dcCall(kind, s);
        const b = labelled(h('button', `dcb-btn ${kind}` + (on ? ' on' : '') + (busy === kind ? ' busy' : ''), icon(ic), h('span', 'dcb-l', label)), label);
        if (ctl && !busy) b.addEventListener('click', () => act(kind)); else b.disabled = true;
        return b;
      };
      let screen = null;
      if (live) {
        screen = labelled(h('button', 'dcb-btn share live' + (shareBusy ? ' busy' : ''), icon('screen-share-off'), h('span', 'dcb-l', t('w.discord.shareStop'))), t('w.discord.shareStop'));
        if (!shareBusy) screen.addEventListener('click', async () => { haptic(); shareBusy = true; render(true); await dcShareAct('stop'); shareBusy = false; await loadShare(); render(true); });
        else screen.disabled = true;
      } else if (data.share) {
        screen = labelled(h('button', 'dcb-btn share', icon('screen-share'), h('span', 'dcb-l', t('w.discord.screen'))), t('w.discord.share'));
        screen.addEventListener('click', () => { haptic(); dcOpen('share'); });
      }
      let chatB = null;
      if (data.chat) {
        chatB = labelled(h('button', 'dcb-btn chat', icon('message-square'), h('span', 'dcb-l', t('w.discord.chat')), data.chatUnread ? h('span', 'dcb-dot') : null), t('w.discord.chat'));
        chatB.addEventListener('click', () => { haptic(); dcOpen('chat'); });
      }
      const btns = [btn('mute'), btn('deafen'), screen, chatB, btn('leave')].filter(Boolean);
      box.replaceChildren(h('div', 'dcb' + (live ? ' live' : ''),
        h('div', 'dcb-info',
          h('div', 'dcb-head', icon('volume-2', 'dcb-vi'), h('b', 'dcb-name', s.channelName || ''), live ? h('span', 'dcb-badge', t('w.discord.live')) : null, here?.guild ? h('span', 'dcb-guild', here.guild) : null),
          h('div', 'dcb-chips', ...chips),
          h('div', 'dcb-stack', ...stack, s.members.length > 5 ? h('span', 'dcb-av dcb-more', '+' + (s.members.length - 5)) : null)),
        h('div', 'dcb-btns n' + btns.length, ...btns)));
    };
    const visible = () => dcVisible(box, page);
    let next = 0, asking = false, shown = false;
    const poll = () => {
      const now = visible();
      if (now && !shown) next = 0;
      shown = now;
      if (asking || !now || Date.now() < next) return;
      asking = true;
      load().then(() => { asking = false; next = Date.now() + (data?.snapshot?.channelId ? 500 : 2000); render(); });
    };
    // the share state every 4 s while in a call, for the Live badge and the stop button
    const pollShare = () => {
      // shareState: the bar's own Control is enough to see whether you share and to stop it
      if (!(data?.shareState ?? data?.share) || !data.snapshot?.channelId) { share = null; return; }
      if (shareWaiting || shareBusy || !visible() || Date.now() < shareNext) return;
      shareNext = Date.now() + 4000;
      loadShare().then(() => render());
    };
    // 'fast' also counts down the grace period before hiding
    return { update: (kind) => { if (destroyed) return; poll(); pollShare(); if (kind !== 'fast' || awaySince) render(); }, destroy: () => { destroyed = true; } };
  },
};


// Discord through the desktop app on the PC: the voice channel / call you are in, who is there, mute / deafen / leave,
// voice channels to join and recent notifications. Buttons only work when the widget allows control.
W.discord = {
  needs: ['tick', 'fast'],
  create(box, ctx) {
    // "focus" (Side by side, a choice) is its own body above; "list", the default, is below
    if (ctx.o.layout === 'focus') return dcFocus.create(box, ctx);
    const { o, page, w } = ctx;
    let data = null, lastKey = '', busy = null;
    const open = new Set(); // servers whose channels are unfolded
    // Favourite servers and voice channels, picked on the phone (★): saved in this widget's options on the computer.
    // A widget inside a Smart Stack or a preview has no saved place of its own to write to.
    let editing = false;
    const canEdit = !PREVIEW && !!w && !String(w.id).includes(':') && !!page?.id;
    // like the agent (DiscordFavorites.Read): text only, each id once, at most 30
    const favs = (key) => (Array.isArray(o[key]) ? [...new Set(o[key].filter((x) => typeof x === 'string' && x))].slice(0, 30) : []);
    let saving = false; // one save at a time: each one carries the settings version the previous one produced
    const fav = async (kind, id, on) => {
      if (saving) return;
      // without a settings version from the computer, what is on screen may be an old copy: nothing is written over it
      if (cfgVersion == null) { toast(t('w.discord.favFailed'), 'err'); return; }
      saving = true;
      const key = kind === 'guild' ? 'favGuilds' : 'favChannels';
      haptic(); render(true);
      try {
        const mark = configMark();
        const r = await api('/widget/' + encodeURIComponent(page.id) + '/' + encodeURIComponent(w.id) + '/discord-favorite?kind=' + kind
          + '&id=' + encodeURIComponent(id) + '&on=' + on + '&version=' + encodeURIComponent(cfgVersion), 'POST', 8000);
        const list = favs(key).filter((x) => x !== id);
        o[key] = on ? [...list, id] : list;
        // keep the panel's own copy of the settings in step, so the saved version doesn't rebuild the page (as Note does)
        if (!configReadSince(mark)) {
          const saved = (cfg.pages || []).find((p) => p.id === page.id)?.widgets?.find((x) => x.id === w.id);
          if (saved) { saved.options = { ...(saved.options || {}), [key]: o[key] }; store.set('cfg', cfg); }
          localWriteStored(r?.version);
        } else await loadConfig(); // a read applied during the save may be from before it: the next save needs the version of this one
      } catch (e) { toast(t(e?.status === 409 ? 'w.discord.favChanged' : 'w.discord.favFailed'), 'err'); }
      saving = false; render(true);
    };
    // a round logo: the image once it has loaded, the first letter until then (and when there is no image)
    const ico = (src, name) => {
      const w = h('span', 'dc-ico', (name || '?').trim().charAt(0).toUpperCase() || '?');
      if (src) { const im = new Image(); im.alt = ''; im.onload = () => w.replaceChildren(im); im.src = src; }
      return w;
    };
    const load = async () => { try { data = await api('/discord'); } catch (_) {} };
    // Chat (read-only, Settings › General › Discord): the widget switches between its usual view and the chat (💬).
    // The agent reads a channel only while the chat is open on screen here; the text is drawn with textContent.
    // With the channel picker open it asks for no messages, so none count as seen while the picker hides them.
    let chatOpen = false, chat = null, picking = false, pickGuild = null, chatAsked = 0, chatWaiting = 0;
    const loadChat = async () => {
      const n = ++chatAsked; // only the newest answer counts: an older one may be for another server or channel
      const forGuild = picking ? pickGuild : null; // the server whose channels the answer lists
      chatWaiting++;
      try {
        const c = await api('/discord/chat' + (picking ? '?picker=true' + (pickGuild ? '&guild=' + encodeURIComponent(pickGuild) : '') : ''), undefined, 8000);
        if (n === chatAsked) chat = { ...c, forGuild };
      } catch (_) {} finally { chatWaiting--; }
    };
    const chooseChat = async (channel) => {
      haptic();
      picking = false; pickGuild = null;
      try { await api('/discord/chat/choose?channel=' + encodeURIComponent(channel), 'POST', 5000); } catch (_) { toast(t('w.discord.failed'), 'err'); }
      // the share view may have taken its place meanwhile: then the poll reads the chosen chat when it shows again
      if (chatOpen && visible()) await loadChat();
      render(true);
    };
    const openInDiscord = async () => {
      haptic();
      try { await api('/discord/chat/open', 'POST', 5000); } catch (_) { toast(t('w.discord.failed'), 'err'); }
    };
    const when = (ms) => {
      if (!ms) return '';
      const d = new Date(ms), now = new Date();
      const time = d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
      return d.toDateString() === now.toDateString() ? time : d.toLocaleDateString(LOCALE, { day: 'numeric', month: 'short' }) + ' ' + time;
    };
    const chatView = () => {
      const c = chat;
      if (!c) return [h('div', 'empty', t('w.loading'))];
      if (!c.enabled) return [h('div', 'empty', t('w.discord.chatOff'))];
      if (!c.ready) return [h('div', 'empty', c.error || t('w.discord.chatApprove'))];
      const head = h('button', 'dc-pick', (picking ? '▾ ' : '▸ ') + (c.channel ? (c.voice ? '🔊 ' : '# ') + (c.channel.name || '') : t('w.discord.pickChannel')),
        c.channel?.guild ? h('small', null, c.channel.guild) : null);
      head.addEventListener('click', () => { haptic(); picking = !picking; pickGuild = null; loadChat().then(() => render(true)); });
      const bar = [head];
      if (data?.widgetControl && c.channel) {
        const open = labelled(h('button', 'dc-open', '↗'), t('w.discord.openInDiscord'));
        open.addEventListener('click', openInDiscord);
        bar.push(open);
      }
      const out = [h('div', 'dc-chatbar', ...bar)];
      if (picking) {
        const rows = [];
        const dot = (on) => (on ? h('span', 'dc-dot') : null);
        if (!pickGuild) {
          const v = h('button', 'dc-pickrow' + (c.voice ? ' cur' : ''), '🔊 ' + t('w.discord.voiceChat') + (c.callName ? ': ' + c.callName : ''));
          v.addEventListener('click', () => chooseChat('voice'));
          rows.push(v);
          for (const g of c.servers || []) {
            const b = h('button', 'dc-pickrow srv', ico(g.icon && img('guild', g.id, g.icon), g.name), h('span', 'dc-pname', g.name), dot(g.unread), h('small', null, '›'));
            b.addEventListener('click', () => { haptic(); pickGuild = g.id; loadChat().then(() => render(true)); });
            rows.push(b);
          }
        } else {
          const back = h('button', 'dc-pickrow back', '‹ ' + ((c.servers || []).find((g) => g.id === pickGuild)?.name || ''));
          back.addEventListener('click', () => { haptic(); pickGuild = null; render(true); });
          rows.push(back);
          // until the answer for this server is in, another server's channels must not show under its name
          const channels = c.forGuild === pickGuild ? c.channels : null;
          if (!channels) rows.push(h('div', 'empty', t('w.loading')));
          for (const ch of channels || []) {
            const b = h('button', 'dc-pickrow' + (!c.voice && c.channel?.id === ch.id ? ' cur' : ''), h('span', 'dc-pname', '# ' + ch.name), dot(ch.unread));
            b.addEventListener('click', () => chooseChat(ch.id));
            rows.push(b);
          }
        }
        out.push(h('div', 'dc-picklist', ...rows));
        return out;
      }
      if (!c.channel) { out.push(h('div', 'empty', t('w.discord.notInChannel'))); return out; }
      if (c.picker) { out.push(h('div', 'empty', t('w.loading'))); return out; } // the picker just closed: its answer holds no messages
      const list = h('div', 'dc-msgs', ...(c.messages || []).map((m, i, all) => {
        const same = i > 0 && all[i - 1].authorId === m.authorId && m.ts - all[i - 1].ts < 5 * 60000;
        const who = h('div', 'dc-who', m.author + (m.bot ? ' 🤖' : ''), h('small', null, when(m.ts)));
        if (m.color) who.style.color = m.color;
        const extra = [m.attachments ? '📎 ' + m.attachments : '', !m.text && m.embed ? '🔗 ' + m.embed : ''].filter(Boolean).join(' · ');
        return h('div', 'dc-msg' + (same ? ' cont' : ''), same ? h('span', 'dc-msgav') : ico(m.avatar && m.authorId && img('user', m.authorId, m.avatar), m.author),
          h('div', 'dc-msgb', same ? null : who, m.text ? h('div', 'dc-msgtext', m.text) : null, extra ? h('div', 'dc-extra', extra) : null));
      }));
      if (!(c.messages || []).length) list.append(h('div', 'empty', t('w.discord.noMessages')));
      out.push(list);
      return out;
    };
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
    // Screen share (its own switch in Settings › General › Discord, and control): 🖥️ switches to the share view. The
    // running apps (names only) are asked for only while that view is open on screen.
    let shareOpen = false, share = null, shareBusy = null;
    let shareWaiting = 0;
    const loadShare = async () => {
      shareWaiting++;
      try { share = await api('/discord/share?apps=true', undefined, 8000); } catch (_) {} finally { shareWaiting--; }
    };
    // what the agent answers (DiscordShareError.Code), in the panel's language; anything else is "Discord didn't do it"
    const SHARE_ERRORS = { off: 'w.discord.shareOff', notAllowed: 'w.discord.shareApprove', unknown: 'w.discord.shareUnknown', noCall: 'w.discord.shareJoin',
      sharing: 'w.discord.shareAlready', gone: 'w.discord.shareGone', notDirect: 'w.discord.shareNotDirect' };
    const shareAct = async (action, pid) => {
      if (shareBusy) return;
      haptic();
      shareBusy = action + (pid || ''); render(true);
      // Longer than the agent waits for a share to start. Never sent again by itself: the same command starts and stops.
      const ctl = new AbortController();
      let timer;
      const deadline = new Promise((_, fail) => { timer = setTimeout(() => { ctl.abort(); fail(new Error('timeout')); }, 12000); });
      try {
        await Promise.race([deadline, (async () => {
          const r = await fetch(`${API}/discord/share/${action}` + (pid ? '?pid=' + pid : ''), { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
          if (r.ok) return;
          const { code } = await r.json().catch(() => ({}));
          toast(t(SHARE_ERRORS[code] || 'w.discord.failed'), 'err');
        })()]);
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); } finally { clearTimeout(timer); shareBusy = null; }
      if (shareOpen && visible()) await loadShare(); // closed or off screen meanwhile: the apps are not asked for
      render(true);
    };
    const shareView = () => {
      const c = share;
      if (!c) return [h('div', 'empty', t('w.loading'))];
      if (!c.enabled) return [h('div', 'empty', t('w.discord.shareOff'))];
      if (!c.ready) return [h('div', 'empty', c.refused ? t('w.discord.shareRefused') : c.error || t('w.discord.shareApprove'))];
      if (c.active === null) return [h('div', 'empty', t('w.discord.shareUnknown'))];
      const out = [];
      if (c.active) {
        const stop = h('button', 'dc-share stop' + (shareBusy === 'stop' ? ' busy' : ''), emo('⏹️'), ' ', t('w.discord.shareStop'));
        if (!shareBusy) stop.addEventListener('click', () => shareAct('stop')); else stop.disabled = true;
        out.push(h('div', 'dc-sharing', h('span', null, c.name ? t('w.discord.sharing', { name: c.name }) : t('w.discord.sharingNow'))), stop);
        return out;
      }
      if (!c.inCall) out.push(h('div', 'dc-hint', t('w.discord.shareJoin')));
      const pick = h('button', 'dc-share' + (shareBusy === 'pick' ? ' busy' : ''), emo('🖥️'), ' ', t('w.discord.sharePick'));
      if (c.inCall && !shareBusy) pick.addEventListener('click', () => shareAct('pick')); else pick.disabled = true;
      out.push(pick);
      if (c.apps?.length) {
        out.push(h('div', 'dc-hint', t('w.discord.shareHint')));
        out.push(h('div', 'dc-apps', ...c.apps.map((a) => {
          const b = h('button', 'dc-app' + (shareBusy === 'app' + a.pid ? ' busy' : ''), a.name);
          if (c.inCall && !shareBusy) b.addEventListener('click', () => shareAct('app', a.pid)); else b.disabled = true;
          return b;
        })));
      }
      return out;
    };
    // Who is speaking changes several times a second: that only moves the ring on the chips already drawn, since a full
    // redraw recreates every avatar and server icon and they flash while they load again.
    const rings = new Map();
    const ring = () => { for (const m of data?.snapshot?.members || []) rings.get(m.id)?.classList.toggle('speaking', !!m.speaking); };
    const render = (force = false) => {
      const s0 = data?.snapshot;
      const quiet = s0?.members ? { ...data, snapshot: { ...s0, members: s0.members.map(({ speaking, ...m }) => m) } } : data;
      const key = JSON.stringify([quiet, busy, [...open], box.clientHeight, editing, saving, o.favGuilds, o.favChannels, shareOpen, shareBusy, shareOpen ? share : null, chatOpen, picking, pickGuild, chatOpen ? chat : null]);
      if (!force && key === lastKey) { ring(); return; }
      lastKey = key;
      rings.clear();
      // keep the chat's place: follow new messages only when it was showing the newest ones
      const oldList = box.querySelector('.dc-msgs');
      const stick = !oldList || oldList.scrollHeight - oldList.scrollTop - oldList.clientHeight < 40;
      const oldTop = oldList?.scrollTop ?? 0;
      queueMicrotask(() => {
        const list = box.querySelector('.dc-msgs');
        if (list) list.scrollTop = stick ? list.scrollHeight : oldTop;
      });
      if (!data) { box.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', en('Discord')), h('div', 'empty', t('w.discord.turnOn'))); return; }
      const s = data.snapshot;
      if (!s.connected) { box.replaceChildren(h('div', 'w-title', en('Discord')), h('div', 'empty', s.error || t('panel.connecting'))); return; }
      const ctl = data.control;
      // Push to talk counts as "on" for the input mode button: it is the mode where you are silent until you press a key.
      const ptt = s.inputMode === 'PUSH_TO_TALK';
      const shareBtn = () => {
        if (!data.share) { shareOpen = false; return null; }
        const b = labelled(h('button', 'dc-btn share' + (shareOpen ? ' on' : ''), emo('🖥️')), t(shareOpen ? 'w.discord.closeShare' : 'w.discord.share'));
        b.addEventListener('click', () => {
          haptic(); shareOpen = !shareOpen;
          // one view at a time: a chat hidden under the share view would still be read and marked as seen
          if (shareOpen) { chatOpen = false; picking = false; pickGuild = null; share = null; loadShare().then(() => render(true)); }
          render(true);
        });
        return b;
      };
      const btn = (cls, label, on, action) => {
        const b = labelled(h('button', 'dc-btn ' + cls + (on ? ' on' : '') + (busy === action ? ' busy' : ''), label),
          t({ mute: on ? 'w.a.unmuteMic' : 'w.a.muteMic', deafen: on ? 'w.a.undeafen' : 'w.a.deafen', leave: 'w.a.leave',
            inputmode: on ? 'w.a.useVoiceActivity' : 'w.a.usePushToTalk' }[action]));
        // joining and the input mode need more than the current-channel bar's option (mute, deafen and leave only)
        if ((action === 'inputmode' ? data.joinControl ?? ctl : ctl) && !busy) b.addEventListener('click', () => act(action)); else b.disabled = true;
        return b;
      };
      let chatBtn = null;
      if (data.chat) {
        // 💬 switches to the chat; the dot: a channel you opened here has a message you haven't seen
        chatBtn = labelled(h('button', 'dc-btn chat' + (chatOpen ? ' on' : ''), emo('💬'), data.chatUnread && !chatOpen ? h('span', 'dc-dot') : null),
          t(chatOpen ? 'w.discord.closeChat' : 'w.discord.chat'));
        chatBtn.addEventListener('click', () => {
          haptic(); chatOpen = !chatOpen; picking = false; pickGuild = null;
          if (chatOpen) { shareOpen = false; chat = null; loadChat().then(() => render(true)); }
          render(true);
        });
      } else chatOpen = false;
      const kids = [h('div', 'dc-top',
        h('span', 'dc-where', s.channelName ? `${s.channelName}` : t('w.discord.notInChannel')),
        s.channelKind ? h('span', 'dc-kind', s.channelKind) : null,
        btn('mute', emo(s.muted ? '🔇' : '🎙️'), s.muted, 'mute'), btn('deaf', emo(s.deafened ? '🔕' : '🎧'), s.deafened, 'deafen'),
        s.inputMode ? btn('mode', emo(ptt ? '✋' : '🗣️'), ptt, 'inputmode') : null,
        chatBtn,
        s.channelId ? btn('leave', emo('📞'), false, 'leave') : null,
        shareBtn())];
      // Push to talk without a key in Discord: nobody hears you, and nothing else on screen says so.
      if (ptt && !s.pttKey) kids.push(h('div', 'dc-warn', t('w.discord.pttNoKey')));
      if (shareOpen) { box.replaceChildren(...kids, ...shareView()); return; }
      if (chatOpen) { box.replaceChildren(...kids, ...chatView()); return; }
      if (s.channelId && s.members.length) {
        kids.push(h('div', 'dc-members', ...s.members.map((m) => {
          const chip = h('span', 'dc-member' + (m.deafened ? ' deaf' : m.muted ? ' muted' : '') + (m.speaking ? ' speaking' : ''), ico(m.avatar && img('user', m.id, m.avatar), m.name), rich((m.deafened ? '🔕 ' : m.muted ? '🔇 ' : '') + m.name));
          rings.set(m.id, chip);
          return chip;
        })));
      }
      if (o.channels !== false && s.channels.length) {
        // one block per server; tap it to unfold its voice channels (the server you are in starts unfolded)
        // grouped by server id, not by name: two servers can have the same name
        const favG = favs('favGuilds'), favC = favs('favChannels');
        // favourites Discord doesn't list (you left the server, or a hand-edited list) filter nothing: with only those, all show
        const filtered = !editing && s.channels.some((c) => favG.includes(c.guildId) || favC.includes(c.id));
        const servers = new Map();
        for (const c of s.channels) {
          const gid = c.guildId || c.guild || 'other';
          if (!servers.has(gid)) servers.set(gid, []);
          servers.get(gid).push(c);
        }
        // With favourites, only favourite servers (and servers holding a favourite channel, or the channel you are in)
        // show, and a server with favourite channels shows only those, plus the one you are in.
        const visibleServers = [...servers].filter(([gid, chans]) => !filtered || favG.includes(gid)
          || chans.some((c) => favC.includes(c.id) || c.id === s.channelId))
          .map(([gid, chans]) => {
            const starred = chans.filter((c) => favC.includes(c.id) || c.id === s.channelId);
            return [gid, filtered && chans.some((c) => favC.includes(c.id)) ? starred : chans];
          });
        if (canEdit) {
          const tgl = h('button', 'dc-favbtn' + (editing ? ' on' : ''), editing ? t('w.discord.done') : '★ ' + t('w.discord.favorites'));
          tgl.addEventListener('click', () => { haptic(); editing = !editing; render(true); });
          kids.push(h('div', 'dc-favbar', editing ? h('span', 'dc-favhint', t('w.discord.favHint')) : null, tgl));
        }
        const star = (kind, id, on) => {
          const b = labelled(h('button', 'dc-star' + (on ? ' on' : ''), on ? '★' : '☆'), t(on ? 'w.discord.removeFav' : 'w.discord.addFav'));
          if (saving) b.disabled = true;
          b.addEventListener('click', (e) => { e.stopPropagation(); fav(kind, id, !on); });
          return b;
        };
        // favourites Discord doesn't list right now have no row of their own below: here they can still be removed
        if (editing) {
          const gone = [...favG.filter((id) => s.channels.every((c) => c.guildId !== id)).map((id) => ['guild', id]),
            ...favC.filter((id) => s.channels.every((c) => c.id !== id)).map((id) => ['channel', id])];
          if (gone.length) {
            kids.push(h('div', 'dc-gone', ...gone.map(([kind, id]) =>
              h('div', 'dc-headrow', h('span', 'dc-goneid', id, h('small', null, t('w.discord.favMissing'))), star(kind, id, true)))));
          }
        }
        kids.push(h('div', 'dc-servers', ...visibleServers.map(([gid, chans]) => {
          const here = chans.some((c) => c.id === s.channelId);
          const isOpen = open.has(gid) || (here && !open.has('!' + gid));
          const g = chans[0];
          const name = g.guild || t('w.discord.otherServer');
          // everyone the agent reported for this server, whichever of its channels this widget shows
          const inVoice = servers.get(gid).reduce((n, c) => n + (c.people?.length || 0), 0);
          const head = h('button', 'dc-server' + (isOpen ? ' open' : '') + (here ? ' here' : ''), ico(g.guildIcon && g.guildId && img('guild', g.guildId, g.guildIcon), name),
            h('span', 'dc-stext', h('b', null, name), h('small', null, t(chans.length === 1 ? 'w.discord.channel' : 'w.discord.channels', { n: chans.length }),
              inVoice ? h('span', 'dc-invoice', ' · 🔊 ' + inVoice) : null)));
          head.addEventListener('click', () => {
            haptic();
            if (isOpen) { open.delete(gid); if (here) open.add('!' + gid); } else { open.add(gid); open.delete('!' + gid); }
            render(true);
          });
          // the star sits next to the server's button, not inside it (a button can't hold another one)
          const block = h('div', 'dc-block' + (isOpen ? ' open' : '') + (editing ? ' editing' : ''),
            editing && g.guildId ? h('div', 'dc-headrow', head, star('guild', g.guildId, favG.includes(g.guildId))) : head);
          if (isOpen) {
            block.append(h('div', 'dc-chans', ...chans.map((c) => {
              const b = h('button', 'dc-chan' + (c.id === s.channelId ? ' cur' : '') + (busy === 'join' + c.id ? ' busy' : ''), rich('🔊 ' + c.name));
              if (!editing && (data.joinControl ?? ctl) && !busy && c.id !== s.channelId) b.addEventListener('click', () => act('join', c.id));
              else if (editing) b.disabled = true;
              // who is in the channel (the agent reads it for favourites only)
              const people = c.people?.length ? h('div', 'dc-people', ...c.people.map((m) =>
                h('span', 'dc-person' + (m.deafened ? ' deaf' : m.muted ? ' muted' : ''), ico(m.avatar && img('user', m.id, m.avatar), m.name),
                  rich((m.deafened ? '🔕 ' : m.muted ? '🔇 ' : '') + m.name)))) : null;
              return h('div', 'dc-room', editing ? h('div', 'dc-headrow', b, star('channel', c.id, favC.includes(c.id))) : b, people);
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
    // In a channel the widget asks every 500 ms so the speaking ring follows the voice (a local request, answered
    // from the agent's memory); otherwise every 2 s as before. Off screen (a neighbouring page, a Smart Stack card
    // that isn't showing, the app in the background) it doesn't ask at all, and catches up within 250 ms of showing.
    const visible = () => box.isConnected && !document.hidden && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    let next = 0, asking = false, shown = false;
    const poll = () => {
      const now = visible();
      if (now && !shown) next = 0; // shown again: catch up at once instead of waiting out the old pace
      shown = now;
      if (asking || !now || Date.now() < next) return; // a slow answer is never overtaken by the next request
      asking = true;
      // the pace follows the answer just received, so joining or leaving a channel changes it at once
      load().then(() => { asking = false; next = Date.now() + (data?.snapshot?.channelId ? 500 : 2000); render(); });
    };
    // the open share view is asked every 2 s, only while it is open and on screen, beside the poll above
    let shareNext = 0;
    const pollShare = () => {
      if (!shareOpen || shareWaiting || shareBusy || !visible() || Date.now() < shareNext) return;
      shareNext = Date.now() + 2000;
      loadShare().then(() => render());
    };
    // the open chat is asked every 2 s, only while it is open and on screen, and beside the poll above, so a slow answer
    // from Discord doesn't hold that up
    let chatNext = 0;
    const pollChat = () => {
      if (!chatOpen || chatWaiting || !visible() || Date.now() < chatNext) return;
      chatNext = Date.now() + 2000;
      loadChat().then(() => render());
    };
    // 'fast' (every 250 ms) drives the asking; 'tick' (every second) also redraws when the widget's size changes
    // the current-channel bar's Screen and Chat buttons: open that view, one at a time as the buttons above do
    const wanted = () => {
      if (box.closest('.stack-item')) return; // the request is for a Discord widget on the page itself, not a stack card
      const to = data && dcTake(page, visible());
      if (to === 'share' && data.share) { shareOpen = true; chatOpen = false; picking = false; pickGuild = null; share = null; loadShare().then(() => render(true)); render(true); }
      if (to === 'chat' && data.chat) { chatOpen = true; shareOpen = false; picking = false; pickGuild = null; chat = null; loadChat().then(() => render(true)); render(true); }
    };
    return { update: (kind) => { poll(); pollShare(); pollChat(); wanted(); if (kind !== 'fast') render(); } };
  },
};

// Steam friends presence via the local agent's Web API connection. The API key never reaches the phone.
const steamAvatarUrls = new Map();
const steamAvatarPending = new Map();
const steamAvatarFailedUntil = new Map();
const steamAvatarTimers = new Map();
const STEAM_AVATAR_CACHE_LIMIT = 200;
const STEAM_AVATAR_REFRESH_MS = 24 * 60 * 60 * 1000;
const STEAM_AVATAR_RETRY_MS = 60_000;
let steamAvatarPortraitObserver = null;

function rememberSteamAvatar(id, url) {
  const previous = steamAvatarUrls.get(id);
  steamAvatarUrls.delete(id);
  steamAvatarUrls.set(id, { url, refreshedAt: Date.now() });
  while (steamAvatarUrls.size > STEAM_AVATAR_CACHE_LIMIT) {
    const [oldId, oldAvatar] = steamAvatarUrls.entries().next().value;
    steamAvatarUrls.delete(oldId);
    URL.revokeObjectURL(oldAvatar.url);
  }
  return previous;
}

function steamAvatarRoots(id) {
  return [...document.querySelectorAll('.st-portrait[data-steam-id]')]
    .filter((root) => root.dataset.steamId === id && root.isConnected);
}

function pruneSteamAvatarTimers() {
  const mounted = new Set([...document.querySelectorAll('.st-portrait[data-steam-id]')]
    .filter((root) => root.isConnected).map((root) => root.dataset.steamId));
  for (const [id, timer] of steamAvatarTimers) {
    if (mounted.has(id)) continue;
    clearTimeout(timer.handle);
    steamAvatarTimers.delete(id);
    steamAvatarFailedUntil.delete(id);
  }
  if (!steamAvatarTimers.size && steamAvatarPortraitObserver) {
    steamAvatarPortraitObserver.disconnect();
    steamAvatarPortraitObserver = null;
  }
}

function watchSteamAvatarPortraits() {
  if (steamAvatarPortraitObserver || !document.documentElement) return;
  steamAvatarPortraitObserver = new MutationObserver(pruneSteamAvatarTimers);
  steamAvatarPortraitObserver.observe(document.documentElement, { childList: true, subtree: true });
}

function scheduleSteamAvatarTimer(id, dueAt) {
  const existing = steamAvatarTimers.get(id);
  if (existing?.dueAt === dueAt) return;
  if (existing) clearTimeout(existing.handle);
  const timer = { dueAt, handle: setTimeout(() => {
    steamAvatarTimers.delete(id);
    if (steamAvatarRoots(id).length) loadSteamAvatar(id);
    else steamAvatarFailedUntil.delete(id);
  }, Math.max(0, dueAt - Date.now())) };
  steamAvatarTimers.set(id, timer);
  watchSteamAvatarPortraits();
  queueMicrotask(pruneSteamAvatarTimers);
}

function showSteamAvatar(root, url) {
  const image = root._steamAvatarImage;
  const fallback = root._steamAvatarFallback;
  const id = root.dataset.steamId;
  image.onerror = () => {
    if (steamAvatarUrls.get(id)?.url === url) {
      steamAvatarUrls.delete(id);
      URL.revokeObjectURL(url);
    }
    if (image.isConnected) image.replaceWith(fallback);
    const dueAt = Date.now() + STEAM_AVATAR_RETRY_MS;
    steamAvatarFailedUntil.set(id, dueAt);
    scheduleSteamAvatarTimer(id, dueAt);
  };
  image.src = url;
  if (fallback.parentNode === root) fallback.replaceWith(image);
  if (typeof image.decode === 'function') return image.decode().catch(() => {});
  if (image.complete) return Promise.resolve();
  return new Promise((resolve) => {
    image.addEventListener('load', resolve, { once: true });
    image.addEventListener('error', resolve, { once: true });
  });
}

function updateMountedSteamAvatars(id, url, previous) {
  const updates = steamAvatarRoots(id).map((root) => showSteamAvatar(root, url));
  Promise.all(updates).then(() => {
    if (previous && previous.url !== url) URL.revokeObjectURL(previous.url);
  });
}

function scheduleSteamAvatarRefresh(id, avatar) {
  const failedUntil = steamAvatarFailedUntil.get(id) || 0;
  const dueAt = failedUntil > Date.now() ? failedUntil : avatar.refreshedAt + STEAM_AVATAR_REFRESH_MS;
  scheduleSteamAvatarTimer(id, dueAt);
}

function loadSteamAvatar(id) {
  if (steamAvatarPending.has(id)) return steamAvatarPending.get(id);
  const failedUntil = steamAvatarFailedUntil.get(id) || 0;
  if (failedUntil > Date.now()) {
    scheduleSteamAvatarTimer(id, failedUntil);
    return Promise.resolve(null);
  }
  const pending = fetch(`${API}/steam/avatar?id=${encodeURIComponent(id)}`, { cache: 'no-store', headers: PANEL_HEADERS })
    .then((response) => { if (!response.ok) throw new Error('avatar unavailable'); return response.blob(); })
    .then((blob) => {
      const url = URL.createObjectURL(blob);
      const previous = rememberSteamAvatar(id, url);
      steamAvatarFailedUntil.delete(id);
      const timer = steamAvatarTimers.get(id);
      if (timer) { clearTimeout(timer.handle); steamAvatarTimers.delete(id); }
      updateMountedSteamAvatars(id, url, previous);
      scheduleSteamAvatarRefresh(id, steamAvatarUrls.get(id));
      return url;
    }).catch(() => {
      const dueAt = Date.now() + STEAM_AVATAR_RETRY_MS;
      steamAvatarFailedUntil.set(id, dueAt);
      scheduleSteamAvatarTimer(id, dueAt);
      return null;
    }).finally(() => steamAvatarPending.delete(id));
  steamAvatarPending.set(id, pending);
  return pending;
}

function steamPortrait(friend) {
  const name = friend.name || t('w.steam.user');
  const fallback = h('span', 'st-avatar-fallback', [...name.trim()][0]?.toLocaleUpperCase() || '🎮');
  const portrait = h('span', 'st-portrait', fallback);
  if (friend.hasAvatar) {
    portrait.dataset.steamId = friend.id;
    const image = h('img', 'st-avatar');
    image.alt = '';
    image.loading = 'lazy';
    image.decoding = 'async';
    image.referrerPolicy = 'no-referrer';
    portrait._steamAvatarImage = image;
    portrait._steamAvatarFallback = fallback;
    const cached = steamAvatarUrls.get(friend.id);
    if (cached) {
      steamAvatarUrls.delete(friend.id);
      steamAvatarUrls.set(friend.id, cached);
      showSteamAvatar(portrait, cached.url);
      scheduleSteamAvatarRefresh(friend.id, cached);
    } else {
      loadSteamAvatar(friend.id);
    }
  }
  if (Number.isInteger(friend.state)) portrait.append(h('i', `st-dot state-${friend.state}`));
  return portrait;
}

function refreshSteamTimes(box) {
  box.querySelectorAll('.st-sub[data-last-logoff]').forEach((label) => {
    const lastLogoff = Number(label.dataset.lastLogoff);
    if (lastLogoff > 0) label.textContent = t('w.steam.lastSeen', { time: shortDur(Date.now() - lastLogoff * 1000) });
  });
  box.querySelectorAll('.st-time[data-at]').forEach((label) => {
    const at = Number(label.dataset.at);
    const age = Math.max(0, Date.now() - at);
    label.textContent = age < 60_000 ? t('time.justNow') : t('time.ago', { dur: shortDur(age) });
  });
}

function steamFriendRow(friend, sub) {
  const classes = ['st-row'];
  if (Number.isInteger(friend.state)) classes.push(`state-${friend.state}`);
  if (friend.state > 0) classes.push('online');
  const detail = h('small', 'st-sub', sub);
  if (friend.state <= 0 && !friend.game && friend.lastLogoff) detail.dataset.lastLogoff = String(friend.lastLogoff);
  return h('div', classes.join(' '),
    steamPortrait(friend), h('span', 'st-info', h('b', 'st-name', friend.name || t('w.steam.user')), detail));
}

W.steam = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, lastKey = '', failed = false, settled = false;
    const load = async () => {
      try { data = await api('/steam', 'GET', 5000); failed = false; settled = true; return true; }
      catch (_) { failed = true; settled = true; return false; }
    };
    const stateLabel = (state) => ({
      1: t('w.steam.state.online'), 2: t('w.steam.state.busy'), 3: t('w.steam.state.away'),
      4: t('w.steam.state.snooze'), 5: t('w.steam.state.trade'), 6: t('w.steam.state.play'),
    }[state] || t('w.offline'));
    const render = () => {
      const key = JSON.stringify([data, o.offline, box.clientHeight, failed, settled]);
      if (key === lastKey) { refreshSteamTimes(box); return; }
      lastKey = key;
      if (!data) { box.replaceChildren(h('div', 'empty', t(failed ? 'w.unavailable' : 'w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', t('w.steam.title')), h('div', 'empty', t('w.steam.turnOn'))); return; }
      const s = data.snapshot || {};
      if (!s.connected && !s.friends?.length) {
        box.replaceChildren(h('div', 'w-title', t('w.steam.title')), h('div', 'empty', s.error || t('panel.connecting')));
        return;
      }
      const all = s.friends || [];
      const friends = (o.offline ? all : all.filter((f) => f.state > 0))
        .sort((a, b) => Number(!!b.game) - Number(!!a.game) || a.name.localeCompare(b.name));
      const body = [];
      if (failed || s.error) body.push(h('div', 'st-error', t('w.steam.stale')));
      if (s.error) body.push(h('div', 'st-error', s.error));
      if (!s.totalFriends) body.push(h('div', 'empty', t('w.steam.noFriends')));
      else if (!all.length) body.push(h('div', 'empty', t('w.steam.noVisibleFriends')));
      else if (!friends.length) body.push(h('div', 'empty', t('w.steam.noOnline')));
      else body.push(h('div', 'st-list', ...friends.map((f) => steamFriendRow(f,
        f.game ? t('w.steam.playing', { game: f.game })
          : f.state > 0 ? stateLabel(f.state)
            : f.lastLogoff ? t('w.steam.lastSeen', { time: shortDur(Date.now() - f.lastLogoff * 1000) })
              : Number.isInteger(f.state) ? stateLabel(f.state) : '?'))));
      box.replaceChildren(h('div', 'st-head', h('div', 'w-title', t('w.steam.title')),
        h('span', 'st-count', t('w.steam.onlineCount', { online: s.onlineCount || 0, total: s.totalFriends || 0 }))), ...body);
    };
    const tick = every(15000, async () => { await load(); render(); });
    return { update: () => { tick(); render(); } };
  },
};

// A short local history of public Steam presence changes (not private chat messages).
W['steam-activity'] = {
  needs: ['tick'],
  create(box) {
    let data = null, lastKey = '', failed = false, settled = false;
    const load = async () => {
      try { data = await api('/steam', 'GET', 5000); failed = false; settled = true; return true; }
      catch (_) { failed = true; settled = true; return false; }
    };
    const render = () => {
      const key = JSON.stringify([data, failed, settled]);
      if (key === lastKey) { refreshSteamTimes(box); return; }
      lastKey = key;
      if (!data) { box.replaceChildren(h('div', 'empty', t(failed ? 'w.unavailable' : 'w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', t('w.steam.activity.title')), h('div', 'empty', t('w.steam.turnOn'))); return; }
      const s = data.snapshot || {};
      if (!s.connected && !s.activity?.length) {
        box.replaceChildren(h('div', 'w-title', t('w.steam.activity.title')), h('div', 'empty', s.error || t('panel.connecting')));
        return;
      }
      const events = s.activity || [];
      const rows = events.map((e) => {
        const label = e.kind === 'playing' ? t('w.steam.activity.playing', { game: e.game || '' })
          : e.kind === 'offline' ? t('w.steam.activity.offline') : t('w.steam.activity.online');
        const age = e.at ? Date.now() - e.at : 0;
        const time = h('small', 'st-time', age < 60_000 ? t('time.justNow') : t('time.ago', { dur: shortDur(age) }));
        if (e.at) time.dataset.at = String(e.at);
        return h('div', 'st-event', steamPortrait(e),
          h('span', 'st-info', h('b', 'st-name', e.name || t('w.steam.user')), h('small', 'st-sub', label)),
          time);
      });
      box.replaceChildren(h('div', 'st-head', h('div', 'w-title', t('w.steam.activity.title')),
        h('span', 'st-count', events.length)),
      failed || s.error ? h('div', 'st-error', t('w.steam.stale')) : null,
      s.error ? h('div', 'st-error', s.error) : null,
      events.length ? h('div', 'st-list st-activity-list', ...rows) : h('div', 'empty', t('w.steam.activity.empty')));
    };
    const poll = every(30000, async () => { await load(); render(); });
    return { update: () => { poll(); render(); } };
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
    if (['door', 'window', 'garage_door', 'opening'].includes(dc)) return on ? tp('Open') : tp('Closed');
    if (['motion', 'occupancy', 'presence'].includes(dc)) return on ? tp('Detected') : tp('No detection');
    if (dc === 'moisture') return on ? tp('Wet') : tp('Dry');
    return on ? tp('On') : tp('Off');
  }
  if (dom === 'scene' || dom === 'script' || dom === 'button' || dom === 'input_button') return tp('Run');
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
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || tp('Could not switch it'), 'err');
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
          ...(it.showIcon ? [h('span', 'ha-ico', rich(icon))] : []), ...(it.showName ? [h('span', 'ha-name', it.name || e.name)] : []), h('b', 'ha-state', haState(e)));
        if (can) tile.addEventListener('click', () => toggle(e));
        return tile;
      }), ...(data.error ? [h('div', 'ha-err', icon('warn', 'm'), data.error)] : []));
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
    const list = h('div', 'dk-list'), head = h('div', 'w-title', o.title || en('Docker'), h('span', 'dk-count'));
    box.append(head, list);
    const act = async (c, action) => {
      haptic();
      busy = c.id; render(true);
      try {
        const r = await fetch(`${API}/docker/${encodeURIComponent(c.id)}/${action}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) toast(j.error || tpv(action === 'stop' ? 'Could not stop {name}' : action === 'start' ? 'Could not start {name}' : 'Could not restart {name}', { name: c.name }), 'err');
        else toast(`${c.name}: ${tp(action === 'stop' ? 'stopped' : action === 'start' ? 'started' : 'restarted')}`, 'ok');
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
      if (!snap.available || (snap.error && !items.length)) { list.replaceChildren(h('div', 'empty', snap.error || tp('Docker is not running'))); return; }
      if (!items.length) {
        const msg = matched.length
          ? tpv('No running containers ({n} stopped)', { n: matched.length })
          : filter ? tpv('No containers match "{filter}"', { filter: o.filter }) : tp('No containers');
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
          if (!data.control) { toast(tp('Turn on "Allow start / stop" in the widget settings on the PC')); return; }
          haptic(); open = open === c.id ? null : c.id; render(true);
        });
        if (open !== c.id) return row;
        const btn = (label, action, cls = '') => {
          const b = h('button', 'dk-btn ' + cls, label);
          b.addEventListener('click', (e) => { e.stopPropagation(); act(c, action); });
          return b;
        };
        return h('div', 'dk-wrap', row, h('div', 'dk-acts', ...(c.state === 'running' ? [btn('↻ ' + tp('Restart'), 'restart'), btn('■ ' + tp('Stop'), 'stop', 'danger')] : [btn('▶ ' + tp('Start'), 'start', 'go')])));
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
    const head = h('div', 'w-title', o.title || en('Apps'), h('span', 'ap-count'));
    const search = h('input', 'ap-search');
    search.type = 'search';
    search.placeholder = tp('Search apps');
    search.autocomplete = 'off';
    search.spellcheck = false;
    search.addEventListener('input', () => { q = search.value.trim().toLowerCase(); render(true); });
    const grid = h('div', 'ap-grid');
    box.append(head, ...(o.search === false ? [] : [search]), grid);

    const load = async () => { try { data = await api('/apps', 'GET', 10_000); } catch (_) { return false; } };
    const open = async (a) => {
      if (!data?.canOpen) { toast(tp('Opening apps needs an All apps widget saved on a page')); return; }
      haptic();
      busy = a.id; render(true);
      try {
        const r = await fetch(`${API}/apps/open?id=${encodeURIComponent(a.id)}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        const j = await r.json().catch(() => ({}));
        if (r.ok) toast(tpv('Opening {name}', { name: a.name }), 'ok'); else toast(j.error || tpv('Could not open {name}', { name: a.name }), 'err');
      } catch (_) { toast(tp('The PC did not answer'), 'err'); }
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
        h('span', 'ap-name', isArmed ? tp('Tap again to open') : a.name));
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
      if (!data) { grid.replaceChildren(h('div', 'empty', tp('Loading…'))); return; }
      if (!data.supported) { grid.replaceChildren(h('div', 'empty', tp('The apps list is available on macOS for now'))); return; }
      if (!items.length) { grid.replaceChildren(h('div', 'empty', q ? tpv('No apps match "{q}"', { q: search.value.trim() }) : tp('No apps found'))); return; }
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
      if (!cal.updated && !list.length) { box.replaceChildren(h('div', 'w-title', o.title || en('Agenda')), h('div', 'empty', tp('Add a calendar in Settings › General'))); return; }
      const timed = list.filter((e) => !e.allDay);
      const cur = timed.find((e) => e.start <= now);
      const next = timed.find((e) => e.start > now);
      if (small) {
        const e = cur || next || list[0];
        if (!e) { box.replaceChildren(h('div', 'ag-big-t', tp('No events')), h('div', 'ag-big-s', tpv('next {n} days', { n: days }))); return; }
        box.style.setProperty('--c', e.color);
        box.replaceChildren(
          h('div', 'ag-big-s', e === cur ? tpv('Now · until {time}', { time: hm(e.end) }) : e.allDay ? dayName(e.start) + tp(' · all day') : `${dayName(e.start) === t('time.today') ? inMin(e.start) || t('time.today') : dayName(e.start)} · ${hm(e.start)}`),
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
          h('span', 'ag-time', e.allDay ? tp('all day') : hm(e.start)),
          h('span', 'ag-title', e.title, o.location !== false && e.location ? h('small', null, ' · ' + e.location) : null),
          h('span', 'ag-when', isNow ? `until ${hm(e.end)}` : e === next ? inMin(e.start) : ''));
        row.style.setProperty('--c', e.color);
        if (isNow) row.style.setProperty('--p', Math.min(1, (now - e.start) / Math.max(1, e.end - e.start)));
        rows.push(row);
      }
      const body = h('div', 'ag-list', ...(rows.length ? rows : [h('div', 'empty', tpv('Nothing in the next {n} days', { n: days }))]));
      box.replaceChildren(h('div', 'w-title', o.title || en('Agenda'), cal.errors?.length ? h('span', 'ag-err', ' ', icon('warn')) : null), body);
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
    const sign = h('div', 'oa-sign', o.text || o.title || tp('ON AIR')), sub = h('div', 'oa-sub');
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
        sub.replaceChildren(rich(on
          ? [mic ? '🎙️' : '', cam ? '📷' : '', obsLive ? (obs.streaming ? tp('OBS live') : tp('OBS recording')) : '', apps.join(', ') || (pushed && typeof pv === 'string' ? pv : '')].filter(Boolean).join(' ')
          : (src === 'mic' || src === 'camera') && p && p.mic == null ? tp('Not detectable on this computer') : tp('Off air')));
      },
    };
  },
};
W['meeting-status'] = W.onair;

W.battery = {
  needs: ['tick'],
  create(box) {
    const dial = dialEl(), sub = h('div', 'g-sub');
    box.append(dial, h('div', 'g-label', tp('PHONE')), sub);
    const load = every(20_000, () => {
      if (!bridge) { dial.set(0, '—'); sub.textContent = tp('Only on the phone'); return; }
      try {
        const b = JSON.parse(bridge.battery());
        box.style.setProperty('--q', b.plugged ? '#22c55e' : b.level <= 20 ? '#ef4444' : b.level <= 40 ? '#f59e0b' : '#84cc16');
        dial.set(b.level, b.level, '%');
        sub.replaceChildren(rich((b.plugged ? '⚡ ' + tp('Charging') : tp('On battery')) + (b.temp ? ` · ${b.temp.toFixed(0)}°C` : '')));
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
    box.append(o.title === '' ? '' : h('div', 'w-title', o.title || en('Computers')), row, sub);
    let shown = '';
    const render = () => {
      const pcs = pairedPcs(), usb = transport() === 'usb';
      const key = JSON.stringify([pcs, usb, !!bridge]);
      if (key === shown) return;
      shown = key;
      if (!bridge) { row.replaceChildren(h('div', 'empty', tp('Only on the phone'))); sub.textContent = ''; return; }
      if (pcs.length < 2) {
        row.replaceChildren(h('div', 'empty', pcs.length ? tp('Pair another computer over Wi-Fi to switch here') : tp('No computer paired over Wi-Fi')));
        sub.textContent = '';
        return;
      }
      row.replaceChildren(...pcs.map((pc) => {
        const b = h('button', 'ov-pc' + (pc.active && !usb ? ' on' : ''), icon('monitor', 'm'), pc.name || t('panel.computer'));
        b.disabled = usb;
        b.addEventListener('click', (e) => { e.stopPropagation(); switchPc(pc); });
        return b;
      }));
      sub.textContent = usb ? tp('USB cable connected: the panel uses that computer') : '';
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
    const title = h('div', 'w-title', en(byMem ? 'Top memory' : 'Top CPU'));
    box.append(title, list);
    const rowMap = new Map();
    return {
      update() {
        const pc = D.state?.pc;
        if (byMem && pc?.ramTotalGb) {
          const pct = Math.round((pc.ramUsedGb / pc.ramTotalGb) * 100);
          title.replaceChildren(en('Top memory'), ` (${pct}% · ${Math.round(pc.ramUsedGb)}/${Math.round(pc.ramTotalGb)} GB)`);
        } else if (!byMem && pc?.cpu != null) {
          title.replaceChildren(en('Top CPU'), ` (${Math.round(pc.cpu)}%)`);
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
    const st = Q_STYLE[String(prov).split('@')[0]] || { color: '#94a3b8', logo: '●' };
    box.style.setProperty('--q', st.color);
    const dial = dialEl(), label = h('div', 'g-label'), sub = h('div', 'g-sub');
    box.append(dial, label, sub);
    let last = 0;
    const render = () => {
      const p = quotaProviders().find((x) => x.id === prov);
      const l = p?.limits?.[idx];
      if (!l) { dial.set(0, '—'); label.textContent = `${st.logo} ${p?.name || prov}`; sub.textContent = D.quotas ? tp('No data') : tp('Waiting…'); return; }
      const pace = paceInfo(l);
      dial.style.setProperty('--qc', l.percent >= 95 ? '#ef4444' : l.percent >= 80 ? '#f59e0b' : 'var(--q)');
      dial.set(l.percent, Math.round(l.percent), '%', pace?.elapsed);
      label.textContent = `${st.logo} ${p.name} · ${quotaText(l.label)}`;
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
    const mute = labelled(h('button', 'pb', icon('volume-x')), t('w.a.mute')), down = h('button', 'pb', '−'), up = h('button', 'pb', '+');
    box.append(h('div', 'w-title', en('Volume')), h('div', 'vb-row', bar, pct), h('div', 'pomo-btns', mute, down, up));
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
        btn.replaceChildren(icon(paused ? 'play' : 'pause', 'm'), paused ? tp('Resume') : tp('Pause'));
        btn.hidden = !D.state;
        box.classList.toggle('paused', !!paused);
        if (!rules.length) { list.replaceChildren(h('div', 'empty', tp('Add rules in Settings › Rules'))); return; }
        const sorted = [...rules].sort((a, b) => active.includes(b.id) - active.includes(a.id));
        list.replaceChildren(...(paused ? [h('div', 'ru-note', tpv('Paused until {time}', { time: fmtHM(new Date(rulesPaused)) }))] : []),
          ...sorted.map((r) => h('div', 'ru-row' + (active.includes(r.id) && !paused ? ' on' : ''),
            h('span', 'ru-ico', emo(RULE_WHEN[r.when] || '⚡')), h('span', 'ru-name', r.name || r.when), h('i', 'ru-dot'))));
      },
    };
  },
};

// The phone's battery with the charge guard's band (Settings › General › Battery care).
W.chargecare = {
  needs: ['tick'],
  create(box) {
    const C = 2 * Math.PI * 42;
    box.innerHTML = '<div class="w-title"></div><div class="cc-body"><div class="cc-wrap"><svg viewBox="0 0 100 100" class="cc-ring">' +
      '<circle class="cc-bg" cx="50" cy="50" r="42"/><circle class="cc-band" cx="50" cy="50" r="42"/><circle class="cc-fg" cx="50" cy="50" r="42"/></svg>' +
      '<b class="cc-pct">—</b></div></div><div class="cc-sub"><span class="cc-state"></span> <span class="cc-text"></span></div>';
    const q = (s) => box.querySelector(s);
    q('.w-title').textContent = tp('Battery care');
    const load = every(15_000, () => {
      const ch = cfg.charge || {};
      const min = ch.min ?? 50, max = ch.max ?? 80;
      const band = q('.cc-band');
      band.style.strokeDasharray = `${(C * (max - min)) / 100} ${C}`;
      band.style.strokeDashoffset = String((-C * min) / 100);
      band.style.opacity = ch.enabled ? '' : '0';
      if (!bridge?.battery) { q('.cc-pct').textContent = '—'; q('.cc-text').textContent = tp('Only on the phone'); return; }
      let b;
      try { b = JSON.parse(bridge.battery()); } catch (_) { return; }
      if (!(b.level >= 0)) return;
      q('.cc-fg').style.strokeDasharray = `${(C * b.level) / 100} ${C}`;
      q('.cc-pct').textContent = b.level + '%';
      q('.cc-state').replaceChildren(emo(b.plugged ? '⚡' : '🔋'));
      const col = b.level <= 20 ? '#ef4444' : ch.enabled && b.level > max ? '#f59e0b' : b.plugged ? '#22c55e' : 'rgb(var(--ambient))';
      box.style.setProperty('--cc', col);
      q('.cc-text').textContent = !ch.enabled ? tp('Battery care is off') + (b.temp ? ` · ${b.temp.toFixed(0)} °C` : '')
        : b.plugged ? tpv('Charges up to {n}%', { n: max }) + (b.level >= max ? tp(' · power off soon') : '') : tpv('Charges again at {n}%', { n: min });
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
    box.append(h('div', 'w-title', o.title || en('Next up')), when, title, meta, track);
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
          title.textContent = !cal ? t('w.loading') : cal.updated || evs.length ? t('w.noEvents') : tp('Add a calendar in Settings › General');
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
          big.replaceChildren(h('div', 'empty', !D.state ? tp('Waiting…') : idx ? tp('No second GPU') : tp('No GPU data')));
          rows.replaceChildren();
          chips.replaceChildren();
          return;
        }
        label.textContent = (GPU_VENDOR[g.vendor] || 'GPU') + (g.integrated ? ' · iGPU' : '');
        name.textContent = shortGpu(g.name);
        name.title = g.name;

        const temps = [g.temp != null ? `${Math.round(g.temp)}°C` : null, g.hotSpot != null ? tpv('hot spot {n}°', { n: Math.round(g.hotSpot) }) : null,
          g.memTemp != null ? tpv('memory {n}°', { n: Math.round(g.memTemp) }) : null].filter(Boolean).join(' · ');
        big.replaceChildren(h('b', null, g.util != null ? String(Math.round(g.util)) : '—'), h('small', null, tp('% load')), h('span', 'gd-temp', temps));

        const list = [];
        const mem = gpuMem(g);
        if (mem) list.push(meter(mem.label === 'VRAM' ? 'VRAM' : tp('GPU memory'), `${r1(mem.used)} / ${Math.round(mem.total)} ${tp('GB')}`, (mem.used / mem.total) * 100));
        if (!g.integrated && g.sharedTotal && g.sharedUsed != null) list.push(meter(tp('Shared memory'), `${r1(g.sharedUsed)} ${tp('GB')}`, (g.sharedUsed / g.sharedTotal) * 100, '#64748b'));
        if (g.power != null) {
          list.push(meter(tp('Power'), g.powerLimit ? `${Math.round(g.power)} / ${Math.round(g.powerLimit)} ${tp("W")}` : `${Math.round(g.power)} ${tp("W")}`,
            g.powerLimit ? (g.power / g.powerLimit) * 100 : g.powerPct, '#facc15'));
        } else if (g.powerPct != null) list.push(meter(tp('Power'), tpv('{n}% of limit', { n: Math.round(g.powerPct) }), g.powerPct, '#facc15'));
        const hottest = Math.max(g.temp ?? -1, g.hotSpot ?? -1);
        if (hottest >= 0) {
          const max = g.tempMax || (g.hotSpot != null ? 110 : 100);
          list.push(meter(tp('Temperature'), `${Math.round(hottest)} / ${Math.round(max)}°C`, (hottest / max) * 100, heat(hottest, max)));
        }
        rows.replaceChildren(...list);

        chips.replaceChildren(...[
          g.clock != null ? tpv('Core {n} MHz', { n: Math.round(g.clock) }) : null,
          g.memClock != null ? tpv('Memory {n} MHz', { n: Math.round(g.memClock) }) : null,
          g.fan != null ? tpv('Fan {n}%', { n: Math.round(g.fan) }) + (g.fanRpm != null ? ' · ' + tpv('{n} rpm', { n: Math.round(g.fanRpm) }) : '')
            : g.fanRpm != null ? tpv('Fan {n} rpm', { n: Math.round(g.fanRpm) }) : null,
          g.driver ? `Driver ${g.driver}` : null,
          g.source ? g.source.split('+').map((x) => GPU_SOURCE[x] || x).join(' + ') : null,
        ].filter(Boolean).map((t) => h('span', 'chip', t)));
      },
    };
  },
};
