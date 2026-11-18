import { D, DEFAULT_CFG, I18n, cfg, fmtDur, fmtHM, h, pad, safeFetch, store, t } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// ---------------------------------------------------------------- zmanim (Hebcal) & work clock
// Zmanim, the Hebrew date and the Shabbat times come from Hebcal (https://www.hebcal.com/home/developer-apis) for the
// weather location: one place is chosen in Settings › General › Weather and the forecast, the air quality and these
// times all follow it. The names of the zmanim are names, not interface text: Hebrew, or Hebcal's transliteration.
const ZMANIM = [
  { k: 'alotHaShachar', he: 'עלות השחר', en: 'Alot HaShachar', full: true },
  { k: 'misheyakir', he: 'משיכיר', en: 'Misheyakir', full: true },
  { k: 'sunrise', he: 'הנץ החמה', en: 'Netz (sunrise)' },
  { k: 'sofZmanShmaMGA', he: 'סוף זמן ק״ש (מג״א)', en: 'Sof Zman Shma (MGA)', full: true },
  { k: 'sofZmanShma', he: 'סוף זמן ק״ש (גר״א)', en: 'Sof Zman Shma (GRA)' },
  { k: 'sofZmanTfilla', he: 'סוף זמן תפילה', en: 'Sof Zman Tfilla' },
  { k: 'chatzot', he: 'חצות היום', en: 'Chatzot' },
  { k: 'minchaGedola', he: 'מנחה גדולה', en: 'Mincha Gedola' },
  { k: 'minchaKetana', he: 'מנחה קטנה', en: 'Mincha Ketana', full: true },
  { k: 'plagHaMincha', he: 'פלג המנחה', en: 'Plag HaMincha' },
  { k: 'sunset', he: 'שקיעה', en: 'Shkia (sunset)' },
  { k: 'tzeit7083deg', he: 'צאת הכוכבים', en: 'Tzeit HaKochavim' },
  { k: 'tzeit72min', he: 'צאת הכוכבים (ר״ת)', en: 'Tzeit (Rabbeinu Tam)', full: true },
];
const SHABBAT = { candles: ['🕯️', 'הדלקת נרות'], havdalah: ['✨', 'הבדלה'] };

const ymd = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const dayAfter = (d) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + 1);
/** Israel keeps one time zone; anywhere else the times are asked for in the phone's own zone. */
function zoneFor(lat, lon) {
  if (lat >= 29.4 && lat <= 33.4 && lon >= 34.2 && lon <= 35.95) return 'Asia/Jerusalem';
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch (_) { return 'UTC'; }
}

async function getJson(url) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), 10_000);
  try {
    const r = await safeFetch(url, { signal: ctl.signal });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return await r.json();
  } finally { clearTimeout(timer); }
}

// One stored answer per kind, replaced when the day or the place changes: a key per day would fill localStorage.
const inflight = {};
function daily(slot, key, load) {
  const c = store.get(slot, null);
  if (c && c.key === key) return Promise.resolve(c);
  const id = slot + '|' + key;
  inflight[id] ??= load().then((data) => { const fresh = { key, ...data }; store.set(slot, fresh); return fresh; })
    .finally(() => { delete inflight[id]; });
  return inflight[id];
}

function loadZmanim(lat, lon) {
  const today = new Date();
  const base = `https://www.hebcal.com/zmanim?cfg=json&latitude=${lat}&longitude=${lon}&tzid=${encodeURIComponent(zoneFor(lat, lon))}&date=`;
  return daily('il:zm', `${lat},${lon}:${ymd(today)}`, async () => {
    // tomorrow's times give the next zman once the last one of today has passed; today's alone are enough to show
    const [a, b] = await Promise.all([getJson(base + ymd(today)), getJson(base + ymd(dayAfter(today))).catch(() => null)]);
    if (!a || !a.times) throw new Error('no zmanim');
    return { today: a.times, tomorrow: (b && b.times) || {} };
  });
}

function loadShabbat(lat, lon, candles) {
  const url = `https://www.hebcal.com/shabbat?cfg=json&latitude=${lat}&longitude=${lon}&tzid=${encodeURIComponent(zoneFor(lat, lon))}&M=on&b=${candles}`;
  return daily('il:sh', `${lat},${lon}:${candles}:${ymd(new Date())}`, async () => {
    const d = await getJson(url);
    if (!d || !Array.isArray(d.items)) throw new Error('no shabbat times');
    return { items: d.items.map((x) => ({ title: x.title, hebrew: x.hebrew, date: x.date, category: x.category })) };
  });
}

