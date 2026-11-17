// Spectrum bars: a live spectrum analyzer of the sound the PC is playing (SpectrumAnalyzer.cs on the agent).
// Frames arrive as "spectrum" stream events (~30 per second, 48 bands of 0–100); this widget merges them into its
// own number of bars and animates the fall and the peak caps itself, so it stays smooth between frames.
import { W } from '../registry.js';
import { h, api, D, emit, streaming, t, pages, curPage } from '../main.js';

const PALETTES = {
  classic: [[0, '#16a34a'], [0.55, '#84cc16'], [0.72, '#facc15'], [0.88, '#f97316'], [1, '#ef4444']],
  fire: [[0, '#7f1d1d'], [0.4, '#dc2626'], [0.7, '#f97316'], [1, '#fde047']],
  ice: [[0, '#1e3a8a'], [0.45, '#2563eb'], [0.75, '#22d3ee'], [1, '#f0f9ff']],
};
const FALL = { slow: 55, normal: 110, fast: 200 }; // bar fall, in % of the height per second
const PEAK_HOLD = 450;                              // ms a peak cap waits before it drops
const PEAK_GRAVITY = 260;                           // %/s² once it drops

/** Called for every frame, from the live stream or the polling fallback below. */
function frame(d) {
  if (!d || !Array.isArray(d.b)) return;
  D.spectrum = d.b;
  D.spectrumAt = Date.now();
  emit('spectrum');
}
window.addEventListener('phonebox-spectrum', (e) => frame(e.detail));

// Every mounted Spectrum bars widget, with its page. A widget counts as on screen only while it is really visible:
// not under the night screen, on the current page, and not in a Smart Stack card that is not showing. Mounted is not
// enough: neighbouring pages and hidden stack cards stay mounted, and asking for them would keep the agent capturing.
const mountedBars = new Set();
const shows = (m) => !document.body.classList.contains('night')
  && m.box.isConnected && pages[curPage] === m.page && !m.box.closest('.stack-item:not(.active)');
const onScreen = () => [...mountedBars].some(shows);

// Keep the agent capturing while a visualizer is on screen (it stops by itself a few seconds after the last request),
// and poll the frames when there is no live stream (desktop live preview, or the stream is reconnecting).
// Fast only while polling frames; otherwise a slow check, so a panel without a visualizer barely notices this.
async function keepAlive() {
  let fast = false;
  for (const m of mountedBars) if (!m.box.isConnected) mountedBars.delete(m);
  if (onScreen()) {
    const stream = streaming();
    fast = !stream;
    if (!stream || Date.now() - (D.spectrumWantAt || 0) >= 2000) {
      try {
        const r = await api('/spectrum', 'GET', 1500);
        D.spectrumWantAt = Date.now();
        D.spectrumShown = r.shown !== false;
        D.spectrumOk = r.active !== false;
        if (!stream && r.b) frame(r);
      } catch (_) { /* offline, or an older agent without the endpoint: the widget shows the waiting hint */ }
    }
  }
  setTimeout(keepAlive, fast ? 40 : 250);
}
// not right away: main.js imports this module before its own exports (pages, curPage) are initialized
setTimeout(keepAlive, 250);

function mix(stops, f) {
  // colour at f (0 = bottom, 1 = top) from the palette's stops, as an rgb() string
  let i = 1;
  while (i < stops.length - 1 && stops[i][0] < f) i++;
  const [f0, c0] = stops[i - 1], [f1, c1] = stops[i];
  const t = Math.max(0, Math.min(1, (f - f0) / (f1 - f0 || 1)));
  const p = (c, k) => parseInt(c.slice(1 + k * 2, 3 + k * 2), 16);
  return `rgb(${[0, 1, 2].map((k) => Math.round(p(c0, k) + (p(c1, k) - p(c0, k)) * t)).join(',')})`;
}

