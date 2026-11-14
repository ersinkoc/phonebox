import { API, PANEL_HEADERS, api, h, haptic, toast, store } from '../main.js';
import { W } from '../registry.js';

let cached = null, at = 0, epoch = 0, pending = null;
export async function deckState(force = false) {
  if (!force && cached && performance.now() - at < 650) return cached;
  if (!force && pending) return pending;
  const stamp = epoch;
  const task = api('/controls/state', 'GET', 15000).then(data => {
    if (stamp === epoch) { cached = data; at = performance.now(); }
    return data;
  }).finally(() => { if (pending === task) pending = null; });
  pending = task;
  return task;
}
export async function deckCommand(kind, values = {}) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value != null) query.set(key, String(value));
  epoch++; at = 0;
  const result = await api(`/controls/command/${encodeURIComponent(kind)}?${query}`, 'POST', 20000);
  if (!result.ok) throw new Error(result.error || 'Control is unavailable');
  return result;
}
const fail = error => toast(error?.message || 'The computer did not answer', 'err');
const button = (label, click, name = label) => {
  const b = h('button', 'deck-button', label); b.type = 'button'; b.setAttribute('aria-label', name);
  b.onclick = async () => { haptic(); b.disabled = true; try { await click(); } catch (e) { fail(e); } finally { b.disabled = false; } };
  return b;
};
const selectedPlayer = (data, options) => {
  const chosen = options.player || store.get('control-player', '');
  return data.media?.find(p => p.id === chosen) || (!options.player ? data.media?.[0] : null);
};
function reconcile(root, nodes) {
  nodes.forEach((node, index) => { if (root.children[index] !== node) root.insertBefore(node, root.children[index] || null); });
  [...root.children].filter(node => !nodes.includes(node)).forEach(node => node.remove());
}
function empty(root, text) { root.replaceChildren(h('div', 'deck-empty', text)); }
function slider(label, send, max = 100) {
  const input = h('input', 'deck-slider'); input.type = 'range'; input.min = 0; input.max = max; input.step = 1;
  input.setAttribute('aria-label', label);
  const number = h('output', 'deck-value');
  let editing = false;
  // the track's filled part (style.css reads --p); min and max may be changed by the caller after creation
  const fill = () => { const lo = +input.min || 0, hi = +input.max || 100; input.style.setProperty('--p', (hi > lo ? Math.max(0, Math.min(100, (input.value - lo) / (hi - lo) * 100)) : 0) + '%'); };
  input.onpointerdown = () => { editing = true; };
  input.oninput = () => { number.textContent = input.value; fill(); };
  input.onchange = async () => { editing = false; try { await send(+input.value); } catch (e) { fail(e); } };
  // a touch that does not move the fader sends no "change": the fader must follow the computer again afterwards
  input.onpointerup = input.onpointercancel = input.onlostpointercapture = input.onblur = () => { editing = false; };
  return { input, number, set(value) { if (!editing && document.activeElement !== input) input.value = String(value); number.textContent = String(editing ? input.value : value); fill(); } };
}
function meter(value) { const e = h('div', 'deck-meter', h('i')); e.firstChild.style.transform = `scaleX(${Math.max(0, Math.min(1, value || 0))})`; return e; }

function audioView(box, options, mode) {
  const root = h('div', `deck-list ${options.layout === 'vertical' ? 'deck-vertical' : ''}`);
  box.append(root);
  const rows = new Map();
  return data => {
    if (!data.audio?.available) { empty(root, data.audio?.error || 'Audio is unavailable on this computer'); return; }
    let items = mode === 'apps' || mode === 'app' ? data.audio.sessions || [] : data.audio.devices || [];
    if (mode === 'mic') items = items.filter(d => d.direction === 'input');
    if (mode === 'output') items = items.filter(d => d.direction === 'output');
    if (options.device) items = items.filter(d => d.id === options.device);
    if (options.application) items = items.filter(d => d.name.toLowerCase().includes(options.application.toLowerCase()));
    if (mode === 'mic' && !options.device) items = items.filter(d => d.default);
    if (mode === 'balance') items = items.filter(d => d.direction === 'output' && d.channels >= 2 && (options.device || d.default));
    if (!items.length) { empty(root, mode === 'app' ? 'Start the selected application and play audio' : 'No matching audio devices'); return; }
    const nodes = items.map(item => {
      let row = rows.get(item.id);
      if (!row) {
        const el = h('div', 'deck-channel');
        const name = h('div', 'deck-channel-name', item.name);
        const signal = meter(item.peak);
        const kind = mode === 'app' || mode === 'apps' ? 'session' : 'device';
        const controls = slider(item.name + ' volume', value => deckCommand(kind, { id: item.id, value }));
        const mute = button('Mute', () => deckCommand(kind, { id: item.id, muted: !row.item.muted }), 'Mute ' + item.name);
        const balance = [];
        el.append(name, signal);
        if (mode === 'balance') {
          const left = slider('Left channel', value => deckCommand('device', { id: item.id, op: 'left', value }));
          const right = slider('Right channel', value => deckCommand('device', { id: item.id, op: 'right', value }));
          left.set(item.volume); right.set(item.volume);
          balance.push(left, right);
          el.append(h('label', '', 'L', left.input, left.number), h('label', '', 'R', right.input, right.number));
        } else if (mode !== 'meter') el.append(controls.input, h('div', 'deck-channel-foot', controls.number, mute));
        row = { el, name, signal, controls, mute, item, balance }; rows.set(item.id, row);
      }
      row.item = item; row.name.textContent = item.name;
      row.controls.set(item.volume); row.mute.textContent = item.muted ? 'Unmute' : 'Mute';
      row.controls.input.disabled = item.canControl === false; row.mute.disabled = item.canControl === false;
      row.balance.forEach((slider, channel) => { const value = item.channelVolumes?.[channel]; slider.input.disabled = value == null; slider.set(value || 0); if (value == null) slider.number.textContent = 'n/a'; });
      row.mute.setAttribute('aria-pressed', String(item.muted));
      row.el.classList.toggle('deck-muted', item.muted); row.signal.firstChild.style.transform = `scaleX(${Math.max(0, Math.min(1, item.peak || 0))})`;
      return row.el;
    });
    reconcile(root, nodes);
    for (const key of rows.keys()) if (!items.some(item => item.id === key)) rows.delete(key);
  };
}

