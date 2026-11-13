import { h, haptic } from '../main.js';
import { W } from '../registry.js';

function parseWidgets(raw) {
  let list = [];
  if (Array.isArray(raw)) {
    list = raw;
  } else if (typeof raw === 'string') {
    const s = raw.trim();
    if (s.startsWith('[') && s.endsWith(']')) {
      try { list = JSON.parse(s); } catch (_) { list = s.split('\n'); }
    } else {
      list = s.split(/[\n,]+/).map((x) => x.trim()).filter(Boolean);
    }
  }

  if (!list || (list.length === 0 && !Array.isArray(raw))) {
    return [
      { type: 'clock', options: {}, enabled: true },
      { type: 'weather', options: {}, enabled: true },
      { type: 'media', options: {}, enabled: true }
    ];
  }

  const normalized = list.slice(0, 12).map((item, index) => {
    if (!item) return null;
    if (typeof item === 'object') {
      if (!item.type) return null;
      return {
        id: String(item.id || `card-${index}`),
        type: String(item.type).trim(),
        options: item.options || {},
        enabled: item.enabled !== false && item.active !== false
      };
    }
    const str = String(item).trim();
    if (str.startsWith('{') && str.endsWith('}')) {
      try {
        const obj = JSON.parse(str);
        if (obj.type) {
          return {
            id: String(obj.id || `card-${index}`),
            type: String(obj.type).trim(),
            options: obj.options || {},
            enabled: obj.enabled !== false && obj.active !== false
          };
        }
      } catch (_) {}
    }
    return { id: `card-${index}`, type: str, options: {}, enabled: true };
  }).filter((x) => x && x.type && !['stack', 'command'].includes(x.type));

  return normalized.filter((x) => x.enabled);
}

