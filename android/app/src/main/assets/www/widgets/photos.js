import { API, PANEL_HEADERS, api, curPage, h, pages, t } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- photo frame
// The agent lists photo ids from a folder on the PC and sends one (scaled-down) photo at a time. Photos are fetched as
// blobs rather than <img src>, so every request carries the panel's headers, at most one is in flight, and each image's
// memory is released after the crossfade. The slideshow only advances while its page is showing and the night screen is off.
W.photos = {
  needs: ['tick'],
  create(box, { o, page }) {
    const every = Math.max(5, Math.min(3600, +o.interval || 30)) * 1000;
    const fit = o.fit === 'contain' ? 'contain' : 'cover';
    box.classList.add('pf');
    const layers = [h('img', 'pf-img'), h('img', 'pf-img')];
    for (const img of layers) { img.alt = ''; img.decoding = 'async'; img.style.objectFit = fit; }
    const note = h('div', 'empty pf-note', t('w.loading'));
    box.append(...layers, note);

    let ids = [], order = [], pos = 0, listAt = 0, nextAt = 0, front = 0, alive = true;
    let inflight = null, shown = null;
    const urls = new Set();

    // neighbouring pages and a Smart Stack's hidden cards get ticks too: the slideshow runs only while it is showing
    const visible = () => !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const say = (text) => { note.textContent = text || ''; note.hidden = !text; };
    const clear = () => {
      for (const img of layers) { img.classList.remove('on'); img.removeAttribute('src'); }
      for (const url of urls) URL.revokeObjectURL(url);
      urls.clear();
      shown = null;
    };
    const shuffle = (a) => { for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [a[i], a[j]] = [a[j], a[i]]; } return a; };
    const reorder = () => { order = o.shuffle === false ? [...ids] : shuffle([...ids]); pos = 0; };

    const loadList = async () => {
      listAt = Date.now();
      try {
        const r = await api('/photos', 'GET', 10_000);
        if (!alive) return;
        if (r.scanning) { listAt = Date.now() - 9 * 60_000; say(t('w.loading')); return; } // ask again in a minute
        const list = Array.isArray(r.ids) ? r.ids : [];
        const same = list.length === ids.length && list.every((id, i) => id === ids[i]);
        ids = list;
        if (!same) reorder();
        if (!ids.length) {
          inflight?.abort();
          clear();
          say(r.error || t('w.photos.setUp'));
        }
      } catch (_) {
        listAt = Date.now() - 9 * 60_000; // ask again in a minute
        if (!shown) say(t('w.offline'));
      }
    };

    const showNext = async () => {
      if (inflight || !order.length) return;
      const id = order[pos];
      pos = (pos + 1) % order.length;
      if (pos === 0 && o.shuffle !== false) reorder();
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 20_000);
      // aborting doesn't always settle a body that stalled half-way, so the read itself has a deadline that rejects
      let late;
      const deadline = new Promise((_, fail) => { late = setTimeout(() => fail(new Error('timeout')), 20_000); });
      deadline.catch(() => {});
      inflight = ctl;
      try {
        const r = await Promise.race([fetch(`${API}/photos/${encodeURIComponent(id)}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal }), deadline]);
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const url = URL.createObjectURL(await Promise.race([r.blob(), deadline]));
        if (!alive || ctl.signal.aborted) { URL.revokeObjectURL(url); return; }
        urls.add(url);
        const next = layers[1 - front], prev = layers[front];
        // the request stays in flight until the image has decoded, so a slow photo is never overtaken
        const ok = await new Promise((done) => {
          next.onload = () => done(true);
          next.onerror = () => done(false);
          ctl.signal.addEventListener('abort', () => done(false));
          next.src = url;
        });
        next.onload = next.onerror = null;
        // hidden while it loaded (another card, page or the night screen): drop it rather than change a hidden frame
        if (!ok || !alive || ctl.signal.aborted || !visible()) {
          if (next.src === url) next.removeAttribute('src');
          URL.revokeObjectURL(url); urls.delete(url);
          if (!ok) nextAt = Date.now() + 5000;
          return;
        }
        next.classList.add('on'); prev.classList.remove('on');
        front = 1 - front; say('');
        const old = shown; shown = url;
        // the old image is released once the crossfade is over
        if (old) setTimeout(() => { if (urls.delete(old)) URL.revokeObjectURL(old); }, 1200);
      } catch (_) {
        nextAt = Date.now() + 5000; // a missing or slow photo: try the next one soon
      } finally { clearTimeout(timer); clearTimeout(late); inflight = null; }
    };

    return {
      update() {
        if (!alive || !visible()) return;
        const now = Date.now();
        if (!listAt || now - listAt > 10 * 60_000) loadList();
        if (order.length && now >= nextAt) { nextAt = now + every; showNext(); }
      },
      destroy() {
        alive = false;
        if (inflight) inflight.abort();
        clear();
      },
    };
  },
};