function chooser(box, options, which) {
  const list = h('div', 'deck-list'); box.append(list);
  let signature = '';
  return data => {
    const items = which === 'media' ? data.media || [] : which === 'plans' ? data.powerPlans || [] : (data.audio?.devices || []).filter(d => d.direction === which);
    const role = options.role || 'all';
    const roleIndex = {console:0,media:1,communications:2}[role];
    const chosen = which === 'media' ? selectedPlayer(data, options)?.id : roleIndex != null && ['input','output'].includes(which)
      ? data.audio?.defaults?.[which+':'+roleIndex] : items.find(d => d.default || d.state === 'Active')?.id;
    const key = JSON.stringify([items.map(i => [i.id, i.name]), chosen]); if (key === signature) return; signature = key;
    if (!items.length) { empty(list, which === 'media' ? 'Start a media player on the computer' : 'No compatible devices found'); return; }
    list.replaceChildren(...items.map(item => {
      const b = button(item.name, async () => {
        if (which === 'media') store.set('control-player', item.id);
        else await deckCommand(which === 'plans' ? 'power-plan' : 'default', { id: item.id, op:role });
        signature = '';
      });
      b.classList.toggle('selected', item.id === chosen); b.setAttribute('aria-pressed', String(item.id === chosen)); return b;
    }));
  };
}

function hold(box, options, power = null) {
  const b = h('button', 'deck-hold', power ? `Hold: ${power}` : 'Hold to talk'); b.type = 'button';
  b.disabled = true;
  const status = h('div', 'deck-caption', power ? 'Hold for 2 seconds' : 'Release to restore the microphone'); box.append(b, status);
  let held = false, lease = null, timer = 0, renewal = 0, device = null;
  const end = async () => {
    held = false; clearTimeout(timer); clearInterval(renewal); b.classList.remove('held');
    const old = lease; lease = null;
    if (old && !power) try { await deckCommand('ptt', { op: 'end', lease: old }); } catch (_) { /* the server lease expires independently */ }
  };
  const begin = async event => {
    if (held || b.disabled) return;
    event.preventDefault(); held = true; b.classList.add('held');
    if (event.pointerId != null) try { b.setPointerCapture(event.pointerId); } catch (_) {}
    try {
      const result = await deckCommand(power ? 'power-arm' : 'ptt', { id: power || device, op: 'begin' });
      lease = result.lease;
      if (!held) { await end(); return; }
      if (power) timer = setTimeout(async () => {
        try { await deckCommand('power', { id: power, lease }); status.textContent = 'Power action scheduled'; } catch (e) { fail(e); }
        await end();
      }, 1700);
      else renewal = setInterval(() => deckCommand('ptt', { op: 'renew', lease }).catch(end), 1000);
    } catch (e) { fail(e); await end(); }
  };
  b.onpointerdown = begin; b.onpointerup = end; b.onpointercancel = end; b.onlostpointercapture = end;
  b.onkeydown = e => { if (!e.repeat && (e.key === ' ' || e.key === 'Enter')) begin(e); };
  b.onkeyup = end;
  const visibility = () => { if (document.hidden) end(); };
  const observer = typeof IntersectionObserver === 'function' ? new IntersectionObserver(entries => { if (entries.some(entry => !entry.isIntersecting)) end(); }) : null;
  observer?.observe(b);
  window.addEventListener('blur', end); document.addEventListener('visibilitychange', visibility);
  return {
    render(data) {
      device = options.device || data.audio?.devices?.find(d => d.direction === 'input' && d.default)?.id;
      b.disabled = power ? !data.allowPower : !device;
      if (b.disabled) status.textContent = power ? 'Enable power controls in desktop settings' : 'No microphone is available';
    },
    destroy() { end(); observer?.disconnect(); window.removeEventListener('blur', end); document.removeEventListener('visibilitychange', visibility); },
  };
}

