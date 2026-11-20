import { $, API, D, LEVEL_COLOR, LOCALE, mediaHtml, METRICS, PANEL_HEADERS, PREVIEW, Q_STYLE, S_ORDER, ago, api, averageColor, buildLayout, buzz, cfg, cfgVersion, chart, color, cssVar, currentProcessesSort, emit, fillIcon, fmtClock, fmtDur, fmtMem, fmtReset, fmtTime, fmtValue, gotoProcesses, gpuMem, gpusOf, h, en, haptic, hasProcessesPage, hist, hour12, icon, keyButton, labelled, levelOf, media, metricText, online, openSheet, paceInfo, pad, prRow, pushView, quotaProviders, r1, registerProcessesSorter, renderStatus, ringSvg, setArtColor, configMark, configReadSince, localWriteStored, setMediaState, setVolDragging, shortDur, shortGpu, store, t, tick, toast, volDragging, wxBlock, wxDeg, wxIcon, emo, rich } from '../main.js';
import { W } from '../registry.js';
import { invalidateDeckState } from './controls.js';

function bindProcessLink(box, sortTarget) {
  if (!hasProcessesPage()) return;
  box.style.cursor = 'pointer';
  box.addEventListener('click', () => {
    if (!hasProcessesPage()) return;
    haptic();
    gotoProcesses(sortTarget);
  });
}

// ================================================================= widgets
// Each widget: { needs: [data kinds], bare?: no card background, create(box, ctx) -> { update(kind) } }
// Data kinds: tick (every second), fast (4×/s), state, weather, quotas, work, procs, push, notes.

W.clock = {
  bare: true, needs: ['tick', 'state', 'weather', 'quotas', 'work'],
  create(box, { o }) {
    const status = o.status !== false ? h('div', 'status') : null;
    const hh = h('span', 'hh', '--'), colon = h('span', 'colon', ':'), mm = h('span', 'mm', '--'), ss = h('span', 'sec'), ap = h('span', 'ap');
    const date = o.date !== false ? h('div', 'date') : null;
    const wx = o.weather !== false ? wxBlock() : null;
    box.append(status || '', h('div', 'clock', hh, colon, mm, ss, ap), date || '', wx || '');
    box.style.setProperty('--tf', (0.84 - (status ? 0.1 : 0) - (date ? 0.11 : 0) - (wx ? 0.2 : 0)).toFixed(2));
    return {
      // without the weather line the clock must not make the panel fetch the weather
      needs: wx ? W.clock.needs : W.clock.needs.filter((k) => k !== 'weather'),
      update(kind) {
        if (kind === 'tick') {
          const now = new Date();
          const H = now.getHours(), h12 = hour12();
          hh.textContent = h12 ? String(H % 12 || 12) : pad(H);
          ap.textContent = h12 ? (H < 12 ? 'AM' : 'PM') : '';
          mm.textContent = pad(now.getMinutes());
          ss.textContent = cfg.display?.showSeconds === false ? '' : pad(now.getSeconds());
          // the colon blinks once a second from here, not from a CSS animation: a running animation makes the browser draw a frame
          // on every display refresh (120 a second on some phones) for something that changes once a second
          colon.classList.toggle('dim', now.getSeconds() % 2 === 1);
          if (date) date.textContent = now.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' });
          if (status && now.getSeconds() % 30 === 0) renderStatus(status, o); // battery
        } else if (kind === 'weather') wx?.update();
        else if (status) renderStatus(status, o);
      },
    };
  },
};

W.weather = {
  needs: ['weather'],
  create(box) {
    const wx = wxBlock();
    box.append(wx);
    return { update: () => wx.update() };
  },
};

W.forecast = {
  needs: ['weather'],
  create(box, { o }) {
    const daily = o.mode === 'daily';
    const row = h('div', 'fc');
    box.append(h('div', 'w-title', en(daily ? '7-day forecast' : 'Next hours'), h('span', 'w-sub', '')), row);
    return {
      update() {
        const w = D.weather;
        if (!w?.hourly || !w?.daily) { row.replaceChildren(h('div', 'empty', D.weather === null ? t('w.loading') : t('w.weatherUnavailable'))); return; }
        box.querySelector('.w-sub').textContent = cfg.weather?.city || '';
        const vertical = box.clientHeight > box.clientWidth * 0.9;
        const room = vertical ? box.clientHeight - 30 : box.clientWidth - 16;
        const n = Math.max(3, Math.min(daily ? 7 : 12, Math.floor(room / (vertical ? 30 : 50))));
        row.classList.toggle('vert', vertical);
        const cells = [];
        if (daily) {
          const d = w.daily;
          for (let i = 0; i < Math.min(n, d.time.length); i++) {
            // some models (Météo-France, CMA) forecast fewer than 7 days and send null for the rest: stop there, not at "0°"
            if (d.temperature_2m_max?.[i] == null) break;
            const day = i === 0 ? t('time.today') : new Date(d.time[i] + 'T12:00').toLocaleDateString(LOCALE, { weekday: 'short' });
            const rain = d.precipitation_probability_max?.[i];
            cells.push(h('div', 'fc-i', h('span', 'fc-t', day), h('span', 'fc-ic', emo(wxIcon(d.weather_code[i]))),
              h('span', 'fc-v', wxDeg(d.temperature_2m_max[i]) + '°', h('small', null, ' ' + wxDeg(d.temperature_2m_min[i]) + '°')),
              h('span', 'fc-r', rain >= 20 ? rich(`💧${rain}%`) : '')));
          }
        } else {
          const hr = w.hourly;
          let start = hr.time.indexOf((w.current?.time || '').slice(0, 13) + ':00');
          if (start < 0) start = 0;
          const step = n >= 10 ? 1 : 2;
          for (let k = 0; k < n; k++) {
            const i = start + k * step;
            if (i >= hr.time.length || hr.temperature_2m?.[i] == null) break;
            const rain = hr.precipitation_probability?.[i];
            cells.push(h('div', 'fc-i', h('span', 'fc-t', k === 0 ? 'Now' : hr.time[i].slice(11, 13)), h('span', 'fc-ic', emo(wxIcon(hr.weather_code[i], hr.is_day?.[i]))),
              h('span', 'fc-v', wxDeg(hr.temperature_2m[i]) + '°'), h('span', 'fc-r', rain >= 20 ? rich(`💧${rain}%`) : '')));
          }
        }
        row.replaceChildren(...cells);
      },
    };
  },
};

