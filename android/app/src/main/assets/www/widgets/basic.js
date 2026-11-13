import { $, API, D, LEVEL_COLOR, LOCALE, MEDIA_HTML, METRICS, Q_STYLE, S_ORDER, ago, api, averageColor, buzz, cfg, chart, color, cssVar, emit, fillIcon, fmtDur, fmtMem, fmtReset, fmtTime, fmtValue, h, haptic, hist, keyButton, levelOf, media, metricText, online, openSheet, paceInfo, pad, prRow, pushView, quotaProviders, r1, renderStatus, ringSvg, setArtColor, setVolDragging, shortDur, store, tick, toast, volDragging, wxBlock, wxIcon } from '../main.js';
import { W } from '../registry.js';

// ================================================================= widgets
// Each widget: { needs: [data kinds], bare?: no card background, create(box, ctx) -> { update(kind) } }
// Data kinds: tick (every second), fast (4×/s), state, weather, quotas, work, procs, push, notes.

W.clock = {
  bare: true, needs: ['tick', 'state', 'weather', 'quotas', 'work'],
  create(box, { o }) {
    const status = o.status !== false ? h('div', 'status') : null;
    const hh = h('span', 'hh', '--'), mm = h('span', 'mm', '--'), ss = h('span', 'sec');
    const date = o.date !== false ? h('div', 'date') : null;
    const wx = o.weather !== false ? wxBlock() : null;
    box.append(status || '', h('div', 'clock', hh, h('span', 'colon', ':'), mm, ss), date || '', wx || '');
    box.style.setProperty('--tf', (0.84 - (status ? 0.1 : 0) - (date ? 0.11 : 0) - (wx ? 0.2 : 0)).toFixed(2));
    return {
      update(kind) {
        if (kind === 'tick') {
          const now = new Date();
          hh.textContent = pad(now.getHours());
          mm.textContent = pad(now.getMinutes());
          ss.textContent = cfg.display?.showSeconds === false ? '' : pad(now.getSeconds());
          if (date) date.textContent = now.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' });
          if (status && now.getSeconds() % 30 === 0) renderStatus(status); // battery
        } else if (kind === 'weather') wx?.update();
        else if (status) renderStatus(status);
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
    box.append(h('div', 'w-title', daily ? '7-day forecast' : 'Next hours', h('span', 'w-sub', '')), row);
    return {
      update() {
        const w = D.weather;
        if (!w?.hourly || !w?.daily) { row.replaceChildren(h('div', 'empty', D.weather === null ? 'Loading…' : 'Weather unavailable')); return; }
        box.querySelector('.w-sub').textContent = cfg.weather?.city || '';
        const vertical = box.clientHeight > box.clientWidth * 0.9;
        const room = vertical ? box.clientHeight - 30 : box.clientWidth - 16;
        const n = Math.max(3, Math.min(daily ? 7 : 12, Math.floor(room / (vertical ? 30 : 50))));
        row.classList.toggle('vert', vertical);
        const cells = [];
        if (daily) {
          const d = w.daily;
          for (let i = 0; i < Math.min(n, d.time.length); i++) {
            const day = i === 0 ? 'Today' : new Date(d.time[i] + 'T12:00').toLocaleDateString(LOCALE, { weekday: 'short' });
            const rain = d.precipitation_probability_max?.[i];
            cells.push(h('div', 'fc-i', h('span', 'fc-t', day), h('span', 'fc-ic', wxIcon(d.weather_code[i])),
              h('span', 'fc-v', Math.round(d.temperature_2m_max[i]) + '°', h('small', null, ' ' + Math.round(d.temperature_2m_min[i]) + '°')),
              h('span', 'fc-r', rain >= 20 ? `💧${rain}%` : '')));
          }
        } else {
          const hr = w.hourly;
          let start = hr.time.indexOf((w.current?.time || '').slice(0, 13) + ':00');
          if (start < 0) start = 0;
          const step = n >= 10 ? 1 : 2;
          for (let k = 0; k < n; k++) {
            const i = start + k * step;
            if (i >= hr.time.length) break;
            const rain = hr.precipitation_probability?.[i];
            cells.push(h('div', 'fc-i', h('span', 'fc-t', k === 0 ? 'Now' : hr.time[i].slice(11, 13)), h('span', 'fc-ic', wxIcon(hr.weather_code[i], hr.is_day?.[i])),
              h('span', 'fc-v', Math.round(hr.temperature_2m[i]) + '°'), h('span', 'fc-r', rain >= 20 ? `💧${rain}%` : '')));
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
    box.innerHTML = MEDIA_HTML;
    const q = (s) => box.querySelector(s);
    const img = q('.art'), vol = q('.volr');
    const sourceIcon = q('.m-source-icon');
    const sourceIcons = {
      YouTube: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="1" y="4" width="22" height="16" rx="5" fill="#ff0033"/><path d="m10 8 6 4-6 4z" fill="#fff"/></svg>',
      'YouTube Music': '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="11" fill="#ff0033"/><circle cx="12" cy="12" r="6.5" fill="none" stroke="#fff" stroke-width="1.4"/><path d="m10 8.5 5.5 3.5-5.5 3.5z" fill="#fff"/></svg>',
    };
    let artId = -1, volTimer = 0;
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
      try { await api('/media/' + action, 'POST'); } catch (_) {}
    }));
    q('.bar').addEventListener('click', async (e) => {
      if (!media || !media.durMs || !media.canSeek) return;
      const r = e.currentTarget.getBoundingClientRect();
      const ms = Math.round(((e.clientX - r.left) / r.width) * media.durMs);
      haptic();
      media.posMs = ms; media.at = performance.now();
      try { await api('/media/seek?ms=' + ms, 'POST'); } catch (_) {}
    });
    const paintVol = (v) => { vol.style.setProperty('--p', v + '%'); q('.vol-val').textContent = v; };
    vol.addEventListener('input', () => {
      setVolDragging(true);
      paintVol(vol.value);
      clearTimeout(volTimer);
      volTimer = setTimeout(() => api('/volume?level=' + vol.value, 'POST').catch(() => {}), 120);
    });
    vol.addEventListener('change', () => setTimeout(() => setVolDragging(false), 600));
    q('.mute').addEventListener('click', async () => {
      haptic();
      if ((!D.state?.audio || D.state.audio.canControl === false)) return;
      try { const a = await api('/volume?op=mute', 'POST'); D.state.audio = a; emit('state'); } catch (_) {}
    });

    return {
      update(kind) {
        if (kind === 'fast') {
          if (!media || !(media.durMs > 0)) return;
          const pos = Math.min(media.durMs, media.posMs + (media.playing ? performance.now() - media.at : 0));
          q('.fill').style.width = (pos / media.durMs) * 100 + '%';
          q('.m-pos').textContent = fmtTime(pos);
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
          box.classList.remove('playing');
          q('.m-app-name').textContent = 'Media';
          sourceIcon.replaceChildren();
          q('.m-title').textContent = 'Nothing playing';
          q('.m-artist').textContent = 'Press ▶ to resume';
          q('.fill').style.width = '0';
          q('.m-pos').textContent = q('.m-dur').textContent = '0:00';
          if (artId !== 0) { artId = 0; img.classList.remove('on'); setArtColor(null); }
          return;
        }
        box.classList.toggle('playing', m.playing);
        q('.m-app-name').textContent = m.app || 'Media';
        sourceIcon.innerHTML = sourceIcons[m.app] || '';
        q('.m-title').textContent = m.title || 'Unknown track';
        q('.m-artist').textContent = [m.artist, m.album].filter(Boolean).join(' · ') || '—';
        q('.m-dur').textContent = fmtTime(m.durMs);
        if (m.artId !== artId) {
          artId = m.artId;
          if (m.artId) img.src = `${API}/art?id=${m.artId}`;
          else { img.classList.remove('on'); setArtColor(null); }
        }
      },
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
    const val = h('b', null, '—'), unit = h('small'), sub = h('div', 't-sub', ' ');
    box.append(canvas, h('div', 't-label', m.label), h('div', 't-val', val, unit), sub);
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const [v, u] = metricText(m, s);
        val.textContent = v;
        unit.textContent = u;
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
    let svg = null;
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const raw = m.pct ? m.pct(s) : m.val(s);
        const p = raw == null ? 0 : (raw / (m.pct ? 100 : m.max(s))) * 100;
        const warn = (o.metric || 'cpu').includes('temp') ? (raw > 85 ? '#ef4444' : raw > 75 ? '#f59e0b' : '') : (p >= 95 ? '#ef4444' : p >= 85 ? '#f59e0b' : '');
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
    const keys = (Array.isArray(o.metrics) && o.metrics.length ? o.metrics : ['cpu']).filter((k) => METRICS[k]);
    const single = keys.length === 1 ? METRICS[keys[0]] : null;
    const isNet = keys.every((k) => k === 'down' || k === 'up');
    const label = single ? single.name : isNet ? 'Network' : keys.map((k) => METRICS[k].name).join(' · ');
    box.style.setProperty('--c', color((single || METRICS[keys[0]]).color));
    const head = h('div', 's-head', h('span', 's-label', label), h('span', 's-name', ''));
    const main = h('div', 's-main');
    const canvas = h('canvas');
    box.append(head, main, canvas);
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
    box.append(h('div', 's-head', h('span', 's-label', 'Memory'), up), list);
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
        const g = pc.gpu;
        if (g && g.vramTotal) rows.push(meter('VRAM', `${r1(g.vramUsed)} / ${Math.round(g.vramTotal)} GB`, (g.vramUsed / g.vramTotal) * 100, cssVar('--gpu')));
        const disks = h('div', 'disks', ...(pc.disks || []).map((dk) => meter(dk.name === '/' ? 'DISK' : dk.name, Math.round((dk.usedGb / dk.totalGb) * 100) + '%', (dk.usedGb / dk.totalGb) * 100, '#94a3b8')));
        list.replaceChildren(...rows, disks);
      },
    };
  },
};

W.quotas = {
  needs: ['quotas', 'tick'],
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
        h('span', 'q-logo', '📊'),
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
          if (compact) pctEl.style.fontSize = '22px';
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

    return {
      update(kind) {
        if (kind === 'tick') {
          const sec = getRotateInterval();
          if (sec > 0 && Date.now() - lastRotate >= sec * 1000) {
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
    const list = h('div', 's-list', h('div', 'empty', 'Waiting for session data…'));
    box.append(h('div', 'w-head', h('span', 's-label', 'Sessions'), sum), list);
    box.style.setProperty('--c', '#d97757');
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const statusText = (s) => s.status === 'busy' ? 'Working' : s.status === 'waiting' ? 'Waiting' : s.status === 'shell' ? 'Running a command' : s.outcome === 'completed' ? 'Completed' : s.outcome === 'aborted' ? 'Aborted' : 'Idle';
    const ageText = (since) => Date.now() - since < 60_000 ? 'just now' : shortDur(Date.now() - since) + ' ago';
    const openDetails = (s) => {
      haptic();
      const backdrop = h('div', 'session-backdrop');
      const panel = h('div', 'session-panel');
      panel.setAttribute('role', 'dialog');
      panel.setAttribute('aria-modal', 'true');
      const title = h('h2', null, s.project || s.name);
      const close = h('button', 'session-close', '✕');
      close.setAttribute('aria-label', 'Close session details');
      const onKey = (e) => { if (e.key === 'Escape') dismiss(); };
      const dismiss = () => { backdrop.remove(); document.removeEventListener('keydown', onKey); };
      close.addEventListener('click', dismiss);
      backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
      panel.append(h('div', 'session-panel-head', h('span', null, s.tool === 'codex' ? '◎ Codex' : '✳ Claude Code'), close), title);
      const rows = [
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
      for (const [label, value] of rows) if (value != null) panel.append(h('div', 'session-detail-row', h('span', null, label), h('strong', null, value)));
      if (s.tool === 'codex') panel.append(h('p', 'session-note', 'Token totals are for this local session, not your account allowance.'));
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
        ].filter(Boolean).join(' · ') || 'No open sessions';
        const sorted = [...sessions].sort((a, b) => (S_ORDER[a.status] ?? 9) - (S_ORDER[b.status] ?? 9) || b.since - a.since);
        const rows = sorted.length ? sorted.map((s) => {
          const row = h('button', 'sess ' + s.status,
            h('span', 'st'), h('span', 'tl ' + s.tool, s.tool === 'claude' ? '✳' : '◎'),
            h('span', 'sess-main', h('span', 'nm', s.project || s.name), h('span', 'sess-meta', [s.model, s.effort].filter(Boolean).join(' · ') || statusText(s))),
            h('span', 'ds', shortDur(Date.now() - s.since)));
          row.type = 'button';
          row.addEventListener('click', () => openDetails(s));
          return row;
        }) : [h('div', 'empty', 'No Claude Code / Codex sessions open')];
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
        bell.textContent = `🔔 ${g.notifications >= 50 ? '50+' : g.notifications}`;
        rc.textContent = g.reviewRequests.length;
        mc.textContent = g.myPrs.length;
        const nRev = Math.min(g.reviewRequests.length || 1, Math.max(1, Math.floor(rows / 2)));
        rev.replaceChildren(...(g.reviewRequests.length ? g.reviewRequests.slice(0, nRev).map((p) => prRow(p, false)) : [h('div', 'gh-pr', 'No pending reviews')]));
        mine.replaceChildren(...(g.myPrs.length ? g.myPrs.slice(0, Math.max(1, rows - nRev)).map((p) => prRow(p, true)) : [h('div', 'gh-pr', 'No open pull requests')]));
        err.textContent = g.error ? 'GitHub: ' + g.error : '';
      },
    };
  },
};

W.processes = {
  needs: ['procs'],
  create(box, { o }) {
    const canKill = o.allowKill !== false;
    let sort = 'cpu';
    const sum = h('span', 'p-sum');
    const seg = h('div', 'seg', h('button', 'on', 'CPU'), h('button', null, 'RAM'));
    const grid = h('div', 'p-grid', h('div', 'empty', 'Processes appear once the computer is connected.'));
    box.style.setProperty('--c', '#f472b6');
    box.append(h('div', 'p-head', h('span', 's-label', 'Processes'), sum, seg), grid);
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
      if (!data.groups.length) { sum.textContent = ''; grid.replaceChildren(h('div', 'empty', 'Measuring…')); return; }
      const cols = box.clientWidth > 460 ? 2 : 1;
      const rows = Math.max(1, Math.floor((box.clientHeight - 46) / 40));
      grid.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
      grid.style.gridTemplateRows = `repeat(${rows}, 1fr)`;
      const list = [...data.groups].sort((a, b) => (sort === 'cpu' ? b.cpu - a.cpu || b.memMb - a.memMb : b.memMb - a.memMb)).slice(0, cols * rows);
      const max = Math.max(0.1, ...list.map((g) => (sort === 'cpu' ? g.cpu : g.memMb)));
      sum.textContent = box.clientWidth > 380 ? `${data.total} processes · ${data.groupCount ?? data.groups.length} apps · tap one for details` : `${data.total} processes`;
      if (grid.querySelector('.empty')) grid.replaceChildren();
      const keep = new Set(list.map((g) => g.name));
      for (const [n, e] of els) if (!keep.has(n)) { e.remove(); els.delete(n); }
      for (const g of list) {
        const e = rowEl(g);
        e._g = g;
        e.querySelector('.p-t').textContent = g.title;
        e.querySelector('.p-count').textContent = g.count > 1 ? '×' + g.count : '';
        e.querySelector('.p-lock').textContent = g.locked ? '🔒' : '';
        e.querySelector('.p-bar i').style.width = ((sort === 'cpu' ? g.cpu : g.memMb) / max) * 100 + '%';
        e.querySelector('.p-nums b').textContent = sort === 'cpu' ? g.cpu.toFixed(1) + '%' : fmtMem(g.memMb);
        e.querySelector('.p-nums small').textContent = sort === 'cpu' ? fmtMem(g.memMb) : g.cpu.toFixed(1) + '%';
        grid.appendChild(e); // keeps the order
      }
    };
    [...seg.children].forEach((b, i) => b.addEventListener('click', () => {
      haptic();
      sort = i === 0 ? 'cpu' : 'mem';
      [...seg.children].forEach((x) => x.classList.toggle('on', x === b));
      render();
    }));
    return { update: render };
  },
};

// On / off keys light up from the agent's state (D.state.toggles: { id: true | false }).
export function applyToggles(root) {
  const t = D.state?.toggles || {};
  root.querySelectorAll('.key.toggle').forEach((b) => b.classList.toggle('on', t[b.dataset.id] === true));
}

W.shortcuts = {
  needs: ['state'],
  create(box, { o }) {
    const all = cfg.shortcuts || [];
    const ids = Array.isArray(o.ids) && o.ids.length ? o.ids : null;
    const list = ids ? ids.map((id) => all.find((s) => s.id === id)).filter(Boolean) : all;
    if (!list.length) {
      box.append(h('div', 'empty', all.length ? 'None of the chosen shortcuts exist any more.' : 'Shortcuts load once the computer is connected.\nEdit them from the tray icon › Settings.'));
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
    box.append(s ? keyButton(s) : h('div', 'empty', 'Pick a shortcut in the settings'));
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
        for (const it of items) {
          if (!it.fmt) continue;
          const p = Object.fromEntries(it.fmt.formatToParts(now).map((x) => [x.type, x.value]));
          it.t.textContent = `${p.hour}:${p.minute}`;
          const zoneUtc = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute);
          const diffH = Math.round((zoneUtc - localUtc) / 360000) / 10;
          const dayDiff = Math.round((Date.UTC(+p.year, +p.month - 1, +p.day) - Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())) / 86400000);
          const day = dayDiff > 0 ? 'Tomorrow' : dayDiff < 0 ? 'Yesterday' : 'Today';
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
        const key = now.toDateString();
        if (key === shown) return;
        shown = key;
        const y = now.getFullYear(), m = now.getMonth();
        const first = new Date(y, m, 1), days = new Date(y, m + 1, 0).getDate();
        const lead = (first.getDay() + 6) % 7; // Monday first
        const cells = ['M', 'T', 'W', 'T', 'F', 'S', 'S'].map((d, i) => h('span', 'cal-h' + (i > 4 ? ' we' : ''), d));
        const prevDays = new Date(y, m, 0).getDate();
        for (let i = 0; i < lead; i++) cells.push(h('span', 'cal-d out', prevDays - lead + 1 + i));
        for (let d = 1; d <= days; d++) {
          const wd = (lead + d - 1) % 7;
          cells.push(h('span', 'cal-d' + (d === now.getDate() ? ' today' : '') + (wd > 4 ? ' we' : ''), d));
        }
        const total = Math.ceil(cells.length / 7) * 7;
        for (let d = 1; cells.length < total; d++) cells.push(h('span', 'cal-d out', d));
        const grid = h('div', 'cal', cells);
        grid.style.gridTemplateRows = `auto repeat(${total / 7 - 1}, 1fr)`;
        box.replaceChildren(h('div', 'w-title', now.toLocaleDateString(LOCALE, { month: 'long', year: 'numeric' }),
          h('span', 'w-sub', 'Week ' + isoWeek(now))), grid);
      },
    };
  },
};

export function isoWeek(d) {
  const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
  t.setUTCDate(t.getUTCDate() + 4 - (t.getUTCDay() || 7));
  return Math.ceil(((t - Date.UTC(t.getUTCFullYear(), 0, 1)) / 86400000 + 1) / 7);
}

W.pomodoro = {
  needs: ['tick'],
  always: true, // the alarm goes off on whatever page is showing
  create(box, { o, page, w }) {
    const key = `pomo:${page.id}/${w.id}`;
    const focusMs = Math.max(1, o.focus || 25) * 60_000, breakMs = Math.max(1, o.break || 5) * 60_000;
    const today = new Date().toDateString();
    let st = store.get(key, null);
    if (!st || typeof st !== 'object') st = { phase: 'focus', endsAt: null, left: focusMs, count: 0, day: today };
    if (st.day !== today) { st.count = 0; st.day = today; }
    const save = () => store.set(key, st);
    const dial = h('div', 'g-dial'), time = h('div', 'g-val'), phase = h('div', 'g-label'), count = h('div', 'g-sub');
    const play = h('button', 'pb main', '▶'), reset = h('button', 'pb', '↺'), skip = h('button', 'pb', '⏭');
    dial.append(time);
    box.append(dial, phase, count, h('div', 'pomo-btns', reset, play, skip));
    let svg = null;
    const total = () => (st.phase === 'focus' ? focusMs : breakMs);
    const remaining = () => (st.endsAt ? Math.max(0, st.endsAt - Date.now()) : st.left);
    const switchPhase = (manual) => {
      if (st.phase === 'focus' && !manual) st.count++;
      st.phase = st.phase === 'focus' ? 'break' : 'focus';
      st.left = total();
      st.endsAt = manual ? null : Date.now() + st.left;
      save();
    };
    const render = () => {
      // "today" starts over at midnight also while the panel stays open
      const day = new Date().toDateString();
      if (st.day !== day) { st.count = 0; st.day = day; save(); }
      if (st.endsAt && remaining() <= 0) {
        const wasFocus = st.phase === 'focus';
        switchPhase(false);
        buzz('0,200,120,200,120,400');
        toast(wasFocus ? 'Time for a break' : 'Back to focus', 'ok', '🍅 Pomodoro', 5);
      }
      const left = remaining();
      box.classList.toggle('brk', st.phase === 'break');
      box.classList.toggle('run', !!st.endsAt);
      if (svg) svg.remove();
      dial.insertAdjacentHTML('afterbegin', ringSvg(100 * (1 - left / total())));
      svg = dial.firstElementChild;
      time.textContent = fmtTime(left + 999);
      phase.textContent = st.phase === 'focus' ? 'Focus' : 'Break';
      count.textContent = st.count ? '🍅'.repeat(Math.min(st.count, 6)) + (st.count > 6 ? ` ×${st.count}` : '') : 'No sessions yet today';
      play.textContent = st.endsAt ? '⏸' : '▶';
    };
    play.addEventListener('click', () => {
      haptic();
      if (st.endsAt) { st.left = remaining(); st.endsAt = null; } else st.endsAt = Date.now() + st.left;
      save(); render();
    });
    reset.addEventListener('click', () => { haptic(); st.endsAt = null; st.left = total(); save(); render(); });
    skip.addEventListener('click', () => { haptic(); switchPhase(true); render(); });
    return { update: render };
  },
};

W.countdown = {
  needs: ['tick'],
  create(box, { o }) {
    const m = String(o.target || '').match(/^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{1,2}):(\d{2}))?/);
    const target = m ? new Date(+m[1], +m[2] - 1, +m[3], +(m[4] || 0), +(m[5] || 0)).getTime() : NaN;
    const big = h('div', 'cd-v'), sub = h('div', 'cd-s');
    box.append(h('div', 'cd-l', o.label || 'Countdown'), big, sub);
    if (Number.isNaN(target)) { big.textContent = '—'; sub.textContent = 'Set a date as YYYY-MM-DD HH:MM'; return {}; }
    sub.textContent = new Date(target).toLocaleString(LOCALE, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
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

W.note = {
  bare: true, needs: [],
  create(box, { o }) {
    const text = String(o.text ?? '');
    box.style.setProperty('--n', o.color || '#fbbf24');
    box.style.setProperty('--nf', text.length < 25 ? 15 : text.length < 70 ? 10.5 : text.length < 160 ? 8 : 6.5);
    box.append(h('div', 'note-t', text));
    return {};
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
    box.append(h('div', 'w-title', o.title || 'Command'), out, foot);
    let last = 0, busy = false, res = null;
    const load = async (force) => {
      if (busy) return;
      busy = true;
      try {
        res = await api(`/widget/${encodeURIComponent(page.id)}/${encodeURIComponent(w.id)}${force ? '?force=true' : ''}`, 'GET', 4000);
        const text = res.output ?? '';
        out.textContent = text || (res.running ? 'Running…' : '(no output)');
        const short = text.length <= 14 && !text.includes('\n');
        out.classList.toggle('big', short);
        box.classList.toggle('bad', res.ok === false);
      } catch (_) { if (!res) out.textContent = online ? 'Unavailable' : '—'; }
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
        box.replaceChildren(h('div', 'w-title', o.title || key), h('div', 'push-empty', 'No data yet', h('code', null, `POST /api/push/${key}`)));
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
      const title = h('div', 'w-title', (v.icon ? v.icon + ' ' : '') + (o.title || v.title || key), h('span', 'push-age w-sub', ago(item.at)));
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
    const clear = h('button', 'mini-btn', 'Clear');
    clear.addEventListener('click', () => { haptic(); D.notes = []; store.set('notes', []); emit('notes'); });
    box.append(h('div', 'w-title', 'Notifications', clear), list);
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
        }) : [h('div', 'empty', 'Nothing yet.\nSend one with POST /api/notify')]));
      },
    };
  },
};