function mediaView(box, options, mode) {
  const title = h('div', 'deck-track'), source = h('div', 'deck-caption'); box.append(title, source);
  let current = null;
  const call = (op, value) => current ? deckCommand('media', { id: current.id, op, value }) : Promise.reject(new Error('No media player selected'));
  const controls = h('div', 'deck-toolbar'); box.append(controls);
  let seek = null, shuffle = null, repeat = null;
  if (mode === 'seek') { seek = slider('Playback position', value => call('seek', value)); box.append(seek.input, seek.number); }
  else if (mode === 'modes') {
    shuffle = button('Shuffle', () => call('shuffle', current.shuffle ? 0 : 1));
    repeat = button('Repeat', () => call('repeat', current.repeat === 'None' ? 1 : current.repeat === 'Track' ? 2 : 0));
    controls.append(shuffle, repeat);
  } else controls.append(button('⏮', () => call('prev'), 'Previous track'), button('⏯', () => call('playpause'), 'Play or pause'), button('⏭', () => call('next'), 'Next track'));
  return data => {
    current = selectedPlayer(data, options);
    title.textContent = current?.title || 'No media player selected'; source.textContent = current?.name || 'Choose a source in Media sources';
    controls.querySelectorAll('button').forEach(b => b.disabled = !current);
    if (seek) { seek.input.max = Math.max(1, current?.duration || 1); seek.set(current?.position || 0); seek.number.textContent = `${Math.floor((current?.position || 0) / 1000)} / ${Math.floor((current?.duration || 0) / 1000)} s`; seek.input.disabled = !current?.canSeek; }
    if (shuffle) { shuffle.disabled = !current?.canShuffle; shuffle.setAttribute('aria-pressed', String(!!current?.shuffle)); }
    if (repeat) { repeat.disabled = !current?.canRepeat; repeat.textContent = 'Repeat: ' + (current?.repeat || 'None'); }
  };
}

const groups = {
  'app-dock': ['app', 'shortcut'], hotkeys: ['keys'], 'macro-pad': ['macro'], 'window-layouts': ['window-layout'],
  'project-launcher': ['project', 'app'], snippets: ['snippet', 'text'], presentation: ['presentation'],
  'display-profiles': ['display-profile'], 'focus-profile': ['focus', 'macro'],
  'meeting-controls': ['meeting'], 'discord-controls': ['discord'], 'meeting-profile': ['meeting', 'macro'],
  'task-runner': ['task'], 'dev-servers': ['server'], services: ['service'], 'workflow-runner': ['workflow'], 'script-pad': ['task', 'shortcut'],
  soundboard: ['sound'], radio: ['radio'], playlists: ['playlist'], 'audio-routing': ['audio-route'],
  'external-mixer': ['mixer', 'mixer-mute'], 'midi-pad': ['midi'], 'osc-pad': ['osc'],
};
function actionsView(box, options, type) {
  const grid = h('div', 'deck-actions'); box.append(grid); let signature = '';
  return data => {
    const permitted = groups[type] || [];
    let items = [...(data.actions || []), ...(data.shortcuts || []).map(s => ({ ...s, id: 'shortcut:' + s.id }))]
      .filter(item => permitted.includes(item.kind));
    if(type === 'focus-profile' || type === 'meeting-profile') items.push(...(data.profiles || []));
    if (options.ids?.length) items = items.filter(item => options.ids.includes(item.id));
    const key = JSON.stringify(items); if (key === signature) return; signature = key;
    if (!items.length) { empty(grid, 'Assign controls in the computer’s Control Deck settings'); return; }
    grid.replaceChildren(...items.map(item => {
      const label = h('span', '', item.name), state = h('small', '', item.state || 'Ready');
      const b = button('', async () => { await deckCommand(item.kind === 'profile' ? 'profile' : 'action', { id: item.id, op: item.kind === 'radio' ? 'toggle' : (type === 'app-dock' ? 'focus' : '') }); signature = ''; });
      b.disabled = item.available === false;
      b.append(label, state);
      if (type === 'dev-servers') return h('div', 'deck-task', b, button('Stop', () => deckCommand('action', { id: item.id, op: 'stop' }), 'Stop ' + item.name));
      if (type === 'soundboard') return h('div', 'deck-task', b, button('Stop', () => deckCommand('sound-stop', { id: item.id }), 'Stop ' + item.name));
      return b;
    }));
  };
}

function radioInitials(label) {
  const parts = (label || 'FM').replace(/\([^)]*\)/g, '').trim().split(/[\s\-_.]+/).filter(Boolean);
  if (!parts.length) return 'FM';
  const chars = (s, n) => Array.from(s).slice(0, n).join('');
  return parts.length > 1 ? (chars(parts[0], 1) + chars(parts[1], 1)).toUpperCase() : chars(parts[0], 2).toUpperCase();
}

