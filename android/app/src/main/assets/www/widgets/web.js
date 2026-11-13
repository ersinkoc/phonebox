import { $, ago, h, safeFetch, store, tick } from '../main.js';
import { W } from '../registry.js';
import { cached, every, fmtNum, lines, pctSpan, sparkLine } from './helpers.js';

// ---------------------------------------------------------------- the internet
W.hackernews = {
  needs: ['tick'],
  create(box) {
    const list = h('div', 'hn-list', h('div', 'empty', 'Loading…'));
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
      } catch (_) { if (!list.querySelector('.hn-i')) list.replaceChildren(h('div', 'empty', 'Offline')); return false; }
    });
    return { update: load };
  },
};

W.exchange = {
  needs: ['tick'],
  create(box, { o }) {
    const base = (o.base || 'EUR').toUpperCase();
    const syms = lines(o.symbols, ['TRY', 'USD', 'GBP']).map((s) => s.toUpperCase()).slice(0, 8);
    const list = h('div', 'fx-list', h('div', 'empty', 'Loading…'));
    box.append(h('div', 'w-title', `1 ${base}`, h('span', 'w-sub', '')), list);
    const load = every(60 * 60_000, async () => {
      try {
        const from = new Date(Date.now() - 31 * 864e5).toISOString().slice(0, 10);
        const d = await cached(`fx:${base}:${syms}`, `https://api.frankfurter.dev/v1/${from}..?base=${base}&symbols=${syms.join(',')}`, 60 * 60_000);
        const days = Object.keys(d.rates).sort();
        box.querySelector('.w-sub').textContent = 'ECB · ' + days.at(-1);
        list.replaceChildren(...syms.map((s) => {
          const series = days.map((k) => d.rates[k][s]).filter((x) => x != null);
          const last = series.at(-1), prev = series.at(-2) ?? last;
          const c = h('canvas', 'fx-sp');
          const row = h('div', 'fx-i', h('b', null, s), h('span', 'fx-v', fmtNum(last)), pctSpan(((last - prev) / prev) * 100), c);
          requestAnimationFrame(() => sparkLine(c, series, last >= series[0] ? '#22c55e' : '#ef4444'));
          return row;
        }));
      } catch (_) { list.replaceChildren(h('div', 'empty', 'Rates unavailable')); return false; }
    });
    return { update: load };
  },
};

W.crypto = {
  needs: ['tick'],
  create(box, { o }) {
    const coins = lines(o.coins, ['bitcoin', 'ethereum', 'solana']).map((s) => s.toLowerCase()).slice(0, 8);
    const cur = (o.currency || 'usd').toLowerCase();
    const list = h('div', 'fx-list', h('div', 'empty', 'Loading…'));
    box.append(h('div', 'w-title', 'Crypto', h('span', 'w-sub', cur.toUpperCase() + ' · 24 h · 7 d')), list);
    const load = every(5 * 60_000, async () => {
      try {
        const d = await cached(`cg:${cur}:${coins}`, `https://api.coingecko.com/api/v3/coins/markets?vs_currency=${cur}&ids=${coins.join(',')}&sparkline=true&price_change_percentage=24h`, 5 * 60_000);
        list.replaceChildren(...d.map((c) => {
          const cv = h('canvas', 'fx-sp');
          const series = c.sparkline_in_7d?.price || [];
          requestAnimationFrame(() => sparkLine(cv, series, (c.price_change_percentage_24h ?? 0) >= 0 ? '#22c55e' : '#ef4444'));
          return h('div', 'fx-i', h('b', null, (c.symbol || '').toUpperCase()), h('span', 'fx-v', fmtNum(c.current_price)), pctSpan(c.price_change_percentage_24h ?? 0), cv);
        }));
      } catch (_) { list.replaceChildren(h('div', 'empty', 'Prices unavailable')); return false; }
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
    if (!/^https?:\/\//i.test(url)) { box.replaceChildren(h('div', 'empty', 'Set an image address (https://…) in the settings')); return {}; }
    img.onerror = () => box.classList.add('err');
    img.onload = () => box.classList.remove('err');
    const mins = Number(o.minutes) || 0;
    const load = every(mins > 0 ? mins * 60_000 : 1e12, () => { img.src = mins > 0 ? url + (url.includes('?') ? '&' : '?') + '_t=' + Date.now() : url; });
    return { update: load };
  },
};
