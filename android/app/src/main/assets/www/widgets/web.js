import { $, API, I18n, LOCALE, PANEL_HEADERS, ago, api, h, haptic, icon, safeFetch, sizeCanvas, store, t, tick, toast, tp } from '../main.js';
import { W } from '../registry.js';
import { cached, every, fmtNum, lines, pctSpan, sparkLine } from './helpers.js';

// ---------------------------------------------------------------- the internet
W.hackernews = {
  needs: ['tick'],
  create(box) {
    const list = h('div', 'hn-list', h('div', 'empty', t('w.loading')));
    box.style.setProperty('--c', '#ff6600');
    box.append(h('div', 'w-title', 'Hacker News', h('span', 'w-sub', '')), list);
    const load = every(15 * 60_000, async () => {
      try {
        const key = 'hn';
        let data = store.get('nc:' + key, null);
        if (!data || Date.now() - data.at > 15 * 60_000) {
          const ids = (await cached('hn-ids', 'https://hacker-news.firebaseio.com/v0/topstories.json', 15 * 60_000)).slice(0, 15);
          const items = await Promise.all(ids.map((id) => safeFetch(`https://hacker-news.firebaseio.com/v0/item/${id}.json`).then((r) => r.json()).catch(() => null)));
          data = { at: Date.now(), data: items.filter(Boolean).map((i) => ({ t: i.title, u: i.url, s: i.score, c: i.descendants || 0 })) };
          store.set('nc:' + key, data);
        }
        box.querySelector('.w-sub').textContent = ago(data.at);
        const n = Math.max(1, Math.floor((box.clientHeight - 30) / 33));
        list.replaceChildren(...data.data.slice(0, n).map((s, i) => {
          let host = '';
          try { host = s.u ? new URL(s.u).hostname.replace(/^www\./, '') : 'news.ycombinator.com'; } catch (_) {}
          return h('div', 'hn-i', h('span', 'hn-r', i + 1), h('div', 'hn-b', h('div', 'hn-t', s.t), h('div', 'hn-m', `▲ ${s.s} · ${s.c} comments · ${host}`)));
        }));
      } catch (_) { if (!list.querySelector('.hn-i')) list.replaceChildren(h('div', 'empty', t('w.offline'))); return false; }
    });
    return { update: load };
  },
};