function radioView(box, options) {
  const isCarousel = options.view === 'carousel';
  box.classList.add('deck-radio-widget', isCarousel ? 'is-carousel' : 'is-grid');
  const wrap = h('div', isCarousel ? 'radio-carousel' : 'radio-grid-wrap');
  const track = h('div', isCarousel ? 'radio-carousel-track' : 'radio-grid');
  let prevBtn = null, nextBtn = null;

  const syncNav = () => {
    if (!isCarousel) return;
    const max = Math.max(0, track.scrollWidth - track.clientWidth);
    const canScroll = max > 6;
    wrap.classList.toggle('can-scroll', canScroll);
    if (prevBtn) prevBtn.disabled = !canScroll || track.scrollLeft <= 4;
    if (nextBtn) nextBtn.disabled = !canScroll || track.scrollLeft >= max - 4;
  };

  if (isCarousel) {
    const stepScroll = (dir) => {
      haptic();
      const step = Math.max(130, Math.round(track.clientWidth * 0.72));
      track.scrollBy({ left: dir * step, behavior: 'smooth' });
    };
    prevBtn = h('button', 'radio-nav radio-nav-prev', '‹');
    prevBtn.type = 'button';
    prevBtn.setAttribute('aria-label', 'Previous stations');
    prevBtn.onclick = (e) => { e.stopPropagation(); stepScroll(-1); };

    nextBtn = h('button', 'radio-nav radio-nav-next', '›');
    nextBtn.type = 'button';
    nextBtn.setAttribute('aria-label', 'Next stations');
    nextBtn.onclick = (e) => { e.stopPropagation(); stepScroll(1); };

    track.addEventListener('scroll', syncNav, { passive: true });

    let startX = 0, startY = 0;
    track.addEventListener('touchstart', (e) => {
      if (e.touches.length !== 1) return;
      startX = e.touches[0].clientX;
      startY = e.touches[0].clientY;
    }, { passive: true });
    track.addEventListener('touchmove', (e) => {
      if (e.touches.length !== 1) return;
      const dx = e.touches[0].clientX - startX;
      const dy = e.touches[0].clientY - startY;
      if (Math.abs(dx) <= Math.abs(dy)) return;
      const max = Math.max(0, track.scrollWidth - track.clientWidth);
      if (max <= 1 || (track.scrollLeft <= 0 && dx > 0) || (track.scrollLeft >= max - 1 && dx < 0)) {
        if (e.cancelable) e.preventDefault();
      }
    }, { passive: false });

    let dragDown = false, dragged = false, dragStartX = 0, dragScrollLeft = 0;
    track.addEventListener('pointerdown', (e) => {
      if (e.pointerType !== 'mouse' || e.button !== 0) return;
      dragDown = true; dragged = false;
      dragStartX = e.clientX; dragScrollLeft = track.scrollLeft;
    });
    track.addEventListener('pointermove', (e) => {
      if (!dragDown) return;
      const dx = e.clientX - dragStartX;
      if (Math.abs(dx) > 6) {
        dragged = true;
        track.classList.add('is-dragging');
      }
      if (dragged) track.scrollLeft = dragScrollLeft - dx;
    });
    const endDrag = () => {
      if (!dragDown) return;
      dragDown = false;
      track.classList.remove('is-dragging');
      if (dragged) setTimeout(() => { dragged = false; }, 0);
    };
    track.addEventListener('pointerup', endDrag);
    track.addEventListener('pointercancel', endDrag);
    track.addEventListener('pointerleave', endDrag);
    track.addEventListener('click', (e) => {
      if (dragged) { e.preventDefault(); e.stopPropagation(); dragged = false; }
    }, true);
    track.addEventListener('wheel', (e) => {
      if (Math.abs(e.deltaY) > Math.abs(e.deltaX) && track.scrollWidth > track.clientWidth) {
        const max = Math.max(0, track.scrollWidth - track.clientWidth);
        const canLeft = e.deltaY < 0 && track.scrollLeft > 0;
        const canRight = e.deltaY > 0 && track.scrollLeft < max;
        if (canLeft || canRight) {
          e.preventDefault();
          track.scrollLeft += e.deltaY;
        }
      }
    }, { passive: false });

    wrap.append(prevBtn, track, nextBtn);
  } else {
    wrap.append(track);
  }
  box.append(wrap);

  const cards = new Map();
  let latestItems = [];
  let optimisticId = null;
  let optimisticUntil = 0;
  let lastScrolledPlayingId = '';
  let cmdSeq = 0;
  let commandBusy = false;

  const effectivePlayingId = () => {
    if (optimisticId !== null && performance.now() < optimisticUntil) return optimisticId;
    optimisticId = null;
    return latestItems.find(i => i.state === 'Playing')?.id || '';
  };

  const syncCardsState = (allowAutoScroll) => {
    const activeId = effectivePlayingId();
    for (const [id, entry] of cards.entries()) {
      const playing = id === activeId;
      const unavail = entry.item.available === false;
      const subState = unavail && entry.item.state ? entry.item.state : '';
      entry.el.classList.toggle('is-playing', playing);
      entry.el.setAttribute('aria-pressed', String(playing));
      entry.el.disabled = unavail;
      entry.el.title = subState || entry.item.name;
      entry.name.textContent = subState ? `${entry.item.name} · ${subState}` : entry.item.name;
      entry.fallback.textContent = radioInitials(entry.item.name);
      entry.el.setAttribute('aria-label', entry.item.name + (playing ? ' (Playing)' : subState ? ` (${subState})` : ''));
    }
    if (isCarousel && allowAutoScroll && activeId && activeId !== lastScrolledPlayingId) {
      const activeEntry = cards.get(activeId);
      if (activeEntry) {
        const firstMount = !lastScrolledPlayingId;
        lastScrolledPlayingId = activeId;
        requestAnimationFrame(() => {
          const target = Math.max(0, activeEntry.el.offsetLeft - (track.clientWidth - activeEntry.el.offsetWidth) / 2);
          track.scrollTo({ left: target, behavior: firstMount ? 'auto' : 'smooth' });
          syncNav();
        });
      }
    } else if (!activeId) {
      lastScrolledPlayingId = '';
    }
  };

  const filterItems = (actions) => {
    const list = (actions || []).filter(item => item.kind === 'radio');
    if (!options.ids?.length) return list;
    const byId = new Map(list.map(item => [item.id, item]));
    return options.ids.map(id => byId.get(id)).filter(Boolean);
  };

  return (data) => {
    const items = filterItems(data.actions);
    latestItems = items;
    if (!items.length) {
      cards.clear();
      empty(track, 'Assign radio streams in the computer’s Control Deck settings');
      syncNav();
      return;
    }
    const emptyEl = track.querySelector('.deck-empty');
    if (emptyEl) emptyEl.remove();

    const nodes = items.map(item => {
      let entry = cards.get(item.id);
      if (!entry) {
        const fallback = h('span', 'radio-fallback', radioInitials(item.name));
        const img = h('img', 'radio-logo');
        img.alt = '';
        img.loading = 'lazy';
        img.decoding = 'async';
        img.draggable = false;
        const eq = h('span', 'radio-eq', h('i'), h('i'), h('i'));
        eq.setAttribute('aria-hidden', 'true');
        const logoBox = h('div', 'radio-logo-box', fallback, img, eq);
        img.onload = () => logoBox.classList.add('has-logo');
        img.onerror = () => logoBox.classList.remove('has-logo');

        const name = h('span', 'radio-name', item.name);
        const el = h('button', 'radio-card');
        el.type = 'button';
        el.append(logoBox, name);

        entry = { el, img, name, fallback, logoBox, item, busy: false, logoKey: '' };
        el.onclick = async () => {
          if (commandBusy || entry.busy) return;
          haptic();
          const seq = ++cmdSeq;
          const wasPlaying = effectivePlayingId() === entry.item.id;
          optimisticId = wasPlaying ? '' : entry.item.id;
          optimisticUntil = performance.now() + 3000;
          lastScrolledPlayingId = optimisticId;
          syncCardsState(false);
          if (isCarousel && optimisticId) {
            const target = Math.max(0, el.offsetLeft - (track.clientWidth - el.offsetWidth) / 2);
            track.scrollTo({ left: target, behavior: 'smooth' });
          }
          commandBusy = true;
          entry.busy = true;
          try {
            await deckCommand('action', { id: entry.item.id, op: 'toggle' });
            const fresh = await deckState(true);
            if (seq !== cmdSeq) return;
            latestItems = filterItems(fresh.actions);
            for (const next of latestItems) { const c = cards.get(next.id); if (c) c.item = next; }
            optimisticId = null; optimisticUntil = 0;
            syncCardsState(false);
          } catch (e) {
            if (seq === cmdSeq) {
              optimisticId = null; optimisticUntil = 0;
              syncCardsState(false);
            }
            fail(e);
          } finally {
            commandBusy = false;
            entry.busy = false;
          }
        };
        cards.set(item.id, entry);
      } else {
        entry.item = item;
      }
      return entry.el;
    });

    reconcile(track, nodes);
    for (const [id, entry] of cards.entries()) {
      const rev = entry.item.rev || entry.item.name || '';
      const nextKey = `${id}:${rev}`;
      if (entry.logoKey !== nextKey) {
        entry.logoKey = nextKey;
        entry.logoBox.classList.remove('has-logo');
        entry.img.src = `${API}/controls/radio-logo?id=${encodeURIComponent(id)}&v=${encodeURIComponent(rev)}`;
      }
    }
    for (const id of [...cards.keys()]) {
      if (!items.some(i => i.id === id)) cards.delete(id);
    }
    syncCardsState(true);
    requestAnimationFrame(syncNav);
  };
}