W.stack = {
  needs: ['tick'],
  /**
   * Creates the Smart Stack widget instance with rotation, swipe, and pause controls.
   */
  create(box, ctx) {
    const o = ctx.o || {};
    const subDefs = parseWidgets(o.widgets);
    const wrap = h('div', 'stack-wrap');
    box.appendChild(wrap);

    if (subDefs.length === 0) {
      wrap.appendChild(h('div', 'empty', 'Smart Stack: no active widgets'));
      return {
        needs: ['tick'],
        update() {},
        destroy() {}
      };
    }

    const children = [];
    const collectedNeeds = new Set(['tick']);

    subDefs.forEach((item, i) => {
      const def = W[item.type];
      const itemEl = h('div', `stack-item${i === 0 ? ' active' : ''}`);
      itemEl.setAttribute('aria-hidden', String(i !== 0));
      itemEl.inert = i !== 0;
      const subBox = h('div', `w w-${item.type}${def?.bare ? ' bare' : ' card'}`);
      PhoneBoxAppearance.widget(subBox, ctx.appearance);
      itemEl.appendChild(subBox);
      wrap.appendChild(itemEl);

      const subW = { ...ctx.w, id: `${ctx.w.id}:${item.id || `card-${i}`}`, type: item.type, options: item.options || {} };
      const subCtx = {
        page: ctx.page,
        w: subW,
        o: subW.options,
        box: subBox,
      };

      let inst = {};
      if (def) {
        for (const need of def.needs || []) collectedNeeds.add(need);
        try {
          inst = def.create(subBox, subCtx) || {};
        } catch (err) {
          console.warn(`stack: error creating child ${item.type}`, err);
          subBox.replaceChildren(h('div', 'empty', `${item.type}: ${err.message}`));
        }
      } else {
        subBox.replaceChildren(h('div', 'empty', `Unknown widget: ${item.type}`));
      }

      children.push({ item, def, inst, el: itemEl });
    });

    let currentIndex = 0;
    let lastRotate = Date.now();
    let paused = false;
    let destroyed = false;
    let dots = null;
    let pauseBtn = null;
    let clearHold = () => {};

    /**
     * Synchronizes DOM attributes and pause indicator state with `paused`.
     */
    function syncPauseUi() {
      if (paused) box.dataset.stackPaused = '1';
      else delete box.dataset.stackPaused;
      if (dots) dots.classList.toggle('paused', paused);
      if (pauseBtn) {
        pauseBtn.classList.toggle('on', paused);
        pauseBtn.setAttribute('aria-pressed', String(paused));
        pauseBtn.title = paused ? 'Rotation paused (tap to resume)' : 'Pause stack rotation';
      }
    }

    /**
     * Toggles the stack rotation paused state and resets the rotation timer.
     */
    function togglePaused() {
      if (children.length <= 1 || !dots) return;
      paused = !paused;
      lastRotate = Date.now();
      syncPauseUi();
      haptic();
    }

    function goTo(nextIdx, dir = 'next') {
      if (children.length <= 1) return;
      const target = (nextIdx + children.length) % children.length;
      if (target === currentIndex) return;

      const prevChild = children[currentIndex];
      const nextChild = children[target];

      prevChild.el.classList.remove('active', 'prev');
      prevChild.el.setAttribute('aria-hidden', 'true');
      prevChild.el.inert = true;
      if (dir === 'next') {
        prevChild.el.classList.add('prev');
      }

      nextChild.el.classList.remove('prev');
      nextChild.el.classList.add('active');
      nextChild.el.setAttribute('aria-hidden', 'false');
      nextChild.el.inert = false;

      currentIndex = target;

      if (dots) {
        dots.querySelectorAll('.stack-dot').forEach((d, idx) => {
          d.classList.toggle('active', idx === currentIndex);
          d.classList.toggle('on', idx === currentIndex);
        });
      }

      for (const need of nextChild.def?.needs || []) {
        try { nextChild.inst.update?.(need); } catch (_) {}
      }
    }

    if (o.showDots !== false && children.length > 1) {
      dots = h('nav', 'stack-dots');
      children.forEach((c, idx) => {
        const dot = h('button', `stack-dot${idx === 0 ? ' active on' : ''}`);
        dot.type = 'button';
        dot.title = c.item.type;
        dot.setAttribute('aria-label', c.item.type);
        dot.addEventListener('click', (e) => {
          e.stopPropagation();
          haptic();
          goTo(idx, idx > currentIndex ? 'next' : 'prev');
          resetTimer();
        });
        dots.appendChild(dot);
      });
      pauseBtn = h('button', 'stack-pause');
      pauseBtn.type = 'button';
      pauseBtn.setAttribute('aria-label', 'Pause stack rotation');
      pauseBtn.innerHTML =
        '<svg class="i-pause" viewBox="0 0 24 24" aria-hidden="true"><path d="M7 5h4v14H7zM13 5h4v14h-4z"/></svg>' +
        '<svg class="i-play" viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5v14l11-7z"/></svg>';
      pauseBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        togglePaused();
      });
      dots.appendChild(pauseBtn);
      syncPauseUi();
      box.appendChild(dots);

      // Long-press on the dots bar (excluding the pause toggle button) toggles rotation pause.
      let holdTimer = 0, holdStart = null, heldDots = false;
      clearHold = () => {
        if (holdTimer) { clearTimeout(holdTimer); holdTimer = 0; }
        holdStart = null;
      };
      dots.addEventListener('pointerdown', (e) => {
        heldDots = false;
        clearHold();
        if (e.pointerType === 'mouse' && e.button !== 0) return;
        if (e.target.closest('.stack-pause')) return;
        holdStart = [e.clientX, e.clientY];
        holdTimer = setTimeout(() => {
          holdTimer = 0;
          holdStart = null;
          heldDots = true;
          togglePaused();
        }, 450);
      });
      dots.addEventListener('pointermove', (e) => {
        if (holdStart && Math.hypot(e.clientX - holdStart[0], e.clientY - holdStart[1]) > 10) clearHold();
      });
      ['pointerup', 'pointercancel', 'pointerleave'].forEach((ev) => dots.addEventListener(ev, clearHold));
      dots.addEventListener('click', (e) => {
        if (heldDots) {
          heldDots = false;
          if (!e.target.closest('.stack-pause')) {
            e.preventDefault();
            e.stopPropagation();
          }
        }
      }, true);
    }

    // Vertical swipes cycle the stack; horizontal swipes remain page navigation.
    wrap.style.touchAction = 'pan-x';
    let startX = 0, startY = 0, startT = 0, swiping = false;
    wrap.addEventListener('touchstart', (e) => {
      swiping = e.touches.length === 1 && !e.target.closest('button, input, textarea, select, a, [contenteditable], .s-list');
      if (!swiping) return;
      startX = e.touches[0].clientX;
      startY = e.touches[0].clientY;
      startT = Date.now();
    }, { passive: true });

    wrap.addEventListener('touchend', (e) => {
      if (!swiping || !e.changedTouches || e.changedTouches.length !== 1) return;
      swiping = false;
      const dx = e.changedTouches[0].clientX - startX;
      const dy = e.changedTouches[0].clientY - startY;
      const dt = Date.now() - startT;

      if (dt < 600) {
        if (Math.abs(dy) > 30 && Math.abs(dy) > Math.abs(dx)) {
          // Vertical swipe
          haptic();
          if (dy < 0) goTo(currentIndex + 1, 'next');
          else goTo(currentIndex - 1, 'prev');
          resetTimer();
        }
      }
    }, { passive: true });

    // Rotation timer
    let intervalSec = Number(o.interval);
    if (!Number.isFinite(intervalSec) || intervalSec < 3) intervalSec = 10;
    if (intervalSec > 120) intervalSec = 120;

    function resetTimer() {
      lastRotate = Date.now();
    }

    return {
      needs: Array.from(collectedNeeds),
      /**
       * Handles periodic tick and widget data updates for the stack and its children.
       */
      update(kind) {
        if (destroyed) return;
        if (kind === 'tick') {
          const r = box.getBoundingClientRect();
          const visible = r.right > 0 && r.left < innerWidth && r.bottom > 0 && r.top < innerHeight && !document.hidden;
          const activeChildEl = children[currentIndex]?.el;
          if (!visible || paused) lastRotate = Date.now();
          else if (Date.now() - lastRotate >= intervalSec * 1000 && !activeChildEl?.contains(document.activeElement)) {
            goTo(currentIndex + 1, 'next');
            lastRotate = Date.now();
          }
        }
        for (const child of children) {
          if (child.def?.needs?.includes(kind)) {
            try { child.inst.update?.(kind); } catch (e) { console.warn(child.item.type, e); }
          }
        }
      },
      /**
       * Cleans up timers, DOM state, and child widget instances.
       */
      destroy() {
        destroyed = true;
        clearHold();
        delete box.dataset.stackPaused;
        for (const child of children) {
          try { child.inst.destroy?.(); } catch (_) {}
        }
        children.length = 0;
      },
    };
  },
};