// Headlines from the RSS / Atom feeds set up on the PC. The agent fetches them (at most every 15 minutes); the
// phone gets titles, the feed's name and times, and opens an article on the PC by its id. The list scrolls, and an
// article opened from this phone loses its accent bar (the ids it opened are kept on the phone).
const FEEDS_READ = 'feeds-read', FEEDS_READ_MAX = 500, FEEDS_SHOWN = 60;
const feedsRead = () => new Set(store.get(FEEDS_READ, []));
const markFeedRead = (id) => {
  const ids = store.get(FEEDS_READ, []).filter((x) => x !== id);
  ids.push(id);
  store.set(FEEDS_READ, ids.slice(-FEEDS_READ_MAX));
};
W.feeds = {
  needs: ['tick'],
  create(box, { o }) {
    const only = String(o.feed || '').trim().toLowerCase();
    const onlyCat = String(o.category || '').trim().toLowerCase();
    const showSource = o.showSource !== false, showTime = o.showTime !== false;
    const lineCount = ['2', '3'].includes(String(o.lines)) ? +o.lines : 1;
    const list = h('div', 'fd-list', h('div', 'empty', t('w.loading')));
    if (lineCount > 1) { list.classList.add('wrap'); list.style.setProperty('--fl', lineCount); }
    const sub = h('span', 'w-sub', '');
    box.style.setProperty('--c', '#f59e0b');
    if (o.title !== '') box.append(h('div', 'w-title', o.title || tp('News'), sub));
    box.append(list);
    let data = null, lastKey = '', busy = null;
    const open = async (it) => {
      if (!it.openable || busy) return;
      // this widget's own option, not just whether some widget allows it (the agent checks the latter)
      if (o.open !== true || !data?.canOpen) { toast(t('w.feeds.openOff'), 'err'); return; }
      haptic();
      busy = it.id;
      // one deadline for the request and its body, so a stalled answer can't leave the widget refusing taps
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 8000);
      try {
        const r = await fetch(`${API}/feeds/open?id=${encodeURIComponent(it.id)}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
        const j = await r.json().catch(() => ({}));
        if (r.ok) { toast(t('w.feeds.opening'), 'ok'); markFeedRead(it.id); lastKey = ''; render(); }
        else toast(j.error || t('w.feeds.openFailed'), 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      finally { clearTimeout(timer); busy = null; }
    };
    const render = () => {
      const items = (data?.items || []).filter((i) => {
        if (only && i.source.toLowerCase() !== only) return false;
        if (onlyCat && (i.category || '').toLowerCase() !== onlyCat) return false;
        return true;
      }).slice(0, FEEDS_SHOWN);
      const canOpen = o.open === true && !!data?.canOpen;
      const read = feedsRead();
      const key = JSON.stringify([!!data, canOpen, items.map((i) => i.id + (read.has(i.id) ? '.' : '')), data?.errors, Math.floor(Date.now() / 60_000)]);
      if (key === lastKey) return;
      lastKey = key;
      const err = (data?.errors || []).filter((e) => !only || e.toLowerCase().startsWith(only + ':'));
      sub.replaceChildren(...(err.length ? [icon('warn')] : []));
      sub.title = err.join('\n');
      if (!data) return;
      if (!items.length) {
        const filterName = o.category && o.feed ? `${o.category} · ${o.feed}` : (o.category || o.feed);
        list.replaceChildren(h('div', 'empty', err.length ? err[0] : data.updated && (data.items || []).length ? t('w.feeds.noneFrom', { name: filterName }) : t('w.feeds.addInSettings')));
        return;
      }
      // the list is rebuilt every minute (the ages): keep where it was scrolled to
      const top = list.scrollTop;
      list.replaceChildren(...items.map((it) => {
        const meta = [showSource ? it.source : '', showTime && it.published ? ago(it.published) : ''].filter(Boolean).join(' · ');
        const link = it.openable && canOpen;
        const row = h('div', 'fd-i' + (link ? ' link' + (read.has(it.id) ? ' read' : '') : ''), h('div', 'fd-t', it.title), meta ? h('div', 'fd-m', meta) : null);
        row.onclick = () => open(it);
        return row;
      }));
      list.scrollTop = top;
    };
    const load = every(60_000, async () => {
      try { data = await api('/feeds', 'GET', 5000); lastKey = ''; render(); }
      catch (_) { if (!data) list.replaceChildren(h('div', 'empty', t('w.offline'))); return false; }
    });
    return { update() { load(); render(); } };
  },
};

W.exchange = {
  needs: ['tick'],
  create(box, { o }) {
    const base = (o.base || 'EUR').toUpperCase();
    const syms = lines(o.symbols, ['TRY', 'USD', 'GBP']).map((s) => s.toUpperCase()).slice(0, 8);
    const list = h('div', 'fx-list', h('div', 'empty', t('w.loading')));
    box.append(h('div', 'w-title', `1 ${base}`, h('span', 'w-sub', '')), list);
    const load = every(60 * 60_000, async () => {
      try {
        const from = new Date(Date.now() - 31 * 864e5).toISOString().slice(0, 10);
        const d = await cached(`fx:${base}:${syms}`, `https://api.frankfurter.dev/v1/${from}..?base=${base}&symbols=${syms.join(',')}`, 60 * 60_000);
        const days = Object.keys(d.rates || {}).sort();
        if (!days.length) {
          box.querySelector('.w-sub').textContent = '';
          list.replaceChildren(h('div', 'empty', t('w.ratesUnavailable')));
          return false;
        }
        box.querySelector('.w-sub').textContent = tp('ECB · ') + days[days.length - 1];
        list.replaceChildren(...syms.map((s) => {
          const series = days.map((k) => d.rates[k]?.[s]).filter((v) => Number.isFinite(v) && v > 0);
          const last = series[series.length - 1], prev = series[series.length - 2];
          const change = prev === undefined ? h('span', 'chg', '—') : pctSpan(((last - prev) / prev) * 100);
          const c = h('canvas', 'fx-sp');
          const row = h('div', 'fx-i', h('b', null, s), h('span', 'fx-v', fmtNum(last)), change, c);
          requestAnimationFrame(() => sparkLine(c, series, last >= series[0] ? '#22c55e' : '#ef4444'));
          return row;
        }));
      } catch (_) { list.replaceChildren(h('div', 'empty', t('w.ratesUnavailable'))); return false; }
    });
    return { update: load };
  },
};