W.media = {
  needs: ['state', 'fast'],
  create(box) {
    box.innerHTML = mediaHtml();
    const q = (s) => box.querySelector(s);
    const img = q('.art'), vol = q('.volr');
    const sourceIcon = q('.m-source-icon');
    const sourceIcons = {
      YouTube: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="1" y="4" width="22" height="16" rx="5" fill="#ff0033"/><path d="m10 8 6 4-6 4z" fill="#fff"/></svg>',
      'YouTube Music': '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="11" fill="#ff0033"/><circle cx="12" cy="12" r="6.5" fill="none" stroke="#fff" stroke-width="1.4"/><path d="m10 8.5 5.5 3.5-5.5 3.5z" fill="#fff"/></svg>',
      'Internet Radio': '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6.2 6.8 18.5 2a1 1 0 0 1 .7 1.9L10.8 7H20a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h2.2zm1.8 5.2a3 3 0 1 0 0 6 3 3 0 0 0 0-6zm6 1.5v1.8h4v-1.8h-4zm0 3.2v1.8h4v-1.8h-4z" fill="#38bdf8"/></svg>',
    };
    let artId = -1, volTimer = 0, volumeRelease = 0;
    img.addEventListener('load', () => { img.classList.add('on'); setArtColor(averageColor(img)); });
    img.addEventListener('error', () => img.classList.remove('on'));
    box.querySelectorAll('[data-media]').forEach((btn) => btn.addEventListener('click', async () => {
      haptic();
      const action = btn.dataset.media;
      if (action === 'playpause' && media) {
        media.playing = !media.playing; // optimistic update
        media.at = performance.now();
        box.classList.toggle('playing', media.playing);
      }
      try {
        const res = await api('/media/' + action, 'POST');
        if (res && typeof res === 'object' && 'media' in res) setMediaState(res.media);
        invalidateDeckState();
        emit('tick');
      } catch (_) {}
    }));
    // The position bar is a slider. Holding it (touch, mouse) or moving it with the arrow keys shows a bubble with the time
    // the song will continue from; it seeks on release, not while moving. Meanwhile the once-a-second state and the 250 ms
    // refresh leave the bar alone (see update()).
    const bar = q('.bar'), tip = q('.scrub-tip');
    let scrub = null, seekTimer = 0, ariaSec = -1; // scrub: { id, ms, key } while held
    const canScrub = () => !!(media && media.durMs > 0 && media.canSeek);
    // which song a held bar belongs to: a different one underneath (next track) cancels the scrub instead of seeking it
    const trackKey = () => (media ? [media.app, media.title, media.durMs].join('\n') : '');
    const paintAria = (ms) => {
      bar.setAttribute('aria-valuenow', Math.round(ms / 1000));
      bar.setAttribute('aria-valuetext', fmtTime(ms));
    };
    const nowMs = () => Math.min(media.durMs, media.posMs + (media.playing ? performance.now() - media.at : 0));
    const msFrom = (clientX) => {
      const r = bar.getBoundingClientRect();
      return Math.round(Math.min(1, Math.max(0, (clientX - r.left) / r.width)) * media.durMs);
    };
    const paintScrub = (ms) => {
      const pct = (ms / media.durMs) * 100;
      q('.fill').style.width = pct + '%';
      q('.m-pos').textContent = tip.textContent = fmtTime(ms);
      bar.style.setProperty('--tip', pct + '%');
      paintAria(ms);
    };
    const endScrub = (commit) => {
      clearTimeout(seekTimer);
      if (!scrub) return;
      const { ms, key } = scrub;
      scrub = null;
      bar.classList.remove('scrubbing');
      if (!commit || !canScrub() || key !== trackKey()) return; // checked again: the song may have changed since it was held
      haptic();
      // the bar stays where it was put until the agent has caught up: its next samples still hold the old position
      media.posMs = ms; media.at = performance.now(); media.hold = performance.now() + 2500;
      api('/media/seek?ms=' + ms, 'POST').catch(() => {});
    };
    const startScrub = (id, ms) => { scrub = { id, ms, key: trackKey() }; bar.classList.add('scrubbing'); paintScrub(ms); };
    bar.addEventListener('pointerdown', (e) => {
      if (!canScrub() || (e.pointerType === 'mouse' && e.button !== 0)) return;
      clearTimeout(seekTimer);
      startScrub(e.pointerId, msFrom(e.clientX));
      try { bar.setPointerCapture(e.pointerId); } catch (_) {}
    });
    bar.addEventListener('pointermove', (e) => {
      if (scrub && e.pointerId === scrub.id) { scrub.ms = msFrom(e.clientX); paintScrub(scrub.ms); }
    });
    bar.addEventListener('pointerup', (e) => { if (scrub && e.pointerId === scrub.id) endScrub(true); });
    bar.addEventListener('pointercancel', () => endScrub(false));
    bar.addEventListener('lostpointercapture', () => endScrub(false)); // a no-op after pointerup: the scrub has ended
    bar.addEventListener('keydown', (e) => {
      const step = { ArrowRight: 5000, ArrowUp: 5000, ArrowLeft: -5000, ArrowDown: -5000, PageUp: 30000, PageDown: -30000 }[e.key];
      if (step === undefined && e.key !== 'Home' && e.key !== 'End') return;
      if (!canScrub()) return;
      e.preventDefault();
      const base = scrub ? scrub.ms : nowMs();
      const ms = e.key === 'Home' ? 0 : e.key === 'End' ? Math.max(0, media.durMs - 1000) : Math.min(media.durMs, Math.max(0, base + step));
      startScrub(-1, ms);
      clearTimeout(seekTimer);
      seekTimer = setTimeout(() => endScrub(true), 350); // a run of key presses seeks once
    });
    const paintVol = (v) => { vol.style.setProperty('--p', v + '%'); q('.vol-val').textContent = v; };
    const cancelVolumeDrag = () => {
      clearTimeout(volTimer);
      clearTimeout(volumeRelease);
      setVolDragging(vol, false);
    };
    vol.addEventListener('input', () => {
      clearTimeout(volumeRelease);
      setVolDragging(vol, true);
      paintVol(vol.value);
      clearTimeout(volTimer);
      volTimer = setTimeout(() => api('/volume?level=' + vol.value, 'POST').catch(() => {}), 120);
    });
    vol.addEventListener('change', () => {
      clearTimeout(volumeRelease);
      volumeRelease = setTimeout(() => setVolDragging(vol, false), 600);
    });
    vol.addEventListener('pointercancel', cancelVolumeDrag);
    q('.mute').addEventListener('click', async () => {
      haptic();
      if ((!D.state?.audio || D.state.audio.canControl === false)) return;
      try { const a = await api('/volume?op=mute', 'POST'); D.state.audio = a; emit('state'); } catch (_) {}
    });

    return {
      update(kind) {
        if (scrub && scrub.key !== trackKey()) endScrub(false); // the song changed under the finger
        if (kind === 'fast') {
          if (!media || scrub) return; // while the bar is held it shows the chosen time, not the song's
          if (media.durMs > 0) {
            const pos = Math.min(media.durMs, media.posMs + (media.playing ? performance.now() - media.at : 0));
            q('.fill').style.width = (pos / media.durMs) * 100 + '%';
            q('.m-pos').textContent = fmtTime(pos);
            if (Math.floor(pos / 1000) !== ariaSec) { ariaSec = Math.floor(pos / 1000); paintAria(pos); } // once a second, not every 250 ms
          } else if (media.app === 'Internet Radio') {
            const pos = Math.max(0, (media.posMs || 0) + (media.playing ? performance.now() - media.at : 0));
            q('.fill').style.width = media.playing ? '100%' : '0';
            q('.m-pos').textContent = fmtTime(pos);
          }
          return;
        }
        const m = D.state?.media, a = D.state?.audio;
        vol.disabled = (!a || a.canControl === false);
        box.classList.toggle('volume-unavailable', (!a || a.canControl === false));
        q('.mute').disabled = (!a || a.canControl === false);
        box.classList.toggle('muted-on', !!a?.muted);
        if (a) {
          if (!volDragging) { vol.value = a.volume; paintVol(a.volume); }
        } else q('.vol-val').textContent = 'n/a';
        box.classList.toggle('idle', !m);
        if (!m) {
          endScrub(false);
          box.classList.remove('playing');
          q('.m-app-name').textContent = 'Media';
          sourceIcon.replaceChildren();
          q('.m-title').textContent = t('w.nothingPlaying');
          q('.m-artist').textContent = t('w.pressToResume');
          q('.fill').style.width = '0';
          q('.m-pos').textContent = q('.m-dur').textContent = '0:00';
          ariaSec = -1; paintAria(0); bar.setAttribute('aria-disabled', 'true');
          if (artId !== 0) { artId = 0; img.classList.remove('on'); setArtColor(null); }
          return;
        }
        box.classList.toggle('playing', m.playing);
        q('.m-app-name').textContent = m.app || 'Media';
        sourceIcon.innerHTML = sourceIcons[m.app] || '';
        q('.m-title').textContent = m.title || 'Unknown track';
        q('.m-artist').textContent = [m.artist, m.album].filter(Boolean).join(' · ') || '—';
        if (m.app === 'Internet Radio' && !(m.durMs > 0)) {
          q('.fill').style.width = m.playing ? '100%' : '0';
          q('.m-pos').textContent = fmtTime(m.posMs || 0);
          q('.m-dur').textContent = m.playing ? 'LIVE' : '0:00';
        } else {
          q('.m-dur').textContent = fmtTime(m.durMs);
        }
        // the slider's range and value for assistive technology (its maximum defaults to 100 without this)
        if (m.durMs > 0) {
          bar.setAttribute('aria-valuemax', Math.max(1, Math.round(m.durMs / 1000)));
          bar.setAttribute('aria-disabled', String(!m.canSeek));
          if (media && !scrub) { ariaSec = Math.floor(nowMs() / 1000); paintAria(nowMs()); }
        } else {
          // no known length (a live stream): no range to move on, and nothing left over from the song before
          bar.setAttribute('aria-valuemax', '0');
          bar.setAttribute('aria-disabled', 'true');
          ariaSec = -1; paintAria(0);
        }
        if (m.artId !== artId) {
          artId = m.artId;
          if (m.artId) img.src = `${API}/art?id=${m.artId}`;
          else { img.classList.remove('on'); setArtColor(null); }
        }
      },
      destroy() { endScrub(false); cancelVolumeDrag(); },
    };
  },
};

W.stat = {
  needs: ['state'],
  create(box, { o }) {
    const m = METRICS[o.metric] || METRICS.cpu;
    const c = color(m.color);
    box.style.setProperty('--c', c);
    const canvas = h('canvas');
    const lbl = h('div', 't-label', m.label);
    const val = h('b', null, '—'), unit = h('small'), sub = h('div', 't-sub', ' ');
    box.append(canvas, lbl, h('div', 't-val', val, unit), sub);
    if (o.metric === 'cpu' || o.metric === 'ram') bindProcessLink(box, o.metric === 'cpu' ? 'cpu' : 'mem');
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const [v, u] = metricText(m, s);
        val.textContent = v;
        unit.textContent = u;
        const pctVal = m.pct?.(s);
        lbl.textContent = pctVal != null && m.label === 'RAM' ? `RAM (${Math.round(pctVal)}%)` : m.label;
        sub.textContent = m.sub?.(s) || ' ';
        chart(canvas, [{ data: hist[m.hist || o.metric] || hist.cpu, color: c, max: m.max(s) }]);
      },
    };
  },
};

W.gauge = {
  needs: ['state'],
  create(box, { o }) {
    const m = METRICS[o.metric] || METRICS.cpu;
    box.style.setProperty('--q', color(m.color));
    const dial = h('div', 'g-dial'), pct = h('div', 'g-val'), sub = h('div', 'g-sub');
    box.append(dial, h('div', 'g-label', m.label), sub);
    dial.append(pct);
    if (o.metric === 'cpu' || o.metric === 'ram') bindProcessLink(box, o.metric === 'cpu' ? 'cpu' : 'mem');
    let svg = null;
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const raw = m.pct ? m.pct(s) : m.val(s);
        const p = raw == null ? 0 : (raw / (m.pct ? 100 : m.max(s))) * 100;
        const warn = m.hot ? (raw > m.hot[1] ? '#ef4444' : raw > m.hot[0] ? '#f59e0b' : '') : (p >= 95 ? '#ef4444' : p >= 85 ? '#f59e0b' : '');
        dial.style.setProperty('--qc', warn || 'var(--q)');
        if (svg) svg.remove();
        dial.insertAdjacentHTML('afterbegin', ringSvg(p));
        svg = dial.firstElementChild;
        const [v, u] = metricText(m, s);
        pct.replaceChildren(v, h('small', null, u));
        sub.textContent = m.sub?.(s) || '';
      },
    };
  },
};