/** 28 → כ״ח, 5787 → תשפ״ז (the thousands are left out, as they are in writing). */
export function gematria(n) {
  n = Math.floor(n) % 1000;
  let s = '';
  for (const [v, c] of [[400, 'ת'], [300, 'ש'], [200, 'ר'], [100, 'ק']]) while (n >= v) { s += c; n -= v; }
  if (n === 15 || n === 16) { s += 'ט' + (n === 15 ? 'ו' : 'ז'); n = 0; }
  const tens = ['', 'י', 'כ', 'ל', 'מ', 'נ', 'ס', 'ע', 'פ', 'צ'], ones = ['', 'א', 'ב', 'ג', 'ד', 'ה', 'ו', 'ז', 'ח', 'ט'];
  s += tens[Math.floor(n / 10)] + ones[n % 10];
  return s.length > 1 ? s.slice(0, -1) + '״' + s.slice(-1) : s + '׳';
}

/** The Hebrew date of a civil day, worked out on the phone: "כ״ח בחשוון תשפ״ז", or "28 Heshvan 5787". */
export function hebrewDate(date, hebrew) {
  try {
    const f = new Intl.DateTimeFormat((hebrew ? 'he' : 'en') + '-u-ca-hebrew', { day: 'numeric', month: 'long', year: 'numeric' });
    if (!hebrew) return f.format(date);
    const p = Object.fromEntries(f.formatToParts(date).map((x) => [x.type, x.value]));
    const day = parseInt(p.day, 10), year = parseInt(p.year, 10);
    if (!day || !year || !p.month) return f.format(date);
    return `${gematria(day)} ב${p.month} ${gematria(year)}`;
  } catch (_) { return ''; }
}

W.zmanim = {
  needs: ['tick'],
  create(box, { o }) {
    const hebrew = o.hebrew !== false || (I18n && I18n.getLang() === 'he');
    const lang = hebrew ? 'he' : 'en';
    const candles = Math.max(0, Math.min(60, Math.round(Number(o.candles ?? 18)) || 0));
    box.dir = hebrew ? 'rtl' : 'ltr';
    const date = h('span', 'w-sub');
    const name = h('span', 'zm-nl'), at = h('b', 'zm-nt', '—'), left = h('span', 'zm-ni', t('w.loading'));
    const list = h('div', 'zm-list'), shab = h('div', 'zm-shab');
    shab.hidden = true;
    box.append(h('div', 'w-title', hebrew ? 'זמני היום' : 'Zmanim', date),
      h('div', 'zm-body', h('div', 'zm-next', name, at, left), list), shab);

    let place = '', zm = null, sh = null, failed = false, shown = '', load = null;
    const fail = (message) => {
      name.textContent = ''; at.textContent = '—'; left.textContent = message;
      list.replaceChildren(); shown = '';
    };
    function loader(lat, lon, here) {
      return every(30 * 60_000, async () => {
        try {
          const z = await loadZmanim(lat, lon);
          if (place !== here) return;
          zm = z; failed = false; shown = '';
        } catch (_) {
          if (place === here && !zm) { failed = true; shown = ''; }
          return false;
        }
        if (o.shabbat === false) return;
        try {
          const s = await loadShabbat(lat, lon, candles);
          if (place === here) { sh = s; shown = ''; }
        } catch (_) { /* the zmanim are shown without the Shabbat line */ }
      });
    }

    function paint(now) {
      const all = (times) => ZMANIM.filter((z) => o.full === true || !z.full)
        .map((z) => ({ name: z[lang], at: new Date(times[z.k] ?? NaN) })).filter((z) => !Number.isNaN(z.at.getTime()));
      const today = all(zm.today);
      const next = today.find((z) => z.at > now) || all(zm.tomorrow).find((z) => z.at > now);
      const sunset = new Date(zm.today.sunset ?? NaN);
      // the Hebrew day begins at sunset
      date.textContent = hebrewDate(now > sunset ? dayAfter(now) : now, hebrew);
      name.textContent = next ? next.name : '';
      at.textContent = next ? fmtHM(next.at) : '—';
      left.textContent = next ? t('time.inDur', { dur: fmtDur(next.at - now) }) : '';
      list.replaceChildren(...today.map((z) => {
        const row = h('div', 'zm-i' + (z.at <= now ? ' past' : '') + (z === next ? ' on' : ''), h('span', null, z.name), h('span', null, fmtHM(z.at)));
        return row;
      }));
      // keep the next zman in view when the widget is too short for the whole day
      const on = list.querySelector('.on');
      list.scrollTop = on ? Math.max(0, on.offsetTop - list.offsetTop - on.offsetHeight) : list.scrollHeight;

      const parts = [];
      for (const x of (sh && o.shabbat !== false ? sh.items : [])) {
        if (x.category === 'parashat') parts.push(hebrew ? (x.hebrew || x.title) : x.title);
        else if (SHABBAT[x.category] && new Date(x.date) > now) {
          const [icon, he] = SHABBAT[x.category];
          // Hebcal's title is "Candle lighting: 16:17": the label is kept, the time is drawn in the panel's own format
          parts.push(`${icon} ${hebrew ? he : String(x.title || '').replace(/:\s*\d.*$/, '')} ${fmtHM(new Date(x.date))}`);
        }
      }
      shab.textContent = parts.join(' · ');
      shab.hidden = !parts.length;
    }

    return {
      update() {
        const { lat, lon } = cfg.weather || DEFAULT_CFG.weather;
        const here = `${lat},${lon}`;
        if (place !== here) {
          place = here; zm = null; sh = null; failed = false;
          fail(t('w.loading'));
          date.textContent = ''; shab.hidden = true;
          load = loader(lat, lon, here);
        }
        const now = new Date();
        // a new day needs new times: drop the old ones and ask again at once
        if (zm && !zm.key.endsWith(':' + ymd(now))) { zm = null; sh = null; fail(t('w.loading')); load = loader(lat, lon, here); }
        load();
        if (!zm) { if (failed) fail(t('w.unavailable')); return; }
        const key = Math.floor(now.getTime() / 60_000) + ':' + zm.key + ':' + (sh ? sh.key : '');
        if (key === shown) return;
        shown = key;
        paint(now);
      },
    };
  },
};