W.crypto = {
  needs: ['tick'],
  create(box, { o }) {
    const coins = lines(o.coins, ['bitcoin', 'ethereum', 'solana']).map((s) => s.toLowerCase()).slice(0, 8);
    const cur = (o.currency || 'usd').toLowerCase();
    const list = h('div', 'fx-list', h('div', 'empty', t('w.loading')));
    box.append(h('div', 'w-title', tp('Crypto'), h('span', 'w-sub', cur.toUpperCase() + ' · 24 h · 7 d')), list);
    const load = every(5 * 60_000, async () => {
      try {
        const d = await cached(`cg:${cur}:${coins}`, `https://api.coingecko.com/api/v3/coins/markets?vs_currency=${cur}&ids=${coins.join(',')}&sparkline=true&price_change_percentage=24h`, 5 * 60_000);
        list.replaceChildren(...d.map((c) => {
          const cv = h('canvas', 'fx-sp');
          const series = c.sparkline_in_7d?.price || [];
          requestAnimationFrame(() => sparkLine(cv, series, (c.price_change_percentage_24h ?? 0) >= 0 ? '#22c55e' : '#ef4444'));
          return h('div', 'fx-i', h('b', null, (c.symbol || '').toUpperCase()), h('span', 'fx-v', fmtNum(c.current_price)), pctSpan(c.price_change_percentage_24h ?? 0), cv);
        }));
      } catch (_) { list.replaceChildren(h('div', 'empty', t('w.pricesUnavailable'))); return false; }
    });
    return { update: load };
  },
};

const stockText = source => I18n?.tc(source) ?? source;
const quotePercent = n => (n > 0 ? '+' : '') + (n || 0).toFixed(2) + '%';

