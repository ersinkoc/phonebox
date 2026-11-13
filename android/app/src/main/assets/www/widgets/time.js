import { $, LOCALE, buzz, fmtTime, h, haptic, pad, r1, store, tick, toast, useHour12 } from '../main.js';
import { W } from '../registry.js';
import { dialEl, lines } from './helpers.js';

// ---------------------------------------------------------------- clocks & time
W.analog = {
  bare: true, needs: ['tick'],
  create(box, { o }) {
    const showSec = o.seconds !== false;
    let fmt = null;
    if (o.zone) { try { fmt = new Intl.DateTimeFormat('en-GB', { timeZone: o.zone, hour: 'numeric', minute: 'numeric', second: 'numeric', hourCycle: 'h23' }); } catch (_) {} }
    const ticks = Array.from({ length: 60 }, (_, i) => {
      const big = i % 5 === 0, a = (i * Math.PI) / 30, r1 = big ? 40 : 43.5;
      return `<line class="tk${big ? ' b' : ''}" x1="${(50 + r1 * Math.sin(a)).toFixed(2)}" y1="${(50 - r1 * Math.cos(a)).toFixed(2)}" x2="${(50 + 46.5 * Math.sin(a)).toFixed(2)}" y2="${(50 - 46.5 * Math.cos(a)).toFixed(2)}"/>`;
    }).join('');
    const nums = o.numbers ? [12, 3, 6, 9].map((n, i) => `<text x="${50 + 33 * Math.sin((i * Math.PI) / 2)}" y="${50 - 33 * Math.cos((i * Math.PI) / 2) + 3.6}">${n}</text>`).join('') : '';
    box.innerHTML = `<svg viewBox="0 0 100 100" class="analog"><circle class="face" cx="50" cy="50" r="48.5"/>${ticks}${nums}<text class="ap" x="50" y="70"></text>` +
      '<line class="hh" x1="50" y1="53" x2="50" y2="27"/><line class="mh" x1="50" y1="55" x2="50" y2="12"/>' +
      (showSec ? '<line class="sh" x1="50" y1="60" x2="50" y2="8"/>' : '') + '<circle class="hub" cx="50" cy="50" r="2.4"/></svg>';
    if (o.label) box.append(h('div', 'an-l', o.label));
    const [hh, mh, sh, ap] = ['.hh', '.mh', '.sh', '.ap'].map((s) => box.querySelector(s));
    return {
      update() {
        let H, M, S;
        if (fmt) { const p = Object.fromEntries(fmt.formatToParts(new Date()).map((x) => [x.type, +x.value])); [H, M, S] = [p.hour, p.minute, p.second]; }
        else { const d = new Date(); [H, M, S] = [d.getHours(), d.getMinutes(), d.getSeconds()]; }
        hh.setAttribute('transform', `rotate(${(H % 12) * 30 + M * 0.5} 50 50)`);
        mh.setAttribute('transform', `rotate(${M * 6 + S * 0.1} 50 50)`);
        sh?.setAttribute('transform', `rotate(${S * 6} 50 50)`);
        ap.textContent = useHour12(o.hour12) ? (H >= 12 ? 'PM' : 'AM') : '';
      },
    };
  },
};

W.progress = {
  needs: ['tick'],
  create(box) {
    const rows = ['Day', 'Week', 'Month', 'Year'].map((n) => {
      const pct = h('span'), bar = h('i');
      const row = h('div', 'pr-row', h('div', 'm-row', h('span', null, n), pct), h('div', 'track', bar));
      return { pct, bar, row };
    });
    box.append(h('div', 'w-title', 'Time'), h('div', 'pr-list', rows.map((r) => r.row)));
    let shown = -1;
    return {
      update() {
        const now = new Date();
        if (now.getMinutes() === shown) return;
        shown = now.getMinutes();
        const day0 = new Date(now.getFullYear(), now.getMonth(), now.getDate());
        const wk0 = new Date(day0); wk0.setDate(day0.getDate() - ((day0.getDay() + 6) % 7));
        const m0 = new Date(now.getFullYear(), now.getMonth(), 1), m1 = new Date(now.getFullYear(), now.getMonth() + 1, 1);
        const y0 = new Date(now.getFullYear(), 0, 1), y1 = new Date(now.getFullYear() + 1, 0, 1);
        const vals = [(now - day0) / 864e5, (now - wk0) / (7 * 864e5), (now - m0) / (m1 - m0), (now - y0) / (y1 - y0)];
        vals.forEach((v, i) => { rows[i].pct.textContent = (v * 100).toFixed(i === 3 ? 1 : 0) + '%'; rows[i].bar.style.width = v * 100 + '%'; });
      },
    };
  },
};

