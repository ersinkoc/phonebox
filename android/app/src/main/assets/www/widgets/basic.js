import { $, API, D, LEVEL_COLOR, LOCALE, mediaHtml, METRICS, PANEL_HEADERS, PREVIEW, Q_STYLE, S_ORDER, ago, api, averageColor, buildLayout, buzz, cfg, chime, cfgVersion, chart, color, cssVar, currentProcessesSort, emit, fillIcon, fmtClock, clockPeriod, fmtDur, fmtHM, fmtMem, fmtReset, fmtTime, fmtValue, gotoProcesses, gpuMem, gpusOf, h, en, haptic, hasProcessesPage, hist, hour12, icon, keyButton, setKeyFace, labelled, levelOf, media, mediaPosMs, metricText, online, openSheet, paceInfo, pad, prRow, pushView, quotaProviders, quotaWanted, r1, registerProcessesSorter, renderStatus, ringSvg, setArtColor, configMark, configReadSince, localWriteStored, setMediaState, setVolDragging, shortDur, shortGpu, store, t, tick, toast, volDragging, wxBlock, wxDeg, wxIcon, emo, rich, tp, tpv, quotaText, pages, curPage, gotoPageRef } from '../main.js';
import { W } from '../registry.js';
import { weekStartDay } from './time.js';
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
    box.classList.toggle('clock-centered', o.align === 'center');
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
          ap.textContent = h12 ? clockPeriod(now) : '';
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
            cells.push(h('div', 'fc-i', h('span', 'fc-t', k === 0 ? tp('Now') : hr.time[i].slice(11, 13)), h('span', 'fc-ic', emo(wxIcon(hr.weather_code[i], hr.is_day?.[i]))),
              h('span', 'fc-v', wxDeg(hr.temperature_2m[i]) + '°'), h('span', 'fc-r', rain >= 20 ? rich(`💧${rain}%`) : '')));
          }
        }
        row.replaceChildren(...cells);
      },
    };
  },
};

W.media = {
  needs: ['state', 'fast', 'media'], // 'media': the agent reads the players every second while this is on screen
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
        media.posMs = mediaPosMs(media);
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
    const nowMs = () => mediaPosMs(media);
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
            const pos = mediaPosMs(media);
            q('.fill').style.width = (pos / media.durMs) * 100 + '%';
            q('.m-pos').textContent = fmtTime(pos);
            if (Math.floor(pos / 1000) !== ariaSec) { ariaSec = Math.floor(pos / 1000); paintAria(pos); } // once a second, not every 250 ms
          } else if (media.app === 'Internet Radio') {
            const pos = mediaPosMs(media);
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
          q('.m-app-name').textContent = tp('Media');
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
        q('.m-app-name').textContent = m.app || tp('Media');
        sourceIcon.innerHTML = sourceIcons[m.app] || '';
        q('.m-title').textContent = m.title || tp('Unknown track');
        q('.m-artist').textContent = [m.artist, m.album].filter(Boolean).join(' · ') || '—';
        if (m.app === 'Internet Radio' && !(m.durMs > 0)) {
          q('.fill').style.width = m.playing ? '100%' : '0';
          q('.m-pos').textContent = fmtTime(m.posMs || 0);
          q('.m-dur').textContent = m.playing ? tp('LIVE') : '0:00';
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
    const lbl = h('div', 't-label', tp(m.label));
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
        lbl.textContent = pctVal != null && m.label === 'RAM' ? `${tp('RAM')} (${Math.round(pctVal)}%)` : tp(m.label);
        sub.replaceChildren(h('bdi', null, m.sub?.(s) || ' '));
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
    box.append(dial, h('div', 'g-label', tp(m.label)), sub);
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
        sub.replaceChildren(h('bdi', null, m.sub?.(s) || ''));
      },
    };
  },
};

