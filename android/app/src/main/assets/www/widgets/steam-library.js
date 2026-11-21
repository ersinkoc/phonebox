import { API, PANEL_HEADERS, api, h, haptic, t, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// ---------------------------------------------------------------- Steam library (read from Steam's files by the agent)

/** Up to two letters of a game's name, shown until (or instead of) its cover. */
export const gameInitials = (name) => String(name || '?').split(/[\s:._–-]+/).filter(Boolean).slice(0, 2)
  .map((w) => Array.from(w)[0]).join('').toUpperCase() || '?';

/** A game's cover from Steam's library cache on the computer, over its initials. */
export function gameCover(game) {
  const wrap = h('div', 'sl-cover', h('span', 'sl-initials', gameInitials(game.name)));
  if (game.cover) {
    const img = h('img');
    img.alt = '';
    img.decoding = 'async';
    img.loading = 'lazy';
    img.addEventListener('load', () => wrap.classList.add('has-art'), { once: true });
    img.addEventListener('error', () => img.remove(), { once: true });
    img.src = `${API}/steam/cover?id=${encodeURIComponent(game.id)}`;
    wrap.append(img);
  }
  return wrap;
}

/** What a tile says under the name: playing now, an update and its progress, or nothing. */
export function gameStatus(game, running) {
  if (running) return t('w.steamlib.playing');
  const n = game.progress != null ? Math.round(game.progress * 100) : null;
  if (game.state === 'updating') return n != null ? t('w.steamlib.updatingPct', { n }) : t('w.steamlib.updating');
  if (game.state === 'paused') return n != null ? t('w.steamlib.pausedPct', { n }) : t('w.steamlib.paused');
  if (game.state === 'update') return t('w.steamlib.queued');
  return '';
}

/** The agent answers a refused start with a code; the phone says it in the panel language. */
const LAUNCH_ERRORS = {
  off: () => t('w.steamlib.launchOff'),
  nosteam: () => t('w.steamlib.notFound'),
  notinstalled: (game) => t('w.steamlib.notInstalled', { game }),
};

/**
 * The games a widget shows: its favorites first, in the order they were picked, then (unless "only favorites" is on)
 * the rest, most recently played first as the agent sends them; at most `limit`.
 */
export function pickGames(games, favorites, onlyFavorites, limit) {
  const favs = (Array.isArray(favorites) ? favorites : []).map(String);
  const byId = new Map((games || []).map((g) => [g.id, g]));
  const first = favs.map((id) => byId.get(id)).filter(Boolean);
  const rest = onlyFavorites ? [] : (games || []).filter((g) => !favs.includes(g.id));
  return [...first, ...rest].slice(0, limit);
}

// The installed Steam games: the favorites picked in the widget's options first, then the most recently played, with
// their covers and update progress. Starting a game from
// the phone is off by default and decided per widget (its "Let the phone start games"): the agent checks the option of
// the widget named in the request, not of any other. With it on, a tap starts the game through Steam, and with "ask
// first" on (the default) the first tap only arms the tile. Nothing needs a Steam account key.
W['steam-library'] = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    const canLaunch = o.allowLaunch === true; // this widget's own option; the agent checks it again for this widget
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const confirm = o.confirm !== false;
    const limit = Math.max(1, Math.min(60, Math.round(+o.limit || 8)));
    const favorites = new Set((Array.isArray(o.favorites) ? o.favorites : []).map(String));
    let data = null, failed = false, armed = null, armTimer = 0, busy = null, lastKey = '';
    const count = h('span', 'sl-count');
    const grid = h('div', 'sl-grid');
    const title = h('div', 'w-title', o.title || t('w.steamlib.title'), count);
    box.append(title, grid);

    const load = async () => {
      try { data = await api('/steam/library', 'GET', 10_000); failed = false; return true; }
      catch (_) { failed = true; return false; }
    };
    const disarm = () => { clearTimeout(armTimer); if (armed) { armed = null; render(true); } };
    const launch = async (g) => {
      haptic();
      busy = g.id; render(true);
      try {
        const r = await fetch(`${API}/steam/launch?id=${encodeURIComponent(g.id)}&${wq}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        const j = await r.json().catch(() => ({}));
        if (r.ok) toast(t('w.steamlib.starting', { game: g.name }), 'ok');
        else toast((LAUNCH_ERRORS[j.code] || (() => t('w.steamlib.startFailed', { game: g.name })))(g.name), 'err');
      } catch (_) { toast(t('w.unavailable'), 'err'); }
      busy = null; render(true);
    };
    const tile = (g) => {
      const running = data?.running === g.id;
      const isArmed = armed === g.id;
      const status = isArmed ? t('w.steamlib.tapAgain') : gameStatus(g, running);
      const b = h('button', 'sl-tile' + (running ? ' running' : '') + (favorites.has(g.id) ? ' fav' : '') + (isArmed ? ' armed' : '') + (busy === g.id ? ' busy' : '')
        + (g.state !== 'ready' ? ' sl-' + g.state : ''), gameCover(g),
        h('span', 'sl-name', g.name), status ? h('span', 'sl-status', status) : null);
      b.type = 'button';
      b.title = g.name;
      b.dataset.id = g.id;
      b.setAttribute('aria-label', status ? `${g.name}, ${status}` : g.name);
      if (g.progress != null && g.state !== 'ready') {
        const bar = h('i', 'sl-progress');
        bar.style.width = Math.round(g.progress * 100) + '%';
        b.append(bar);
      }
      b.addEventListener('click', () => {
        if (!canLaunch) { toast(t('w.steamlib.launchOff')); return; }
        if (running) { toast(t('w.steamlib.alreadyRunning', { game: g.name })); return; }
        if (!confirm || armed === g.id) { clearTimeout(armTimer); armed = null; launch(g); return; }
        haptic();
        armed = g.id;
        clearTimeout(armTimer);
        armTimer = setTimeout(disarm, 4000);
        render(true);
      });
      return b;
    };
    const message = (text) => grid.replaceChildren(h('div', 'empty', text));
    const render = (force = false) => {
      const games = pickGames(data?.games, o.favorites, o.onlyFavorites === true, limit);
      const key = JSON.stringify([games, data?.running, data?.enabled, data?.found, data?.error, failed, armed, busy]);
      if (!force && key === lastKey) return;
      lastKey = key;
      count.textContent = data?.games?.length ? ' ' + data.games.length : ''; // how many are installed
      box.classList.toggle('sl-can-launch', canLaunch);
      if (!data) { message(t(failed ? 'w.unavailable' : 'w.loading')); return; }
      if (!data.enabled) { message(t('w.steamlib.off')); return; }
      if (!data.found) { message(t('w.steamlib.notFound')); return; }
      if (data.error && !games.length) { message(t('w.steamlib.unreadable')); return; }
      if (!games.length) { message(t(data.games?.length ? 'w.steamlib.noFavorites' : 'w.steamlib.empty')); return; }
      // a tile that had the focus (a keyboard or switch user) keeps it in its new copy, so a second Enter confirms
      const focused = grid.contains(document.activeElement) ? document.activeElement.dataset?.id : null;
      grid.replaceChildren(...games.map(tile));
      if (focused) grid.querySelector(`.sl-tile[data-id="${CSS.escape(focused)}"]`)?.focus();
    };
    // an update under way is followed every 5 s; otherwise the list changes rarely and is read every 30 s
    let fast = false;
    const tick = every(5000, async () => {
      const ok = await load();
      render();
      fast = !!data?.games?.some((g) => g.state === 'updating');
      return ok;
    }, 5000);
    const slow = every(30_000, () => tick(), 5000);
    return { update: () => { (fast || !data ? tick : slow)(); render(); }, destroy: () => clearTimeout(armTimer) };
  },
};