W.stopwatch = {
  needs: ['fast'],
  create(box, { page, w }) {
    const key = `sw:${page.id}/${w.id}`;
    let st = store.get(key, null) || { start: null, acc: 0, laps: [] };
    const save = () => store.set(key, st);
    const t = h('div', 'sw-t'), laps = h('div', 'sw-laps');
    const a = h('button', 'pb main', '▶'), b = h('button', 'pb', '↺');
    box.append(h('div', 'w-title', 'Stopwatch'), t, h('div', 'pomo-btns', b, a), laps);
    const elapsed = () => st.acc + (st.start ? Date.now() - st.start : 0);
    const fmtSw = (ms) => { const s = Math.floor(ms / 1000); const hh = Math.floor(s / 3600); return (hh ? hh + ':' + pad(Math.floor((s % 3600) / 60)) : Math.floor(s / 60)) + ':' + pad(s % 60) + '.' + Math.floor((ms % 1000) / 100); };
    const renderLaps = () => laps.replaceChildren(...st.laps.slice(-6).reverse().map((l, i, arr) => h('div', 'sw-lap', h('span', null, '#' + (st.laps.length - i)), h('span', null, fmtSw(l)))));
    const paint = () => { a.textContent = st.start ? '⏸' : '▶'; b.textContent = st.start ? '⚑' : '↺'; box.classList.toggle('run', !!st.start); };
    a.addEventListener('click', () => { haptic(); if (st.start) { st.acc = elapsed(); st.start = null; } else st.start = Date.now(); save(); paint(); });
    b.addEventListener('click', () => { haptic(); if (st.start) st.laps.push(elapsed()); else st = { start: null, acc: 0, laps: [] }; save(); paint(); renderLaps(); });
    paint(); renderLaps();
    return { update() { t.textContent = fmtSw(elapsed()); } };
  },
};

W.timer = {
  needs: ['tick'],
  always: true, // the alarm goes off on whatever page is showing
  create(box, { o, page, w }) {
    const key = `tm:${page.id}/${w.id}`;
    const presets = lines(o.presets, ['1', '3', '5', '10', '15', '25']).map(Number).filter((n) => n > 0).slice(0, 8);
    let st = store.get(key, null) || { endsAt: null, left: 0, total: 0 };
    const save = () => store.set(key, st);
    const dial = dialEl(), btns = h('div', 'tm-btns');
    box.append(h('div', 'w-title', o.label || 'Timer'), dial, btns);
    box.style.setProperty('--q', o.color || '#38bdf8');
    const remaining = () => (st.endsAt ? Math.max(0, st.endsAt - Date.now()) : st.left);
    const renderBtns = () => {
      if (!st.total) {
        btns.replaceChildren(...presets.map((m) => { const b = h('button', 'chip-btn', m + 'm'); b.addEventListener('click', () => { haptic(); st = { endsAt: Date.now() + m * 60000, left: m * 60000, total: m * 60000 }; save(); renderBtns(); render(); }); return b; }));
      } else {
        const p = h('button', 'pb main', st.endsAt ? '⏸' : '▶'), x = h('button', 'pb', '✕');
        p.addEventListener('click', () => { haptic(); if (st.endsAt) { st.left = remaining(); st.endsAt = null; } else st.endsAt = Date.now() + st.left; save(); renderBtns(); });
        x.addEventListener('click', () => { haptic(); st = { endsAt: null, left: 0, total: 0 }; save(); renderBtns(); render(); });
        btns.replaceChildren(x, p);
      }
    };
    const render = () => {
      if (st.endsAt && remaining() <= 0) {
        st = { endsAt: null, left: 0, total: 0 }; save(); renderBtns();
        buzz('0,300,150,300,150,600');
        toast('Time is up', 'ok', '⏳ ' + (o.label || 'Timer'), 6);
      }
      const left = remaining();
      dial.set(st.total ? 100 * (left / st.total) : 0, st.total ? fmtTime(left + 999) : '—');
      box.classList.toggle('run', !!st.endsAt);
    };
    renderBtns();
    return { update: render };
  },
};