W.stocks = {
  needs: ['tick'],
  create(box, { o }) {
    const greenUp = o.colours === 'green-up';
    const upColor = greenUp ? '#22c55e' : '#ef4444';
    const downColor = greenUp ? '#ef4444' : '#22c55e';
    const quoteColor = pct => pct > 0 ? upColor : pct < 0 ? downColor : 'var(--muted)';
    const staleText = stockText('Some data may be stale');
    const chartMode = (o.mode || 'list') === 'chart';
    // ---- chart mode: rows x cols intraday charts on one screen ----
    if (chartMode) {
      // "2*4" = 2 rows, 4 columns (8 charts); legacy "chartCount" = total per screen
      const m = /^\s*(\d+)\s*[*x×]\s*(\d+)\s*$/i.exec(String(o.chartGrid || ''));
      let rows, cols;
      if (m) { rows = Math.min(5, Math.max(1, +m[1])); cols = Math.min(5, Math.max(1, +m[2])); }
      else {
        cols = Math.max(1, Math.min(4, Number(o.chartCount) || 2));
        rows = 1;
      }
      const count = rows * cols;
      const tiles = [];
      let quoteError = false;
      const status = h('span', 'w-sub sc-status');
      status.hidden = true;
      status.setAttribute('aria-live', 'polite');
      const updateStatus = () => {
        status.hidden = !quoteError && !tiles.some(tile => tile.q.err);
        status.textContent = status.hidden ? '' : staleText;
      };
      const grid = h('div', 'sc-grid');
      grid.style.display = 'grid';
      grid.style.gridTemplateColumns = `repeat(${cols}, 1fr)`;
      grid.style.gridTemplateRows = `repeat(${rows}, 1fr)`;
      grid.style.gap = '6px';
      grid.style.height = '100%';
      grid.style.alignContent = 'stretch';
      const drawTile = (tile, q) => {
        const [ctx, w, hh, dpr] = sizeCanvas(tile.canvas);
        const W2 = w, H2 = hh;
        ctx.clearRect(0, 0, W2, H2);
        // frame: the day's open as baseline; prices scale to the data with padding
        const pts = q.points || [];
        if (pts.length < 2) {
          // visible reason instead of a blank tile
          ctx.fillStyle = '#888';
          ctx.font = '11px sans-serif';
          ctx.textAlign = 'center';
          ctx.fillText(q.err ? t('w.offline') : pts.length ? t('w.ratesUnavailable') : t('w.loading'), W2 / 2, H2 / 2);
          return;
        }
        const prices = pts.map((p) => p[1]);
        const open = prices[0];
        // prevClose is the 0% baseline; the scale must include it and the last price
        const prev = q.prevClose > 0 ? q.prevClose : open;
        const last = prices[prices.length - 1];
        const lo = Math.min(...prices, prev), hi = Math.max(...prices, prev);
        const span = (hi - lo) || Math.abs(hi) * 0.001 || 1;
        const pad = H2 * 0.10;
        const up = last >= prev;
        const col = up ? upColor : downColor;
        // grid lines (3 horizontal)
        ctx.strokeStyle = 'rgba(128,128,128,0.18)';
        ctx.lineWidth = 1;
        for (let i = 1; i < 3; i++) {
          const y = pad + ((H2 - pad * 2) * i) / 3;
          ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W2, y); ctx.stroke();
        }
        // 0% baseline (yesterday's close) dashed
        const oy = pad + (H2 - pad * 2) * (1 - (prev - lo) / span);
        ctx.setLineDash([3, 3]);
        ctx.strokeStyle = 'rgba(128,128,128,0.45)';
        ctx.beginPath(); ctx.moveTo(0, oy); ctx.lineTo(W2, oy); ctx.stroke();
        ctx.setLineDash([]);
        // x mapping: by clock time on the market's full-day axis, so a
        // half-day chart only fills the left part (like 同花顺). Codes:
        // sh/sz = A-shares (09:15 auction .. 15:30), hk = 09:30..16:00,
        // anything else falls back to index-based spread.
        const t0 = String(pts[0][0] || '');
        let total = 0, elapsed = (i) => i;
        if (t0.length === 4 && /^\d{4}$/.test(t0)) {
          const mins = (s) => ((+s.slice(0, 2)) * 60 + (+s.slice(2)));
          if (/^(sh|sz)/.test(String(q.code || ''))) {
            const start = mins('0915');               // auction included
            total = mins('1530') - start;             // to 15:30 after-hours
            elapsed = (i) => mins(pts[i][0]) - start;
          } else if (/^hk/.test(String(q.code || ''))) {
            const start = mins('0930');
            total = mins('1600') - start;
            elapsed = (i) => mins(pts[i][0]) - start;
          }
        }
        const n = pts.length;
        const x = (i) => (total > 0
          ? (elapsed(i) / total) * W2
          : (i / (n - 1)) * W2);
        const y = (p) => pad + (H2 - pad * 2) * (1 - (p - lo) / span);
        ctx.strokeStyle = col;
        ctx.lineWidth = Math.max(1.5, dpr > 1 ? 1.5 : 1);
        ctx.beginPath();
        pts.forEach(([t, p], i) => {
          const px = x(i), py = y(p);
          if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
        });
        ctx.stroke();
        // fill between the line and the 0% baseline (prevClose), gradient fading down
        ctx.lineTo(x(n - 1), y(prev));
        ctx.lineTo(x(0), y(prev));
        ctx.closePath();
        const g = ctx.createLinearGradient(0, pad, 0, H2);
        const c = (up !== greenUp) ? '239,68,68' : '34,197,94';
        g.addColorStop(0, `rgba(${c},0.22)`);
        g.addColorStop(1, `rgba(${c},0.03)`);
        ctx.fillStyle = g;
        ctx.fill();
        // last price label (top-right; the name lives in the tile header, not here)
        ctx.fillStyle = col;
        ctx.font = `${Math.max(10, Math.round(H2 * 0.12))}px sans-serif`;
        ctx.textAlign = 'right';
        ctx.fillText(String(Math.round(last * 100) / 100), W2 - 2, pad - 1);
      };
      box.append(status, grid);
      let lastKey = '';
      const load = every(60_000, async () => {
        try {
          const d = await api('/stocks', 'GET', 6000);
          const quotes = (d.quotes || []).slice(0, count);
          quoteError = Boolean(d.error && quotes.length);
          if (!quotes.length) {
            lastKey = '';
            tiles.length = 0;
            updateStatus();
            grid.replaceChildren(h('div', 'empty', t('w.ratesUnavailable')));
            return false;
          }
          const key = quotes.map(q => q.code).join(',');
          if (key === lastKey) {
            quotes.forEach((q, i) => {
              const tile = tiles[i];
              tile.q = { ...tile.q, ...q };
              tile.pct.textContent = quotePercent(q.changePct);
              tile.pct.style.color = quoteColor(q.changePct);
            });
            updateStatus();
            loadMinutes();
            return;
          }
          lastKey = key;
          tiles.length = 0;
          grid.replaceChildren(...quotes.map((q) => {
            const cell = h('div', 'sc-tile');
            cell.style.cssText = 'display:flex;flex-direction:column;min-height:0;border:1px solid var(--border,#333);border-radius:8px;overflow:hidden;background:var(--card-bg,transparent)';
            const head = h('div', 'sc-h');
            head.style.cssText = 'display:flex;justify-content:space-between;padding:4px 6px 0;font-size:11px;white-space:nowrap;overflow:hidden';
            const canvas = h('canvas');
            canvas.style.cssText = 'flex:1;width:100%;height:100%;min-height:0;display:block';
            const name = h('b', 'sc-name', q.name || q.code);
            name.style.cssText = 'overflow:hidden;text-overflow:ellipsis';
            const pct = h('span', 'sc-pct', quotePercent(q.changePct));
            pct.style.color = quoteColor(q.changePct);
            head.append(name, pct);
            cell.append(head, canvas);
            tiles.push({ canvas, pct, q });
            return cell;
          }));
          updateStatus();
          loadMinutes();
        } catch (_) {
          quoteError = Boolean(lastKey);
          updateStatus();
          if (!lastKey) grid.replaceChildren(h('div', 'empty', t('w.offline')));
          return false;
        }
      });
      const loadMinutes = () => {
        for (const tile of tiles) {
          api('/stocks/minutes?code=' + encodeURIComponent(tile.q.code), 'GET', 8000)
            .then(m => {
              if (!Array.isArray(m.points)) { tile.q.err = true; return; }
              tile.q = { ...tile.q, points: m.points, prevClose: Number(m.prevClose) || 0, err: Boolean(m.error) };
            })
            .catch(() => { tile.q.err = true; })
            .finally(() => {
              if (tiles.includes(tile)) {
                updateStatus();
                requestAnimationFrame(() => drawTile(tile, tile.q));
              }
            });
        }
      };
      const ro = new ResizeObserver(() => {
        for (const tile of tiles) drawTile(tile, tile.q);
      });
      ro.observe(grid);
      return { update: load, destroy() { ro.disconnect(); tiles.length = 0; } };
    }
    // ---- list mode ----
    const list = h('div', 'fx-list', h('div', 'empty', t('w.loading')));
    const status = h('span', 'w-sub');
    status.setAttribute('aria-live', 'polite');
    box.append(h('div', 'w-title', stockText('Stocks'), status), list);
    const price = new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 8 });
    let last = '';
    const load = every(30_000, async () => {
      try {
        const d = await api('/stocks', 'GET', 6000);
        const quotes = d.quotes || [];
        const updated = d.updated ? new Date(d.updated).toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' }) : '';
        status.textContent = quotes.length ? (d.error ? staleText : updated) : '';
        if (!quotes.length) { last = ''; list.replaceChildren(h('div', 'empty', t('w.ratesUnavailable'))); return false; }
        const key = JSON.stringify(quotes.map((q) => [q.code, q.price, q.changePct]));
        if (key === last) return;
        last = key;
        list.replaceChildren(...quotes.map((q) => {
          // up = red, down = green, flat = dim (A-share colours); the whole row takes the colour
          const col = quoteColor(q.changePct);
          const name = h('b', null, q.name || q.code);
          name.style.cssText = 'white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
          const pct = h('span', null, quotePercent(q.changePct));
          pct.style.cssText = 'font-weight:600;min-width:56px;text-align:right';
          const row = h('div', 'fx-i', name, h('span', 'fx-v', price.format(q.price)), pct,
            h('span', 'fx-c', (q.change > 0 ? '+' : '') + (q.change || 0).toFixed(2)));
          row.style.color = col;
          return row;
        }));
      } catch (_) {
        status.textContent = last ? staleText : '';
        if (!last) list.replaceChildren(h('div', 'empty', t('w.offline')));
        return false;
      }
    });
    return { update: load };
  },
};