W.chart = {
  needs: ['state'],
  create(box, { o }) {
    const requested = Array.isArray(o.metrics) && o.metrics.length ? o.metrics : ['cpu'];
    const known = k => Object.prototype.hasOwnProperty.call(METRICS, k);
    const keys = requested.filter(known);
    const missing = [...new Set(requested.filter(k => typeof k === 'string' && !known(k)))].join(', ');
    if (!keys.length) keys.push('cpu');
    const single = keys.length === 1 ? METRICS[keys[0]] : null;
    const isNet = keys.every((k) => k === 'down' || k === 'up');
    const label = single ? tp(single.name) : isNet ? tp('Network') : keys.map((k) => tp(METRICS[k].name)).join(' · ');
    box.style.setProperty('--c', color((single || METRICS[keys[0]]).color));
    const head = h('div', 's-head', h('span', 's-label', label), h('span', 's-name', ''));
    const main = h('div', 's-main');
    const canvas = h('canvas');
    box.append(head, main, canvas);
    if (missing) {
      const message = t('w.chartUnknownMetrics', { metrics: missing });
      const note = h('div', 'chart-missing', message);
      note.title = message;
      box.append(note);
    }
    if (keys.includes('cpu') || keys.includes('ram')) bindProcessLink(box, keys.includes('cpu') ? 'cpu' : 'mem');
    return {
      update() {
        const s = D.state;
        if (!s) return;
        if (single) {
          const [v, u] = metricText(single, s);
          main.replaceChildren(h('b', null, v), h('small', null, u), h('span', 'chips', single.sub?.(s) ? h('span', 'chip', h('bdi', null, single.sub(s))) : ''));
          head.lastChild.textContent = single.title?.(s) || '';
        } else {
          main.replaceChildren(...keys.map((k) => {
            const mm = METRICS[k];
            const [v, u] = metricText(mm, s);
            const e = h('span', 'multi', (k === 'down' ? '↓ ' : k === 'up' ? '↑ ' : tp(mm.label) + ' '), h('b', null, v + ' ' + u));
            e.style.color = color(mm.color);
            return e;
          }));
          head.lastChild.textContent = isNet && s.ping ? tpv('Ping {ms} · loss {loss}%', { ms: s.ping.ms != null ? Math.round(s.ping.ms) + ' ' + tp('ms') : '—', loss: s.ping.lossPct }) : '';
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
        up.textContent = tp('Up ') + (d ? t('time.dH', { d, h: hr }) : hr ? t('time.hMin', { h: hr, m }) : t('time.min', { m }));
        const rows = [meter(tp('RAM'), `${r1(pc.ramUsedGb)} / ${Math.round(pc.ramTotalGb)} ${tp('GB')}`, (pc.ramUsedGb / pc.ramTotalGb) * 100)];
        // one row per GPU (the integrated one counts its shared memory); named once there is more than one
        const gpus = gpusOf(D.state);
        for (const g of gpus) {
          const m = gpuMem(g);
          if (m) rows.push(meter(gpus.length > 1 ? `${m.label === 'VRAM' ? 'VRAM' : 'GPU'} · ${shortGpu(g.name)}` : m.label === 'VRAM' ? 'VRAM' : tp('GPU MEMORY'),
            `${r1(m.used)} / ${Math.round(m.total)} ${tp('GB')}`, (m.used / m.total) * 100, cssVar('--gpu')));
        }
        const disks = h('div', 'disks', ...(pc.disks || []).map((dk) => meter(dk.name === '/' ? tp('DISK') : dk.name, Math.round((dk.usedGb / dk.totalGb) * 100) + '%', (dk.usedGb / dk.totalGb) * 100, '#94a3b8')));
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
    const grid = h('div', 'q-grid', h('div', 'empty', tp('Quota data arrives once the computer is connected.')));
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
      return ['claude', 'codex', 'antigravity', 'grok'];
    };

    const isOverviewEnabled = () => o.showOverview === true;

    const getItems = () => {
      const want = getProviders();
      const items = quotaProviders().filter((p) => quotaWanted(want, p.id));
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
        h('span', 'q-name', tp('Overview')),
        h('span', 'q-plan', tpv('{n}-day spend', { n: days })),
        updStr ? h('span', 'q-upd', updStr) : null
      ));
      const summary = h('div', 'q-ov-hero',
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', tp('Estimated spend')), h('strong', 'q-ov-val', fmtCurrency(ov.totalCost, ov.currencyCode))),
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', tp('Recorded tokens')), h('strong', 'q-ov-val', tokens(ov.totalTokens || 0)))
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
      if (!list.length) { grid.appendChild(h('div', 'empty', want.length ? tp('Quota data is unavailable for the selected providers') : tp('No quota providers enabled'))); return; }

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
        const st = Q_STYLE[p.id.split('@')[0]] || { color: '#94a3b8', logo: '●' };
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
          rings.appendChild(h('div', 'q-ring', dial, h('div', 'q-label', quotaText(l.label)), p.id === 'codex' ? h('div', 'q-left', tpv('{n}% left', { n: Math.max(0, Math.round(100 - l.percent)) })) : null, h('div', 'q-reset', fmtReset(l.resetsAt)),
            pace ? h('div', 'q-pace ' + pace.cls, pace.text) : null));
        }
        if (!p.limits.length) rings.appendChild(h('div', 'q-reset', tp('No data')));
        card.append(rings);
        if (p.id === 'codex' && p.codex) {
          const c = p.codex;
          const details = h('div', 'q-details');
          if (c.model) details.append(h('div', 'q-detail', h('span', null, tp('Latest local session')), h('strong', null, c.model + (c.effort ? ` · ${c.effort}` : ''))));
          if (c.totalTokens != null) details.append(h('div', 'q-detail', h('span', null, tp('Session tokens')), h('strong', null, tokens(c.totalTokens))));
          if (c.totalInput != null || c.totalOutput != null) details.append(h('div', 'q-detail q-breakdown', tpv('In {in} · Out {out} · Cached {cached} · Reasoning {reasoning}', { in: tokens(c.totalInput), out: tokens(c.totalOutput), cached: tokens(c.totalCached), reasoning: tokens(c.totalReasoning) })));
          if (c.lastInput != null && c.contextWindow) details.append(h('div', 'q-detail q-breakdown', tpv('Last input {n} / {max} context window', { n: tokens(c.lastInput), max: tokens(c.contextWindow) })));
          if (details.childNodes.length) card.append(details);
        }
        card.append(h('div', 'q-foot', h('span', p.extra?.startsWith('Limit reached') ? 'q-alert' : null, quotaText(p.extra) || ''), p.error ? h('span', 'q-err', p.error) : h('span')));

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

W.codexbar = W.quotas;

W.sessions = {
  needs: ['work', 'tick'],
  create(box) {
    const sum = h('span', 'w-sum');
    const list = h('div', 's-list', h('div', 'empty', t('sess.waitingData')));
    box.append(h('div', 'w-head', h('span', 's-label', t('w.sessions')), sum), list);
    box.style.setProperty('--c', '#d97757');
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const statusText = (s) => s.status === 'busy' ? t('sess.working') : s.status === 'waiting' ? t('sess.waiting') : s.status === 'shell' ? t('sess.shell') : s.outcome === 'completed' ? t('sess.completed') : s.outcome === 'failed' ? t('sess.failed') : s.outcome === 'aborted' ? t('sess.aborted') : t('sess.idle');
    // The tool each session comes from: one glyph in the list, a glyph and a name in the details panel.
    const toolIcon = (tool) => tool === 'claude' ? '✳' : tool === 'codex' ? '◎' : tool === 'cursor' ? '◆' : tool === 'opencode' ? '◈' : tool === 'antigravity' ? Q_STYLE.antigravity.logo : tool === 'grok' ? Q_STYLE.grok.logo : tool === 'workbuddy' ? '❖' : '◎';
    const toolTitle = (tool) => tool === 'claude' ? 'Claude Code' : tool === 'codex' ? 'Codex' : tool === 'cursor' ? 'Cursor' : tool === 'opencode' ? 'OpenCode' : tool === 'antigravity' ? 'Antigravity' : tool === 'grok' ? 'Grok' : tool === 'workbuddy' ? 'WorkBuddy' : 'Codex';
    const ageText = (since) => Date.now() - since < 60_000 ? 'just now' : shortDur(Date.now() - since) + ' ago';
    // A session's title is the main label and the project folder goes under it: Claude Code sends it only while
    // Settings › Claude Code › "Show session titles on the phone" is on, OpenCode only while "Show OpenCode session
    // titles" is on, and Antigravity only while "Show Antigravity chat titles on the phone" is on; without a title
    // the folder is the label, as before.
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
        h('span', 'conv-who', m.role === 'user' ? tp('You') : 'Claude'),
        h('div', full ? 'conv-text' : 'conv-text clamp', full ? m.text : clip(m.text, 500)));
      const render = () => {
        const prevList = box.querySelector('.conv-list');
        // a list read to its end follows the chat as new messages come in; one scrolled back stays where it is (#460)
        const atEnd = !prevList || prevList.scrollHeight - prevList.scrollTop - prevList.clientHeight < 24;
        const top = prevList && !atEnd ? prevList.scrollTop : null;
        labels = [];
        if (!data.enabled) {
          box.replaceChildren(h('p', 'session-note', tp('The conversation is off. Turn it on in Settings › Claude Code › Show the conversation on the phone.')));
          return;
        }
        const messages = data.messages || [], steps = data.steps || [];
        const kids = [];
        if (steps.length) {
          kids.push(h('div', 'conv-h', tp('Recent steps')));
          for (const st of [...steps].reverse().slice(0, 6)) {
            const age = h('span', 'cs-a', stepAge(st));
            labels.push([age, st]);
            kids.push(h('div', 'conv-step' + (st.running ? ' on' : ''), h('span', 'cs-t', toolName(st.tool)), h('span', 'cs-d', st.detail || ''), age));
          }
        }
        if (!messages.length) kids.push(h('p', 'session-note', tp('No messages yet.')));
        else if (expanded) {
          kids.push(h('div', 'conv-h', tp('Conversation')), h('div', 'conv-list', messages.map((m) => message(m, true))));
        } else {
          const lastOf = (role) => [...messages].reverse().find((m) => m.role === role);
          kids.push(h('div', 'conv-h', tp('Last exchange')));
          for (const m of [lastOf('user'), lastOf('assistant')]) if (m) kids.push(message(m, false));
        }
        if (messages.length) {
          const toggle = h('button', 'conv-toggle', expanded ? tp('Hide the conversation') : tpv('Show the conversation ({n})', { n: messages.length }));
          toggle.type = 'button';
          toggle.addEventListener('click', () => { haptic(); expanded = !expanded; render(); });
          kids.push(toggle);
        }
        box.replaceChildren(...kids);
        const list = box.querySelector('.conv-list');
        if (list) list.scrollTop = top ?? list.scrollHeight; // a new view starts at the newest message, and keeps up with it
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
          if (!gone && !data) box.replaceChildren(h('p', 'session-note', tp('Could not read the conversation.')));
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
      close.setAttribute('aria-label', tp('Close session details'));
      let stopConversation = () => {};
      const onKey = (e) => { if (e.key === 'Escape') dismiss(); };
      const dismiss = () => { stopConversation(); backdrop.remove(); document.removeEventListener('keydown', onKey); };
      close.addEventListener('click', dismiss);
      backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
      panel.append(h('div', 'session-panel-head', h('span', null, toolIcon(s.tool) + ' ' + toolTitle(s.tool)), close), title);
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
        .map(([label, value]) => h('div', 'session-cell', h('span', null, tp(label)), h('strong', null, value)))));
      if (s.tool === 'codex' || s.tool === 'grok' || (s.tool === 'antigravity' && s.totalTokens != null)) panel.append(h('p', 'session-note', tp('Token totals are for this local session, not your account allowance.')));
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
          counts.busy + counts.shell ? tpv('{n} working', { n: counts.busy + counts.shell }) : '',
          counts.waiting ? tpv('{n} waiting', { n: counts.waiting }) : '',
          counts.idle ? tpv('{n} idle', { n: counts.idle }) : '',
        ].filter(Boolean).join(' · ') || t('w.noOpenSessions');
        const sorted = [...sessions].sort((a, b) => (S_ORDER[a.status] ?? 9) - (S_ORDER[b.status] ?? 9) || b.since - a.since);
        const rows = sorted.length ? sorted.map((s) => {
          const row = h('button', 'sess ' + s.status,
            h('span', 'st'), h('span', 'tl ' + s.tool, toolIcon(s.tool)),
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
    const rc = h('b'), mc = h('b'), rev = h('div', 'gh-list'), mine = h('div', 'gh-list'), err = h('div', 'gh-err', tp('Waiting for GitHub data…'));
    box.append(h('div', 'w-head', h('span', 's-label', 'GitHub'), user, bell),
      h('div', 'gh-sec', h('span', null, tp('Review requests')), rc), rev,
      h('div', 'gh-sec', h('span', null, tp('My pull requests')), mc), mine, err);
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
        ? tpv('Total RAM {pct}% ({used}/{total} GB) · ', { pct: ramPct, used: Math.round(pc.ramUsedGb), total: Math.round(pc.ramTotalGb) })
        : (ramPct != null ? tpv('Total RAM {pct}% · ', { pct: ramPct }) : '');
      const loadPart = sort === 'cpu'
        ? (cpuPct != null ? tpv('Total CPU {pct}% · ', { pct: cpuPct }) : '')
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

// On / off keys light up from the agent's state (D.state.toggles: { id: true | false }).
export function applyToggles(root) {
  const t = D.state?.toggles || {};
  root.querySelectorAll('.key.toggle').forEach((b) => b.classList.toggle('on', t[b.dataset.id] === true));
  applyCycles(root);
}

// A key that cycles through other keys shows the one that ran last (D.state.cycles: { id: the id of that key }); before the
// first tap, and after the agent restarted, it shows its own icon and name. The agent sends the id, not a position, so
// the face never depends on which version of the list the phone has loaded.
function applyCycles(root) {
  const at = D.state?.cycles || {}, all = cfg.shortcuts || [];
  root.querySelectorAll('.key.cycle').forEach((b) => {
    const id = b.dataset.id, own = all.find((x) => x.id === id);
    if (!own) return;
    const want = at[id] != null ? all.find((x) => x.id === at[id]) : (b.dataset.face ? own : null);
    // a key the phone has not loaded yet leaves the face as it is: not the key's own, which did not run
    if (!want || (b.dataset.face || '') === (want.id === id ? '' : want.id)) return;
    setKeyFace(b, want, b._cover === true);
  });
}

W.shortcuts = {
  needs: ['state'],
  create(box, { o }) {
    const all = cfg.shortcuts || [];
    const ids = Array.isArray(o.ids) && o.ids.length ? o.ids : null;
    const list = ids ? ids.map((id) => all.find((s) => s.id === id)).filter(Boolean) : all;
    if (!list.length) {
      box.append(h('div', 'empty', all.length ? t('w.shortcutsMissing') : t('w.shortcutsWait')));
      return {};
    }
    const cols = Math.min(8, Math.max(1, o.columns || 4));
    const grid = h('div', 'deck-grid', ...list.map(keyButton));
    grid.style.gridTemplateColumns = `repeat(${Math.min(cols, list.length)}, 1fr)`;
    grid.style.gridTemplateRows = `repeat(${Math.ceil(list.length / cols)}, 1fr)`;
    box.append(grid);
    return { update: () => applyToggles(box) };
  },
};

W.button = {
  bare: true, needs: ['state'],
  create(box, { o }) {
    const s = (cfg.shortcuts || []).find((x) => x.id === o.id) || (cfg.shortcuts || [])[0];
    box.append(s ? keyButton(s, { cover: o.cover === true, label: o.showLabel !== false }) : h('div', 'empty', t('w.pickShortcut')));
    return { update: () => applyToggles(box) };
  },
};

W.worldclock = {
  needs: ['tick'],
  create(box, { o }) {
    const zones = (Array.isArray(o.zones) && o.zones.length ? o.zones : ['Tallinn=Europe/Tallinn', 'Istanbul=Europe/Istanbul', 'London=Europe/London', 'New York=America/New_York', 'Tokyo=Asia/Tokyo'])
      .map((z) => { const i = z.indexOf('='); return i > 0 ? [z.slice(0, i).trim(), z.slice(i + 1).trim()] : [z.split('/').pop().replace(/_/g, ' '), z.trim()]; });
    const items = zones.map(([name, tz]) => {
      let fmt = null;
      try { fmt = new Intl.DateTimeFormat('en-GB', { timeZone: tz, year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }); } catch (_) {}
      const t = h('div', 'wc-t', fmt ? '--:--' : '??'), s = h('div', 'wc-s', fmt ? '' : 'unknown zone');
      return { fmt, t, s, el: h('div', 'wc', h('div', 'wc-n', name), t, s) };
    });
    const row = h('div', 'wc-row', items.map((i) => i.el));
    box.append(row);
    let shown = -1;
    return {
      update() {
        // once a minute, and at once after ticks were missed (the page was not visible, the night screen was on)
        const minute = Math.floor(Date.now() / 60000);
        if (minute === shown) return;
        shown = minute;
        const now = new Date();
        const localUtc = Date.UTC(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), now.getMinutes());
        row.classList.toggle('vert', box.clientHeight > box.clientWidth * 1.1);
        row.classList.toggle('h12', hour12());
        for (const it of items) {
          if (!it.fmt) continue;
          const p = Object.fromEntries(it.fmt.formatToParts(now).map((x) => [x.type, x.value]));
          const [hm, ap] = fmtClock(+p.hour % 24, +p.minute).split(' ');
          it.t.replaceChildren(hm, ap ? h('span', 'wc-ap', ' ' + ap) : '');
          const zoneUtc = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute);
          const diffH = (zoneUtc - localUtc) / 3600000;
          const dayDiff = Math.round((Date.UTC(+p.year, +p.month - 1, +p.day) - Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())) / 86400000);
          const day = dayDiff > 0 ? t('time.tomorrow') : dayDiff < 0 ? t('time.yesterday') : t('time.today');
          it.s.textContent = diffH === 0 ? day : `${day}, ${diffH > 0 ? '+' : '−'}${Math.abs(diffH)}h`;
        }
      },
    };
  },
};

W.calendar = {
  needs: ['tick'],
  create(box) {
    let shown = '';
    return {
      update() {
        const now = new Date();
        const firstDay = weekStartDay();
        const key = now.toDateString() + '/' + firstDay;
        if (key === shown) return;
        shown = key;
        const y = now.getFullYear(), m = now.getMonth();
        const first = new Date(y, m, 1), days = new Date(y, m + 1, 0).getDate();
        const lead = (first.getDay() - firstDay + 7) % 7;
        const weekday = new Intl.DateTimeFormat(LOCALE, { weekday: 'narrow' });
        const weekend = (day) => day === 0 || day === 6;
        const cells = Array.from({ length: 7 }, (_, i) => {
          const day = (firstDay + i) % 7;
          return h('span', 'cal-h' + (weekend(day) ? ' we' : ''), weekday.format(new Date(2024, 0, 7 + day)));
        });
        const prevDays = new Date(y, m, 0).getDate();
        for (let i = 0; i < lead; i++) cells.push(h('span', 'cal-d out', prevDays - lead + 1 + i));
        for (let d = 1; d <= days; d++) {
          const wd = (first.getDay() + d - 1) % 7;
          cells.push(h('span', 'cal-d' + (d === now.getDate() ? ' today' : '') + (weekend(wd) ? ' we' : ''), d));
        }
        const total = Math.ceil(cells.length / 7) * 7;
        for (let d = 1; cells.length < total; d++) cells.push(h('span', 'cal-d out', d));
        const grid = h('div', 'cal', cells);
        grid.style.gridTemplateRows = `auto repeat(${total / 7 - 1}, 1fr)`;
        box.replaceChildren(h('div', 'w-title', now.toLocaleDateString(LOCALE, { month: 'long', year: 'numeric' }),
          h('span', 'w-sub', tp('Week ') + weekNumber(now, firstDay))), grid);
      },
    };
  },
};

function weekNumber(d, firstDay) {
  if (firstDay === 1) return isoWeek(d);
  const start = Date.UTC(d.getFullYear(), 0, 1);
  const leadingDays = (new Date(start).getUTCDay() - firstDay + 7) % 7;
  const elapsed = (Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()) - start) / 86400000;
  return 1 + Math.floor((elapsed + leadingDays) / 7);
}

export function isoWeek(d) {
  const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
  t.setUTCDate(t.getUTCDate() + 4 - (t.getUTCDay() || 7));
  return Math.ceil(((t - Date.UTC(t.getUTCFullYear(), 0, 1)) / 86400000 + 1) / 7);
}

/**
 * A Pomodoro sequence from text such as "25 focus, 5 break, 25 focus, 15 long" ("focus 25", "25 min long break" and
 * "f / b / l" work too). Returns { steps, bad }: steps is null when the text is empty or not understood.
 */
export function parsePomodoro(text) {
  const kindOf = (k) => (/^l/i.test(k) ? 'long' : /^(b|s)/i.test(k) ? 'break' : 'focus');
  const KIND = '(focus|work|f|short\\s*break|break|b|long\\s*break|long|l)';
  const re = new RegExp(`^(?:${KIND}\\s*(\\d{1,3})\\s*(?:m|min|minutes?)?|(\\d{1,3})\\s*(?:m|min|minutes?)?\\s*${KIND})$`, 'i');
  const steps = [];
  for (const raw of String(text || '').split(/[,;\n]+/)) {
    const part = raw.trim();
    if (!part) continue;
    const m = part.match(re);
    const min = m ? +(m[2] || m[3]) : 0;
    if (!m || min < 1 || min > 600) return { steps: null, bad: part };
    steps.push({ kind: kindOf(m[1] || m[4]), ms: min * 60_000 });
    if (steps.length > 48) return { steps: null, bad: 'more than 48 steps' };
  }
  return { steps: steps.length ? steps : null, bad: null };
}

W.pomodoro = {
  needs: ['tick'],
  always: true, // the alarm goes off on whatever page is showing
  create(box, { o, page, w }) {
    const key = `pomo:${page.id}/${w.id}`;
    const minutes = (v, d, max) => Math.min(max, Math.max(1, +v || d)) * 60_000;
    const focus = { kind: 'focus', ms: minutes(o.focus, 25, 180) }, brk = { kind: 'break', ms: minutes(o.break, 5, 60) };
    const long = { kind: 'long', ms: minutes(o.longBreak, 15, 120) };
    const every = Math.min(12, Math.max(0, Math.floor(+o.longEvery || 0)));
    const rounds = Math.min(99, Math.max(0, Math.floor(+o.rounds || 0)));
    const custom = parsePomodoro(o.sequence);
    // the plan: the own sequence, or focus / break with a long break after every N focus sessions
    let plan = custom.steps;
    if (!plan) {
      plan = [];
      if (!every) plan.push(focus, brk);
      else for (let i = 0; i < every; i++) plan.push(focus, i === every - 1 ? long : brk);
    }
    const sig = plan.map((s) => `${s.kind[0]}${s.ms / 60_000}`).join(' ') + `/${rounds}`;
    const focusTotal = plan.filter((s) => s.kind === 'focus').length;
    const today = new Date().toDateString();
    let st = store.get(key, null);
    if (!st || typeof st !== 'object') st = { phase: 'focus', endsAt: null, left: plan[0].ms, count: 0, day: today };
    if (st.sig === undefined) {
      // state from before sequences (focus / break only): keep a running timer where it is
      st.step = st.phase === 'break' && plan.length > 1 ? 1 : 0;
      st.round = 0;
      st.sig = sig;
    } else if (st.sig !== sig) {
      // the sequence was changed in the settings: start it from the beginning
      Object.assign(st, { step: 0, round: 0, endsAt: null, left: plan[0].ms, sig });
    }
    if (!(st.step >= 0 && st.step < plan.length)) st.step = 0;
    if (st.day !== today) { st.count = 0; st.day = today; }
    const save = () => { st.phase = plan[st.step].kind; store.set(key, st); };
    save();
    const dial = h('div', 'g-dial'), time = h('div', 'g-val'), phase = h('div', 'g-label'), count = h('div', 'g-sub');
    const play = labelled(h('button', 'pb main', icon('play')), t('w.a.start')), reset = labelled(h('button', 'pb', icon('rotate-ccw')), t('w.a.restart')), skip = labelled(h('button', 'pb', icon('skip-forward')), t('w.a.skip'));
    dial.append(time);
    box.append(h('div', 'timer-layout', dial, phase, count, h('div', 'pomo-btns', reset, play, skip)));
    let svg = null;
    const total = () => plan[st.step].ms;
    const remaining = () => (st.endsAt ? Math.max(0, st.endsAt - Date.now()) : st.left);
    /** Moves to the next step; returns true when the last round of the sequence is over (the timer then stops). */
    const advance = (manual) => {
      if (plan[st.step].kind === 'focus' && !manual) st.count++;
      st.step++;
      if (st.step >= plan.length) { st.step = 0; st.round = (st.round || 0) + 1; }
      st.left = total();
      const finished = rounds > 0 && st.round >= rounds;
      if (finished) st.round = 0;
      st.endsAt = manual || finished ? null : Date.now() + st.left;
      save();
      return finished;
    };
    const label = () => {
      const s = plan[st.step];
      if (s.kind === 'long') return t('w.longBreak');
      if (s.kind === 'break') return t('w.break');
      const n = plan.slice(0, st.step + 1).filter((x) => x.kind === 'focus').length;
      return focusTotal > 1 ? `${t('w.focus')} ${n}/${focusTotal}` : t('w.focus');
    };
    const render = () => {
      // "today" starts over at midnight also while the panel stays open
      const day = new Date().toDateString();
      if (st.day !== day) { st.count = 0; st.day = day; save(); }
      if (st.endsAt && remaining() <= 0) {
        const was = plan[st.step].kind;
        const finished = advance(false);
        buzz(finished ? '0,200,120,200,120,200,120,600' : '0,200,120,200,120,400');
        if (o.sound === true) chime(o.soundVolume, finished);
        const next = plan[st.step].kind;
        toast(finished ? tp('Sequence finished, well done')
          : next === 'long' ? tp('Time for a long break') : next === 'break' ? tp('Time for a break')
            : was === 'focus' ? tp('Next focus session') : tp('Back to focus'), 'ok', '🍅 Pomodoro', 5);
      }
      const left = remaining();
      const kind = plan[st.step].kind;
      box.classList.toggle('brk', kind !== 'focus');
      box.classList.toggle('long', kind === 'long');
      box.classList.toggle('run', !!st.endsAt);
      if (svg) svg.remove();
      dial.insertAdjacentHTML('afterbegin', ringSvg(100 * (1 - left / total())));
      svg = dial.firstElementChild;
      time.textContent = fmtTime(left + 999);
      phase.textContent = label();
      const tomatoes = st.count ? '🍅'.repeat(Math.min(st.count, 6)) + (st.count > 6 ? ` ×${st.count}` : '') : t('w.noSessionsToday');
      count.replaceChildren(rich(custom.bad ? tpv('Sequence not understood: "{seq}"', { seq: custom.bad })
        : rounds > 1 ? tpv('Round {n}/{total}', { n: (st.round || 0) + 1, total: rounds }) + ` · ${tomatoes}` : tomatoes));
      // the tomato icons are decorative (aria-hidden), so say the count in words for screen readers
      if (st.count && !custom.bad) count.append(h('span', 'sr-only', ' ' + t('w.a.sessionsToday', { n: st.count })));
      play.replaceChildren(icon(st.endsAt ? 'pause' : 'play'));
      labelled(play, t(st.endsAt ? 'w.a.pause' : 'w.a.start'));
    };
    play.addEventListener('click', () => {
      haptic();
      if (st.endsAt && remaining() <= 0) {
        render();
        if (!st.endsAt) return;
      }
      if (st.endsAt) { st.left = remaining(); st.endsAt = null; } else st.endsAt = Date.now() + st.left;
      save(); render();
    });
    // ↺ restarts this step; pressed again on a step that has not started, it goes back to the start of the sequence
    reset.addEventListener('click', () => {
      haptic();
      if (!st.endsAt && st.left === total()) { st.step = 0; st.round = 0; }
      st.endsAt = null; st.left = total(); save(); render();
    });
    skip.addEventListener('click', () => { haptic(); advance(true); render(); });
    return { update: render };
  },
};

W.countdown = {
  needs: ['tick'],
  create(box, { o }) {
    const m = String(o.target || '').trim().match(/^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/);
    let target = NaN;
    if (m) {
      const [, year, month, day, hour = '0', minute = '0', second = '0'] = m;
      const date = new Date(0);
      date.setFullYear(+year, +month - 1, +day);
      date.setHours(+hour, +minute, +second, 0);
      if (date.getFullYear() === +year && date.getMonth() === +month - 1 &&
          date.getDate() === +day && +hour < 24 && +minute < 60 && +second < 60) {
        // the time is checked as numbers: one the clocks skip (spring forward) still counts, moved on like before
        target = date.getTime();
      }
    }
    const big = h('div', 'cd-v'), sub = h('div', 'cd-s');
    box.append(h('div', 'cd-l', o.label || tp('Countdown')), big, sub);
    if (Number.isNaN(target)) { big.textContent = '—'; sub.textContent = t('w.setDateFormat'); return {}; }
    sub.textContent = new Date(target).toLocaleDateString(LOCALE, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' }) + ', ' + fmtHM(new Date(target));
    return {
      update() {
        let d = target - Date.now();
        const past = d < 0;
        d = Math.abs(d);
        const days = Math.floor(d / 86400000), hrs = Math.floor((d % 86400000) / 3600000), mins = Math.floor((d % 3600000) / 60000), secs = Math.floor((d % 60000) / 1000);
        big.textContent = (past ? '+' : '') + (days ? `${days}d ${hrs}h` : hrs ? `${hrs}h ${pad(mins)}m` : `${mins}:${pad(secs)}`);
        box.classList.toggle('past', past);
      },
    };
  },
};

const NOTE_MAX = 500;

W.note = {
  bare: true, needs: [],
  create(box, { o, page, w }) {
    const paint = () => {
      const text = String(o.text ?? '');
      box.style.setProperty('--n', o.color || '#fbbf24');
      box.style.setProperty('--nf', text.length < 25 ? 15 : text.length < 70 ? 10.5 : text.length < 160 ? 8 : 6.5);
      box.replaceChildren(h('div', 'note-t', text));
    };
    paint();
    if (PREVIEW || String(w.id).includes(':')) return {};
    box.addEventListener('click', () => {
      if (!$('overview').hidden) return;
      haptic();
      const wrap = h('div', 'note-edit');
      wrap.onclick = (e) => { if (e.target === wrap) wrap.remove(); };
      const card = h('div', 'note-card');
      const ta = document.createElement('textarea');
      ta.className = 'note-ta';
      ta.value = String(o.text ?? '');
      let opened = cfgVersion;
      let unsure = null; // the text of a save that timed out: the agent may have stored it anyway
      ta.maxLength = NOTE_MAX;
      ta.rows = 6;
      const row = h('div', 'note-actions');
      const cancel = h('button', 'btn sm', t('w.note.cancel'));
      cancel.onclick = () => wrap.remove();
      const save = h('button', 'btn primary sm', t('w.note.save'));
      save.onclick = async () => {
        const submitted = ta.value;
        save.disabled = true;
        ta.disabled = true;
        try {
          const path = '/widget/' + encodeURIComponent(page.id) + '/' + encodeURIComponent(w.id)
            + '/note?text=' + encodeURIComponent(submitted) + '&version=' + encodeURIComponent(opened ?? '');
          const mark = configMark();
          const result = await api(path, 'POST', 8000);
          o.text = submitted;
          // a config read that started after this save already holds it (or something newer): leave it alone.
          // Otherwise the config the panel holds gets the text first, then the version, so an identical refresh keeps
          // it and a rebuild doesn't bring the old text back; reads that started before the save can't apply over it
          if (!configReadSince(mark)) {
            const saved = (cfg.pages || []).find((p) => p.id === page.id)?.widgets?.find((x) => x.id === w.id);
            if (saved) { saved.options = { ...(saved.options || {}), text: submitted }; store.set('cfg', cfg); }
            localWriteStored(result?.version);
          }
          paint();
          // a refresh while the editor was open rebuilt the page under it: draw the new widget from the updated config
          if (!box.isConnected) buildLayout();
          wrap.remove();
        } catch (error) {
          if (error?.status === 409) {
            // a save that timed out may have been stored: then this conflict is our own, so save again on top of it
            if (unsure != null) {
              const earlier = unsure;
              unsure = null;
              // read the agent's config without applying it: a rebuild now would replace this widget under the editor
              const current = await api('/config').catch(() => null);
              if (!wrap.isConnected) return; // the editor was closed meanwhile: nothing to save
              const now = (current?.config?.pages || []).find((p) => p.id === page.id)?.widgets?.find((x) => x.id === w.id)?.options?.text;
              if (now === earlier && current?.version != null) {
                opened = current.version;
                return save.onclick();
              }
            }
            wrap.remove();
            toast(t('w.note.changed'));
            return;
          }
          // no HTTP answer (a timeout or a dropped connection): the agent may still have stored it
          if (error?.status == null) unsure = submitted;
          save.disabled = false;
          ta.disabled = false;
          toast(t('w.note.failed'));
        }
      };
      row.append(cancel, save);
      card.append(ta, row);
      wrap.append(card);
      document.body.append(wrap);
      ta.focus();
    });
    return {};
  },
};

// Page buttons: one button per page (the ticked ones, or all), the page you are on highlighted. A tap goes there the
// same way a rule or a webhook does.
W.pagebuttons = {
  bare: true, needs: ['tick'],
  create(box, { o }) {
    const list = h('div', 'pgb');
    if (o.title) box.append(h('div', 'w-title', o.title));
    box.append(list);
    let shown = '';
    const render = () => {
      const ids = Array.isArray(o.ids) ? o.ids : [];
      let items = pages.map((p, i) => ({ p, i })).filter(({ p }) => !ids.length || ids.includes(p.id));
      if (!items.length) items = pages.map((p, i) => ({ p, i })); // the ticked pages are gone: show them all
      const key = items.map(({ p, i }) => `${i}:${p.id}:${p.icon || ''}:${p.title}`).join('|');
      if (key !== shown) {
        shown = key;
        list.replaceChildren(...items.map(({ p, i }) => {
          const b = h('button', 'pgb-b', p.icon ? h('span', 'pgb-i', rich(p.icon)) : null, h('span', 'pgb-t', p.title || `${i + 1}`));
          b.type = 'button';
          b.dataset.i = i;
          b.setAttribute('aria-label', `${i + 1} · ${p.title || ''}`);
          b.onclick = (e) => { e.stopPropagation(); haptic(); gotoPageRef(i + 1); };
          return b;
        }));
      }
      list.querySelectorAll('.pgb-b').forEach((b) => b.classList.toggle('on', +b.dataset.i === curPage));
      fit();
    };
    // columns for the widget's shape: buttons about twice as wide as tall, with few empty cells
    let fitted = '';
    const fit = () => {
      const n = list.children.length, W = list.clientWidth, H = list.clientHeight;
      if (!n || !W || !H) return;
      let best = 1, score = Infinity;
      for (let cols = 1; cols <= n; cols++) {
        const rows = Math.ceil(n / cols);
        const s = Math.abs(Math.log((W / cols) / (H / rows) / 2)) + 0.15 * (cols * rows - n);
        if (s < score) { score = s; best = cols; }
      }
      const value = `repeat(${best}, minmax(0, 1fr))`;
      if (value !== fitted) { fitted = value; list.style.gridTemplateColumns = value; }
    };
    const watch = typeof ResizeObserver === 'function' ? new ResizeObserver(fit) : null;
    watch?.observe(list);
    render();
    return { update: render, destroy() { watch?.disconnect(); } };
  },
};

W.label = {
  bare: true, needs: [],
  create(box, { o }) {
    box.append(h('div', 'lbl-t', o.text || ''));
    return {};
  },
};

W.command = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    const out = h('div', 'cmd-o', '…'), foot = h('div', 'cmd-f', '');
    box.append(h('div', 'w-title', o.title || en('Command')), out, foot);
    let last = 0, busy = false, res = null;
    const load = async (force) => {
      if (busy) return;
      busy = true;
      try {
        res = await api(`/widget/${encodeURIComponent(page.id)}/${encodeURIComponent(w.id)}${force ? '?force=true' : ''}`, 'GET', 4000);
        const text = res.output ?? '';
        out.textContent = text || (res.running ? t('w.running') : t('w.noOutput'));
        const short = text.length <= 14 && !text.includes('\n');
        out.classList.toggle('big', short);
        box.classList.toggle('bad', res.ok === false);
      } catch (_) { if (!res) out.textContent = online ? t('w.unavailable') : '—'; }
      busy = false;
    };
    box.addEventListener('click', () => { haptic(); last = Date.now(); load(true).then(() => setTimeout(() => load(false), 1500)); });
    return {
      update() {
        if (res?.at) foot.textContent = (res.running ? 'refreshing · ' : '') + ago(res.at) + (res.durationMs ? ` · ${Math.round(res.durationMs)} ms` : '');
        if (!online || Date.now() - last < (res?.running || !res?.at ? 2000 : 5000)) return;
        last = Date.now();
        load(false);
      },
    };
  },
};