W.spectrum = {
  needs: ['spectrum'],
  create(box, ctx) {
    const o = ctx.o;
    const me = { box, page: ctx.page };
    mountedBars.add(me);
    const bars =Math.max(4, Math.min(48, Math.round(+o.bars || 24)));
    const style = PALETTES[o.style] ? o.style : (o.style === 'single' ? 'single' : 'classic');
    const color = /^#[0-9a-f]{6}$/i.test(o.color || '') ? o.color : '#2dd4bf';
    const stops = style === 'single' ? [[0, color], [1, color]] : PALETTES[style];
    const segmented = o.segments !== false, peaks = o.peaks !== false;
    const fall = FALL[o.falloff] || FALL.normal;

    const canvas = h('canvas', 'spec-canvas');
    const wrap = h('div', 'spec-wrap', canvas);
    const msg = h('div', 'spec-msg', t('w.spectrum.waiting'));
    if (o.title) box.append(h('div', 'w-title', o.title));
    box.append(wrap, msg);
    const g = canvas.getContext('2d');

    const level = new Float32Array(bars), peak = new Float32Array(bars), peakAt = new Float64Array(bars), peakV = new Float32Array(bars);
    let target = new Float32Array(bars);
    let W2 = 0, H2 = 0, dpr = 1, colors = [], raf = 0, last = 0;

    const resize = () => {
      if (!box.isConnected) return ro.disconnect();
      dpr = window.devicePixelRatio || 1;
      W2 = Math.max(1, Math.round(wrap.clientWidth * dpr));
      H2 = Math.max(1, Math.round(wrap.clientHeight * dpr));
      canvas.width = W2; canvas.height = H2;
      colors = [];
      draw();
    };
    const ro = new ResizeObserver(resize);
    ro.observe(wrap);

    const draw = () => {
      g.clearRect(0, 0, W2, H2);
      if (!W2 || !H2) return;
      const gap = Math.max(1, Math.round(W2 / bars * 0.18));
      const bw = (W2 - gap * (bars - 1)) / bars;
      const segH = Math.max(2 * dpr, Math.round(H2 / 34)), segGap = Math.max(1, Math.round(segH * 0.35));
      const segs = segmented ? Math.max(4, Math.floor((H2 + segGap) / (segH + segGap))) : 0;
      if (segmented && colors.length !== segs) colors = Array.from({ length: segs }, (_, s) => mix(stops, (s + 0.5) / segs));
      let grad = null;
      if (!segmented) {
        grad = g.createLinearGradient(0, H2, 0, 0);
        for (const [f, c] of stops) grad.addColorStop(f, c);
      }
      for (let i = 0; i < bars; i++) {
        const x = Math.round(i * (bw + gap)), w = Math.max(1, Math.round(bw));
        const v = level[i] / 100;
        if (segmented) {
          const lit = Math.round(v * segs);
          for (let s = 0; s < segs; s++) {
            const y = H2 - (s + 1) * segH - s * segGap;
            g.globalAlpha = s < lit ? 1 : 0.07; // unlit LEDs stay faintly visible
            g.fillStyle = colors[s];
            g.fillRect(x, y, w, segH);
          }
          g.globalAlpha = 1;
        } else if (v > 0) {
          g.fillStyle = grad;
          g.fillRect(x, H2 - v * H2, w, v * H2);
        }
        if (peaks && peak[i] > 0.5) {
          g.fillStyle = style === 'single' ? color : 'rgba(226,232,240,.9)';
          if (segmented) {
            // the cap is one LED, sitting just above the top lit one while the bar is at its peak
            const s = Math.min(segs - 1, Math.round((peak[i] / 100) * segs));
            g.fillRect(x, H2 - (s + 1) * segH - s * segGap, w, segH);
          } else {
            const capH = Math.max(2 * dpr, Math.round(H2 / 60));
            g.fillRect(x, Math.max(0, H2 - (peak[i] / 100) * H2 - capH), w, capH);
          }
        }
      }
    };

    const step = (t) => {
      raf = 0;
      if (!box.isConnected) return;
      // neighbouring pages stay mounted and still get frames meant for the page on screen: an instance that is not
      // showing stops its loop and starts flat when it shows again (the next frame's update() kicks it)
      if (!shows(me)) { level.fill(0); peak.fill(0); target.fill(0); last = 0; draw(); return; }
      const dt = last ? Math.min(0.1, (t - last) / 1000) : 0;
      last = t;
      let moving = false;
      for (let i = 0; i < bars; i++) {
        level[i] = target[i] >= level[i] ? target[i] : Math.max(target[i], level[i] - fall * dt);
        if (level[i] >= peak[i]) { peak[i] = level[i]; peakAt[i] = t; peakV[i] = 0; }
        else if (t - peakAt[i] > PEAK_HOLD) { peakV[i] += PEAK_GRAVITY * dt; peak[i] = Math.max(level[i], peak[i] - peakV[i] * dt); }
        if (level[i] > 0.1 || peak[i] > 0.5 || target[i] > 0) moving = true;
      }
      // frames stopped (page left, agent gone): let everything fall, then idle without burning frames
      if (Date.now() - (D.spectrumAt || 0) > 600) target.fill(0);
      draw();
      if (moving || Date.now() - (D.spectrumAt || 0) < 600) raf = requestAnimationFrame(step);
      else last = 0;
    };
    const kick = () => { if (!raf && shows(me)) raf = requestAnimationFrame(step); };

    // the hint only while nothing arrives; it also says when the agent cannot capture (not Windows) or refuses to
    // (live preview of a page that is not saved yet: capture only runs for a widget on an enabled, saved page)
    const hint = setInterval(() => {
      if (!box.isConnected) return clearInterval(hint);
      const stale = Date.now() - (D.spectrumAt || 0) > 3000;
      msg.textContent = t(D.spectrumShown === false ? 'w.spectrum.save' : D.spectrumOk === false ? 'w.spectrum.unavailable' : 'w.spectrum.waiting');
      msg.hidden = !stale;
    }, 1000);

    return {
      update() {
        const src = D.spectrum;
        if (!src || !src.length) return;
        const next = new Float32Array(bars);
        // merge the agent's bands into this widget's bars: the loudest band in each bar's range
        for (let i = 0; i < bars; i++) {
          const a = Math.floor(i * src.length / bars), b = Math.max(a + 1, Math.floor((i + 1) * src.length / bars));
          let m = 0;
          for (let k = a; k < b; k++) m = Math.max(m, src[k] || 0);
          next[i] = m;
        }
        target = next;
        msg.hidden = true;
        kick();
      },
    };
  },
};