// Guba (East Money stock forum): latest 10 posts for the stock configured
// on the computer; the agent fetches guba.eastmoney.com and answers /agent/guba.
W.guba = {
  needs: ['tick'],
  create(box, { o }) {
    const code = String(o.code || '002317').trim();
    if (!/^[0-9]{6}$/.test(code)) { box.append(h('div', 'empty', t('w.ratesUnavailable'))); return {}; }
    const count = Math.max(1, Math.min(15, Number(o.count) || 10));
    const list = h('div', 'gb-list', h('div', 'empty', t('w.loading')));
    list.style.cssText = 'flex:1;min-height:0;display:flex;flex-direction:column;justify-content:space-evenly;gap:2px;overflow:hidden';
    box.append(h('div', 'w-title', (I18n?.tc('Stock forum') ?? 'Stock forum') + ' · ' + code, h('span', 'w-sub', '')), list);
    let last = '';
    const load = every(300_000, async () => {
      try {
        const d = await api('/guba?code=' + code + '&count=' + count, 'GET', 8000);
        const posts = d.posts || [];
        if (!posts.length) { last = ''; list.replaceChildren(h('div', 'empty', t('w.offline'))); return false; }
        const updated = d.updated ? new Date(d.updated).toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' }) : '';
        box.querySelector('.w-sub').textContent = d.error === 'stale' ? [updated, t('w.offline')].filter(Boolean).join(' · ') : updated;
        const key = JSON.stringify(posts);
        if (key === last) return;
        last = key;
        list.replaceChildren(...posts.map((p) => {
          const row = h('div', 'gb-i');
          row.style.cssText = 'display:flex;flex-direction:column;min-height:0;overflow:hidden';
          // one summary line: title (whole row clickable in a real browser; here just text)
          const title = h('div', 'gb-t', String(p.title || '').slice(0, 40));
          title.style.cssText = 'font-size:12px;line-height:1.25;overflow:hidden;text-overflow:ellipsis;white-space:nowrap';
          const meta = h('div', 'gb-m');
          meta.style.cssText = 'font-size:10px;color:var(--muted);display:flex;gap:8px';
          const when = String(p.time || '').slice(5, 16); // "11-23 22:35"
          meta.append(h('span', null, String(p.author || '').slice(0, 10)), h('span', null, when),
            h('span', null, (I18n?.tc('Reads') ?? 'Reads') + ' ' + (p.reads || 0)), h('span', null, (I18n?.tc('Comments') ?? 'Comments') + ' ' + (p.comments || 0)));
          row.append(title, meta);
          return row;
        }));
      } catch (_) { if (!last) list.replaceChildren(h('div', 'empty', t('w.offline'))); return false; }
    });
    return { update: load };
  },
};

W.image = {
  needs: ['tick'],
  create(box, { o }) {
    const url = String(o.url || '');
    const img = h('img', 'im-img');
    img.alt = '';
    img.style.objectFit = o.fit === 'contain' ? 'contain' : 'cover';
    box.append(img);
    if (o.caption) box.append(h('div', 'im-cap', o.caption));
    if (!/^https?:\/\//i.test(url)) { box.replaceChildren(h('div', 'empty', t('w.setImageUrl'))); return {}; }
    img.onerror = () => box.classList.add('err');
    img.onload = () => box.classList.remove('err');
    const mins = Number(o.minutes) || 0;
    const load = every(mins > 0 ? mins * 60_000 : 1e12, () => { img.src = mins > 0 ? url + (url.includes('?') ? '&' : '?') + '_t=' + Date.now() : url; });
    return { update: load };
  },
};
