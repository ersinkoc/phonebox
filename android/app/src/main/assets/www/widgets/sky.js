import { $, D, DEFAULT_CFG, LOCALE, cfg, fmtDur, fmtHM, h, en, t, tick } from '../main.js';
import { W } from '../registry.js';
import { cached, every } from './helpers.js';

// ---------------------------------------------------------------- sky & air
export function moonPhase(date) {
  const syn = 29.530588853;
  const age = ((((date - Date.UTC(2000, 0, 6, 18, 14)) / 864e5) % syn) + syn) % syn;
  const names = ['New moon', 'Waxing crescent', 'First quarter', 'Waxing gibbous', 'Full moon', 'Waning gibbous', 'Last quarter', 'Waning crescent'];
  const i = Math.floor(((age + syn / 16) % syn) / (syn / 8));
  return { name: names[i], age, illum: (1 - Math.cos((2 * Math.PI * age) / syn)) / 2 };
}

// The lit part of the moon in a 100 × 100 box (outer edge on the lit side, back along the terminator, an ellipse).
export function moonLitPath({ age, illum }, r = 44) {
  const syn = 29.530588853;
  const rx = Math.abs(Math.cos((2 * Math.PI * age) / syn)) * r, waxing = age < syn / 2, gibbous = illum > 0.5;
  return waxing ? `M50 ${50 - r} A${r} ${r} 0 0 1 50 ${50 + r} A${rx.toFixed(2)} ${r} 0 0 ${gibbous ? 1 : 0} 50 ${50 - r}Z`
    : `M50 ${50 - r} A${r} ${r} 0 0 0 50 ${50 + r} A${rx.toFixed(2)} ${r} 0 0 ${gibbous ? 0 : 1} 50 ${50 - r}Z`;
}
// A small inline moon icon drawn to the phase, in the same line style as the other icons.
function moonIcon(m) {
  const t = document.createElement('template');
  t.innerHTML = `<svg class="ico m" viewBox="0 0 100 100" aria-hidden="true"><circle cx="50" cy="50" r="44" fill="none" stroke="currentColor" stroke-width="8"/><path d="${moonLitPath(m, 44)}" fill="currentColor"/></svg>`;
  return t.content.firstElementChild;
}

W.sun = {
  needs: ['weather', 'tick'],
  create(box) {
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
        const rise = new Date(d?.sunrise?.[0] ?? NaN), set = new Date(d?.sunset?.[0] ?? NaN);
        if (!(set > rise)) {
          if (shown) {
            shown = '';
            fg.style.strokeDashoffset = len;
            box.classList.remove('night');
            for (const selector of ['.sr', '.ss', '.w-sub', '.moon']) q(selector).textContent = '';
          }
          sun.style.display = 'none';
          q('.sl').textContent = D.weather === null ? t('w.loading') : t('w.weatherUnavailable');
          return;
        }
        const now = new Date();
        const key = [Math.floor(now.getTime() / 60_000), rise.getTime(), set.getTime(),
          d.daylight_duration?.[0], d.uv_index_max?.[0]].join(':');
        if (key === shown) return;
        shown = key;
        sun.style.display = '';
        const f = Math.max(0, Math.min(1, (now - rise) / (set - rise)));
        fg.style.strokeDashoffset = len * (1 - f);
        const pt = fg.getPointAtLength(len * f);
        sun.setAttribute('cx', pt.x.toFixed(1)); sun.setAttribute('cy', pt.y.toFixed(1));
        box.classList.toggle('night', now < rise || now > set);
        const hm = fmtHM;
        q('.sr').textContent = '↑ ' + hm(rise);
        q('.ss').textContent = hm(set) + ' ↓';
        q('.sl').textContent = now < rise ? t('w.risesIn', { dur: fmtDur(rise - now) }) : now > set ? t('w.afterSunset') : t('w.lightLeft', { dur: fmtDur(set - now) });
        q('.w-sub').textContent = t('w.day') + ' ' + fmtDur((d.daylight_duration?.[0] ?? (set - rise) / 1000) * 1000) + (d.uv_index_max?.[0] != null ? ` · UV ${Math.round(d.uv_index_max[0])}` : '');
        const m = moonPhase(now);
        q('.moon').replaceChildren(moonIcon(m), `${m.name} · ${Math.round(m.illum * 100)}%`);
      },
    };
  },
};

export const AQI_BANDS = [[20, 'Good', '#22c55e'], [40, 'Fair', '#84cc16'], [60, 'Moderate', '#eab308'], [80, 'Poor', '#f97316'], [100, 'Very poor', '#ef4444'], [1e9, 'Extremely poor', '#a855f7']];
W.air = {
  needs: ['tick'],
  create(box) {
    const city = h('span', 'w-sub');
    const v = h('div', 'aq-v', '—'), cat = h('div', 'aq-c', t('w.loading')), chips = h('div', 'aq-chips');
    box.append(h('div', 'w-title', en('Air quality'), city), h('div', 'aq-main', v, cat), chips);
    let coordinates, load;
    const clearIndex = (message) => {
      box.style.removeProperty('--c');
      v.textContent = '—';
      cat.textContent = message;
    };

    function loader(lat, lon, location) {
      return every(30 * 60_000, async () => {
        try {
          const d = await cached(`air:${location}`, `https://air-quality-api.open-meteo.com/v1/air-quality?latitude=${lat}&longitude=${lon}` +
            '&current=european_aqi,pm2_5,pm10,ozone,nitrogen_dioxide&timezone=auto', 30 * 60_000);
          if (coordinates !== location) return;
          const c = d.current || {};
          const aqi = c.european_aqi;
          const band = Number.isFinite(aqi) && aqi >= 0 ? AQI_BANDS.find((b) => aqi <= b[0]) : null;
          if (band) {
            box.style.setProperty('--c', band[2]);
            v.textContent = Math.round(aqi);
            cat.textContent = band[1] + ' · ' + t('w.euroAqi');
          } else clearIndex(t('w.unavailable'));
          chips.replaceChildren(...[['PM2.5', c.pm2_5], ['PM10', c.pm10], ['O₃', c.ozone], ['NO₂', c.nitrogen_dioxide]]
            .filter((x) => x[1] != null).map(([n, x]) => h('span', 'chip', `${n} ${Math.round(x)}`)));
        } catch (_) {
          if (coordinates === location) cat.textContent = t('w.unavailable');
          return false;
        }
      });
    }

    return {
      update() {
        const { city: name, lat, lon } = cfg.weather || DEFAULT_CFG.weather;
        if (city.textContent !== (name || '')) city.textContent = name || '';
        const location = `${lat},${lon}`;
        if (coordinates !== location) {
          coordinates = location;
          clearIndex(t('w.loading'));
          chips.replaceChildren();
          load = loader(lat, lon, location);
        }
        load();
      },
    };
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
        q('.mo-lit').setAttribute('d', moonLitPath(m));
        q('.mo-name').textContent = m.name;
        q('.mo-ill').textContent = `${Math.round(m.illum * 100)}% lit`;
        const inDays = (target) => { const dd = ((target - age) % syn + syn) % syn; return dd < 0.5 ? 'tonight' : dd < 1.5 ? 'tomorrow' : `in ${Math.round(dd)} days`; };
        q('.mo-next').textContent = age < syn / 2 ? `Full moon ${inDays(syn / 2)}` : `New moon ${inDays(syn)}`;
      },
    };
  },
};