W.counter = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    const key = `ct:${page.id}/${w.id}`;
    let st = store.get(key, null) || { n: 0, day: new Date().toDateString() };
    const step = Number(o.step) || 1;
    const num = h('div', 'ct-n'), minus = h('button', 'pb', '−'), plus = h('button', 'pb main', '+'), reset = h('button', 'mini-btn', 'Reset');
    box.style.setProperty('--q', o.color || '#a78bfa');
    box.append(h('div', 'w-title', o.label || 'Counter', reset), num, h('div', 'pomo-btns', minus, plus));
    // "Start from zero every day" also when the panel stays open over midnight (it is never rebuilt then)
    const fresh = () => { const today = new Date().toDateString(); if (o.daily !== false && st.day !== today) { st = { n: 0, day: today }; num.textContent = st.n; } };
    const set = (n) => { st.n = n; st.day = new Date().toDateString(); store.set(key, st); num.textContent = st.n; };
    plus.addEventListener('click', () => { haptic(); fresh(); set(st.n + step); });
    minus.addEventListener('click', () => { haptic(); fresh(); set(st.n - step); });
    reset.addEventListener('click', () => { haptic(); set(0); });
    fresh();
    num.textContent = st.n;
    return { update: fresh };
  },
};

W.breathe = {
  needs: ['tick'],
  create(box, { o }) {
    const PATTERNS = { box: [['Inhale', 4], ['Hold', 4], ['Exhale', 4], ['Hold', 4]], relax: [['Inhale', 4], ['Hold', 7], ['Exhale', 8]], calm: [['Inhale', 5], ['Exhale', 5]] };
    const phases = PATTERNS[o.pattern] || PATTERNS.box;
    const cycle = phases.reduce((a, p) => a + p[1], 0);
    const ball = h('div', 'br-ball'), word = h('div', 'br-w', 'Tap to start'), count = h('div', 'br-c');
    box.append(h('div', 'br-stage', ball, h('div', 'br-txt', word, count)));
    let start = null, lastPhase = -1;
    box.addEventListener('click', () => { haptic(); start = start ? null : Date.now(); lastPhase = -1; if (!start) { ball.style.transition = 'transform 1s ease'; ball.style.transform = 'scale(.45)'; word.textContent = 'Tap to start'; count.textContent = ''; } });
    ball.style.transform = 'scale(.45)';
    return {
      update() {
        if (!start) return;
        let t = ((Date.now() - start) / 1000) % cycle, i = 0;
        while (t >= phases[i][1]) { t -= phases[i][1]; i++; }
        const [name, secs] = phases[i];
        if (i !== lastPhase) {
          lastPhase = i;
          ball.style.transition = `transform ${secs}s ease-in-out`;
          if (name === 'Inhale') ball.style.transform = 'scale(1)';
          if (name === 'Exhale') ball.style.transform = 'scale(.45)';
          word.textContent = name;
        }
        count.textContent = Math.ceil(secs - t);
      },
    };
  },
};

export const QUOTES = [
  ['The best way out is always through.', 'Robert Frost'],
  ['Simplicity is the ultimate sophistication.', 'Leonardo da Vinci'],
  ['Well begun is half done.', 'Aristotle'],
  ['Premature optimization is the root of all evil.', 'Donald Knuth'],
  ['Talk is cheap. Show me the code.', 'Linus Torvalds'],
  ['First, solve the problem. Then, write the code.', 'John Johnson'],
  ['Programs must be written for people to read.', 'Harold Abelson'],
  ['Make it work, make it right, make it fast.', 'Kent Beck'],
  ['Deleted code is debugged code.', 'Jeff Sickel'],
  ['Waste no more time arguing what a good man should be. Be one.', 'Marcus Aurelius'],
  ['What stands in the way becomes the way.', 'Marcus Aurelius'],
  ['We suffer more often in imagination than in reality.', 'Seneca'],
  ['Luck is what happens when preparation meets opportunity.', 'Seneca'],
  ['Do what you can, with what you have, where you are.', 'Theodore Roosevelt'],
  ['Nothing in life is to be feared, it is only to be understood.', 'Marie Curie'],
  ['Imagination is more important than knowledge.', 'Albert Einstein'],
  ['Knowledge is power.', 'Francis Bacon'],
  ['Measure twice, cut once.', 'Proverb'],
  ['Rome wasn’t built in a day.', 'Proverb'],
  ['Drop by drop, a lake is formed.', 'Turkish proverb'],
  ['Patience is bitter, but its fruit is sweet.', 'Proverb'],
  ['A journey of a thousand miles begins with a single step.', 'Laozi'],
];