W.chart = {
  needs: ['state'],
  create(box, { o }) {
    // a list of only unknown names falls back like no list at all; own keys only, so "constructor" is unknown too
    const keys = (Array.isArray(o.metrics) && o.metrics.length ? o.metrics : ['cpu']).filter((k) => Object.prototype.hasOwnProperty.call(METRICS, k));
    if (!keys.length) keys.push('cpu');
    const single = keys.length === 1 ? METRICS[keys[0]] : null;
    const isNet = keys.every((k) => k === 'down' || k === 'up');
    const label = single ? single.name : isNet ? 'Network' : keys.map((k) => METRICS[k].name).join(' · ');
    box.style.setProperty('--c', color((single || METRICS[keys[0]]).color));
    const head = h('div', 's-head', h('span', 's-label', label), h('span', 's-name', ''));
    const main = h('div', 's-main');
    const canvas = h('canvas');
    box.append(head, main, canvas);
    if (keys.includes('cpu') || keys.includes('ram')) bindProcessLink(box, keys.includes('cpu') ? 'cpu' : 'mem');
    return {
      update() {
        const s = D.state;
        if (!s) return;
        if (single) {
          const [v, u] = metricText(single, s);
          main.replaceChildren(h('b', null, v), h('small', null, u), h('span', 'chips', single.sub?.(s) ? h('span', 'chip', single.sub(s)) : ''));
          head.lastChild.textContent = single.title?.(s) || '';
        } else {
          main.replaceChildren(...keys.map((k) => {
            const mm = METRICS[k];
            const [v, u] = metricText(mm, s);
            const e = h('span', 'multi', (k === 'down' ? '↓ ' : k === 'up' ? '↑ ' : mm.label + ' '), h('b', null, v + ' ' + u));
            e.style.color = color(mm.color);
            return e;
          }));
          head.lastChild.textContent = isNet && s.ping ? `Ping ${s.ping.ms != null ? Math.round(s.ping.ms) + ' ms' : '—'} · loss ${s.ping.lossPct}%` : '';
        }
        chart(canvas, keys.map((k) => ({ data: hist[METRICS[k].hist || k], color: color(METRICS[k].color), max: METRICS[k].max(s) })), { grid: true });
      },
    };
  },
};

W.meters = {
  needs: ['state'],
  create(box) {
    box.style.setProperty('--c', cssVar('--ram'));
    const up = h('span', 's-name');
    const list = h('div', 'meters');
    box.append(h('div', 's-head', h('span', 's-label', t('w.memory')), up), list);
    bindProcessLink(box, 'mem');
    const meter = (name, txt, pct, c) => {
      const e = h('div', 'meter', h('div', 'm-row', h('span', null, name), h('span', null, txt)), h('div', 'track', h('i')));
      e.querySelector('i').style.width = Math.min(100, pct) + '%';
      if (c) e.style.setProperty('--c', c);
      return e;
    };
    return {
      update() {
        const pc = D.state?.pc;
        if (!pc) return;
        const s = pc.uptimeSec, d = Math.floor(s / 86400), hr = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
        up.textContent = 'Up ' + (d ? `${d} d ${hr} h` : hr ? `${hr} h ${m} min` : `${m} min`);
        const rows = [meter('RAM', `${r1(pc.ramUsedGb)} / ${Math.round(pc.ramTotalGb)} GB`, (pc.ramUsedGb / pc.ramTotalGb) * 100)];
        // one row per GPU (the integrated one counts its shared memory); named once there is more than one
        const gpus = gpusOf(D.state);
        for (const g of gpus) {
          const m = gpuMem(g);
          if (m) rows.push(meter(gpus.length > 1 ? `${m.label === 'VRAM' ? 'VRAM' : 'GPU'} · ${shortGpu(g.name)}` : m.label === 'VRAM' ? 'VRAM' : 'GPU MEMORY',
            `${r1(m.used)} / ${Math.round(m.total)} GB`, (m.used / m.total) * 100, cssVar('--gpu')));
        }
        const disks = h('div', 'disks', ...(pc.disks || []).map((dk) => meter(dk.name === '/' ? 'DISK' : dk.name, Math.round((dk.usedGb / dk.totalGb) * 100) + '%', (dk.usedGb / dk.totalGb) * 100, '#94a3b8')));
        list.replaceChildren(...rows, disks);
      },
    };
  },
};

W.quotas = {
  needs: ['quotas', 'tick'],
  /**
   * Creates the AI Quotas widget instance with optional paged provider rotation.
   */
  create(box, { o }) {
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const grid = h('div', 'q-grid', h('div', 'empty', 'Quota data arrives once the computer is connected.'));
    box.append(grid);
    let last = 0;
    let offset = 0;
    let lastRotate = Date.now();

    const isRotateEnabled = () => {
      if (o.rotate !== undefined && o.rotate !== null) return !!o.rotate;
      if (cfg.codexBar?.rotate !== undefined && cfg.codexBar?.rotate !== null) return !!cfg.codexBar.rotate;
      return true;
    };

    const getPageSize = () => {
      if (o.pageSize !== undefined && o.pageSize !== null && o.pageSize !== '') {
        const n = Number(o.pageSize);
        return Number.isFinite(n) ? Math.max(0, Math.min(6, Math.floor(n))) : 2;
      }
      return cfg.codexBar?.pageSize ?? 2;
    };

    const getRotateInterval = () => {
      if (!isRotateEnabled()) return 0;
      if (o.rotateInterval !== undefined && o.rotateInterval !== null && o.rotateInterval !== '') {
        const n = Number(o.rotateInterval);
        return Number.isFinite(n) ? Math.max(2, Math.min(300, n)) : 8;
      }
      return cfg.codexBar?.rotateInterval ?? 8;
    };

    const getProviders = () => {
      if (Array.isArray(o.providers)) return o.providers;
      if (Array.isArray(cfg.codexBar?.providers)) return cfg.codexBar.providers;
      return ['claude', 'codex', 'antigravity'];
    };

    const isOverviewEnabled = () => o.showOverview === true;

    const getItems = () => {
      const want = getProviders();
      const items = quotaProviders().filter((p) => want.includes(p.id));
      if (isOverviewEnabled() && D.quotas?.overview) {
        return [{ id: '__overview__', isOverview: true, overview: D.quotas.overview }, ...items];
      }
      return items;
    };

    const fmtCurrency = (amount, code = 'USD') => {
      const n = Number(amount || 0);
      const cur = (code || 'USD').toUpperCase();
      try { return new Intl.NumberFormat(LOCALE, { style: 'currency', currency: cur }).format(n); }
      catch (_) { return `${cur === 'USD' ? '$' : cur + ' '}${n.toFixed(2)}`; }
    };

    const renderOverviewCard = (ov, flexVal) => {
      const card = h('div', 'q-card q-overview-card');
      card.style.setProperty('--q', '#f97316');
      card.style.flex = flexVal;
      const days = ov.windowDays || 30;
      const age = ov.updatedAt ? Math.max(0, Date.now() - ov.updatedAt) : null;
      const updStr = age != null ? `checked ${age < 60_000 ? 'just now' : fmtDur(age) + ' ago'}` : '';
      card.appendChild(h('div', 'q-head',
        h('span', 'q-logo', emo('📊')),
        h('span', 'q-name', 'Overview'),
        h('span', 'q-plan', `${days}-day spend`),
        updStr ? h('span', 'q-upd', updStr) : null
      ));
      const summary = h('div', 'q-ov-hero',
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', 'Estimated spend'), h('strong', 'q-ov-val', fmtCurrency(ov.totalCost, ov.currencyCode))),
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', 'Recorded tokens'), h('strong', 'q-ov-val', tokens(ov.totalTokens || 0)))
      );
      card.appendChild(summary);
      const details = h('div', 'q-details q-ov-details');
      for (const s of (ov.subscriptions || []).slice(0, 3)) {
        details.appendChild(h('div', 'q-detail',
          h('span', null, s.name),
          h('strong', null, `${tokens(s.tokens)} · ${fmtCurrency(s.cost, s.currencyCode || ov.currencyCode)}`)
        ));
      }
      for (const m of (ov.topModels || []).slice(0, 2)) {
        details.appendChild(h('div', 'q-detail q-breakdown',
          h('span', null, m.modelName || m.name),
          h('strong', null, fmtCurrency(m.cost, m.currencyCode || ov.currencyCode))
        ));
      }
      if (details.childNodes.length) card.appendChild(details);
      const count = ov.providerCount ?? (ov.subscriptions || []).length;
      card.appendChild(h('div', 'q-foot', h('span', null, `${count} ${count === 1 ? 'provider' : 'providers'}`), h('span')));
      return card;
    };

    const render = () => {
      if (!D.quotas) return;
      const want = getProviders();
      const list = getItems();
      grid.replaceChildren();
      if (!list.length) { grid.appendChild(h('div', 'empty', want.length ? 'Quota data is unavailable for the selected providers' : 'No quota providers enabled')); return; }

      const ps = getPageSize();
      const needsPaging = isRotateEnabled() && ps > 0 && list.length > ps;
      if (offset >= list.length) offset = 0;

      let visible = list;
      if (needsPaging) {
        visible = [];
        for (let i = 0; i < ps; i++) {
          visible.push(list[(offset + i) % list.length]);
        }
      }

      for (const p of visible) {
        if (p.isOverview) {
          grid.appendChild(renderOverviewCard(p.overview, needsPaging && ps === 2 ? '1 1 0' : '1.5'));
          continue;
        }
        const st = Q_STYLE[p.id] || { color: '#94a3b8', logo: '●' };
        const card = h('div', 'q-card' + (p.limits.length >= 4 ? ' q-card-compact' : ''));
        card.style.setProperty('--q', st.color);
        card.style.flex = needsPaging && ps === 2 ? '1 1 0' : String(Math.max(1, p.limits.length) + 0.3);
        card.appendChild(h('div', 'q-head', h('span', 'q-logo', st.logo), h('span', 'q-name', p.name), h('span', 'q-plan', p.plan),
          p.updatedAt ? h('span', 'q-upd', fmtDur(Date.now() - p.updatedAt) + ' ago') : null));
        const compact = p.limits.length >= 4;
        const rings = h('div', 'q-rings' + (compact ? ' q-rings-grid' : ''));
        for (const l of p.limits) {
          const pace = paceInfo(l);
          const dial = h('div', 'q-dial');
          if (compact) {
            dial.style.width = '86px';
            dial.style.height = '86px';
          }
          if (l.percent >= 95 || l.severity === 'critical') dial.style.setProperty('--qc', '#ef4444');
          else if (l.percent >= 80 || l.severity === 'warning') dial.style.setProperty('--qc', '#f59e0b');
          dial.innerHTML = ringSvg(l.percent, pace?.elapsed); // numbers only
          const pctEl = h('div', 'q-pct', String(Math.round(l.percent)), h('small', null, '%'));
          if (compact) pctEl.style.fontSize = 'calc(22px * var(--widget-font-scale, 1))';
          dial.appendChild(pctEl);
          rings.appendChild(h('div', 'q-ring', dial, h('div', 'q-label', l.label), p.id === 'codex' ? h('div', 'q-left', `${Math.max(0, Math.round(100 - l.percent))}% left`) : null, h('div', 'q-reset', fmtReset(l.resetsAt)),
            pace ? h('div', 'q-pace ' + pace.cls, pace.text) : null));
        }
        if (!p.limits.length) rings.appendChild(h('div', 'q-reset', 'No data'));
        card.append(rings);
        if (p.id === 'codex' && p.codex) {
          const c = p.codex;
          const details = h('div', 'q-details');
          if (c.model) details.append(h('div', 'q-detail', h('span', null, 'Latest local session'), h('strong', null, c.model + (c.effort ? ` · ${c.effort}` : ''))));
          if (c.totalTokens != null) details.append(h('div', 'q-detail', h('span', null, 'Session tokens'), h('strong', null, tokens(c.totalTokens))));
          if (c.totalInput != null || c.totalOutput != null) details.append(h('div', 'q-detail q-breakdown', `In ${tokens(c.totalInput)} · Out ${tokens(c.totalOutput)} · Cached ${tokens(c.totalCached)} · Reasoning ${tokens(c.totalReasoning)}`));
          if (c.lastInput != null && c.contextWindow) details.append(h('div', 'q-detail q-breakdown', `Last input ${tokens(c.lastInput)} / ${tokens(c.contextWindow)} context window`));
          if (details.childNodes.length) card.append(details);
        }
        card.append(h('div', 'q-foot', h('span', p.extra?.startsWith('Limit reached') ? 'q-alert' : null, p.extra || ''), p.error ? h('span', 'q-err', p.error) : h('span')));

        grid.appendChild(card);
      }

      if (needsPaging) {
        const dots = h('div', 'q-dots');
        for (let i = 0; i < list.length; i++) {
          const dot = h('span', 'q-dot' + (i === offset ? ' active' : ''));
          dot.onclick = (e) => {
            e.stopPropagation();
            offset = i;
            lastRotate = Date.now();
            render();
            haptic();
          };
          dots.appendChild(dot);
        }
        grid.appendChild(dots);
      }
    };

    grid.onclick = () => {
      const ps = getPageSize();
      const list = getItems();
      if (ps > 0 && list.length > ps) {
        offset = (offset + 1) % list.length;
        lastRotate = Date.now();
        render();
        haptic();
      }
    };

    /**
     * Checks whether an enclosing Smart Stack widget has paused rotation.
     */
    const isStackPaused = () => box.closest('.w-stack')?.dataset.stackPaused === '1';

    return {
      /**
       * Updates quota display and advances paged provider rotation on ticks.
       */
      update(kind) {
        if (kind === 'tick') {
          const sec = getRotateInterval();
          if (isStackPaused()) {
            lastRotate = Date.now();
          } else if (sec > 0 && Date.now() - lastRotate >= sec * 1000) {
            const ps = getPageSize();
            const list = getItems();
            if (ps > 0 && list.length > ps) {
              offset = (offset + 1) % list.length;
              lastRotate = Date.now();
              render();
            }
          }
          if (Date.now() - last > 30_000) { last = Date.now(); render(); }
          return;
        }
        last = Date.now();
        render();
      },
    };
  },
};

