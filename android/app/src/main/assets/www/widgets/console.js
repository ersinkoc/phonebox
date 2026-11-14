import { API, PANEL_HEADERS, api, cfg, h, haptic, toast } from '../main.js';
import { W } from '../registry.js';

let cached = null;
let at = 0;
let epoch = 0;
let pending = null;
const listeners = new Set();

function notify(state) {
  for (const fn of listeners) {
    try { fn(state); } catch (_) {}
  }
}

export async function consoleState(force = false) {
  if (!force && cached && performance.now() - at < 550) return cached;
  if (!force && pending) return pending;
  const stamp = epoch;
  const task = api('/console/state', 'GET', 2500).then((data) => {
    if (stamp === epoch) {
      cached = data;
      at = performance.now();
      notify(data);
    }
    return data;
  }).finally(() => { if (pending === task) pending = null; });
  pending = task;
  return task;
}

export async function consoleAct(body) {
  epoch++;
  at = 0;
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), 5000);
  try {
    const r = await fetch(`${API}/console/act`, {
      method: 'POST',
      headers: { ...PANEL_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: ctl.signal,
    });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const data = await r.json();
    if (data.state) {
      cached = data.state;
      at = performance.now();
      notify(data.state);
    }
    if (!data.ok) throw new Error(data.error || 'Console action failed');
    return data;
  } finally {
    clearTimeout(t);
  }
}

const enabledProfiles = () => (cfg.console?.profiles || []).filter((p) => p && p.enabled !== false);

function resolveProfile(o, state, lastMatchedId) {
  const list = enabledProfiles();
  if (!list.length) return null;
  const fallback = list.find((p) => p.isDefault) || list[0];
  if (o.mode === 'pinned') {
    return list.find((p) => p.id === o.profileId) || fallback;
  }
  const id = state?.pinnedProfileId
    || state?.matchedProfileId
    || (o.unmatched === 'keep' ? lastMatchedId : null);
  return list.find((p) => p.id === id) || fallback;
}

function makeAxisDispatcher(getTarget) {
  let queued = 0;
  let sending = false;
  let flushTarget = null;

  async function flush() {
    if (sending) return;
    sending = true;
    try {
      while (queued !== 0) {
        const delta = Math.max(-10, Math.min(10, queued));
        queued -= delta;
        const target = flushTarget;
        if (!target) break;
        try {
          await consoleAct({ kind: 'axis', profileId: target.profileId, axis: target.axis, delta });
        } catch (e) {
          queued = 0;
          toast(e?.message || 'Axis action failed', 'err');
          break;
        }
      }
    } finally {
      sending = false;
    }
  }

  return (step) => {
    const target = getTarget();
    if (!target) return;
    flushTarget = target;
    queued += step;
    flush();
  };
}

function appBadge(profile, state) {
  const badge = h('span', 'ck-ico', profile.icon || '🎛️');
  if (!window.PhoneBoxDemo && state?.activeProcess && !profile.isDefault && profile.id === (state.pinnedProfileId || state.matchedProfileId)) {
    const img = new Image();
    img.className = 'ck-app-img';
    img.alt = '';
    img.onload = () => badge.replaceChildren(img);
    img.src = `${API}/procs/icon?name=${encodeURIComponent(state.activeProcess)}`;
  }
  return badge;
}

function subPageFor(profile, id) {
  const subs = profile?.subPages || [];
  if (!subs.length) return { sub: null, index: 0 };
  const idx = subs.findIndex((s) => s.id === id);
  return idx >= 0 ? { sub: subs[idx], index: idx } : { sub: subs[0], index: 0 };
}

