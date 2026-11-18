import { API, PANEL_HEADERS, cfg, curPage, h, pages, t } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- camera
// The latest picture of an IP camera the computer pulls with ffmpeg (CameraFeeds.cs). Pictures come as blobs with the
// panel's headers, one request at a time, and each one's memory is released when the next is shown. The agent only
// pulls a camera while a widget keeps asking, so the widget asks only while it is really on screen: its page, its
// Smart Stack card, and no night screen. Two image layers: the next picture is decoded in the hidden one and only then
// swapped in, so the picture never goes blank between frames.
const DEFAULT_FPS = 5; // CameraFeeds.DefaultFps
const STATES = {
  pick: 'w.camera.pick', starting: 'w.camera.starting', 'no-ffmpeg': 'w.camera.noFfmpeg', unreachable: 'w.camera.unreachable',
  login: 'w.camera.login', 'no-picture': 'w.camera.noPicture', busy: 'w.camera.busy', unknown: 'w.camera.save',
};

W.camera = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('cam');
    const layers = [h('img', 'cam-img'), h('img', 'cam-img')];
    for (const img of layers) { img.alt = ''; img.style.objectFit = o.fit === 'contain' ? 'contain' : 'cover'; }
    const note = h('div', 'empty cam-note', t('w.camera.starting'));
    box.append(...layers);
    if (o.caption) box.append(h('div', 'cam-cap', String(o.caption)));
    box.append(note);

    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    let alive = true, running = false, inflight = null, front = 0;
    const urls = [null, null]; // the picture in each layer
    // how often to ask: the camera's own rate (its settings), from the public config
    const every = () => 1000 / Math.max(1, Math.min(10, +(Array.isArray(cfg.cameras) ? cfg.cameras : []).find((c) => c.id === o.camera)?.fps || DEFAULT_FPS));

    // document.hidden: the app went to the background (another app, screen off)
    const visible = () => alive && box.isConnected && !document.hidden && !document.body.classList.contains('night')
      && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const say = (text) => { note.textContent = text || ''; note.hidden = !text; };
    const clear = () => {
      for (let i = 0; i < 2; i++) {
        layers[i].classList.remove('on');
        layers[i].removeAttribute('src');
        if (urls[i]) { URL.revokeObjectURL(urls[i]); urls[i] = null; }
      }
    };
    // decoded before it is shown: swapping a src in place blanks the picture until the new one is ready
    const decoded = (img) => (img.decode ? img.decode() : new Promise((done, fail) => { img.onload = done; img.onerror = fail; }));

    const one = async () => {
      const ctl = new AbortController();
      inflight = ctl;
      // aborting doesn't always settle a body that stalled half-way, so the read itself has a deadline that rejects
      let late;
      const deadline = new Promise((_, fail) => { late = setTimeout(() => { ctl.abort(); fail(new Error('timeout')); }, 8000); });
      deadline.catch(() => {});
      try {
        // nothing on screen (just shown again, or the last download failed): ask for the latest picture even if this widget
        // was sent it before, instead of an "unchanged" 204
        const fresh = urls[front] ? '' : '&fresh=true';
        const r = await Promise.race([fetch(`${API}/camera?${wq}${fresh}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal }), deadline]);
        if (r.status === 204) return every(); // the agent has nothing newer than what is shown
        if (r.ok) {
          const url = URL.createObjectURL(await Promise.race([r.blob(), deadline]));
          const back = 1 - front, next = layers[back];
          next.src = url;
          try { await Promise.race([decoded(next), deadline]); } catch (_) {
            URL.revokeObjectURL(url);
            clear();
            say(t('w.camera.noPicture'));
            return every();
          }
          // out of sight while it loaded: drop it rather than change a hidden picture
          if (!visible()) { next.removeAttribute('src'); URL.revokeObjectURL(url); return 0; }
          if (urls[back]) URL.revokeObjectURL(urls[back]);
          urls[back] = url;
          next.classList.add('on');
          layers[front].classList.remove('on');
          front = back;
          say('');
          return every();
        }
        const body = await Promise.race([r.json().catch(() => ({})), deadline]);
        // no fresh picture: never leave an old one up as if it were live
        clear();
        say(t(STATES[body.state] || 'w.camera.noPicture'));
        return body.state === 'pick' || body.state === 'unknown' ? 5000 : 1500;
      } catch (_) {
        clear();
        say(t('w.offline'));
        return 3000;
      } finally { clearTimeout(late); inflight = null; }
    };

    const loop = async () => {
      if (running) return;
      running = true;
      try {
        while (visible()) {
          const started = Date.now();
          const wait = await one();
          if (!visible()) break;
          // the time the request took counts toward the interval, so the rate holds on a slower link
          await new Promise((done) => setTimeout(done, Math.max(0, wait - (Date.now() - started))));
        }
      } finally {
        running = false;
        // out of sight: the agent stops the camera soon, so don't keep its last picture for when the widget comes back
        if (!visible()) { clear(); say(t('w.camera.starting')); }
      }
    };

    return {
      update() { if (visible()) loop(); },
      destroy() {
        alive = false;
        if (inflight) inflight.abort();
        clear();
      },
    };
  },
};
