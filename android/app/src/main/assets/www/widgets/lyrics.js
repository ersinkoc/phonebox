import { D, api, h, icon, media, t } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- lyrics (LRCLIB, asked by the agent)

/** Timed lyrics in LRC form ("[01:02.50] words") as [{ ms, text }] in time order; lines without a time are left out. */
export function parseLrc(text) {
  const lines = [];
  for (const raw of String(text || '').split(/\r?\n/)) {
    const lead = raw.match(/^\s*(?:\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]\s*)+/);
    if (!lead) continue;
    const stamps = [...lead[0].matchAll(/\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]/g)];
    const words = raw.slice(lead[0].length).trim();
    // a line sung twice carries two times: "[00:10.00][01:20.00] chorus"
    for (const m of stamps) lines.push({ ms: Math.round((+m[1] * 60 + parseFloat(m[2].replace(':', '.'))) * 1000), text: words });
  }
  return lines.sort((a, b) => a.ms - b.ms);
}

/** The index of the line being sung at `ms` (the last one that has started), or -1 before the first. */
export function lyricAt(lines, ms) {
  let lo = 0, hi = lines.length - 1, at = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (lines[mid].ms <= ms) { at = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return at;
}

// Synced lyrics: the words of the song playing on the computer, the line being sung lit and kept in the middle. Timed
// lyrics follow the position; plain ones are shown as a page, and so are timed ones when the player reports no position
// (a note says why). The agent asks LRCLIB (free, no account) once per song, and only for a widget whose own "Look up
// lyrics on LRCLIB" is turned on (off by default): the phone names the widget, and the agent checks that widget's option.
W.lyrics = {
  needs: ['state', 'fast', 'media'],
  create(box, { o, page, w }) {
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const list = h('div', 'ly-list');
    const note = h('div', 'ly-note');
    box.append(h('div', 'w-title', o.title || t('w.lyrics.title')), list, note, h('div', 'ly-credit', 'LRCLIB'));
    const offset = Math.max(-5000, Math.min(5000, Number(o.offset) || 0)); // ms: a positive value shows the lines earlier
    const plainToo = o.plain !== false;
    let track = '', result = null, lines = [], rows = [], shown = -2, loading = false, retryAt = 0, timedShown = null, requestGen = 0;
    const trackKey = (m) => (m?.title ? [m.app, m.title, m.artist, m.album, m.durMs].join('\n') : '');
    // a player that reports no timeline (no length) gives no position to follow
    const hasPosition = () => Number(media?.durMs) > 0;
    const say = (text) => { note.textContent = text; note.hidden = !text; };
    const asPage = (texts) => texts.map((l) => h('div', 'ly-line' + (l.trim() ? '' : ' gap'), l.trim() || ' '));
    const paint = () => {
      rows = []; shown = -2; timedShown = null;
      box.classList.remove('timed', 'plain');
      if (!track) { list.replaceChildren(); say(t('w.lyrics.idle')); return; }
      if (loading || !result) { list.replaceChildren(); say(t('w.lyrics.loading')); return; }
      const s = result.status;
      if (s === 'off') { list.replaceChildren(); say(t('w.lyrics.off')); return; }
      // the agent found nothing to look up (a title that is all "(Official Video)", say): as with nothing playing
      if (s === 'idle') { list.replaceChildren(); say(t('w.lyrics.idle')); return; }
      // an error, or a status this version of the panel does not know
      if (s !== 'ok' && s !== 'notfound') { list.replaceChildren(); say(t('w.lyrics.error')); return; }
      if (s === 'notfound' || (s === 'ok' && !result.instrumental && !lines.length && !(plainToo && result.plain))) {
        list.replaceChildren(); say(t('w.lyrics.notFound')); return;
      }
      if (result.instrumental && !lines.length) { list.replaceChildren(); say(t('w.lyrics.instrumental')); return; }
      if (lines.length && hasPosition()) {
        box.classList.add('timed');
        timedShown = true;
        rows = lines.map((l) => h('div', 'ly-line' + (l.text ? '' : ' gap'), l.text || icon('music')));
        list.replaceChildren(...rows);
        say('');
        follow(true);
        return;
      }
      box.classList.add('plain');
      if (lines.length) {
        // timed lyrics, but nothing to time them with: the words as a page, and why they don't move
        timedShown = false;
        list.replaceChildren(...asPage(lines.map((l) => l.text)));
        say(t('w.lyrics.noPosition'));
      } else {
        list.replaceChildren(...asPage(String(result.plain).split(/\r?\n/)));
        say(t('w.lyrics.plain'));
      }
      list.scrollTop = 0;
    };
    const follow = (jump = false) => {
      if (!rows.length || !media) return;
      const pos = media.posMs + (media.playing ? performance.now() - media.at : 0) + offset;
      const at = lyricAt(lines, pos);
      if (at === shown && !jump) return;
      shown = at;
      rows.forEach((r, i) => { r.classList.toggle('now', i === at); r.classList.toggle('past', i < at); });
      const el = rows[Math.max(0, at)];
      const top = el.offsetTop - list.clientHeight / 2 + el.offsetHeight / 2;
      if (jump) list.scrollTop = top; else list.scrollTo?.({ top, behavior: 'smooth' });
    };
    const fetchFor = async (key) => {
      // only the newest request counts: after A → B → A, the first request for A must not overwrite the second's answer
      const gen = ++requestGen;
      // each widget keeps to its own option, even when another one on the page has the lookup on
      if (o.lookup !== true) { loading = false; result = { status: 'off' }; lines = []; paint(); return; }
      loading = true; result = null; lines = []; paint();
      let answer;
      try { answer = await api(`/lyrics?${wq}`, 'GET', 20_000); }
      catch (_) { answer = { status: 'error' }; }
      if (gen !== requestGen || key !== track) return; // a newer request (or another song) is under way
      loading = false;
      result = answer && typeof answer === 'object' ? answer : { status: 'error' };
      if (result.status === 'error') retryAt = Date.now() + 30_000;
      lines = result.synced ? parseLrc(result.synced) : [];
      paint();
    };
    paint(); // nothing playing yet: the idle note until the first update brings a track
    return {
      update(kind) {
        const key = trackKey(D.state?.media);
        if (key !== track) {
          track = key;
          if (key) fetchFor(key); else { result = null; lines = []; paint(); }
          return;
        }
        if (key && result?.status === 'error' && !loading && Date.now() > retryAt) { fetchFor(key); return; }
        // the player started or stopped reporting a position: follow the lines, or show them as a page
        if (timedShown !== null && timedShown !== hasPosition()) { paint(); return; }
        if (kind === 'fast' || kind === 'state') follow();
      },
    };
  },
};