// The work clock of a time tracker, pushed to the agent by whatever keeps the hours (a CRM, a script):
//   POST /api/push/workclock  {"running": true, "startedAt": "2026-11-18T09:12:00Z", "client": "…", "project": "…",
//                              "task": "…", "todaySec": 5400, "targetHours": 8}
// startedAt is an ISO time or unix seconds/milliseconds; todaySec is the time already logged today, without the entry
// that is running. The phone counts the running entry itself, so one push per start, stop or switch is enough.
const toMs = (v) => {
  if (v == null || v === '') return null;
  if (typeof v === 'number') return v < 1e12 ? v * 1000 : v;
  const n = Number(v);
  if (Number.isFinite(n)) return n < 1e12 ? n * 1000 : n;
  const d = Date.parse(v);
  return Number.isNaN(d) ? null : d;
};
const num = (v) => { const n = Number(v); return v != null && v !== '' && Number.isFinite(n) ? n : null; };
const hm = (ms) => { const m = Math.floor(Math.max(0, ms) / 60_000); return `${Math.floor(m / 60)}:${pad(m % 60)}`; };
const hms = (ms) => { const s = Math.floor(Math.max(0, ms) / 1000); return `${Math.floor(s / 3600)}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}`; };

W.workclock = {
  needs: ['push', 'tick'],
  create(box, { o }) {
    const key = o.key || 'workclock';
    const state = h('span', 'w-sub'), title = h('div', 'w-title');
    const big = h('div', 'wk-t'), what = h('div', 'wk-p');
    const today = h('span', 'wk-day'), bar = h('i'), track = h('div', 'wk-bar', bar);
    what.dir = 'auto';
    let built = false, shown = '';
    return {
      update() {
        const item = D.push[key];
        if (!item) {
          if (built || !box.firstChild) {
            built = false; shown = '';
            box.classList.remove('run');
            box.removeAttribute('dir');
            box.replaceChildren(h('div', 'w-title', o.title || key), h('div', 'push-empty', t('w.noDataYet'), h('code', null, `POST /api/push/${key}`)));
          }
          return;
        }
        if (!built) {
          built = true;
          box.replaceChildren(title, h('div', 'wk-main', big, what), h('div', 'wk-foot', today, track));
        }
        const d = item.data !== null && typeof item.data === 'object' && !Array.isArray(item.data) ? item.data : {};
        const start = toMs(d.startedAt ?? d.started ?? d.start);
        const flag = d.running;
        const run = start != null && (flag === undefined ? true : flag === true || flag === 1 || flag === 'true');
        const now = Date.now();
        const logged = (num(d.todaySec) ?? num(d.todaySeconds) ?? (num(d.todayMinutes) != null ? num(d.todayMinutes) * 60 : null)
          ?? (num(d.todayHours) != null ? num(d.todayHours) * 3600 : 0)) * 1000;
        const total = logged + (run ? Math.max(0, now - start) : 0);
        const target = (num(d.targetHours) ?? num(o.target) ?? 0) * 3_600_000;

        // the seconds tick every time; the rest is redrawn when the pushed data changes
        big.textContent = run ? hms(now - start) : hm(total);
        today.textContent = `${t('time.today')} ${hm(total)}` + (target > 0 ? ` / ${hm(target)}` : '');
        bar.style.width = target > 0 ? Math.min(100, (total / target) * 100) + '%' : '0';
        const stamp = `${item.at}:${run}`;
        if (stamp === shown) return;
        shown = stamp;
        box.classList.toggle('run', run);
        const names = [d.client, d.project, d.task].map((x) => (x == null ? '' : String(x).trim())).filter(Boolean);
        const head = o.title || d.title || names.shift() || key;
        title.replaceChildren(head, state);
        // a Hebrew client or project name turns the whole card, so the clock lines up with the text beside it
        box.dir = /[\u0590-\u05FF]/.test(head + names.join('')) ? 'rtl' : 'ltr';
        state.textContent = run ? '● ' + fmtHM(new Date(start)) : '⏸';
        what.textContent = names.join(' · ');
        track.hidden = !(target > 0);
      },
    };
  },
};