W.sessions = {
  needs: ['work', 'tick'],
  create(box) {
    const sum = h('span', 'w-sum');
    const list = h('div', 's-list', h('div', 'empty', t('sess.waitingData')));
    box.append(h('div', 'w-head', h('span', 's-label', t('w.sessions')), sum), list);
    box.style.setProperty('--c', '#d97757');
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const statusText = (s) => s.status === 'busy' ? t('sess.working') : s.status === 'waiting' ? t('sess.waiting') : s.status === 'shell' ? t('sess.shell') : s.outcome === 'completed' ? t('sess.completed') : s.outcome === 'aborted' ? t('sess.aborted') : t('sess.idle');
    const ageText = (since) => Date.now() - since < 60_000 ? 'just now' : shortDur(Date.now() - since) + ' ago';
    // A Claude Code chat's title (the agent sends it only when Settings › Claude Code › "Show session titles on the phone"
    // is on) is the main label and the folder goes under it; without a title the folder is the label, as before.
    const titleOf = (s) => s.title || s.project || s.name;
    const folderOf = (s) => (s.title ? s.project : '');
    // A Claude Code chat's recent steps, last exchange and message list. The agent sends them only when Settings › Claude Code
    // › "Show the conversation on the phone" is on. Returns the function that stops the refresh.
    const conversationSection = (panel, s) => {
      const box = h('div', 'session-conv');
      panel.append(box);
      let data = null, expanded = false, gone = false, asked = 0, applied = 0;
      let labels = []; // the time label of each step on screen, so they can move on without drawing everything again
      const stepAge = (st) => (st.running ? (st.at ? 'running ' + shortDur(Date.now() - st.at) : 'running') : st.at ? ageText(st.at) : '');
      const refreshLabels = () => { for (const [el, st] of labels) el.textContent = stepAge(st); };
      const toolName = (n) => n.startsWith('mcp__') ? n.slice(5).replace(/__/g, ' › ') : n;
      const clip = (text, n) => text.length > n ? text.slice(0, n) + '…' : text;
      const message = (m, full) => h('div', 'conv-msg ' + m.role,
        h('span', 'conv-who', m.role === 'user' ? 'You' : 'Claude'),
        h('div', full ? 'conv-text' : 'conv-text clamp', full ? m.text : clip(m.text, 500)));
      const render = () => {
        const prevList = box.querySelector('.conv-list');
        const top = prevList ? prevList.scrollTop : null;
        labels = [];
        if (!data.enabled) {
          box.replaceChildren(h('p', 'session-note', 'The conversation is off. Turn it on in Settings › Claude Code › Show the conversation on the phone.'));
          return;
        }
        const messages = data.messages || [], steps = data.steps || [];
        const kids = [];
        if (steps.length) {
          kids.push(h('div', 'conv-h', 'Recent steps'));
          for (const st of [...steps].reverse().slice(0, 6)) {
            const age = h('span', 'cs-a', stepAge(st));
            labels.push([age, st]);
            kids.push(h('div', 'conv-step' + (st.running ? ' on' : ''), h('span', 'cs-t', toolName(st.tool)), h('span', 'cs-d', st.detail || ''), age));
          }
        }
        if (!messages.length) kids.push(h('p', 'session-note', 'No messages yet.'));
        else if (expanded) {
          kids.push(h('div', 'conv-h', 'Conversation'), h('div', 'conv-list', messages.map((m) => message(m, true))));
        } else {
          const lastOf = (role) => [...messages].reverse().find((m) => m.role === role);
          kids.push(h('div', 'conv-h', 'Last exchange'));
          for (const m of [lastOf('user'), lastOf('assistant')]) if (m) kids.push(message(m, false));
        }
        if (messages.length) {
          const toggle = h('button', 'conv-toggle', expanded ? 'Hide the conversation' : `Show the conversation (${messages.length})`);
          toggle.type = 'button';
          toggle.addEventListener('click', () => { haptic(); expanded = !expanded; render(); });
          kids.push(toggle);
        }
        box.replaceChildren(...kids);
        const list = box.querySelector('.conv-list');
        if (list) list.scrollTop = top ?? list.scrollHeight; // a new view starts at the newest message
      };
      const load = async () => {
        const mine = ++asked;
        try {
          const d = await api('/work/conversation?id=' + encodeURIComponent(s.id), 'GET', 5000);
          // A slower earlier request can finish after a newer one: its answer is older, and must not replace it (for
          // example a "turned off" answer that has already been shown).
          if (gone || mine < applied) return;
          applied = mine;
          // the messages are drawn again only when they changed, to keep the scroll position and the focus; the ages
          // of the steps move on with the clock either way
          if (JSON.stringify(d) !== JSON.stringify(data)) { data = d; render(); } else refreshLabels();
        } catch (_) {
          if (!gone && !data) box.replaceChildren(h('p', 'session-note', 'Could not read the conversation.'));
        }
      };
      load();
      const timer = setInterval(load, 3000);
      return () => { gone = true; clearInterval(timer); };
    };
    const openDetails = (s) => {
      haptic();
      const backdrop = h('div', 'session-backdrop');
      const panel = h('div', 'session-panel');
      panel.setAttribute('role', 'dialog');
      panel.setAttribute('aria-modal', 'true');
      const title = h('h2', null, titleOf(s));
      const close = h('button', 'session-close', icon('x'));
      close.setAttribute('aria-label', 'Close session details');
      let stopConversation = () => {};
      const onKey = (e) => { if (e.key === 'Escape') dismiss(); };
      const dismiss = () => { stopConversation(); backdrop.remove(); document.removeEventListener('keydown', onKey); };
      close.addEventListener('click', dismiss);
      backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
      panel.append(h('div', 'session-panel-head', h('span', null, s.tool === 'codex' ? '◎ Codex' : s.tool === 'cursor' ? '◆ Cursor' : '✳ Claude Code'), close), title);
      const rows = [
        ['Folder', folderOf(s) || null],
        ['Status', statusText(s)],
        ['Started', s.startedAt ? ageText(s.startedAt) : null],
        ['Last activity', s.since ? ageText(s.since) : null],
        ['Current turn', s.status === 'busy' && s.turnStartedAt ? shortDur(Date.now() - s.turnStartedAt) : null],
        ['Model', s.model], ['Reasoning', s.effort],
        ['Session tokens', s.totalTokens != null ? tokens(s.totalTokens) : null],
        ['Input / output', s.inputTokens != null || s.outputTokens != null ? `${tokens(s.inputTokens)} / ${tokens(s.outputTokens)}` : null],
        ['Cached input', s.cachedInputTokens != null ? tokens(s.cachedInputTokens) : null],
        ['Reasoning output', s.reasoningTokens != null ? tokens(s.reasoningTokens) : null],
        ['Last input / context window', s.lastInputTokens != null && s.contextWindow ? `${tokens(s.lastInputTokens)} / ${tokens(s.contextWindow)}` : null],
        ['Recent tool', s.lastTool],
      ];
      // a small label over each value, four to a row: the facts take one line instead of four
      panel.append(h('div', 'session-grid', rows.filter(([, value]) => value != null)
        .map(([label, value]) => h('div', 'session-cell', h('span', null, label), h('strong', null, value)))));
      if (s.tool === 'codex') panel.append(h('p', 'session-note', 'Token totals are for this local session, not your account allowance.'));
      if (s.tool === 'claude') stopConversation = conversationSection(panel, s);
      backdrop.append(panel);
      document.body.append(backdrop);
      close.focus();
      document.addEventListener('keydown', onKey);
    };
    return {
      update() {
        const sessions = D.work?.sessions;
        if (!sessions) return;
        const counts = { waiting: 0, busy: 0, shell: 0, idle: 0 };
        sessions.forEach((s) => (counts[s.status] = (counts[s.status] || 0) + 1));
        sum.textContent = [
          counts.busy + counts.shell ? `${counts.busy + counts.shell} working` : '',
          counts.waiting ? `${counts.waiting} waiting` : '',
          counts.idle ? `${counts.idle} idle` : '',
        ].filter(Boolean).join(' · ') || t('w.noOpenSessions');
        const sorted = [...sessions].sort((a, b) => (S_ORDER[a.status] ?? 9) - (S_ORDER[b.status] ?? 9) || b.since - a.since);
        const rows = sorted.length ? sorted.map((s) => {
          const row = h('button', 'sess ' + s.status,
            h('span', 'st'), h('span', 'tl ' + s.tool, s.tool === 'claude' ? '✳' : s.tool === 'cursor' ? '◆' : '◎'),
            h('span', 'sess-main', h('span', 'nm', titleOf(s)), h('span', 'sess-meta', [folderOf(s), [s.model, s.effort].filter(Boolean).join(' · ') || statusText(s)].filter(Boolean).join(' · '))),
            h('span', 'ds', shortDur(Date.now() - s.since)));
          row.type = 'button';
          row.addEventListener('click', () => openDetails(s));
          return row;
        }) : [h('div', 'empty', t('w.noAiSessions'))];
        list.replaceChildren(...rows);
      },
    };
  },
};