function mixerView(box,options){
  const root=h('div','deck-list');box.append(root);const rows=new Map();
  return data=>{
    const items=(data.actions || []).filter(a=>['mixer','mixer-mute'].includes(a.kind)&&(!options.ids?.length || options.ids.includes(a.id)));
    if(!items.length){empty(root,'Configure Voicemeeter bindings in desktop settings');return;}
    reconcile(root,items.map(item=>{
      let row=rows.get(item.id);
      if(!row){
        const el=h('div','deck-channel'),label=h('div','deck-channel-name',item.name),state=h('div','deck-caption');
        el.append(label,state);row={el,label,state,item};
        if(item.kind==='mixer'){
          row.slider=slider(item.name+' gain',value=>deckCommand('action',{id:item.id,value}),12);row.slider.input.min=-60;
          el.append(row.slider.input,row.slider.number);
        }else {row.toggle=button('Toggle',()=>deckCommand('action',{id:item.id,value:Number(row.item.state)>0?0:1}));el.append(row.toggle);}
        rows.set(item.id,row);
      }
      row.item=item;row.label.textContent=item.name;row.state.textContent=item.available===false?item.state || 'Not connected':'';
      if(row.slider){row.slider.input.disabled=item.available===false;row.slider.set(Number(item.state)||0);row.slider.number.textContent=item.available===false?'n/a':item.state+' dB';}
      if(row.toggle){row.toggle.disabled=item.available===false;row.toggle.textContent=Number(item.state)>0?'Disable':'Enable';row.toggle.setAttribute('aria-pressed',String(Number(item.state)>0));}
      return row.el;
    }));
    for(const id of rows.keys())if(!items.some(i=>i.id===id))rows.delete(id);
  };
}