W.quote = {
  needs: ['tick'],
  create(box, { o }) {
    const custom = lines(o.quotes, []).map((l) => { const i = l.lastIndexOf(' — '); return i > 0 ? [l.slice(0, i), l.slice(i + 3)] : [l, '']; });
    const list = custom.length ? custom : QUOTES;
    const mins = Math.max(1, Number(o.minutes) || 30);
    const q = h('div', 'qt-q'), a = h('div', 'qt-a');
    box.append(h('div', 'qt-mark', '“'), q, a);
    let offset = 0, shown = -1;
    const pick = () => {
      const idx = (Math.floor(Date.now() / (mins * 60000)) + offset) % list.length;
      if (idx === shown) return;
      shown = idx;
      q.textContent = list[idx][0];
      a.textContent = list[idx][1] ? '— ' + list[idx][1] : '';
      box.style.setProperty('--qf', list[idx][0].length < 40 ? 1.25 : list[idx][0].length < 80 ? 1 : 0.8);
    };
    box.addEventListener('click', () => { haptic(); offset++; pick(); });
    return { update: pick };
  },
};

W.checklist = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    const items = lines(o.items, ['Water the plants', 'Stretch', 'Inbox zero']);
    const key = `cl:${page.id}/${w.id}`;
    const day = () => new Date().toDateString();
    let st = store.get(key, null) || { day: day(), done: [] };
    const fresh = () => { if (o.daily !== false && st.day !== day()) st = { day: day(), done: [] }; };
    const head = h('span', 'w-sub');
    const list = h('div', 'cl-list');
    box.append(h('div', 'w-title', o.title || 'Today', head), list);
    const render = () => {
      fresh();
      const n = items.filter((x) => st.done.includes(x)).length;
      head.textContent = `${n} / ${items.length}`;
      box.classList.toggle('all', n === items.length && n > 0);
      list.replaceChildren(...items.map((it) => {
        const done = st.done.includes(it);
        const row = h('button', 'cl-i' + (done ? ' done' : ''), h('span', 'cl-box', done ? '✓' : ''), h('span', 'cl-t', it));
        row.addEventListener('click', () => {
          haptic();
          st.done = done ? st.done.filter((x) => x !== it) : [...st.done, it];
          st.day = day();
          store.set(key, st);
          render();
        });
        return row;
      }));
    };
    let lastDay = day();
    render();
    return { update() { if (day() !== lastDay) { lastDay = day(); render(); } } };
  },
};

// The time in words, to the nearest five minutes ("quarter past ten").
const NUM_WORDS = ['twelve', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten', 'eleven'];
export function timeInWords(d) {
  const total = d.getHours() * 60 + d.getMinutes();
  const r = Math.round(total / 5) * 5;
  const exact = r === total, over = total > r;
  const m = r % 60, hr = Math.floor(r / 60) % 24;
  const hourName = (x) => NUM_WORDS[x % 12];
  const minutes = { 5: 'five', 10: 'ten', 15: 'quarter', 20: 'twenty', 25: 'twenty-five', 30: 'half' };
  let main;
  if (m === 0) main = hr === 0 ? ['midnight'] : hr === 12 ? ['noon'] : [hourName(hr), "o'clock"];
  else if (m <= 30) main = [minutes[m], 'past', hourName(hr)];
  else main = [minutes[60 - m], 'to', hourName(hr + 1)];
  return { lead: exact ? "it's" : over ? "it's just after" : "it's almost", main };
}
W.wordclock = {
  bare: true, needs: ['tick'],
  create(box) {
    const lead = h('div', 'wc-lead'), main = h('div', 'wc-main'), date = h('div', 'wc-date');
    box.append(lead, main, date);
    let shown = '';
    return {
      update() {
        const now = new Date();
        const t = timeInWords(now);
        const k = t.lead + t.main.join(' ');
        if (k === shown) return;
        shown = k;
        lead.textContent = t.lead;
        main.replaceChildren(...t.main.map((w, i) => h('span', i === t.main.length - 1 || t.main.length === 1 ? 'hr' : null, w + ' ')));
        date.textContent = now.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' });
      },
    };
  },
};