W.github = {
  needs: ['work'],
  create(box) {
    box.style.setProperty('--c', '#c9d1d9');
    const user = h('span', 'w-sum'), bell = h('span', 'gh-bell');
    const rc = h('b'), mc = h('b'), rev = h('div', 'gh-list'), mine = h('div', 'gh-list'), err = h('div', 'gh-err', 'Waiting for GitHub data…');
    box.append(h('div', 'w-head', h('span', 's-label', 'GitHub'), user, bell),
      h('div', 'gh-sec', h('span', null, 'Review requests'), rc), rev,
      h('div', 'gh-sec', h('span', null, 'My pull requests'), mc), mine, err);
    return {
      update() {
        const g = D.work?.github;
        if (!g) return;
        const rows = Math.max(1, Math.floor((box.clientHeight - 90) / 25));
        user.textContent = g.user ? '@' + g.user : '';
        bell.replaceChildren(rich(`🔔 ${g.notifications >= 50 ? '50+' : g.notifications}`));
        rc.textContent = g.reviewRequests.length;
        mc.textContent = g.myPrs.length;
        const nRev = Math.min(g.reviewRequests.length || 1, Math.max(1, Math.floor(rows / 2)));
        rev.replaceChildren(...(g.reviewRequests.length ? g.reviewRequests.slice(0, nRev).map((p) => prRow(p, false)) : [h('div', 'gh-pr', t('w.noReviews'))]));
        mine.replaceChildren(...(g.myPrs.length ? g.myPrs.slice(0, Math.max(1, rows - nRev)).map((p) => prRow(p, true)) : [h('div', 'gh-pr', t('w.noPrs'))]));
        err.textContent = g.error ? 'GitHub: ' + g.error : '';
      },
    };
  },
};

W.processes = {
  needs: ['procs', 'state'],
  create(box, { o }) {
    const canKill = o.allowKill !== false;
    let sort = currentProcessesSort();
    const sum = h('span', 'p-sum');
    const seg = h('div', 'seg', h('button', sort === 'cpu' ? 'on' : null, 'CPU'), h('button', sort === 'mem' ? 'on' : null, 'RAM'));
    const grid = h('div', 'p-grid', h('div', 'empty', t('w.procsWait')));
    box.style.setProperty('--c', '#f472b6');
    box.append(h('div', 'p-head', h('span', 's-label', t('w.processes')), sum, seg), grid);
    const els = new Map();
    const rowEl = (g) => {
      let e = els.get(g.name);
      if (e) return e;
      e = h('div', 'proc');
      e.innerHTML = '<div class="p-ico"></div><div class="p-info"><div class="p-title"><span class="p-t"></span><span class="p-count"></span><span class="p-lock"></span></div>' +
        '<div class="p-bar"><i></i></div></div><div class="p-nums"><b></b><small></small></div>';
      fillIcon(e.querySelector('.p-ico'), g);
      e.addEventListener('click', () => { haptic(); openSheet(e._g, canKill); });
      els.set(g.name, e);
      return e;
    };
    const render = () => {
      const data = D.procs;
      if (!data) return;
      if (!data.groups.length) { sum.textContent = ''; grid.replaceChildren(h('div', 'empty', t('w.measuring'))); return; }
      const cols = box.clientWidth > 460 ? 2 : 1;
      const rows = Math.max(1, Math.floor((box.clientHeight - 46) / 40));
      grid.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
      grid.style.gridTemplateRows = `repeat(${rows}, 1fr)`;
      const list = [...data.groups].sort((a, b) => (sort === 'cpu' ? b.cpu - a.cpu || b.memMb - a.memMb : b.memMb - a.memMb)).slice(0, cols * rows);
      const max = Math.max(0.1, ...list.map((g) => (sort === 'cpu' ? g.cpu : g.memMb)));
      const pc = D.state?.pc;
      const cpuPct = pc && pc.cpu != null ? Math.round(pc.cpu) : null;
      const ramPct = pc && pc.ramTotalGb ? Math.round((pc.ramUsedGb / pc.ramTotalGb) * 100) : null;
      const ramStr = ramPct != null && pc.ramUsedGb != null && pc.ramTotalGb != null
        ? `Total RAM ${ramPct}% (${Math.round(pc.ramUsedGb)}/${Math.round(pc.ramTotalGb)} GB) · `
        : (ramPct != null ? `Total RAM ${ramPct}% · ` : '');
      const loadPart = sort === 'cpu'
        ? (cpuPct != null ? `Total CPU ${cpuPct}% · ` : '')
        : ramStr;
      sum.textContent = box.clientWidth > 380 ? loadPart + t('w.procsSummary', { n: data.total, apps: data.groupCount ?? data.groups.length }) : loadPart + t('w.procsSummaryShort', { n: data.total });
      if (grid.querySelector('.empty')) grid.replaceChildren();
      const keep = new Set(list.map((g) => g.name));
      for (const [n, e] of els) if (!keep.has(n)) { e.remove(); els.delete(n); }
      for (const g of list) {
        const e = rowEl(g);
        e._g = g;
        e.querySelector('.p-t').textContent = g.title;
        e.querySelector('.p-count').textContent = g.count > 1 ? '×' + g.count : '';
        e.querySelector('.p-lock').replaceChildren(...(g.locked ? [emo('🔒')] : []));
        e.querySelector('.p-bar i').style.width = ((sort === 'cpu' ? g.cpu : g.memMb) / max) * 100 + '%';
        e.querySelector('.p-nums b').textContent = sort === 'cpu' ? g.cpu.toFixed(1) + '%' : fmtMem(g.memMb);
        e.querySelector('.p-nums small').textContent = sort === 'cpu' ? fmtMem(g.memMb) : g.cpu.toFixed(1) + '%';
        grid.appendChild(e); // keeps the order
      }
    };
    const setSort = (s) => {
      sort = s === 'mem' ? 'mem' : 'cpu';
      [...seg.children].forEach((x, i) => x.classList.toggle('on', (i === 0 ? 'cpu' : 'mem') === sort));
      render();
    };
    registerProcessesSorter(setSort);
    [...seg.children].forEach((b, i) => b.addEventListener('click', () => {
      haptic();
      setSort(i === 0 ? 'cpu' : 'mem');
    }));
    return { update: render };
  },
};

