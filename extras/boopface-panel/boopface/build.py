#!/usr/bin/env python3
"""BoopFace page builder: face + skin browser (🎨) + settings (⚙️) driven by the library's own options.

The settings sheet exposes exactly what BoopFace documents in its README/index.d.ts:
size, color, auto-sleep, wake-on, motion, press-squeeze, antenna-drag, skin, plus the runtime
calls setPointerFollow() and setHeadRoundness().

Persistence: the widget runs in a sandboxed iframe with an opaque origin, so it has no storage and
no network — a change made on the phone lives until the panel reloads. This script bakes the saved
values into the widget HTML, which is what survives.

  python3 boopface-build.py                                  # reinstall with the saved settings
  python3 boopface-build.py --set motion=reduce --set autoSleep=60000
  python3 boopface-build.py --set skin=305 --set color=#ff8a65
"""
import json
import pathlib
import sys
import urllib.request

AGENT = "http://127.0.0.1:8765"
HERE = pathlib.Path(__file__).parent
BUNDLE = HERE / "boopface.iife.js"
SETTINGS = HERE / "boopface-settings.json"

DEFAULTS = {
    "skin": 902,
    "color": "#2dd4bf",
    "size": 0.86,
    "autoSleep": 45000,
    "wakeOn": "activity",
    "motion": "auto",
    "pointerFollow": False,
    "roundness": None,
    "pressSqueeze": False,
    "antennaDrag": False,
}