function listening(box) {
  const state = h('div', 'deck-caption', 'Listening is off'); let ctl = null, context = null, next = 0, check = 0;
  const stop = () => { ctl?.abort(); ctl = null; context?.close(); context = null; clearInterval(check); toggle.textContent = 'Listen'; state.textContent = 'Listening is off'; };
  const toggle = button('Listen', async () => {
    if (ctl) { stop(); return; }
    const Audio = window.AudioContext || window.webkitAudioContext;
    if (!Audio || !window.ReadableStream) throw new Error('This WebView does not support live audio');
    context = new Audio(); await context.resume(); ctl = new AbortController(); toggle.textContent = 'Stop listening';
    state.textContent = 'Listening to computer audio';
    const own = ctl;
    check = setInterval(() => { const r = box.getBoundingClientRect(); if (document.hidden || r.right <= 0 || r.left >= innerWidth) stop(); }, 250);
    (async () => {
      try {
        const response = await fetch(API + '/controls/listen', { headers: PANEL_HEADERS, signal: own.signal });
        if (!response.ok || !response.body) throw new Error('Listening is unavailable');
        const reader = response.body.getReader(), decoder = new TextDecoder(); let text = '';
        while (ctl === own) {
          const chunk = await reader.read(); if (chunk.done) break;
          text += decoder.decode(chunk.value, { stream: true });
          if (text.length > 1e6) throw new Error('Invalid audio stream');
          let newline;
          while ((newline = text.indexOf('\n')) >= 0) {
            const packet = JSON.parse(text.slice(0, newline)); text = text.slice(newline + 1);
            if (!packet.data || !context) continue;
            const raw = Uint8Array.from(atob(packet.data), c => c.charCodeAt(0)); const view = new DataView(raw.buffer);
            const bytes = packet.bits / 8, frames = raw.length / bytes / packet.channels;
            if (!Number.isInteger(frames) || ![16, 32].includes(packet.bits)) throw new Error('Unsupported audio format');
            const buffer = context.createBuffer(packet.channels, frames, packet.rate);
            for (let channel = 0; channel < packet.channels; channel++) {
              const samples = buffer.getChannelData(channel);
              for (let frame = 0; frame < frames; frame++) {
                const offset = (frame * packet.channels + channel) * bytes;
                samples[frame] = packet.encoding === 'IeeeFloat' ? view.getFloat32(offset, true)
                  : packet.bits === 16 ? view.getInt16(offset, true) / 32768 : view.getInt32(offset, true) / 2147483648;
              }
            }
            const source = context.createBufferSource(); source.buffer = buffer; source.connect(context.destination);
            if (next < context.currentTime || next > context.currentTime + 0.5) next = context.currentTime + 0.06;
            source.start(next); next += buffer.duration;
          }
        }
      } catch (e) { if (!own.signal.aborted) fail(e); }
      finally { if (ctl === own) stop(); }
    })();
  });
  toggle.disabled = true;
  box.append(toggle, state);
  return { render(data) { toggle.disabled = !data.allowListening; if (!data.allowListening) state.textContent = 'Enable listening in desktop settings'; }, destroy: stop };
}