W['app-keypad'] = {
  needs: ['tick'],
  create(box, { o }) {
    box.classList.add('control-deck', 'ck-widget');
    const root = h('div', 'ck-root');
    const head = h('div', 'ck-head');
    const grid = h('div', 'ck-grid');
    const dots = h('div', 'ck-subdots');
    const picker = h('div', 'ck-picker');
    picker.hidden = true;
    root.append(head, grid, dots, picker);
    box.append(root);

    let state = cached;
    let lastMatchedId = null;
    let alive = true;
    let loading = false;
    let suppressClickUntil = 0;
    const subByProfile = new Map();

    function stepSubPage(profile, dir) {
      const subs = profile?.subPages || [];
      if (subs.length < 2) return;
      const { index } = subPageFor(profile, subByProfile.get(profile.id));
      const next = subs[(index + dir + subs.length) % subs.length];
      subByProfile.set(profile.id, next.id);
      haptic();
      render();
    }

    // Vertical swipe on the grid switches sub-pages while keeping horizontal page swipes intact.
    let swipe = null;
    grid.addEventListener('pointerdown', (e) => {
      if (e.button && e.button !== 0) return;
      swipe = { id: e.pointerId, x: e.clientX, y: e.clientY, done: false };
    });
    grid.addEventListener('pointermove', (e) => {
      if (!swipe || e.pointerId !== swipe.id || swipe.done) return;
      const dx = e.clientX - swipe.x;
      const dy = e.clientY - swipe.y;
      if (Math.abs(dy) >= 26 && Math.abs(dy) > Math.abs(dx) * 1.15) {
        const profile = resolveProfile(o, state, lastMatchedId);
        if (profile && (profile.subPages?.length || 0) > 1) {
          swipe.done = true;
          suppressClickUntil = Date.now() + 350;
          stepSubPage(profile, dy < 0 ? 1 : -1);
        }
      }
    });
    const endSwipe = (e) => { if (swipe && e.pointerId === swipe.id) swipe = null; };
    grid.addEventListener('pointerup', endSwipe);
    grid.addEventListener('pointercancel', endSwipe);

    function renderPicker(list, active) {
      if (picker.hidden) { picker.dataset.key = ''; return; }
      const key = `${state?.pinnedProfileId || ''}|${active.id}|${list.map((p) => `${p.id}:${p.name}:${p.icon || ''}`).join(',')}`;
      if (picker.dataset.key === key) return;
      picker.dataset.key = key;
      const items = [];
      if (o.mode !== 'pinned' && cfg.console?.autoFollow !== false) {
        const autoBtn = h('button', 'ck-pick' + (!state?.pinnedProfileId ? ' on' : ''), '⚡ Auto-follow active app');
        autoBtn.type = 'button';
        autoBtn.onclick = async () => {
          haptic();
          picker.hidden = true;
          picker.dataset.key = '';
          try { await consoleAct({ kind: 'pin', pinned: '' }); } catch (e) { toast(e.message, 'err'); }
        };
        items.push(autoBtn);
      }
      for (const p of list) {
        const btn = h('button', 'ck-pick' + (p.id === active.id ? ' on' : ''), `${p.icon || '🎛️'} ${p.name}`);
        btn.type = 'button';
        btn.onclick = async () => {
          haptic();
          picker.hidden = true;
          picker.dataset.key = '';
          try { await consoleAct({ kind: 'pin', pinned: p.id }); } catch (e) { toast(e.message, 'err'); }
        };
        items.push(btn);
      }
      picker.replaceChildren(...items);
    }

    function render() {
      const list = enabledProfiles();
      const profile = resolveProfile(o, state, lastMatchedId);
      if (!profile) {
        head.replaceChildren();
        grid.replaceChildren(h('div', 'deck-empty', 'Add a profile in Settings › Console'));
        dots.replaceChildren();
        picker.hidden = true;
        return;
      }

      root.style.setProperty('--ck-accent', profile.color || '#38bdf8');
      const subs = profile.subPages || [];
      const { sub } = subPageFor(profile, subByProfile.get(profile.id));
      const isPinnedServer = o.mode !== 'pinned' && state?.pinnedProfileId === profile.id;

      head.hidden = o.showHeader === false;
      if (o.showHeader !== false) {
        const titleBtn = h('button', 'ck-title');
        titleBtn.type = 'button';
        titleBtn.setAttribute('aria-label', 'Switch console profile');
        titleBtn.append(appBadge(profile, state), h('span', 'ck-name', profile.name));
        if (subs.length > 1 && sub) titleBtn.append(h('span', 'ck-sub', sub.name));
        titleBtn.onclick = () => {
          haptic();
          picker.hidden = !picker.hidden;
          renderPicker(list, profile);
        };

        const pinBtn = h('button', 'ck-pin');
        pinBtn.type = 'button';
        if (o.mode === 'pinned') {
          pinBtn.textContent = '📌 Locked';
          pinBtn.disabled = true;
        } else {
          pinBtn.textContent = isPinnedServer ? '📌 Pinned' : '⚡ Auto';
          pinBtn.classList.toggle('on', isPinnedServer);
          pinBtn.setAttribute('aria-label', 'Pin profile');
          pinBtn.setAttribute('aria-pressed', String(isPinnedServer));
          pinBtn.onclick = async () => {
            haptic();
            try { await consoleAct({ kind: 'pin', pinned: isPinnedServer ? '' : profile.id }); }
            catch (e) { toast(e.message, 'err'); }
          };
        }
        head.replaceChildren(titleBtn, pinBtn);
      }

      const cols = Math.max(2, Math.min(6, profile.columns || 4));
      const rows = Math.max(1, Math.min(4, profile.rows || 2));
      grid.style.setProperty('--cols', cols);
      grid.style.setProperty('--rows', rows);

      const slots = sub?.slots || [];
      const cells = [];
      for (let i = 0; i < cols * rows; i++) {
        const slot = slots[i];
        if (!slot || slot.kind === 'empty') {
          const spacer = h('div', 'ck-spacer');
          spacer.setAttribute('aria-hidden', 'true');
          cells.push(spacer);
          continue;
        }
        const keyBtn = h('button', 'ck-key');
        keyBtn.type = 'button';
        if (slot.color) keyBtn.style.setProperty('--kc', slot.color);
        const stateKey = slot.stateKey || `${profile.id}:${sub?.id || 'main'}:${i}`;
        const activeOn = !!slot.toggle && !!state?.slotStates?.[stateKey];
        keyBtn.classList.toggle('on', activeOn);
        if (slot.toggle) keyBtn.setAttribute('aria-pressed', String(activeOn));
        keyBtn.setAttribute('aria-label', slot.label || 'Key');

        const ico = h('span', 'ck-key-ico', slot.icon || '🔘');
        const lbl = h('span', 'ck-key-lbl', slot.label || '');
        const hint = o.showHints !== false && slot.hint ? h('span', 'ck-key-hint', slot.hint) : null;
        keyBtn.append(ico, lbl);
        if (hint) keyBtn.append(hint);

        keyBtn.onclick = async () => {
          if (Date.now() < suppressClickUntil) return;
          haptic();
          keyBtn.disabled = true;
          try {
            await consoleAct({ kind: 'slot', profileId: profile.id, subPageId: sub?.id, slot: i });
          } catch (e) {
            toast(e?.message || 'Shortcut failed', 'err');
          } finally {
            keyBtn.disabled = false;
          }
        };
        cells.push(keyBtn);
      }
      grid.replaceChildren(...cells);

      if (subs.length > 1) {
        dots.hidden = false;
        dots.replaceChildren(...subs.map((sp, idx) => {
          const d = h('button', 'ck-dot' + (sp.id === sub?.id ? ' on' : ''));
          d.type = 'button';
          d.setAttribute('aria-label', `Sub-page ${idx + 1}: ${sp.name}`);
          d.onclick = () => { haptic(); subByProfile.set(profile.id, sp.id); render(); };
          return d;
        }));
      } else {
        dots.hidden = true;
        dots.replaceChildren();
      }
      renderPicker(list, profile);
    }

    const onUpdate = (next) => {
      if (!alive) return;
      state = next;
      if (next?.matchedProfileId && enabledProfiles().some((p) => p.id === next.matchedProfileId)) {
        lastMatchedId = next.matchedProfileId;
      }
      render();
    };
    listeners.add(onUpdate);
    render();

    return {
      async update() {
        if (loading || !alive) return;
        loading = true;
        try {
          const next = await consoleState();
          if (alive) onUpdate(next);
        } catch (_) {
        } finally {
          loading = false;
        }
      },
      destroy() {
        alive = false;
        listeners.delete(onUpdate);
      },
    };
  },
};