TEMPLATE = """<style>
  *{-webkit-tap-highlight-color:transparent}
  html,body{height:100%;margin:0;background:transparent;overflow:hidden;
    display:grid;place-items:center;transition:opacity .6s;
    -webkit-touch-callout:none;user-select:none;-webkit-user-select:none;
    font-family:Roboto,system-ui,sans-serif}
  #stage{display:grid;place-items:center}
  boop-face#avatar{touch-action:none}
  #tools{position:fixed;right:10px;bottom:10px;display:flex;gap:6px}
  #tools button{width:34px;height:34px;border-radius:17px;border:1px solid rgba(42,48,58,.7);
    background:rgba(23,27,33,.55);color:#8a94a3;font-size:15px;line-height:1;padding:0;opacity:.55}
  .panel{position:fixed;inset:0;background:rgba(8,10,13,.96);display:none;flex-direction:column;z-index:5}
  .panel.open{display:flex}
  #skins{z-index:6}
  .bar{display:flex;align-items:center;gap:10px;padding:10px 14px;border-bottom:1px solid #20262e}
  .bar b{font-size:13px;color:#e8ecf1;font-weight:600;flex:1}
  .bar button{border:1px solid #2a303a;background:#171b21;color:#e8ecf1;border-radius:12px;
    height:30px;padding:0 12px;font-size:12px}
  #list,#form{overflow-y:auto;padding:8px 12px 24px}
  .ser{font-size:11px;color:#6b7280;text-transform:uppercase;letter-spacing:.08em;margin:14px 0 6px}
  .grid{display:flex;flex-wrap:wrap;gap:8px}
  .cell{width:64px;height:64px;border-radius:12px;border:1px solid #262b34;background:#12161b;
    padding:0;overflow:hidden;display:grid;place-items:center}
  .cell.on{border-color:#2dd4bf;box-shadow:0 0 0 1px #2dd4bf inset}
  .row{display:flex;align-items:center;gap:10px;padding:10px 2px;border-bottom:1px solid #1b2027}
  .row label{flex:1;font-size:13px;color:#c9d2de}
  .row small{display:block;color:#6b7280;font-size:11px;margin-top:2px}
  .row select,.row input[type=number]{background:#171b21;border:1px solid #2a303a;color:#e8ecf1;
    border-radius:10px;height:30px;padding:0 8px;font-size:12px;min-width:110px}
  .row input[type=range]{width:130px}
  .sw{display:flex;gap:6px;flex-wrap:wrap}
  .sw button{width:26px;height:26px;border-radius:13px;border:2px solid transparent;padding:0}
  .sw button.on{border-color:#e8ecf1}
  .swi{display:flex;gap:6px}
  .swi button{width:44px;height:26px;border-radius:13px;border:1px solid #2a303a;background:#171b21;
    color:#8a94a3;font-size:11px}
  .swi button.on{border-color:#2dd4bf;color:#2dd4bf}
  #toast{position:fixed;top:10px;left:50%;transform:translateX(-50%);background:rgba(23,27,33,.92);
    border:1px solid #2a303a;color:#e8ecf1;font-size:12px;padding:6px 12px;border-radius:14px;
    opacity:0;transition:opacity .25s;pointer-events:none;z-index:9}
  #toast.show{opacity:1}
  .fail{position:fixed;inset:auto 0 8px;text-align:center;font-size:12px;color:#8a94a3}
</style>
<div id="stage"><boop-face id="avatar" press-squeeze="false" antenna-drag="false"></boop-face></div>
<div id="tools"><button id="pick" title="skins">🎨</button><button id="gear" title="settings">⚙️</button></div>
<div id="skins" class="panel"><div class="bar"><b>Skin</b><button id="skinsClose">close</button></div><div id="list"></div></div>
<div id="settings" class="panel"><div class="bar"><b>BoopFace</b><button id="setClose">close</button></div><div id="form"></div></div>
<div id="toast"></div>
<script>{bundle}</script>
<script type="module">
const a = document.getElementById('avatar');
const listEl = document.getElementById('list');
const formEl = document.getElementById('form');
const skinsEl = document.getElementById('skins');
const settingsEl = document.getElementById('settings');
const toast = document.getElementById('toast');
const CFG = {cfg};
const S = Object.assign({{}}, CFG);

const fit = () => {
  const box = Math.min(innerWidth, innerHeight * 0.94) * 0.94 * (Number(S.size) || 0.86);
  a.setAttribute('size', String(Math.round(Math.max(64, box))));
};
fit(); addEventListener('resize', fit);

if (!customElements.get('boop-face')) {
  for (const url of ['https://cdn.jsdelivr.net/npm/boopface@0.8.0/+esm',
                     'https://unpkg.com/boopface@0.8.0/boopface.js']) {
    try { await import(url); break; } catch (e) { console.warn('boopface:', url, e.message); }
  }
}
if (!customElements.get('boop-face'))
  document.body.insertAdjacentHTML('beforeend', '<p class="fail">avatar unavailable</p>');
try { await window.BoopFace?.loadSkins?.(); } catch (e) { console.warn('skins:', e.message); }

if (S.roundness === null || S.roundness === undefined) {
  try { S.roundness = a.getHeadRoundness?.() ?? 0.5; } catch (e) {}
}

const h = new Date().getHours();
if (h >= 23 || h < 7) document.body.style.opacity = '0.55';

let mode = 'idle', skin = S.skin, toastTimer = 0;
const say = (t) => {
  toast.textContent = t; toast.classList.add('show');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => toast.classList.remove('show'), 1600);
};

const apply = () => {
  ['color', 'auto-sleep', 'wake-on', 'motion'].forEach((k) => {
    const v = S[{{'color':'color','auto-sleep':'autoSleep','wake-on':'wakeOn','motion':'motion'}}[k]];
    if (v !== null && v !== undefined) a.setAttribute(k, String(v));
  });
  a.style.pointerEvents = (S.pressSqueeze || S.antennaDrag) ? 'auto' : 'none';
  try { a.setPressSqueeze?.(!!S.pressSqueeze); } catch (e) {{}}
  try { a.setAntennaDrag?.(!!S.antennaDrag); } catch (e) {{}}
  try { a.setPointerFollow?.(!!S.pointerFollow); } catch (e) {{}}
  if (S.roundness !== null && S.roundness !== undefined) {{ try {{ a.setHeadRoundness?.(Number(S.roundness)); }} catch (e) {{}} }}
  try {{ a.setSkin(S.skin ? Number(S.skin) : null); skin = S.skin || null; }} catch (e) {{ console.warn(e); }}
  fit();
};

const mark = (n) => {
  listEl.querySelectorAll('.cell').forEach((b) => b.classList.toggle('on', Number(b.dataset.n) === n));
};
const applySkin = (n) => {
  S.skin = n; apply(); mark(n);
  say('skin ' + n);
};

/* ---------- skin browser: real avatars, mounted only while on screen ---------- */
const groups = (() => {
  try {
    const s = window.BoopFace?.SKIN_SERIES;
    if (Array.isArray(s) && s.length) return s.map((g) => ({
      title: g.titleEn || g.title || String(g.id || g.number || ''),
      skins: (g.skins || []).map((x) => (x && typeof x === 'object') ? (x.number ?? x.id) : x)
    }));
  } catch (e) {{ console.warn(e); }}
  return [];
})();

const io = new IntersectionObserver((entries) => {
  for (const en of entries) {
    const cell = en.target;
    if (en.isIntersecting) {
      if (cell.firstElementChild) continue;
      const mini = document.createElement('boop-face');
      mini.setAttribute('size', '56');
      mini.setAttribute('motion', 'reduce');
      mini.setAttribute('auto-sleep', '0');
      mini.setAttribute('wake-on', 'manual');
      mini.setAttribute('press-squeeze', 'false');
      mini.setAttribute('antenna-drag', 'false');
      cell.append(mini);
      try {{ mini.setSkin(Number(cell.dataset.n)); mini.setPointerFollow?.(false); }} catch (e) {{}}
    }} else cell.replaceChildren();
  }}
}}, {{ root: listEl, rootMargin: '120px' }});

let built = false;
const buildSkins = () => {
  if (built) return; built = true;
  if (!groups.length) {{ listEl.textContent = 'skin catalog unavailable'; return; }}
  const frag = document.createDocumentFragment();
  for (const g of groups) {
    const h3 = document.createElement('div'); h3.className = 'ser'; h3.textContent = g.title;
    const grid = document.createElement('div'); grid.className = 'grid';
    for (const n of g.skins) {
      if (n == null) continue;
      const cell = document.createElement('button');
      cell.className = 'cell'; cell.dataset.n = String(n);
      cell.onclick = () => applySkin(Number(n));
      grid.append(cell);
    }
    frag.append(h3, grid);
  }
  listEl.append(frag);
  listEl.querySelectorAll('.cell').forEach((c) => io.observe(c));
  mark(Number(S.skin) || 0);
};

/* ---------- settings: the documented attributes ---------- */
const COLORS = ['#2dd4bf', '#ffffff', '#8a94a3', '#ffb74d', '#ff8a80', '#82b1ff', '#b388ff', '#a5d6a7'];
const enumRow = (label, hint, key, values) => {
  const row = document.createElement('div'); row.className = 'row';
  const lab = document.createElement('label'); lab.textContent = label;
  if (hint) {{ const s = document.createElement('small'); s.textContent = hint; lab.append(s); }}
  const sel = document.createElement('select');
  for (const v of values) {{
    const o = document.createElement('option'); o.value = v.value; o.textContent = v.text; sel.append(o);
  }}
  sel.value = String(S[key]); sel.onchange = () => {{ S[key] = sel.value; apply(); say(label + ': ' + sel.value); }};
  row.append(lab, sel); return row;
};
const numRow = (label, hint, key, min, max, step, fmt) => {
  const row = document.createElement('div'); row.className = 'row';
  const lab = document.createElement('label'); lab.textContent = label;
  if (hint) {{ const s = document.createElement('small'); s.textContent = hint; lab.append(s); }}
  const inp = document.createElement('input');
  inp.type = 'range'; inp.min = min; inp.max = max; inp.step = step; inp.value = S[key];
  const out = document.createElement('span'); out.style.cssText = 'min-width:52px;text-align:right;color:#8a94a3;font-size:12px';
  out.textContent = fmt ? fmt(S[key]) : S[key];
  inp.oninput = () => {{ S[key] = Number(inp.value); out.textContent = fmt ? fmt(S[key]) : S[key]; apply(); }};
  row.append(lab, inp, out); return row;
};
const boolRow = (label, hint, key) => {
  const row = document.createElement('div'); row.className = 'row';
  const lab = document.createElement('label'); lab.textContent = label;
  if (hint) {{ const s = document.createElement('small'); s.textContent = hint; lab.append(s); }}
  const wrap = document.createElement('div'); wrap.className = 'swi';
  for (const v of [true, false]) {{
    const b = document.createElement('button'); b.textContent = v ? 'on' : 'off';
    b.classList.toggle('on', !!S[key] === v);
    b.onclick = () => {{
      S[key] = v; apply();
      [...wrap.children].forEach((x) => x.classList.toggle('on', (x.textContent === 'on') === v));
      say(label + ': ' + (v ? 'on' : 'off'));
    }};
    wrap.append(b);
  }}
  row.append(lab, wrap); return row;
};

let formBuilt = false;
const buildSettings = () => {
  if (formBuilt) return; formBuilt = true;
  const frag = document.createDocumentFragment();

  const skinRow = document.createElement('div'); skinRow.className = 'row';
  const skinLab = document.createElement('label'); skinLab.textContent = 'Skin';
  const skinBtn = document.createElement('button');
  skinBtn.textContent = 'browse'; skinBtn.style.cssText = 'border:1px solid #2a303a;background:#171b21;color:#e8ecf1;border-radius:10px;height:30px;padding:0 12px;font-size:12px';
  skinBtn.onclick = () => {{ skinsEl.classList.add('open'); buildSkins(); mark(Number(S.skin) || 0); }};
  skinRow.append(skinLab, skinBtn);
  frag.append(skinRow);

  const colorRow = document.createElement('div'); colorRow.className = 'row';
  const colorLab = document.createElement('label'); colorLab.textContent = 'Colour';
  const sw = document.createElement('div'); sw.className = 'sw';
  for (const c of COLORS) {{
    const b = document.createElement('button'); b.style.background = c; b.title = c;
    b.classList.toggle('on', String(S.color).toLowerCase() === c);
    b.onclick = () => {{
      S.color = c; apply();
      [...sw.children].forEach((x) => x.classList.remove('on')); b.classList.add('on');
      say('colour ' + c);
    }};
    sw.append(b);
  }}
  colorRow.append(colorLab, sw);
  frag.append(colorRow);

  frag.append(numRow('Size', 'of the tile', 'size', 0.4, 1, 0.02, (v) => Math.round(v * 100) + '%'));
  frag.append(enumRow('Auto sleep', 'idle before the face sleeps', 'autoSleep', [
    {{ value: '0', text: 'never' }}, {{ value: '15000', text: '15 s' }}, {{ value: '45000', text: '45 s' }},
    {{ value: '120000', text: '2 min' }}, {{ value: '600000', text: '10 min' }}]));
  frag.append(enumRow('Wake on', '', 'wakeOn', [
    {{ value: 'activity', text: 'activity' }}, {{ value: 'interaction', text: 'touch' }}, {{ value: 'manual', text: 'manual' }}]));
  frag.append(enumRow('Motion', '', 'motion', [
    {{ value: 'auto', text: 'auto' }}, {{ value: 'reduce', text: 'reduced' }}, {{ value: 'full', text: 'full' }}]));
  frag.append(boolRow('Pointer follow', 'eyes follow the touch', 'pointerFollow'));
  frag.append(numRow('Head roundness', '', 'roundness', 0, 0.8, 0.02, (v) => Number(v).toFixed(2)));
  frag.append(boolRow('Press squeeze', 'press and hold the face', 'pressSqueeze'));
  frag.append(boolRow('Antenna drag', 'drag the antenna', 'antennaDrag'));

  const note = document.createElement('div');
  note.style.cssText = 'color:#6b7280;font-size:11px;line-height:1.6;padding:14px 2px';
  note.textContent = 'Changes apply immediately. The page cannot save them (sandboxed, no storage), '
    + 'so after a reload the values written on the computer come back.';
  frag.append(note);
  formEl.append(frag);
};

document.getElementById('pick').onclick = () => {{ skinsEl.classList.add('open'); buildSkins(); mark(Number(S.skin) || 0); }};
document.getElementById('gear').onclick = () => {{ settingsEl.classList.add('open'); buildSettings(); }};
document.getElementById('skinsClose').onclick = () => skinsEl.classList.remove('open');
document.getElementById('setClose').onclick = () => settingsEl.classList.remove('open');

/* ---------- behaviour ---------- */
const setMode = (next) => {
  if (next === mode) return;
  const was = mode;
  mode = next;
  if (next === 'busy') a.startWaiting?.({{ variant: 'wrap' }});
  else if (next === 'wait') a.play?.('wary');
  else if (was === 'busy') a.play?.('success');
  else if (was === 'wait') a.play?.('affirm');
  else a.stopWaiting?.();
};

// BoopFace refuses press-squeeze for pointerType 'touch' (gestures.js:72), so a phone can never
// trigger it. When the setting is on, replay the touch as a synthetic mouse press at the same spot;
// the library's own gesture code then runs, animation included. Radius matches its own 0.32 test.
let squeezeWired = false;
const wireTouchSqueeze = () => {
  if (squeezeWired) return; squeezeWired = true;
  const fire = (type, e) => {
    const r = a.getBoundingClientRect();
    if (!r.width || !r.height) return;
    const nx = (e.clientX - r.left) / r.width - .5, ny = (e.clientY - r.top) / r.height - .5;
    if (type === 'pointerdown' && nx * nx + ny * ny > 0.32 * 0.32) return;
    try {
      a.dispatchEvent(new PointerEvent(type, { bubbles: true, cancelable: true, pointerId: 999,
        pointerType: 'mouse', isPrimary: true, button: 0, buttons: type === 'pointerup' ? 0 : 1,
        clientX: e.clientX, clientY: e.clientY }));
    } catch (err) { console.warn('squeeze', err.message); }
  };
  a.addEventListener('pointerdown', (e) => { if (S.pressSqueeze && e.pointerType === 'touch') fire('pointerdown', e); });
  addEventListener('pointerup', (e) => { if (S.pressSqueeze) fire('pointerup', e); });
  addEventListener('pointercancel', (e) => { if (S.pressSqueeze) fire('pointercancel', e); });
};
wireTouchSqueeze();

apply();

try {
  phonebox.on('work', (w) => {
    const s = Array.isArray(w?.sessions) ? w.sessions : [];
    if (s.some((x) => x?.status === 'waiting')) setMode('wait');
    else if (s.some((x) => x?.status === 'busy')) setMode('busy');
    else setMode('idle');
  });
  let was = null;
  phonebox.on('state', (st) => {
    const playing = !!st?.media?.playing;
    if (was !== null && playing !== was && mode === 'idle') a.play?.(playing ? 'success' : 'idle');
    was = playing;
  });
}} catch (e) {{ console.warn(e); }}
</script>
"""


