import { API, PANEL_HEADERS, bridge, curPage, h, haptic, icon, pages, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- dictation
// Tap the microphone, speak, and the text is typed into the app in front on the PC. The phone's own speech recognizer
// (Android, through the app's bridge) turns speech into text; the agent types it, and only when this widget's own
// "Type the text on the computer" option is on. The microphone is open only while the button shows it, at most a
// minute, and never while the widget is out of sight. Nothing is stored.

// Each recognition session has an id from the bridge; its events go to the widget that started it, so one widget's
// session never ends or answers another's.
const sessions = new Map();
window.onAndroidSpeech = (kind, text, id) => { const handler = sessions.get(id); if (handler) handler(kind, text); };

const MAX_CHARS = 1000; // Dictation.MaxChars on the agent

const ERRORS = {
  '1': 'w.dictate.network', '2': 'w.dictate.network', '4': 'w.dictate.network', '13': 'w.dictate.network',
  '6': 'w.dictate.noMatch', '7': 'w.dictate.noMatch',
  '9': 'w.dictate.permission', permission: 'w.dictate.permission', 'permission-blocked': 'w.dictate.permissionBlocked',
  unavailable: 'w.dictate.unavailable', 'offline-unsupported': 'w.dictate.offlineUnsupported', 'offline-unavailable': 'w.dictate.network',
};

W.dictate = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('dc');
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const mic = h('button', 'dc-mic', icon('mic'));
    mic.setAttribute('aria-label', t('w.dictate.tap'));
    const said = h('div', 'dc-text');
    const note = h('div', 'dc-note', t('w.dictate.tap'));
    box.append(mic, said, note);

    let alive = true, session = 0;
    const can = !!(bridge && bridge.listen && bridge.canListen);
    const visible = () => !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    let watch = 0;
    const done = () => { sessions.delete(session); session = 0; mic.classList.remove('on'); clearInterval(watch); watch = 0; };
    // update() reaches only widgets near the shown page and stops at night, so a session watches by itself
    const guard = () => { if (session && !visible()) { bridge.cancelListening(session); done(); say('w.dictate.tap'); } };
    const say = (key) => { note.textContent = key ? t(key) : ''; };

    async function send(text, confirmed = false) {
      if (o.control !== true) { say('w.dictate.controlOff'); return; }
      // in the query: the app's transport sends no request bodies. Nothing is cut short: a text the agent wouldn't take
      // whole (Dictation.MaxChars, its 16 KB request line) isn't sent at all
      const query = encodeURIComponent(text);
      if (text.length > MAX_CHARS || query.length > 12_000) { say('w.dictate.tooLong'); return; }
      // the agent may take up to 25 s for the longest text (typing plus the queue); the app's transport waits 30 s
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 30_000);
      try {
        const asst = o.assistant === true ? '&assistant=1' : '';
        const conf = confirmed ? '&confirmed=1' : '';
        const r = await fetch(`${API}/dictate?${wq}&text=${query}${asst}${conf}`, { method: 'POST', cache: 'no-store', signal: ctl.signal,
          headers: PANEL_HEADERS });
        if (!r.ok) {
          let error = 'HTTP ' + r.status;
          try { error = (await r.json()).error || error; } catch (_) {}
          throw new Error(error);
        }
        let body = null;
        try { body = await r.json(); } catch (_) {}
        if (alive) {
          if (body && body.confirmRequired) {
            const ov = h('div', 'dc-confirm');
            ov.style.cssText = 'position:fixed;inset:0;z-index:120;background:rgba(0,0,0,.72);backdrop-filter:blur(6px);display:grid;place-items:center;padding:16px;';
            const card = h('div', '');
            card.style.cssText = 'width:100%;max-width:280px;background:var(--card,#14161c);border:1px solid rgba(239,68,68,.45);border-radius:18px;padding:16px;display:flex;flex-direction:column;gap:10px;';
            const title = h('div', '', (body.action || 'Run command') + '?');
            title.style.cssText = 'font-size:15px;font-weight:700;color:var(--text,#fff);';
            const bYes = h('button', 'btn', 'Confirm');
            bYes.style.cssText = 'padding:10px;border-radius:10px;background:#dc2626;color:#fff;font-weight:600;border:0;cursor:pointer;';
            const bNo = h('button', 'btn', 'Cancel');
            bNo.style.cssText = 'padding:10px;border-radius:10px;background:transparent;color:var(--muted,#94a3b8);font-weight:600;border:1px solid var(--line,rgba(255,255,255,.15));cursor:pointer;';
            bYes.onclick = async () => { ov.remove(); await send(text, true); };
            bNo.onclick = () => { ov.remove(); say('w.dictate.tap'); };
            ov.onclick = (ev) => { if (ev.target === ov) { ov.remove(); say('w.dictate.tap'); } };
            card.append(title, bYes, bNo);
            ov.append(card);
            document.body.append(ov);
            return;
          }
          if (body && body.action) note.textContent = body.action;
          else say('w.dictate.sent');
        }
      } catch (e) {
        if (alive) { say(''); toast(e?.message || t('w.offline'), 'err'); }
      } finally { clearTimeout(timer); }
    }

    function onSpeech(kind, text) {
      if (!alive) return;
      if (kind === 'ready') { said.textContent = ''; say('w.dictate.listening'); }
      else if (kind === 'partial') said.textContent = text;
      else if (kind === 'final') {
        done();
        said.textContent = text;
        if (text.trim()) send(text); else say('w.dictate.noMatch');
      } else if (kind === 'error') {
        done();
        say(text === 'cancelled' ? 'w.dictate.tap' : ERRORS[text] || 'w.dictate.noMatch');
      }
    }

    mic.onclick = () => {
      haptic();
      if (!can) { say('w.dictate.update'); return; }
      if (session) { bridge.stopListening(session); return; }
      if (!bridge.canListen()) { say('w.dictate.unavailable'); return; }
      // a session another widget runs is cancelled by the bridge, and that widget hears so through its own id
      session = bridge.listen(String(o.language || '').trim(), o.offline === true) || 0;
      if (!session) return;
      sessions.set(session, onSpeech);
      watch = setInterval(guard, 500);
      mic.classList.add('on');
      said.textContent = '';
      say('w.dictate.listening');
    };

    if (!can) say('w.dictate.update');
    else if (o.control !== true) say('w.dictate.controlOff');

    return {
      update() {
        // the microphone closes when the widget leaves the screen (another page or card, the night screen)
        guard();
      },
      destroy() {
        alive = false;
        if (session) bridge.cancelListening(session);
        done();
      },
    };
  },
};