W['tcp-status'] = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('tcp-status-widget');
    box.append(h('div', 'w-title', o.title || 'Connections'));
    const list = h('div', 'tcp-status-list');
    box.append(list);

    let nextCheck = 0;
    let busy = false;
    let lastKey = '';

    const load = async () => {
      if (busy || !online) return;
      busy = true;
      try {
        const pageId = encodeURIComponent(page.id);
        const widgetId = encodeURIComponent(w.id);
        const r = await api(`/tcp/status?page=${pageId}&widget=${widgetId}`, 'GET', 7000);
        const connections = r.connections || [];
        const key = JSON.stringify(connections);
        if (key !== lastKey) {
          lastKey = key;
          if (!connections.length) {
            list.replaceChildren(h('div', 'empty', 'Add connections in the widget settings'));
          } else {
            list.replaceChildren(...connections.map((c) => {
              const row = h('div', 'tcp-status-row');
              const dot = h('span', 'tcp-status-dot');
              dot.classList.add(c.online ? 'online' : 'offline');
              const icon = h('span', 'tcp-status-icon');
              if (/^https?:\/\//i.test(c.icon || '')) {
                const img = h('img', 'tcp-status-icon-image');
                img.src = c.icon;
                img.alt = c.name || '';
                img.referrerPolicy = 'no-referrer';
                img.onerror = () => { img.remove(); icon.textContent = '🌐'; };
                icon.append(img);
              } else {
                icon.textContent = c.icon ? String(c.icon) : '🌐';
              }
              const text = h('span', 'tcp-status-name', c.name);
              const address = h('span', 'tcp-status-address', `${c.host}:${c.port}`);
              const status = h('span', 'tcp-status-state' + (c.online ? ' online' : ' offline'), c.online ? 'Online' : 'Offline');
              if (c.online && c.latencyMs != null) status.append(h('small', null, ` · ${Math.round(c.latencyMs)} ms`));
              row.append(icon, h('span', 'tcp-status-text', text, address), dot, status);
              return row;
            }));
          }
        }
      } catch (_) {
        if (!list.childElementCount) list.replaceChildren(h('div', 'empty', 'Unable to check connections'));
      } finally {
        busy = false;
      }
    };

    return {
      needs: ['tick'],
      update() {
        const intervalSeconds = Math.max(1, Number(o.interval) || 10);
        if (Date.now() >= nextCheck) {
          nextCheck = Date.now() + intervalSeconds * 1000;
          load();
        }
      }
    };
  }
};

W.stat = {
  needs: ['state'],
  create(box, { o }) {
    const m = METRICS[o.metric] || METRICS.cpu;
    const c = color(m.color);
    box.style.setProperty('--c', c);
    const canvas = h('canvas');
    const lbl = h('div', 't-label', m.label);
    const val = h('b', null, '—'), unit = h('small'), sub = h('div', 't-sub', ' ');
    box.append(canvas, lbl, h('div', 't-val', val, unit), sub);
    if (o.metric === 'cpu' || o.metric === 'ram') bindProcessLink(box, o.metric === 'cpu' ? 'cpu' : 'mem');
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const [v, u] = metricText(m, s);
        val.textContent = v;
        unit.textContent = u;
        const pctVal = m.pct?.(s);
        lbl.textContent = pctVal != null && m.label === 'RAM' ? `RAM (${Math.round(pctVal)}%)` : m.label;
        sub.textContent = m.sub?.(s) || ' ';
        chart(canvas, [{ data: hist[m.hist || o.metric] || hist.cpu, color: c, max: m.max(s) }]);
      },
    };
  },
};

W.gauge = {
  needs: ['state'],
  create(box, { o }) {
    const m = METRICS[o.metric] || METRICS.cpu;
    box.style.setProperty('--q', color(m.color));
    const dial = h('div', 'g-dial'), pct = h('div', 'g-val'), sub = h('div', 'g-sub');
    box.append(dial, h('div', 'g-label', m.label), sub);
    dial.append(pct);
    if (o.metric === 'cpu' || o.metric === 'ram') bindProcessLink(box, o.metric === 'cpu' ? 'cpu' : 'mem');
    let svg = null;
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const raw = m.pct ? m.pct(s) : m.val(s);
        const p = raw == null ? 0 : (raw / (m.pct ? 100 : m.max(s))) * 100;
        const warn = m.hot ? (raw > m.hot[1] ? '#ef4444' : raw > m.hot[0] ? '#f59e0b' : '') : (p >= 95 ? '#ef4444' : p >= 85 ? '#f59e0b' : '');
        dial.style.setProperty('--qc', warn || 'var(--q)');
        if (svg) svg.remove();
        dial.insertAdjacentHTML('afterbegin', ringSvg(p));
        svg = dial.firstElementChild;
        const [v, u] = metricText(m, s);
        pct.replaceChildren(v, h('small', null, u));
        sub.textContent = m.sub?.(s) || '';
      },
    };
  },
};

W.chart = {
  needs: ['state'],
  create(box, { o }) {
    // a list of only unknown names falls back like no list at all; own keys only, so "constructor" is unknown too
    const keys = (Array.isArray(o.metrics) && o.metrics.length ? o.metrics : ['cpu']).filter((k) => Object.prototype.hasOwnProperty.call(METRICS, k));
    if (!keys.length) keys.push('cpu');
    const single = keys.length === 1 ? METRICS[keys[0]] : null;
    const isNet = keys.every((k) => k === 'down' || k === 'up');
    const label = single ? single.name : isNet ? 'Network' : keys.map((k) => METRICS[k].name).join(' · ');
    box.style.setProperty('--c', color((single || METRICS[keys[0]]).color));
    const head = h('div', 's-head', h('span', 's-label', label), h('span', 's-name', ''));
    const main = h('div', 's-main');
    const canvas = h('canvas');
    box.append(head, main, canvas);
    if (keys.includes('cpu') || keys.includes('ram')) bindProcessLink(box, keys.includes('cpu') ? 'cpu' : 'mem');
    return {
      update() {
        const s = D.state;
        if (!s) return;
        if (single) {
          const [v, u] = metricText(single, s);
          main.replaceChildren(h('b', null, v), h('small', null, u), h('span', 'chips', single.sub?.(s) ? h('span', 'chip', single.sub(s)) : ''));
          head.lastChild.textContent = single.title?.(s) || '';
        } else {
          main.replaceChildren(...keys.map((k) => {
            const mm = METRICS[k];
            const [v, u] = metricText(mm, s);
            const e = h('span', 'multi', (k === 'down' ? '↓ ' : k === 'up' ? '↑ ' : mm.label + ' '), h('b', null, v + ' ' + u));
            e.style.color = color(mm.color);
            return e;
          }));
          head.lastChild.textContent = isNet && s.ping ? `Ping ${s.ping.ms != null ? Math.round(s.ping.ms) + ' ms' : '—'} · loss ${s.ping.lossPct}%` : '';
        }
        chart(canvas, keys.map((k) => ({ data: hist[METRICS[k].hist || k], color: color(METRICS[k].color), max: METRICS[k].max(s) })), { grid: true });
      },
    };
  },
};

W.meters = {
  needs: ['state'],
  create(box) {
    box.style.setProperty('--c', cssVar('--ram'));
    const up = h('span', 's-name');
    const list = h('div', 'meters');
    box.append(h('div', 's-head', h('span', 's-label', t('w.memory')), up), list);
    bindProcessLink(box, 'mem');
    const meter = (name, txt, pct, c) => {
      const e = h('div', 'meter', h('div', 'm-row', h('span', null, name), h('span', null, txt)), h('div', 'track', h('i')));
      e.querySelector('i').style.width = Math.min(100, pct) + '%';
      if (c) e.style.setProperty('--c', c);
      return e;
    };
    return {
      update() {
        const pc = D.state?.pc;
        if (!pc) return;
        const s = pc.uptimeSec, d = Math.floor(s / 86400), hr = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
        up.textContent = 'Up ' + (d ? `${d} d ${hr} h` : hr ? `${hr} h ${m} min` : `${m} min`);
        const rows = [meter('RAM', `${r1(pc.ramUsedGb)} / ${Math.round(pc.ramTotalGb)} GB`, (pc.ramUsedGb / pc.ramTotalGb) * 100)];
        // one row per GPU (the integrated one counts its shared memory); named once there is more than one
        const gpus = gpusOf(D.state);
        for (const g of gpus) {
          const m = gpuMem(g);
          if (m) rows.push(meter(gpus.length > 1 ? `${m.label === 'VRAM' ? 'VRAM' : 'GPU'} · ${shortGpu(g.name)}` : m.label === 'VRAM' ? 'VRAM' : 'GPU MEMORY',
            `${r1(m.used)} / ${Math.round(m.total)} GB`, (m.used / m.total) * 100, cssVar('--gpu')));
        }
        const disks = h('div', 'disks', ...(pc.disks || []).map((dk) => meter(dk.name === '/' ? 'DISK' : dk.name, Math.round((dk.usedGb / dk.totalGb) * 100) + '%', (dk.usedGb / dk.totalGb) * 100, '#94a3b8')));
        list.replaceChildren(...rows, disks);
      },
    };
  },
};

