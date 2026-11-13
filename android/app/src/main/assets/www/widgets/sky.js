import { $, D, DEFAULT_CFG, LOCALE, cfg, fmtClock, fmtDur, h, tick, useHour12 } from '../main.js';
import { W } from '../registry.js';
import { cached, every } from './helpers.js';

// ---------------------------------------------------------------- sky & air
export function moonPhase(date) {
  const syn = 29.530588853;
  const age = ((((date - Date.UTC(2000, 0, 6, 18, 14)) / 864e5) % syn) + syn) % syn;
  const names = ['New moon', 'Waxing crescent', 'First quarter', 'Waxing gibbous', 'Full moon', 'Waning gibbous', 'Last quarter', 'Waning crescent'];
  const i = Math.floor(((age + syn / 16) % syn) / (syn / 8));
  return { name: names[i], icon: '🌑🌒🌓🌔🌕🌖🌗🌘'.match(/./gu)[i], illum: (1 - Math.cos((2 * Math.PI * age) / syn)) / 2 };
}

W.sun = {
  needs: ['weather', 'tick'],
  create(box, { o }) {
    box.innerHTML = '<div class="w-title">Sun &amp; moon<span class="w-sub"></span></div>' +
      '<svg class="sun-arc" viewBox="0 0 200 80" preserveAspectRatio="xMidYMax meet"><path class="sa-bg" d="M10 75 A90 70 0 0 1 190 75"/><path class="sa-fg" d="M10 75 A90 70 0 0 1 190 75"/><line class="sa-h" x1="2" y1="75" x2="198" y2="75"/><circle class="sa-sun" r="7" cx="10" cy="75"/></svg>' +
      '<div class="sun-row"><span class="sr"></span><span class="sl"></span><span class="ss"></span></div><div class="moon"></div>';
    const q = (s) => box.querySelector(s);
    const fg = q('.sa-fg'), sun = q('.sa-sun');
    const len = fg.getTotalLength();
    fg.style.strokeDasharray = len;
    let shown = '';
    return {
      update() {
        const d = D.weather?.daily;
        if (!d?.sunrise) { q('.sl').textContent = D.weather === null ? 'Loading…' : 'Weather unavailable'; return; }
        const now = new Date();
        const is12 = useHour12(o.hour12);
        const key = `${now.getHours()}:${now.getMinutes()}:${is12}`;
        if (key === shown) return;
        shown = key;
        const rise = new Date(d.sunrise[0]), set = new Date(d.sunset[0]);
        const f = Math.max(0, Math.min(1, (now - rise) / (set - rise)));
        fg.style.strokeDashoffset = len * (1 - f);
        const pt = fg.getPointAtLength(len * f);
        sun.setAttribute('cx', pt.x.toFixed(1)); sun.setAttribute('cy', pt.y.toFixed(1));
        box.classList.toggle('night', now < rise || now > set);
        const hm = (t) => fmtClock(t, { hour12: is12 });
        q('.sr').textContent = '↑ ' + hm(rise);
        q('.ss').textContent = hm(set) + ' ↓';
        q('.sl').textContent = now < rise ? `rises in ${fmtDur(rise - now)}` : now > set ? 'after sunset' : `${fmtDur(set - now)} of light left`;
        q('.w-sub').textContent = 'Day ' + fmtDur((d.daylight_duration?.[0] ?? (set - rise) / 1000) * 1000) + (d.uv_index_max?.[0] != null ? ` · UV ${Math.round(d.uv_index_max[0])}` : '');
        const m = moonPhase(now);
        q('.moon').textContent = `${m.icon} ${m.name} · ${Math.round(m.illum * 100)}%`;
      },
    };
  },
};

export const AQI_BANDS = [[20, 'Good', '#22c55e'], [40, 'Fair', '#84cc16'], [60, 'Moderate', '#eab308'], [80, 'Poor', '#f97316'], [100, 'Very poor', '#ef4444'], [1e9, 'Extremely poor', '#a855f7']];
W.air = {
  needs: ['tick'],
  create(box) {
    const v = h('div', 'aq-v', '—'), cat = h('div', 'aq-c', 'Loading…'), chips = h('div', 'aq-chips');
    box.append(h('div', 'w-title', 'Air quality', h('span', 'w-sub', cfg.weather?.city || '')), h('div', 'aq-main', v, cat), chips);
    const load = every(30 * 60_000, async () => {
      const { lat, lon } = cfg.weather || DEFAULT_CFG.weather;
      try {
        const d = await cached(`air:${lat},${lon}`, `https://air-quality-api.open-meteo.com/v1/air-quality?latitude=${lat}&longitude=${lon}` +
          '&current=european_aqi,pm2_5,pm10,ozone,nitrogen_dioxide&timezone=auto', 30 * 60_000);
        const c = d.current || {};
        const band = AQI_BANDS.find((b) => (c.european_aqi ?? 0) <= b[0]);
        box.style.setProperty('--c', band[2]);
        v.textContent = Math.round(c.european_aqi ?? 0);
        cat.textContent = band[1] + ' · European AQI';
        chips.replaceChildren(...[['PM2.5', c.pm2_5], ['PM10', c.pm10], ['O₃', c.ozone], ['NO₂', c.nitrogen_dioxide]]
          .filter((x) => x[1] != null).map(([n, x]) => h('span', 'chip', `${n} ${Math.round(x)}`)));
      } catch (_) { cat.textContent = 'Unavailable'; return false; }
    });
    return { update: load };
  },
};

// The moon tonight, drawn to its phase, with the next full and new moon.
W.moon = {
  needs: ['tick'],
  create(box) {
    box.innerHTML = '<div class="w-title">Moon</div><div class="mo-body"><svg viewBox="0 0 100 100" class="mo-disc"><circle class="mo-dark" cx="50" cy="50" r="44"/><path class="mo-lit"/></svg>' +
      '<div class="mo-text"><b class="mo-name"></b><span class="mo-ill"></span><small class="mo-next"></small></div></div>';
    const q = (s) => box.querySelector(s);
    let shown = -1;
    return {
      update() {
        const now = new Date();
        const hour = Math.floor(now / 36e5);
        if (hour === shown) return;
        shown = hour;
        const syn = 29.530588853;
        const age = ((((now - Date.UTC(2000, 0, 6, 18, 14)) / 864e5) % syn) + syn) % syn;
        const m = moonPhase(now);
        const r = 44, rx = Math.abs(Math.cos((2 * Math.PI * age) / syn)) * r;
        const waxing = age < syn / 2, gibbous = m.illum > 0.5;
        // outer edge on the lit side, back along the terminator (an ellipse)
        const d = waxing ? `M50 6 A${r} ${r} 0 0 1 50 94 A${rx.toFixed(2)} ${r} 0 0 ${gibbous ? 1 : 0} 50 6Z`
          : `M50 6 A${r} ${r} 0 0 0 50 94 A${rx.toFixed(2)} ${r} 0 0 ${gibbous ? 0 : 1} 50 6Z`;
        q('.mo-lit').setAttribute('d', d);
        q('.mo-name').textContent = m.name;
        q('.mo-ill').textContent = `${Math.round(m.illum * 100)}% lit`;
        const inDays = (target) => { const dd = ((target - age) % syn + syn) % syn; return dd < 0.5 ? 'tonight' : dd < 1.5 ? 'tomorrow' : `in ${Math.round(dd)} days`; };
        q('.mo-next').textContent = age < syn / 2 ? `Full moon ${inDays(syn / 2)}` : `New moon ${inDays(syn)}`;
      },
    };
  },
};
