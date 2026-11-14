import { API, PANEL_HEADERS, PREVIEW, LOCALE, h, haptic, online, pad } from '../main.js';
import { W } from '../registry.js';

function resolveApiUrl(pathOrUrl) {
  if (!pathOrUrl) return '';
  if (pathOrUrl.startsWith('/api/')) return API + pathOrUrl.slice(4);
  if (pathOrUrl.startsWith(API + '/')) return pathOrUrl;
  return '';
}

function shuffleArray(list) {
  const arr = list.slice();
  for (let i = arr.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j], arr[i]];
  }
  return arr;
}

function formatPhotoDate(ms) {
  if (ms == null || !Number.isFinite(ms)) return '';
  try {
    return new Intl.DateTimeFormat(LOCALE, { day: 'numeric', month: 'short', year: 'numeric' }).format(new Date(ms));
  } catch (_) {
    return '';
  }
}

function formatClockNow() {
  const d = new Date();
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

const MANIFEST_RETRY_MS = 30_000;

W.photos = {
  needs: ['tick'],
  create(box, { w, o }) {
    let destroyed = false;
    let paused = false;
    let infoForced = null; // null = follow option, boolean = toggled by tap
    let feed = null;
    let playlist = [];
    let index = -1;
    let visibleElapsedMs = 0;
    let lastTickAt = Date.now();
    let lastManifestAt = 0;
    let nextManifestRetryAt = 0;
    let manifestBusy = false;
    let slideBusy = false;
    let slideAbort = null;
    let manifestAbort = null;
    let slideSeq = 0;
    let layerCursor = 0;
    let layerBlobs = [null, null];
    let bgBlob = null;
    let downloadingCloud = false;

    const fit = ['cover', 'contain-blur', 'contain'].includes(o.fit) ? o.fit : 'cover';
    const transition = ['fade', 'kenburns', 'slide', 'none'].includes(o.transition) ? o.transition : 'fade';
    const baseInfo = ['none', 'caption', 'caption-date', 'full'].includes(o.info) ? o.info : 'caption-date';
    const tapAction = ['next', 'pause', 'info', 'none'].includes(o.tapAction) ? o.tapAction : 'next';
    const order = ['shuffle', 'newest', 'oldest', 'name'].includes(o.order) ? o.order : 'shuffle';
    const intervalSec = Math.max(5, Math.min(86400, Number(o.interval) || 60));

    const stage = h('div', 'ph-stage');
    stage.dataset.fit = fit;
    stage.dataset.tr = transition;

    const bgImg = fit === 'contain-blur' ? h('img', 'ph-bg') : null;
    if (bgImg) bgImg.alt = '';

    const layers = [0, 1].map(() => {
      const img = h('img', 'ph-img');
      img.alt = '';
      img.decoding = 'async';
      return h('div', 'ph-layer', img);
    });

    const titleBadge = h('span', 'ph-badge ph-title');
    const stateBadge = h('span', 'ph-badge ph-state');
    const badges = h('div', 'ph-badges', titleBadge, stateBadge);
    const clockEl = o.clockOverlay === true ? h('div', 'ph-clock', formatClockNow()) : null;

    const metaCap = h('div', 'ph-cap');
    const metaSub = h('div', 'ph-sub');
    const meta = h('div', 'ph-meta', metaCap, metaSub);
    const emptyEl = h('div', 'empty', 'Loading photos…');

    stage.append(bgImg, layers[0], layers[1], badges, clockEl, meta, emptyEl);
    box.append(stage);

    function revokeLater(url) {
      if (!url) return;
      setTimeout(() => {
        try { URL.revokeObjectURL(url); } catch (_) {}
      }, 1500);
    }

    function updateBadges() {
      const titleText = String(o.title || feed?.title || '').trim();
      titleBadge.textContent = titleText;
      titleBadge.hidden = !titleText;

      let stateText = '';
      if (paused) stateText = 'Paused';
      else if (downloadingCloud) stateText = 'Downloading from cloud…';
      else if (feed?.stale) stateText = online === false ? 'Offline (cached)' : 'Cached';
      stateBadge.textContent = stateText;
      stateBadge.hidden = !stateText;
    }

    function updateMeta(item) {
      const mode = infoForced === true ? (baseInfo === 'none' ? 'full' : baseInfo)
        : infoForced === false ? 'none' : baseInfo;
      if (!item || mode === 'none') {
        meta.hidden = true;
        return;
      }

      const capText = item.caption || item.album || feed?.sourceLabel || '';
      const dateStr = formatPhotoDate(item.takenAt);
      const parts = [];

      if (mode === 'caption-date' && dateStr) parts.push(dateStr);
      if (mode === 'full') {
        if (item.album && item.album !== capText) parts.push(item.album);
        if (item.source && item.source !== item.album) parts.push(item.source);
        if (dateStr) parts.push(dateStr);
        if (playlist.length > 0 && index >= 0) parts.push(`${index + 1} / ${playlist.length}`);
      }

      metaCap.textContent = capText;
      metaCap.hidden = !capText;
      metaSub.textContent = parts.join(' · ');
      metaSub.hidden = parts.length === 0;
      meta.hidden = !capText && parts.length === 0;
    }

    function cancelPendingSlide() {
      ++slideSeq;
      if (slideAbort) {
        slideAbort.abort();
        slideAbort = null;
      }
      slideBusy = false;
      downloadingCloud = false;
    }

    function clearDisplayLayers() {
      layers.forEach((l) => {
        l.classList.remove('on', 'exit');
        l.firstChild.removeAttribute('src');
      });
      layerBlobs.forEach((u) => {
        if (u) {
          try { URL.revokeObjectURL(u); } catch (_) {}
        }
      });
      layerBlobs = [null, null];
      if (bgImg) bgImg.removeAttribute('src');
      if (bgBlob) {
        try { URL.revokeObjectURL(bgBlob); } catch (_) {}
        bgBlob = null;
      }
    }

    function clearDisplayMedia() {
      cancelPendingSlide();
      clearDisplayLayers();
      playlist = [];
      index = -1;
      visibleElapsedMs = 0;
    }

    function showEmpty(message) {
      clearDisplayLayers();
      meta.hidden = true;
      emptyEl.textContent = message;
      emptyEl.hidden = false;
      updateBadges();
    }

    async function fetchPhotoBlobUrl(url, signal) {
      const resolved = resolveApiUrl(url);
      if (!resolved) throw new Error('Invalid image URL');
      const r = await fetch(resolved, { headers: PANEL_HEADERS, cache: 'no-store', signal });
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      const blob = await r.blob();
      return URL.createObjectURL(blob);
    }

    async function showSlide(step = 1) {
      if (destroyed || playlist.length === 0) return;

      const mySeq = ++slideSeq;
      if (slideAbort) slideAbort.abort();
      const myAbort = new AbortController();
      slideAbort = myAbort;
      slideBusy = true;

      try {
        const maxAttempts = Math.min(playlist.length, 8);
        const mayDownloadFromCloud = (feed?.cloudOnlyCount > 0) || (o.provider && o.provider !== 'local');
        if (mayDownloadFromCloud && !emptyEl.hidden) {
          emptyEl.textContent = 'Downloading photo from cloud…';
        }

        for (let attempts = 0; attempts < maxAttempts; attempts++) {
          if (order === 'shuffle' && index + step >= playlist.length && attempts === 0) {
            playlist = shuffleArray(feed?.photos || playlist);
            index = -1;
          }
          index = ((index + step) % playlist.length + playlist.length) % playlist.length;
          const item = playlist[index];

          let blobUrl = null;
          let dlTimer = null;
          try {
            if (mayDownloadFromCloud) {
              dlTimer = setTimeout(() => {
                if (!destroyed && mySeq === slideSeq) {
                  downloadingCloud = true;
                  updateBadges();
                }
              }, 350);
            }
            blobUrl = await fetchPhotoBlobUrl(item.url, myAbort.signal);
            if (dlTimer) { clearTimeout(dlTimer); dlTimer = null; }
            if (downloadingCloud) { downloadingCloud = false; updateBadges(); }
            if (destroyed || mySeq !== slideSeq) {
              URL.revokeObjectURL(blobUrl);
              return;
            }

            const nextIdx = layerCursor ^ 1;
            const prevIdx = layerCursor;
            const nextLayer = layers[nextIdx];
            const prevLayer = layers[prevIdx];
            const nextImg = nextLayer.firstChild;

            await new Promise((resolve, reject) => {
              const probe = new Image();
              const onAbort = () => {
                probe.onload = null;
                probe.onerror = null;
                probe.src = '';
                reject(new DOMException('Aborted', 'AbortError'));
              };
              if (myAbort.signal.aborted) {
                onAbort();
                return;
              }
              myAbort.signal.addEventListener('abort', onAbort, { once: true });
              probe.onload = () => {
                myAbort.signal.removeEventListener('abort', onAbort);
                resolve();
              };
              probe.onerror = () => {
                myAbort.signal.removeEventListener('abort', onAbort);
                reject(new Error('Decode failed'));
              };
              probe.src = blobUrl;
            });
            if (destroyed || mySeq !== slideSeq) {
              URL.revokeObjectURL(blobUrl);
              return;
            }

            nextImg.src = blobUrl;
            if (bgImg) {
              bgImg.src = blobUrl;
              const oldBg = bgBlob;
              bgBlob = null;
              revokeLater(oldBg);
            }

            const oldBlob = layerBlobs[nextIdx];
            layerBlobs[nextIdx] = blobUrl;
            revokeLater(oldBlob);

            nextLayer.classList.remove('exit', 'on');
            void nextLayer.offsetWidth; // restart Ken Burns / slide animation cleanly
            nextLayer.classList.add('on');
            prevLayer.classList.remove('on');
            if (prevLayer.firstChild.getAttribute('src')) prevLayer.classList.add('exit');

            layerCursor = nextIdx;
            emptyEl.hidden = true;
            updateBadges();
            updateMeta(item);
            visibleElapsedMs = 0;
            return;
          } catch (err) {
            if (dlTimer) { clearTimeout(dlTimer); dlTimer = null; }
            if (downloadingCloud) { downloadingCloud = false; updateBadges(); }
            if (blobUrl) URL.revokeObjectURL(blobUrl);
            if (err?.name === 'AbortError' || destroyed || mySeq !== slideSeq) return;
            step = 1;
          }
        }

        if (layerBlobs[layerCursor] && layers[layerCursor].classList.contains('on')) {
          emptyEl.hidden = true;
          updateBadges();
        } else {
          showEmpty('Photos could not be loaded');
        }
      } finally {
        if (mySeq === slideSeq) {
          slideBusy = false;
          if (slideAbort === myAbort) slideAbort = null;
        }
      }
    }

    async function loadManifest(forceRefresh = false) {
      if (destroyed || manifestBusy) return;
      manifestBusy = true;
      if (manifestAbort) manifestAbort.abort();
      manifestAbort = new AbortController();

      try {
        let data;
        if (PREVIEW) {
          const r = await fetch(`${API}/photos/preview-feed`, {
            method: 'POST',
            headers: { ...PANEL_HEADERS, 'Content-Type': 'application/json' },
            body: JSON.stringify({ options: o }),
            signal: manifestAbort.signal,
            cache: 'no-store',
          });
          if (!r.ok) throw new Error(`HTTP ${r.status}`);
          data = await r.json();
        } else {
          const id = encodeURIComponent(w.id || '');
          const qs = forceRefresh ? '&refresh=1' : '';
          const r = await fetch(`${API}/photos/feed?widgetId=${id}${qs}`, {
            headers: PANEL_HEADERS,
            signal: manifestAbort.signal,
            cache: 'no-store',
          });
          if (!r.ok) throw new Error(`HTTP ${r.status}`);
          data = await r.json();
        }
        if (destroyed) return;

        feed = data;
        lastManifestAt = Date.now();
        nextManifestRetryAt = 0;
        updateBadges();

        const items = Array.isArray(data.photos) ? data.photos : [];
        if (!data.configured) {
          clearDisplayMedia();
          showEmpty(data.error || 'Choose a photo album or folder in PhoneBox Settings');
          return;
        }
        if (items.length === 0) {
          clearDisplayMedia();
          showEmpty(data.error || 'No photos found in the selected source');
          return;
        }

        const prevId = playlist[index]?.id;
        playlist = order === 'shuffle' ? shuffleArray(items) : items.slice();
        const kept = prevId ? playlist.findIndex((p) => p.id === prevId) : -1;
        if (kept >= 0 && layerBlobs[layerCursor]) {
          index = kept;
          updateMeta(playlist[index]);
        } else {
          index = -1;
          await showSlide(1);
        }
      } catch (err) {
        if (err?.name === 'AbortError' || destroyed) return;
        nextManifestRetryAt = Date.now() + MANIFEST_RETRY_MS;
        if (playlist.length === 0) {
          showEmpty('Waiting for photo source from the agent');
        }
      } finally {
        manifestBusy = false;
      }
    }

    let tapStart = null;
    stage.addEventListener('pointerdown', (e) => {
      if (e.target.closest('button, a, input, select, textarea')) return;
      tapStart = { x: e.clientX, y: e.clientY, t: Date.now() };
    }, { passive: true });

    stage.addEventListener('click', (e) => {
      if (e.target.closest('button, a, input, select, textarea')) return;
      if (tapStart) {
        const dx = Math.abs(e.clientX - tapStart.x);
        const dy = Math.abs(e.clientY - tapStart.y);
        tapStart = null;
        if (dx > 12 || dy > 12) return;
      }
      if (tapAction === 'none') return;

      haptic();
      if (tapAction === 'next') {
        if (playlist.length > 0) showSlide(1);
      } else if (tapAction === 'pause') {
        paused = !paused;
        stage.classList.toggle('paused', paused);
        updateBadges();
      } else if (tapAction === 'info') {
        const curShown = infoForced === true || (infoForced === null && baseInfo !== 'none');
        infoForced = !curShown;
        updateMeta(playlist[index]);
      }
    });

    function isVisible() {
      if (document.hidden || box.closest('.stack-item:not(.active)')) return false;
      const r = box.getBoundingClientRect();
      return r.width > 0 && r.height > 0 && r.right > 0 && r.left < innerWidth && r.bottom > 0 && r.top < innerHeight;
    }

    loadManifest(false);

    return {
      update(kind) {
        if (destroyed || kind !== 'tick') return;
        if (clockEl) clockEl.textContent = formatClockNow();

        const now = Date.now();
        const dt = Math.max(0, Math.min(2500, now - lastTickAt));
        lastTickAt = now;

        const visible = isVisible();
        if (!visible) return;

        if (!manifestBusy && now >= nextManifestRetryAt && (lastManifestAt === 0 || now - lastManifestAt >= 15 * 60_000)) {
          loadManifest(false);
        }

        if (!paused && !slideBusy && playlist.length > 1) {
          visibleElapsedMs += dt;
          if (visibleElapsedMs >= intervalSec * 1000) {
            visibleElapsedMs = 0;
            showSlide(1);
          }
        }
      },
      destroy() {
        destroyed = true;
        clearDisplayMedia();
        if (manifestAbort) manifestAbort.abort();
      },
    };
  },
};