function selectAxis(profile, axisIdx) {
  const axes = profile?.axes || [];
  if (!axes.length) return null;
  return axes.find((a) => a.index === axisIdx) || axes[0];
}

function makeDialSvg() {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 120 120');
  svg.setAttribute('class', 'cd-svg');
  const outer = document.createElementNS(ns, 'circle');
  outer.setAttribute('cx', '60'); outer.setAttribute('cy', '60'); outer.setAttribute('r', '52');
  outer.setAttribute('class', 'cd-track');
  const ring = document.createElementNS(ns, 'circle');
  ring.setAttribute('cx', '60'); ring.setAttribute('cy', '60'); ring.setAttribute('r', '52');
  ring.setAttribute('class', 'cd-ticks');
  const rotor = document.createElementNS(ns, 'g');
  rotor.setAttribute('class', 'cd-rotor');
  const notch = document.createElementNS(ns, 'line');
  notch.setAttribute('x1', '60'); notch.setAttribute('y1', '12');
  notch.setAttribute('x2', '60'); notch.setAttribute('y2', '24');
  notch.setAttribute('class', 'cd-notch');
  rotor.append(notch);
  svg.append(outer, ring, rotor);
  return { svg, rotor };
}

W['app-dial'] = {
  needs: ['tick'],
  create(box, { o }) {
    box.classList.add('control-deck', 'cd-widget');
    const root = h('div', 'cd-root');
    const head = h('div', 'cd-head');
    const ring = h('div', 'cd-ring');
    ring.setAttribute('data-no-page-swipe', '1');
    ring.setAttribute('role', 'slider');
    ring.setAttribute('aria-label', 'Rotary dial');
    const { svg, rotor } = makeDialSvg();
    const center = h('button', 'cd-center');
    center.type = 'button';
    ring.append(svg, center);
    const pills = h('div', 'cd-pills');
    root.append(head, ring, pills);
    box.append(root);

    let state = cached;
    let lastMatchedId = null;
    let selectedAxisIdx = Number(o.axis) || 1;
    let alive = true;
    let loading = false;
    let visualDeg = 0;
    let suppressCenterUntil = 0;

    const dispatchStep = makeAxisDispatcher(() => {
      const profile = resolveProfile(o, state, lastMatchedId);
      const axis = selectAxis(profile, selectedAxisIdx);
      return profile && axis ? { profileId: profile.id, axis: axis.index } : null;
    });

    const STEP_RAD = Math.PI / 9;
    let drag = null;
    ring.addEventListener('pointerdown', (e) => {
      if (e.target.closest?.('.cd-center')) return;
      if (e.button && e.button !== 0) return;
      const rect = ring.getBoundingClientRect();
      const angle = Math.atan2(e.clientY - (rect.top + rect.height / 2), e.clientX - (rect.left + rect.width / 2));
      drag = { id: e.pointerId, lastAngle: angle, accum: 0, moved: false };
      try { ring.setPointerCapture(e.pointerId); } catch (_) {}
    });
    ring.addEventListener('pointermove', (e) => {
      if (!drag || e.pointerId !== drag.id) return;
      const rect = ring.getBoundingClientRect();
      const angle = Math.atan2(e.clientY - (rect.top + rect.height / 2), e.clientX - (rect.left + rect.width / 2));
      let d = angle - drag.lastAngle;
      if (d > Math.PI) d -= 2 * Math.PI;
      if (d < -Math.PI) d += 2 * Math.PI;
      drag.lastAngle = angle;
      drag.accum += d;
      visualDeg = (visualDeg + (d * 180) / Math.PI) % 360;
      rotor.style.transform = `rotate(${visualDeg.toFixed(1)}deg)`;
      while (Math.abs(drag.accum) >= STEP_RAD) {
        const step = drag.accum > 0 ? 1 : -1;
        drag.accum -= step * STEP_RAD;
        drag.moved = true;
        haptic();
        dispatchStep(step);
      }
    });
    const endDrag = (e) => {
      if (!drag || e.pointerId !== drag.id) return;
      if (drag.moved) suppressCenterUntil = Date.now() + 250;
      drag = null;
    };
    ring.addEventListener('pointerup', endDrag);
    ring.addEventListener('pointercancel', endDrag);

    ring.addEventListener('wheel', (e) => {
      e.preventDefault();
      const step = (Math.abs(e.deltaY) >= Math.abs(e.deltaX) ? -e.deltaY : e.deltaX) > 0 ? 1 : -1;
      visualDeg = (visualDeg + step * 20) % 360;
      rotor.style.transform = `rotate(${visualDeg.toFixed(1)}deg)`;
      dispatchStep(step);
    }, { passive: false });

    center.onclick = async () => {
      if (Date.now() < suppressCenterUntil) return;
      const profile = resolveProfile(o, state, lastMatchedId);
      const axis = selectAxis(profile, selectedAxisIdx);
      if (!profile || !axis || !axis.hasTap) return;
      haptic();
      try { await consoleAct({ kind: 'axis-tap', profileId: profile.id, axis: axis.index }); }
      catch (e) { toast(e?.message || 'Tap action failed', 'err'); }
    };

    function render() {
      const profile = resolveProfile(o, state, lastMatchedId);
      if (!profile) {
        head.replaceChildren();
        center.replaceChildren(h('span', 'cd-axis-name', 'No profile'));
        pills.replaceChildren();
        return;
      }
      root.style.setProperty('--ck-accent', profile.color || '#38bdf8');
      const axis = selectAxis(profile, selectedAxisIdx);
      head.replaceChildren(h('span', 'cd-prof', `${profile.icon || '🎛️'} ${profile.name}`));
      if (axis) head.append(h('span', 'cd-sens', `#${axis.index}`));
      if (axis) {
        center.replaceChildren(
          h('span', 'cd-axis-ico', axis.icon || '🎚️'),
          h('span', 'cd-axis-name', axis.name),
        );
        center.disabled = !axis.hasTap;
        ring.setAttribute('aria-label', `${profile.name}: ${axis.name} dial`);
      } else {
        center.replaceChildren(h('span', 'cd-axis-name', 'No axis'));
        center.disabled = true;
      }

      const axes = profile.axes || [];
      if (o.showPills !== false && axes.length > 1) {
        pills.hidden = false;
        pills.replaceChildren(...axes.map((a) => {
          const b = h('button', 'cd-pill' + (axis && a.index === axis.index ? ' on' : ''), `${a.icon || ''} ${a.name}`.trim());
          b.type = 'button';
          b.onclick = () => { haptic(); selectedAxisIdx = a.index; render(); };
          return b;
        }));
      } else {
        pills.hidden = true;
        pills.replaceChildren();
      }
    }

    const onUpdate = (next) => {
      if (!alive) return;
      state = next;
      if (next?.matchedProfileId && enabledProfiles().some((p) => p.id === next.matchedProfileId)) {
        lastMatchedId = next.matchedProfileId;
      }
      render();
    };
    listeners.add(onUpdate);
    render();

    return {
      async update() {
        if (loading || !alive) return;
        loading = true;
        try {
          const next = await consoleState();
          if (alive) onUpdate(next);
        } catch (_) {
        } finally {
          loading = false;
        }
      },
      destroy() {
        alive = false;
        listeners.delete(onUpdate);
      },
    };
  },
};