export function registerDeckWidget(type, title, mode = type) {
  W[type] = {
    needs: ['tick'],
    create(box, { o }) {
      box.classList.add('control-deck'); box.append(h('div', 'w-title', o.title || title));
      let render = null, component = null, alive = true, loading = false;
      if (['apps', 'app', 'mic', 'output', 'balance', 'meter'].includes(mode)) render = audioView(box, o, mode);
      else if(mode === 'external-mixer')render=mixerView(box,o);
      else if (mode === 'inputs' || mode === 'outputs') render = chooser(box, o, mode === 'inputs' ? 'input' : 'output');
      else if (mode === 'sources' || mode === 'plans') render = chooser(box, o, mode === 'sources' ? 'media' : 'plans');
      else if (mode === 'ptt') component = hold(box, o);
      else if (mode === 'listen') component = listening(box);
      else if (['remote', 'seek', 'modes'].includes(mode)) render = mediaView(box, o, mode);
      else if (mode === 'panic') box.append(button('Mute audio + microphones', () => deckCommand('panic')));
      else if (mode === 'desktops') box.append(button('← Desktop', () => deckCommand('desktop', { value: -1 })), button('Desktop →', () => deckCommand('desktop', { value: 1 })));
      else if (mode === 'system') box.append(button('Lock', () => deckCommand('lock')), button('Display off', () => deckCommand('monitor-off')));
      else if (mode === 'power') {
        const group = h('div', 'deck-power-grid'); box.append(group);
        const components = ['sleep', 'restart', 'shutdown'].map(action => { const item = h('div', 'deck-power-item'); group.append(item); return hold(item, o, action); });
        component = { render(data) { components.forEach(c => c.render(data)); }, destroy() { components.forEach(c => c.destroy()); } };
      } else if (mode === 'duck') {
        let enabled = false; const b = button('Enable ducking', () => deckCommand('duck', { op: enabled ? 'off' : 'on', value: o.level ?? 25 })); box.append(b);
        render = data => { enabled = data.ducking; b.textContent = enabled ? 'Disable ducking' : 'Enable ducking'; b.setAttribute('aria-pressed', String(enabled)); };
      } else if (mode === 'profiles') {
        const list = h('div', 'deck-actions'); box.append(list); let key = '';
        render = data => { const next = JSON.stringify(data.profiles); if (next === key) return; key = next;
          if (!data.profiles?.length) empty(list, 'Save an audio profile in desktop settings');
          else list.replaceChildren(...data.profiles.map(p => button(p.name, () => deckCommand('profile', { id: p.id })))); };
      } else if (mode === 'sleep') {
        let player = null; const caption = h('div', 'deck-caption');
        box.append(button(`Sleep in ${o.minutes || 30} min`, () => deckCommand('sleep', { id: player?.id || '', value: o.minutes || 30 })),
          button('Cancel timer', () => deckCommand('sleep', { id: player?.id || '', value: 0 })), caption);
        render = data => { player = selectedPlayer(data, o); const timer = data.sleep?.find(t => t.id === player?.id);
          caption.textContent = timer ? `Pausing ${player?.name || 'player'} in ${Math.max(0, Math.ceil((timer.at - Date.now()) / 60000))} min` : 'No sleep timer'; };
      } else if (mode === 'brightness') {
        const list = h('div', 'deck-list'); box.append(list); const rows = new Map();
        render = data => {
          const items = (data.monitors || []).filter(m => !o.device || m.id === o.device);
          if (!items.length) { empty(list, 'No monitors detected'); return; }
          reconcile(list, items.map(m => { if (!rows.has(m.id)) { const s = slider(m.name + ' brightness', value => deckCommand('brightness', { id: m.id, value }));
            rows.set(m.id, { ...s, el: h('div', 'deck-channel', h('div', 'deck-channel-name', m.name), s.input, s.number) }); }
            const row = rows.get(m.id); row.input.disabled = m.brightness == null; row.set(m.brightness || 0); if (m.brightness == null) row.number.textContent = 'DDC/CI unavailable'; return row.el; }));
        };
      } else if (mode === 'radio') render = radioView(box, o);
      else render = actionsView(box, o, type);
      return {
        async update() {
          if (loading || !alive || (!render && !component)) return;
          loading = true;
          try { const data = await deckState(); if (alive) { render?.(data); component?.render(data); box.classList.remove('deck-offline'); } }
          catch (_) { if (alive) box.classList.add('deck-offline'); }
          finally { loading = false; }
        },
        destroy() { alive = false; component?.destroy(); },
      };
    },
  };
}

for (const [type, title, mode] of [
  ['audio-mixer', 'Application mixer', 'apps'], ['app-volume', 'Application volume', 'app'],
  ['device-volume', 'Device volume', 'output'], ['audio-balance', 'Channel balance', 'balance'], ['audio-meter', 'Audio levels', 'meter'],
  ['panic-mute', 'Emergency mute', 'panic'], ['microphone', 'Microphone', 'mic'], ['audio-inputs', 'Microphone selection', 'inputs'],
  ['audio-outputs', 'Output selection', 'outputs'], ['push-to-talk', 'Push to talk', 'ptt'], ['audio-profiles', 'Audio profiles', 'profiles'],
  ['audio-ducking', 'Audio ducking', 'duck'], ['media-sources', 'Media sources', 'sources'], ['media-remote', 'Media remote', 'remote'],
  ['media-seek', 'Playback position', 'seek'], ['media-modes', 'Shuffle and repeat', 'modes'], ['media-sleep', 'Music sleep timer', 'sleep'],
  ['brightness', 'Display brightness', 'brightness'], ['power-plans', 'Power plans', 'plans'], ['desktops', 'Virtual desktops', 'desktops'],
  ['system-keys', 'Lock and displays', 'system'], ['power-controls', 'Power controls', 'power'], ['pc-listen', 'Listen to PC audio', 'listen'],
]) registerDeckWidget(type, title, mode);
for (const type of Object.keys(groups)) registerDeckWidget(type, type.split('-').map(s => s[0].toUpperCase() + s.slice(1)).join(' '));

W['meeting-status'] = W.onair;