W.quotas = {
  needs: ['quotas', 'tick'],
  /**
   * Creates the AI Quotas widget instance with optional paged provider rotation.
   */
  create(box, { o }) {
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const grid = h('div', 'q-grid', h('div', 'empty', 'Quota data arrives once the computer is connected.'));
    box.append(grid);
    let last = 0;
    let offset = 0;
    let lastRotate = Date.now();

    const isRotateEnabled = () => {
      if (o.rotate !== undefined && o.rotate !== null) return !!o.rotate;
      if (cfg.codexBar?.rotate !== undefined && cfg.codexBar?.rotate !== null) return !!cfg.codexBar.rotate;
      return true;
    };

    const getPageSize = () => {
      if (o.pageSize !== undefined && o.pageSize !== null && o.pageSize !== '') {
        const n = Number(o.pageSize);
        return Number.isFinite(n) ? Math.max(0, Math.min(6, Math.floor(n))) : 2;
      }
      return cfg.codexBar?.pageSize ?? 2;
    };

    const getRotateInterval = () => {
      if (!isRotateEnabled()) return 0;
      if (o.rotateInterval !== undefined && o.rotateInterval !== null && o.rotateInterval !== '') {
        const n = Number(o.rotateInterval);
        return Number.isFinite(n) ? Math.max(2, Math.min(300, n)) : 8;
      }
      return cfg.codexBar?.rotateInterval ?? 8;
    };

    const getProviders = () => {
      if (Array.isArray(o.providers)) return o.providers;
      if (Array.isArray(cfg.codexBar?.providers)) return cfg.codexBar.providers;
      return ['claude', 'codex', 'antigravity'];
    };

    const isOverviewEnabled = () => o.showOverview === true;

    const getItems = () => {
      const want = getProviders();
      const items = quotaProviders().filter((p) => want.includes(p.id));
      if (isOverviewEnabled() && D.quotas?.overview) {
        return [{ id: '__overview__', isOverview: true, overview: D.quotas.overview }, ...items];
      }
      return items;
    };

    const fmtCurrency = (amount, code = 'USD') => {
      const n = Number(amount || 0);
      const cur = (code || 'USD').toUpperCase();
      try { return new Intl.NumberFormat(LOCALE, { style: 'currency', currency: cur }).format(n); }
      catch (_) { return `${cur === 'USD' ? '$' : cur + ' '}${n.toFixed(2)}`; }
    };

    const renderOverviewCard = (ov, flexVal) => {
      const card = h('div', 'q-card q-overview-card');
      card.style.setProperty('--q', '#f97316');
      card.style.flex = flexVal;
      const days = ov.windowDays || 30;
      const age = ov.updatedAt ? Math.max(0, Date.now() - ov.updatedAt) : null;
      const updStr = age != null ? `checked ${age < 60_000 ? 'just now' : fmtDur(age) + ' ago'}` : '';
      card.appendChild(h('div', 'q-head',
        h('span', 'q-logo', emo('📊')),
        h('span', 'q-name', 'Overview'),
        h('span', 'q-plan', `${days}-day spend`),
        updStr ? h('span', 'q-upd', updStr) : null
      ));
      const summary = h('div', 'q-ov-hero',
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', 'Estimated spend'), h('strong', 'q-ov-val', fmtCurrency(ov.totalCost, ov.currencyCode))),
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', 'Recorded tokens'), h('strong', 'q-ov-val', tokens(ov.totalTokens || 0)))
      );
      card.appendChild(summary);
      const details = h('div', 'q-details q-ov-details');
      for (const s of (ov.subscriptions || []).slice(0, 3)) {
        details.appendChild(h('div', 'q-detail',
          h('span', null, s.name),
          h('strong', null, `${tokens(s.tokens)} · ${fmtCurrency(s.cost, s.currencyCode || ov.currencyCode)}`)
        ));
      }
      for (const m of (ov.topModels || []).slice(0, 2)) {
        details.appendChild(h('div', 'q-detail q-breakdown',
          h('span', null, m.modelName || m.name),
          h('strong', null, fmtCurrency(m.cost, m.currencyCode || ov.currencyCode))
        ));
      }
      if (details.childNodes.length) card.appendChild(details);
      const count = ov.providerCount ?? (ov.subscriptions || []).length;
      card.appendChild(h('div', 'q-foot', h('span', null, `${count} ${count === 1 ? 'provider' : 'providers'}`), h('span')));
      return card;
    };

    const render = () => {
      if (!D.quotas) return;
      const want = getProviders();
      const list = getItems();
      grid.replaceChildren();
      if (!list.length) { grid.appendChild(h('div', 'empty', want.length ? 'Quota data is unavailable for the selected providers' : 'No quota providers enabled')); return; }

      const ps = getPageSize();
      const needsPaging = isRotateEnabled() && ps > 0 && list.length > ps;
      if (offset >= list.length) offset = 0;

      let visible = list;
      if (needsPaging) {
        visible = [];
        for (let i = 0; i < ps; i++) {
          visible.push(list[(offset + i) % list.length]);
        }
      }

      for (const p of visible) {
        if (p.isOverview) {
          grid.appendChild(renderOverviewCard(p.overview, needsPaging && ps === 2 ? '1 1 0' : '1.5'));
          continue;
        }
        const st = Q_STYLE[p.id] || { color: '#94a3b8', logo: '●' };
        const card = h('div', 'q-card' + (p.limits.length >= 4 ? ' q-card-compact' : ''));
        card.style.setProperty('--q', st.color);
        card.style.flex = needsPaging && ps === 2 ? '1 1 0' : String(Math.max(1, p.limits.length) + 0.3);
        card.appendChild(h('div', 'q-head', h('span', 'q-logo', st.logo), h('span', 'q-name', p.name), h('span', 'q-plan', p.plan),
          p.updatedAt ? h('span', 'q-upd', fmtDur(Date.now() - p.updatedAt) + ' ago') : null));
        const compact = p.limits.length >= 4;
        const rings = h('div', 'q-rings' + (compact ? ' q-rings-grid' : ''));
        for (const l of p.limits) {
          const pace = paceInfo(l);
          const dial = h('div', 'q-dial');
          if (compact) {
            dial.style.width = '86px';
            dial.style.height = '86px';
          }
          if (l.percent >= 95 || l.severity === 'critical') dial.style.setProperty('--qc', '#ef4444');
          else if (l.percent >= 80 || l.severity === 'warning') dial.style.setProperty('--qc', '#f59e0b');
          dial.innerHTML = ringSvg(l.percent, pace?.elapsed); // numbers only
          const pctEl = h('div', 'q-pct', String(Math.round(l.percent)), h('small', null, '%'));
          if (compact) pctEl.style.fontSize = 'calc(22px * var(--widget-font-scale, 1))';
          dial.appendChild(pctEl);
          rings.appendChild(h('div', 'q-ring', dial, h('div', 'q-label', l.label), p.id === 'codex' ? h('div', 'q-left', `${Math.max(0, Math.round(100 - l.percent))}% left`) : null, h('div', 'q-reset', fmtReset(l.resetsAt)),
            pace ? h('div', 'q-pace ' + pace.cls, pace.text) : null));
        }
        if (!p.limits.length) rings.appendChild(h('div', 'q-reset', 'No data'));
        card.append(rings);
        if (p.id === 'codex' && p.codex) {
          const c = p.codex;
          const details = h('div', 'q-details');
          if (c.model) details.append(h('div', 'q-detail', h('span', null, 'Latest local session'), h('strong', null, c.model + (c.effort ? ` · ${c.effort}` : ''))));
          if (c.totalTokens != null) details.append(h('div', 'q-detail', h('span', null, 'Session tokens'), h('strong', null, tokens(c.totalTokens))));
          if (c.totalInput != null || c.totalOutput != null) details.append(h('div', 'q-detail q-breakdown', `In ${tokens(c.totalInput)} · Out ${tokens(c.totalOutput)} · Cached ${tokens(c.totalCached)} · Reasoning ${tokens(c.totalReasoning)}`));
          if (c.lastInput != null && c.contextWindow) details.append(h('div', 'q-detail q-breakdown', `Last input ${tokens(c.lastInput)} / ${tokens(c.contextWindow)} context window`));
          if (details.childNodes.length) card.append(details);
        }
        card.append(h('div', 'q-foot', h('span', p.extra?.startsWith('Limit reached') ? 'q-alert' : null, p.extra || ''), p.error ? h('span', 'q-err', p.error) : h('span')));

        grid.appendChild(card);
      }

      if (needsPaging) {
        const dots = h('div', 'q-dots');
        for (let i = 0; i < list.length; i++) {
          const dot = h('span', 'q-dot' + (i === offset ? ' active' : ''));
          dot.onclick = (e) => {
            e.stopPropagation();
            offset = i;
            lastRotate = Date.now();
            render();
            haptic();
          };
          dots.appendChild(dot);
        }
        grid.appendChild(dots);
      }
    };

    grid.onclick = () => {
      const ps = getPageSize();
      const list = getItems();
      if (ps > 0 && list.length > ps) {
        offset = (offset + 1) % list.length;
        lastRotate = Date.now();
        render();
        haptic();
      }
    };

    /**
     * Checks whether an enclosing Smart Stack widget has paused rotation.
     */
    const isStackPaused = () => box.closest('.w-stack')?.dataset.stackPaused === '1';

    return {
      /**
       * Updates quota display and advances paged provider rotation on ticks.
       */
      update(kind) {
        if (kind === 'tick') {
          const sec = getRotateInterval();
          if (isStackPaused()) {
            lastRotate = Date.now();
          } else if (sec > 0 && Date.now() - lastRotate >= sec * 1000) {
            const ps = getPageSize();
            const list = getItems();
            if (ps > 0 && list.length > ps) {
              offset = (offset + 1) % list.length;
              lastRotate = Date.now();
              render();
            }
          }
          if (Date.now() - last > 30_000) { last = Date.now(); render(); }
          return;
        }
        last = Date.now();
        render();
      },
    };
  },
};