W['app-roller'] = {
  needs: ['tick'],
  create(box, { w, o }) {
    box.classList.add('control-deck', 'cr-widget');
    const root = h('div', 'cr-root');
    const meta = h('div', 'cr-meta');
    const track = h('div', 'cr-track');
    track.setAttribute('data-no-page-swipe', '1');
    track.setAttribute('role', 'slider');
    track.setAttribute('aria-label', 'Roller axis');
    const tap = h('button', 'cr-tap', '⏺');
    tap.type = 'button';
    root.append(meta, track, tap);
    box.append(root);

    let state = cached;
    let lastMatchedId = null;
    let alive = true;
    let loading = false;
    let groovePx = 0;
    const axisIdx = Number(o.axis) || 2;

    const isVertical = () => o.orientation === 'vertical'
      || (o.orientation !== 'horizontal' && ((box.clientWidth || box.clientHeight) ? box.clientHeight > box.clientWidth * 1.05 : (w?.h || 2) > (w?.w || 6)));

    const syncOrientation = () => {
      const vert = isVertical();
      root.classList.toggle('is-vert', vert);
      root.classList.toggle('is-horiz', !vert);
    };

    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(syncOrientation) : null;
    ro?.observe(box);

    const dispatchStep = makeAxisDispatcher(() => {
      const profile = resolveProfile(o, state, lastMatchedId);
      const axis = selectAxis(profile, axisIdx);
      return profile && axis ? { profileId: profile.id, axis: axis.index } : null;
    });

    const STEP_PX = 20;
    let drag = null;
    track.addEventListener('pointerdown', (e) => {
      if (e.button && e.button !== 0) return;
      drag = { id: e.pointerId, x: e.clientX, y: e.clientY, accum: 0 };
      try { track.setPointerCapture(e.pointerId); } catch (_) {}
    });
    track.addEventListener('pointermove', (e) => {
      if (!drag || e.pointerId !== drag.id) return;
      const vert = root.classList.contains('is-vert');
      const d = vert ? (drag.y - e.clientY) : (e.clientX - drag.x);
      drag.x = e.clientX;
      drag.y = e.clientY;
      groovePx = (groovePx + (vert ? -d : d)) % 240;
      track.style.setProperty('--pos', `${groovePx.toFixed(1)}px`);
      drag.accum += d;
      while (Math.abs(drag.accum) >= STEP_PX) {
        const step = drag.accum > 0 ? 1 : -1;
        drag.accum -= step * STEP_PX;
        haptic();
        dispatchStep(step);
      }
    });
    const endDrag = (e) => { if (drag && e.pointerId === drag.id) drag = null; };
    track.addEventListener('pointerup', endDrag);
    track.addEventListener('pointercancel', endDrag);

    track.addEventListener('wheel', (e) => {
      e.preventDefault();
      const raw = Math.abs(e.deltaY) >= Math.abs(e.deltaX) ? -e.deltaY : e.deltaX;
      if (!raw) return;
      const step = raw > 0 ? 1 : -1;
      groovePx = (groovePx + step * 10) % 240;
      track.style.setProperty('--pos', `${groovePx.toFixed(1)}px`);
      dispatchStep(step);
    }, { passive: false });

    tap.onclick = async () => {
      const profile = resolveProfile(o, state, lastMatchedId);
      const axis = selectAxis(profile, axisIdx);
      if (!profile || !axis || !axis.hasTap) return;
      haptic();
      try { await consoleAct({ kind: 'axis-tap', profileId: profile.id, axis: axis.index }); }
      catch (e) { toast(e?.message || 'Tap action failed', 'err'); }
    };

    function render() {
      syncOrientation();
      const profile = resolveProfile(o, state, lastMatchedId);
      const axis = selectAxis(profile, axisIdx);
      if (!profile) {
        meta.replaceChildren(h('span', 'cr-name', 'No profile'));
        tap.hidden = true;
        return;
      }
      root.style.setProperty('--ck-accent', profile.color || '#38bdf8');
      meta.replaceChildren(
        h('span', 'cr-ico', axis?.icon || profile.icon || '🎚️'),
        h('span', 'cr-name', axis ? axis.name : `Axis #${axisIdx}`),
        h('span', 'cr-prof', profile.name),
      );
      track.setAttribute('aria-label', `${profile.name}: ${axis?.name || 'Roller'} axis`);
      tap.hidden = o.showReset === false || !axis?.hasTap;
      tap.setAttribute('aria-label', `Tap ${axis?.name || 'axis'}`);
    }

    const onUpdate = (next) => {
      if (!alive) return;
      state = next;
      if (next?.matchedProfileId && enabledProfiles().some((p) => p.id === next.matchedProfileId)) {
        lastMatchedId = next.matchedProfileId;
      }
      render();
    };
    listeners.add(onUpdate);
    render();
    requestAnimationFrame(syncOrientation);

    return {
      async update() {
        if (loading || !alive) return;
        loading = true;
        try {
          const next = await consoleState();
          if (alive) onUpdate(next);
        } catch (_) {
        } finally {
          loading = false;
        }
      },
      destroy() {
        alive = false;
        ro?.disconnect();
        listeners.delete(onUpdate);
      },
    };
  },
};
