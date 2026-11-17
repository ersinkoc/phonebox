import { API, PANEL_HEADERS, curPage, h, pages, t } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- camera
// The latest picture of an IP camera the computer pulls with ffmpeg (CameraFeeds.cs). Pictures come as blobs with the
// panel's headers, one request at a time, and each one's memory is released when the next is shown. The agent only
// pulls a camera while a widget keeps asking, so the widget asks only while it is really on screen: its page, its
// Smart Stack card, and no night screen.
const EVERY = 500; // ms between pictures (CameraFeeds.Fps = 2)
const STATES = {
  pick: 'w.camera.pick', starting: 'w.camera.starting', 'no-ffmpeg': 'w.camera.noFfmpeg', unreachable: 'w.camera.unreachable',
  login: 'w.camera.login', 'no-picture': 'w.camera.noPicture', busy: 'w.camera.busy', unknown: 'w.camera.save',
};

W.camera = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('cam');
    const img = h('img', 'cam-img');
    img.alt = '';
    img.decoding = 'async';
    img.style.objectFit = o.fit === 'contain' ? 'contain' : 'cover';
    const note = h('div', 'empty cam-note', t('w.camera.starting'));
    box.append(img);
    if (o.caption) box.append(h('div', 'cam-cap', String(o.caption)));
    box.append(note);

    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    let alive = true, running = false, shown = null, inflight = null;

    // document.hidden: the app went to the background (another app, screen off)
    const visible = () => alive && box.isConnected && !document.hidden && !document.body.classList.contains('night')
      && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const say = (text) => { note.textContent = text || ''; note.hidden = !text; };
    const clear = () => { if (shown) { URL.revokeObjectURL(shown); shown = null; } img.removeAttribute('src'); img.hidden = true; };

    const one = async () => {
      const ctl = new AbortController();
      inflight = ctl;
      // aborting doesn't always settle a body that stalled half-way, so the read itself has a deadline that rejects
      let late;
      const deadline = new Promise((_, fail) => { late = setTimeout(() => { ctl.abort(); fail(new Error('timeout')); }, 8000); });
      deadline.catch(() => {});
      try {
        const r = await Promise.race([fetch(`${API}/camera?${wq}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal }), deadline]);
        if (r.ok) {
          const url = URL.createObjectURL(await Promise.race([r.blob(), deadline]));
          // out of sight while it loaded: drop it rather than change a hidden picture
          if (!visible()) { URL.revokeObjectURL(url); return 0; }
          const old = shown;
          shown = url;
          img.src = url;
          img.hidden = false;
          say('');
          if (old) URL.revokeObjectURL(old);
          return EVERY;
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
          const wait = await one();
          if (!visible()) break;
          await new Promise((done) => setTimeout(done, wait));
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