W.push = {
  needs: ['push', 'tick'],
  create(box, { o }) {
    const key = o.key || '';
    let lastAt = -1;
    const render = () => {
      const item = D.push[key];
      if (!item) {
        if (lastAt === 0) return;
        lastAt = 0;
        box.style.removeProperty('--c');
        box.replaceChildren(h('div', 'w-title', o.title || key), h('div', 'push-empty', t('w.noDataYet'), h('code', null, `POST /api/push/${key}`)));
        return;
      }
      const ageEl = box.querySelector('.push-age');
      if (item.at === lastAt) { if (ageEl) ageEl.textContent = ago(item.at); return; }
      lastAt = item.at;
      const v = pushView(item);
      const lvl = levelOf(v);
      const c = v.color || LEVEL_COLOR[lvl] || '';
      if (c) box.style.setProperty('--c', c); else box.style.removeProperty('--c');
      const unit = v.unit ?? o.unit ?? '';
      const style = o.style && o.style !== 'auto' ? o.style : v.items ? 'list' : 'big';
      const title = h('div', 'w-title', v.icon ? rich(v.icon + ' ') : '', o.title || v.title || key, h('span', 'push-age w-sub', ago(item.at)));
      let body;
      if (style === 'list') {
        const items = v.items || (v.value != null ? [v.value] : []);
        body = h('div', 'push-list', items.slice(0, 30).map((x) => {
          if (x && typeof x === 'object') {
            const li = h('div', 'pl-i', h('span', 'pl-l', x.label ?? x.title ?? x.name ?? ''), h('span', 'pl-v', fmtValue(x.value ?? x.status ?? '')));
            const lc = LEVEL_COLOR[x.level || levelOf({ value: x.value ?? x.status })];
            if (lc) li.style.setProperty('--lc', lc);
            return li;
          }
          return h('div', 'pl-i', h('span', 'pl-l', fmtValue(x)));
        }));
      } else if (style === 'gauge') {
        const min = v.min ?? (typeof o.min === 'number' ? o.min : 0), max = v.max ?? (typeof o.max === 'number' ? o.max : 100);
        const num = typeof v.value === 'number' ? v.value : parseFloat(v.value);
        const pct = v.progress != null ? v.progress * 100 : Number.isFinite(num) ? ((num - min) / (max - min || 1)) * 100 : 0;
        const dial = h('div', 'g-dial');
        dial.innerHTML = ringSvg(pct);
        dial.appendChild(h('div', 'g-val', fmtValue(v.value ?? Math.round(pct)), h('small', null, unit)));
        box.style.setProperty('--q', c || 'var(--cpu)');
        body = h('div', 'push-gauge', dial, v.sub ? h('div', 'g-sub', v.sub) : null);
      } else {
        body = h('div', 'push-big', h('div', 'pv', fmtValue(v.value), unit ? h('small', null, unit) : null),
          v.sub ? h('div', 'ps', String(v.sub)) : null,
          v.progress != null ? h('div', 'track', h('i')) : null);
        const bar = body.querySelector('.track i');
        if (bar) bar.style.width = Math.max(0, Math.min(100, v.progress * 100)) + '%';
      }
      box.className = box.className.replace(/\bps-\w+/g, '') + ' ps-' + style;
      box.replaceChildren(title, body);
    };
    return { update: render };
  },
};

W.inbox = {
  needs: ['notes', 'tick'],
  create(box) {
    const list = h('div', 'inbox');
    const clear = h('button', 'mini-btn', tp('Clear'));
    clear.addEventListener('click', () => { haptic(); D.notes = []; store.set('notes', []); emit('notes'); });
    box.append(h('div', 'w-title', en('Notifications'), clear), list);
    let shown = '';
    return {
      update(kind) {
        const key = D.notes.length + ':' + (D.notes[0]?.at || 0) + ':' + Math.floor(Date.now() / 60000);
        if (key === shown && kind === 'tick') return;
        shown = key;
        list.replaceChildren(...(D.notes.length ? D.notes.map((n) => {
          const e = h('div', 'ib-i', h('div', 'ib-h', h('b', null, n.title || n.text), h('span', null, ago(n.at))), n.title && n.text ? h('div', 'ib-t', n.text) : null);
          e.style.setProperty('--lc', LEVEL_COLOR[n.level] || 'var(--dim)');
          return e;
        }) : [h('div', 'empty', t('w.notifyEmpty'))]));
      },
    };
  },
};