async function integrationCommand(path, values = {}) {
  const result = await api('/controls/' + path + '?' + new URLSearchParams(values), 'POST', 20000);
  if (!result.ok) throw new Error(result.error || 'Integration refused this control');
  return result;
}
function integrationWidget(type, title, endpoint, create) {
  W[type] = { needs: ['tick'], create(box, {o}) {
    box.classList.add('control-deck'); box.append(h('div', 'w-title', o.title || title));
    const root = h('div', 'deck-list'); box.append(root);
    let alive = true, busy = false, signature = '', data = null;
    return { async update() {
      if (!alive || busy) return; busy = true;
      try {
        data = await api('/controls/' + endpoint, 'GET', 20000); if (!alive) return;
        if (root.contains(document.activeElement) && document.activeElement.matches('input,textarea,select') || root.matches(':active') || root.querySelector(':active')) return;
        const key = JSON.stringify(data); if (key === signature) return; signature = key;
        if (!data.connected) { empty(root, data.error || 'Connect this integration in desktop settings'); return; }
        root.replaceChildren();
        create(root, data, (action, args) => integrationCommand(endpoint === 'obs' ? 'obs/' + action : 'spotify/action/' + action, args).then(()=>{signature='';}), o);
      } catch (error) { if (alive) empty(root, error.message || 'Integration is unavailable'); }
      finally { busy = false; }
    }, destroy() { alive = false; } };
  } };
}
const timeText = ms => { const s = Math.floor((ms || 0) / 1000); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };
integrationWidget('obs-mixer', 'OBS mixer', 'obs', (root, data, send) => {
  if (!data.inputs?.length) { empty(root, 'No OBS inputs found'); return; }
  for (const input of data.inputs) {
    const s = slider(input.name + ' volume', value => send('volume', {id:input.id,value}));
    s.set(input.volume || 0); s.input.disabled = input.volume == null;
    const mute = button(input.muted ? 'Unmute' : 'Mute', ()=>send('mute',{id:input.id,enabled:!input.muted}), 'Mute ' + input.name);
    mute.disabled = input.muted == null;
    root.append(h('div','deck-channel',h('div','deck-channel-name',input.name),s.input,h('div','deck-channel-foot',s.number,mute)));
  }
});
integrationWidget('obs-scenes', 'OBS scenes', 'obs', (root, data, send) => {
  for (const name of data.scenes || []) { const b=button(name,()=>send('scene',{id:name}));b.classList.toggle('selected',name===data.scene);root.append(b); }
});
integrationWidget('obs-sources', 'OBS sources', 'obs', (root, data, send) => {
  if (!data.sources?.length) empty(root,'No sources in the current scene');
  for(const source of data.sources || []) { const b=button((source.enabled?'● ':'○ ')+source.name,()=>send('source',{id:source.id,enabled:!source.enabled}));b.setAttribute('aria-pressed',String(source.enabled));root.append(b); }
});
integrationWidget('obs-recording', 'OBS recording and stream', 'obs', (root,data,send)=>{
  root.append(h('div','deck-track','Recording '+timeText(data.recordMs)),h('div','deck-caption','Stream '+timeText(data.streamMs)),
    button(data.recording?'Stop recording':'Start recording',()=>send('record')),
    button(data.streaming?'Stop stream':'Start stream',()=>send('stream')));
  const pause=button(data.recordPaused?'Resume recording':'Pause recording',()=>send('pause'));pause.disabled=!data.recording;root.append(pause);
});
integrationWidget('obs-replay', 'OBS replay buffer', 'obs', (root,data,send)=>{
  const toggle=button(data.replayActive?'Stop replay buffer':'Start replay buffer',()=>send('replay'));toggle.disabled=!data.replayAvailable;
  const save=button('Save replay',()=>send('save-replay'));save.disabled=!data.replayActive;root.append(toggle,save);
});
integrationWidget('obs-transition', 'OBS transition', 'obs', (root,data,send)=>{
  for(const t of data.transitions || []) {const b=button(t.transitionName,()=>send('transition',{id:t.transitionName}));b.classList.toggle('selected',data.transition===t.transitionName);root.append(b);}
  const s=slider('Transition duration',value=>send('duration',{value}),10000);s.input.min=50;s.input.step=50;s.set(data.duration || 300);s.input.disabled=data.duration==null;root.append(s.input,s.number);
});
integrationWidget('obs-camera', 'OBS virtual camera', 'obs', (root,data,send)=>{
  const b=button(data.cameraActive?'Stop virtual camera':'Start virtual camera',()=>send('camera'));b.disabled=!data.cameraAvailable;root.append(b);
});
integrationWidget('media-queue', 'Spotify queue', 'spotify/state', (root,data,send)=>{
  root.append(h('div','deck-track',data.current?.name || 'Nothing playing'));
  for(const item of data.queue || []) root.append(button(item.name+(item.artist?' · '+item.artist:''),()=>send('play-queued',{id:item.id})));
  if(!data.queue?.length)root.append(h('div','deck-empty','The queue is empty'));
});
integrationWidget('media-favorites', 'Spotify favorites', 'spotify/state', (root,data,send)=>{
  root.append(h('div','deck-track',data.current?.name || 'Nothing playing'),h('div','deck-caption',data.current?.artist || ''));
  const b=button('Save to library',()=>send('favorite'));b.disabled=!data.current?.id;root.append(b);
});
W['meeting-timer'] = {needs:['tick'],create(box,{o}){
  const key='meeting-timer:'+ (o.timerId || 'default');
  const clock=h('div','deck-track');box.classList.add('control-deck');box.append(h('div','w-title',o.title || 'Meeting timer'),clock);
  let state=store.get(key,{elapsed:0,started:0});
  const persist=()=>store.set(key,state);
  const start=button(state.started?'Pause':'Start',()=>{state=store.get(key,state);if(state.started){state.elapsed+=Date.now()-state.started;state.started=0;}else state.started=Date.now();persist();render();});
  box.append(start,button('Reset',()=>{state={elapsed:0,started:0};persist();render();}));
  function render(){state=store.get(key,state);const elapsed=state.elapsed+(state.started?Date.now()-state.started:0);clock.textContent=timeText(elapsed);start.textContent=state.started?'Pause':'Start';start.setAttribute('aria-label',start.textContent);clock.classList.toggle('deck-overdue',elapsed>=(o.minutes || 30)*60000);}
  render();return {update:render};
}};