def load_settings():
    cfg = dict(DEFAULTS)
    if SETTINGS.exists():
        try:
            cfg.update(json.loads(SETTINGS.read_text()))
        except Exception as exc:
            print("ignoring bad settings file:", exc)
    return cfg


def main():
    args = sys.argv[1:]
    cfg = load_settings()
    if args:
        if args[0] != "--set":
            raise SystemExit(__doc__)
        for pair in args[1:]:
            key, _, value = pair.partition("=")
            key = key.strip()
            if key not in DEFAULTS:
                raise SystemExit(f"unknown key: {key} (known: {', '.join(DEFAULTS)})")
            if isinstance(DEFAULTS[key], bool):
                cfg[key] = value.strip().lower() in ("1", "true", "on", "yes")
            elif isinstance(DEFAULTS[key], float):
                cfg[key] = float(value)
            elif isinstance(DEFAULTS[key], int):
                cfg[key] = int(value)
            elif key == "roundness":
                cfg[key] = None if value in ("none", "") else float(value)
            elif key == "skin":
                cfg[key] = None if value in ("none", "flat", "0", "") else int(value)
            else:
                cfg[key] = value
        SETTINGS.write_text(json.dumps(cfg, indent=2))

    bundle = BUNDLE.read_text(encoding="utf-8")
    if "</script" in bundle.lower():
        raise SystemExit("bundle contains </script")
    html = (TEMPLATE.replace("{{", "{").replace("}}", "}")
            .replace("{bundle}", bundle, 1).replace("{cfg}", json.dumps(cfg)))

    with urllib.request.urlopen(AGENT + "/api/settings", timeout=10) as r:
        version = r.headers.get("X-Config-Version")
        page_cfg = json.loads(r.read())
    page = next(p for p in page_cfg["pages"] if p["id"] == "boopface")
    page["widgets"][0]["options"]["html"] = html

    body = json.dumps(page_cfg, separators=(",", ":")).encode()
    req = urllib.request.Request(AGENT + "/api/settings", data=body, method="PUT",
                                 headers={"Content-Type": "application/json", "X-Config-Version": version})
    with urllib.request.urlopen(req, timeout=60) as r:
        print("PUT ->", r.status, "| widget", len(html), "B | config", len(body), "B")
    print("settings:", json.dumps(cfg))


if __name__ == "__main__":
    main()