W.sessions = {
  needs: ['work', 'tick'],
  create(box) {
    const sum = h('span', 'w-sum');
    const list = h('div', 's-list', h('div', 'empty', t('sess.waitingData')));
    box.append(h('div', 'w-head', h('span', 's-label', t('w.sessions')), sum), list);
    box.style.setProperty('--c', '#d97757');
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const statusText = (s) => s.status === 'busy' ? t('sess.working') : s.status === 'waiting' ? t('sess.waiting') : s.status === 'shell' ? t('sess.shell') : s.outcome === 'completed' ? t('sess.completed') : s.outcome === 'aborted' ? t('sess.aborted') : t('sess.idle');
    const ageText = (since) => Date.now() - since < 60_000 ? 'just now' : shortDur(Date.now() - since) + ' ago';
    // A Claude Code chat's title (the agent sends it only when Settings › Claude Code › "Show session titles on the phone"
    // is on) is the main label and the folder goes under it; without a title the folder is the label, as before.
    const titleOf = (s) => s.title || s.project || s.name;
    const folderOf = (s) => (s.title ? s.project : '');
    // A Claude Code chat's recent steps, last exchange and message list. The agent sends them only when Settings › Claude Code
    // › "Show the conversation on the phone" is on. Returns the function that stops the refresh.
    const conversationSection = (panel, s) => {
      const box = h('div', 'session-conv');
      panel.append(box);
      let data = null, expanded = false, gone = false, asked = 0, applied = 0;
      let labels = []; // the time label of each step on screen, so they can move on without drawing everything again
      const stepAge = (st) => (st.running ? (st.at ? 'running ' + shortDur(Date.now() - st.at) : 'running') : st.at ? ageText(st.at) : '');
      const refreshLabels = () => { for (const [el, st] of labels) el.textContent = stepAge(st); };
      const toolName = (n) => n.startsWith('mcp__') ? n.slice(5).replace(/__/g, ' › ') : n;
      const clip = (text, n) => text.length > n ? text.slice(0, n) + '…' : text;
      const message = (m, full) => h('div', 'conv-msg ' + m.role,
        h('span', 'conv-who', m.role === 'user' ? 'You' : 'Claude'),
        h('div', full ? 'conv-text' : 'conv-text clamp', full ? m.text : clip(m.text, 500)));
      const render = () => {
        const prevList = box.querySelector('.conv-list');
        const top = prevList ? prevList.scrollTop : null;
        labels = [];
        if (!data.enabled) {
          box.replaceChildren(h('p', 'session-note', 'The conversation is off. Turn it on in Settings › Claude Code › Show the conversation on the phone.'));
          return;
        }
        const messages = data.messages || [], steps = data.steps || [];
        const kids = [];
        if (steps.length) {
          kids.push(h('div', 'conv-h', 'Recent steps'));
          for (const st of [...steps].reverse().slice(0, 6)) {
            const age = h('span', 'cs-a', stepAge(st));
            labels.push([age, st]);
            kids.push(h('div', 'conv-step' + (st.running ? ' on' : ''), h('span', 'cs-t', toolName(st.tool)), h('span', 'cs-d', st.detail || ''), age));
          }
        }
        if (!messages.length) kids.push(h('p', 'session-note', 'No messages yet.'));
        else if (expanded) {
          kids.push(h('div', 'conv-h', 'Conversation'), h('div', 'conv-list', messages.map((m) => message(m, true))));
        } else {
          const lastOf = (role) => [...messages].reverse().find((m) => m.role === role);
          kids.push(h('div', 'conv-h', 'Last exchange'));
          for (const m of [lastOf('user'), lastOf('assistant')]) if (m) kids.push(message(m, false));
        }
        if (messages.length) {
          const toggle = h('button', 'conv-toggle', expanded ? 'Hide the conversation' : `Show the conversation (${messages.length})`);
          toggle.type = 'button';
          toggle.addEventListener('click', () => { haptic(); expanded = !expanded; render(); });
          kids.push(toggle);
        }
        box.replaceChildren(...kids);
        const list = box.querySelector('.conv-list');
        if (list) list.scrollTop = top ?? list.scrollHeight; // a new view starts at the newest message
      };
      const load = async () => {
        const mine = ++asked;
        try {
          const d = await api('/work/conversation?id=' + encodeURIComponent(s.id), 'GET', 5000);
          // A slower earlier request can finish after a newer one: its answer is older, and must not replace it (for
          // example a "turned off" answer that has already been shown).
          if (gone || mine < applied) return;
          applied = mine;
          // the messages are drawn again only when they changed, to keep the scroll position and the focus; the ages
          // of the steps move on with the clock either way
          if (JSON.stringify(d) !== JSON.stringify(data)) { data = d; render(); } else refreshLabels();
        } catch (_) {
          if (!gone && !data) box.replaceChildren(h('p', 'session-note', 'Could not read the conversation.'));
        }
      };
      load();
      const timer = setInterval(load, 3000);
      return () => { gone = true; clearInterval(timer); };
    };
    const openDetails = (s) => {
      haptic();
      const backdrop = h('div', 'session-backdrop');
      const panel = h('div', 'session-panel');
      panel.setAttribute('role', 'dialog');
      panel.setAttribute('aria-modal', 'true');
      const title = h('h2', null, titleOf(s));
      const close = h('button', 'session-close', icon('x'));
      close.setAttribute('aria-label', 'Close session details');
      let stopConversation = () => {};
      const onKey = (e) => { if (e.key === 'Escape') dismiss(); };
      const dismiss = () => { stopConversation(); backdrop.remove(); document.removeEventListener('keydown', onKey); };
      close.addEventListener('click', dismiss);
      backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
      panel.append(h('div', 'session-panel-head', h('span', null, s.tool === 'codex' ? '◎ Codex' : s.tool === 'cursor' ? '◆ Cursor' : '✳ Claude Code'), close), title);
      const rows = [
        ['Folder', folderOf(s) || null],
        ['Status', statusText(s)],
        ['Started', s.startedAt ? ageText(s.startedAt) : null],
        ['Last activity', s.since ? ageText(s.since) : null],
        ['Current turn', s.status === 'busy' && s.turnStartedAt ? shortDur(Date.now() - s.turnStartedAt) : null],
        ['Model', s.model], ['Reasoning', s.effort],
        ['Session tokens', s.totalTokens != null ? tokens(s.totalTokens) : null],
        ['Input / output', s.inputTokens != null || s.outputTokens != null ? `${tokens(s.inputTokens)} / ${tokens(s.outputTokens)}` : null],
        ['Cached input', s.cachedInputTokens != null ? tokens(s.cachedInputTokens) : null],
        ['Reasoning output', s.reasoningTokens != null ? tokens(s.reasoningTokens) : null],
        ['Last input / context window', s.lastInputTokens != null && s.contextWindow ? `${tokens(s.lastInputTokens)} / ${tokens(s.contextWindow)}` : null],
        ['Recent tool', s.lastTool],
      ];
      // a small label over each value, four to a row: the facts take one line instead of four
      panel.append(h('div', 'session-grid', rows.filter(([, value]) => value != null)
        .map(([label, value]) => h('div', 'session-cell', h('span', null, label), h('strong', null, value)))));
      if (s.tool === 'codex') panel.append(h('p', 'session-note', 'Token totals are for this local session, not your account allowance.'));
      if (s.tool === 'claude') stopConversation = conversationSection(panel, s);
      backdrop.append(panel);
      document.body.append(backdrop);
      close.focus();
      document.addEventListener('keydown', onKey);
    };
    return {
      update() {
        const sessions = D.work?.sessions;
        if (!sessions) return;
        const counts = { waiting: 0, busy: 0, shell: 0, idle: 0 };
        sessions.forEach((s) => (counts[s.status] = (counts[s.status] || 0) + 1));
        sum.textContent = [
          counts.busy + counts.shell ? `${counts.busy + counts.shell} working` : '',
          counts.waiting ? `${counts.waiting} waiting` : '',
          counts.idle ? `${counts.idle} idle` : '',
        ].filter(Boolean).join(' · ') || t('w.noOpenSessions');
        const sorted = [...sessions].sort((a, b) => (S_ORDER[a.status] ?? 9) - (S_ORDER[b.status] ?? 9) || b.since - a.since);
        const rows = sorted.length ? sorted.map((s) => {
          const row = h('button', 'sess ' + s.status,
            h('span', 'st'), h('span', 'tl ' + s.tool, s.tool === 'claude' ? '✳' : s.tool === 'cursor' ? '◆' : '◎'),
            h('span', 'sess-main', h('span', 'nm', titleOf(s)), h('span', 'sess-meta', [folderOf(s), [s.model, s.effort].filter(Boolean).join(' · ') || statusText(s)].filter(Boolean).join(' · '))),
            h('span', 'ds', shortDur(Date.now() - s.since)));
          row.type = 'button';
          row.addEventListener('click', () => openDetails(s));
          return row;
        }) : [h('div', 'empty', t('w.noAiSessions'))];
        list.replaceChildren(...rows);
      },
    };
  },
};
