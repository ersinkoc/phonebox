// The three checks below are the ones whose absence let a partly-English settings page and an incomplete widget
// catalog pass review twice. They look at what reaches the screen, not at what the dictionary contains: a string that
// was never wrapped in t() has no key, so a key-coverage check cannot see it.
const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

// Names the demo config carries, so they are data rather than interface text. Read from the demo server so the list
// stays right when the sample config changes.
const DEMO_NAMES = [
  '🏠 Home', '🧭 Dashboard', '✳️ AI quotas', '🤖 Sessions', '📊 System', '⚙️ Processes', '🎛️ Shortcuts',
  '🎵 Music', '🎯 Status', '🌅 Ambient', '🌍 World', '✅ Daily', '💱 Markets', '📡 Feeds', '🍅 Focus',
  '🫁 Calm', '🕰️ Clock', '⚡ Automation', '🗓️ Planner', '🛏️ Nightstand', '🏖️ Weekend', '🎧 Deep work',
  '🧭 CodexBar', '🛠️ DevOps', '🎛️ Audio deck', '🎛️ Streaming deck', '🎛️ Meeting deck',
  '🎛️ Development deck', '🎛️ Music deck', '🎛️ Computer controls',
  // shortcut presets shown in the sample deck, and the ids the deck sample uses
  'terminal — Terminal', 'code — VS Code', 'browser — Browser', 'files — Files', 'music — Spotify',
  'chat — Discord', 'snip — Snip', 'taskmgr — Task Manager', 'mute — Mute', 'screenoff — Screen Off',
  'lock — Lock', 'settings — Settings',
  '⌨️ Terminal', '📝 VS Code', '🌐 Browser', '📁 Files', '🎧 Spotify', '💬 Discord', '✂️ Snip',
  '📊 Task Manager', '🔇 Mute', '🌙 Screen Off', '🔒 Lock', '⚙️ Settings',
  // adapter and device names reported by the machine
  'Intel(R) Ethernet Controller I225-V', 'Intel(R) Wi-Fi 6E AX211 160MHz', 'WireGuard Tunnel',
  'UMIDIGI BISON · connected now over USB · WebView 134',
  'samsung SM-X200 · seen 2 min ago over Wi-Fi · WebView 134',
  '0.9.16 (up to date)',
  // deck controls, radio slots and shortcut sample data the demo config carries
  'AI quotas', 'Deep work', 'Audio deck', 'Streaming deck', 'Meeting deck', 'Development deck',
  'Music deck', 'Computer controls', 'Hacker News', 'Smart Stack', 'Workflow GitHub',
  'Radio internet', 'Mixer OBS', 'Screen Off', 'Run tests', 'Web dev server', 'API dev server',
  'Mute in meeting', 'Raise hand',
  // placeholder paths shown as example values inside inputs
  'C:\\Users\\you\\Pictures\\Frame', 'C:\\ffmpeg\\bin\\ffmpeg.exe',
];

// Any run of two English words with no Vietnamese diacritics. Ids, codes, units, urls and single words (product names
// like "Control Deck", "Spotify", "Claude") are allowed to stay English.
function englishLeftovers(texts, allowed) {
  const allow = new Set(allowed);
  const norm = (s) => s.replace(/\s+/g, ' ').trim();
  const isEnglish = (t) => {
    if (!t || t.length < 5) return false;
    if (allow.has(t)) return false;
    // a list bullet prefixes data the machine reported (adapter and device names), not interface text
    if (allow.has(t.replace(/^[·•]\s*/, ''))) return false;
    if (/[\u00c0-\u1ef9]/i.test(t)) return false;               // Vietnamese diacritics
    if (!/[A-Za-z]{3,}/.test(t)) return false;
    // Exempt only real identifier, code and numeric/unit forms. The earlier pattern allowed letters and spaces, so an
    // ordinary label like "Save all settings" counted as an identifier and slipped through.
    if (/^(?:[\w.\-+#/%:]+|[-\d.,\s]+(?:%|GB|MB|KB|ms|s|°C|W|V|Hz|%\/s)?|[\w.\-]+\/(?:s|min|h))$/.test(t)) return false;
    return /[A-Za-z]{2,}[^A-Za-z]{1,3}[A-Za-z]{2,}/.test(t);     // at least two words
  };
  return [...new Set(texts.map(norm))].filter(isEnglish);
}

// isEnglish below flags a run of two words with no diacritic anywhere in the string. Vietnamese marks tone on
// nearly every syllable, so that is a reliable signal there; Portuguese is not (plenty of whole phrases, like
// "Nenhum telefone conectado", carry no accent at all), so this check stays Vietnamese-only. The language-agnostic
// completeness checks for Portuguese (Brazil) and every other language live in tools/ui-tests/i18n.spec.js instead,
// comparing each dictionary directly against the English one rather than guessing from the rendered text.
test('the settings page shows no English left in Vietnamese, with every section open', async ({ page }) => {
  await page.goto('/settings/');
  // A page-level fetch mock would hide the real /api/settings answer, so switch the language through the UI contract:
  // the settings editor reads display.language from the file it loads, and the demo server serves a config we can set.
  await page.evaluate(async () => {
    const r = await fetch('/api/settings');
    const cfg = await r.json();
    cfg.display = { ...(cfg.display || {}), language: 'vi' };
    await fetch('/api/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(cfg) });
  });
  await page.goto('/settings/');
  await expect(page.locator('#langSel')).toHaveValue('vi', { timeout: 15000 });

  // <details> hides its contents from a text scan until it is open, which is how the Control Deck hints and the audio
  // profile labels escaped earlier sweeps: open every one, then read the visible text of every leaf element.
  await page.evaluate(() => document.querySelectorAll('details').forEach((d) => { d.open = true; }));
  await page.waitForTimeout(800);

  const leftovers = await page.evaluate(() => {
    // Input values are user data, not interface text; their placeholder and aria-label still are.
    const skip = new Set(['SCRIPT', 'STYLE', 'CODE', 'PRE', 'TEXTAREA']);
    const out = [];
    const walk = (el) => {
      if (skip.has(el.tagName)) return;
      // Read an element's own text nodes, not just its subtree: a <label> that wraps an <input> has children, so a
      // leaf-only rule dropped its text (and the input's placeholder with it).
      for (const node of el.childNodes) {
        if (node.nodeType === Node.TEXT_NODE) {
          const t = node.nodeValue || '';
          if (t.trim()) out.push(t);
        }
      }
      if (el.title) out.push(el.title);
      if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') {
        if (el.placeholder) out.push(el.placeholder);
        if (el.tagName === 'INPUT' && el.type !== 'text' && el.value && el.type === 'button') out.push(el.value);
      }
      if (el.getAttribute && el.getAttribute('aria-label')) out.push(el.getAttribute('aria-label'));
      for (const c of el.children) walk(c);
    };
    walk(document.body);
    return out;
  });

  // Product names and platform names that are not translated on purpose, and demo/sample data the config carries.
  const leftoversKept = englishLeftovers(leftovers, [
    'Control Deck', 'PhoneBox', 'Spotify', 'Claude', 'Codex', 'GitHub', 'OBS Studio', 'Home Assistant',
    'Discord', 'Antigravity', 'Tailscale', 'Windows', 'macOS', 'Linux', 'Android', 'WebView',
    'Windows Terminal', 'File Explorer (args: folder)', 'Task Manager', 'AppID', 'PowerShell',
    'JSON', 'HTTPS', 'USB', 'Wi-Fi', 'MIDI', 'OSC', 'SPA', 'CodexBar', 'radio-browser.info',
    'SoundVolumeView', 'xdotool', 'wtype', 'soffice', 'POWERPNT', 'VS Code', 'Strip[0].Gain',
    // Wi-Fi and Tailscale are product names, so the section title stays the same in every language
    'Wi-Fi & Tailscale',
    // Open-Meteo forecast model names (and the agencies that run them) are proper names in every language
    'GFS (NOAA)', 'ECMWF IFS', 'ICON (DWD)', 'UKMO (Met Office)', 'GEM (ECCC)', 'CMA GRAPES',
    // an OBS/Home Assistant UI label the user looks for inside those programs, not our own interface text
    'Long-lived access tokens', 'Spotify Client ID', 'Client ID', 'Client secret',
    'Enable WebSocket server', 'Show Connect Info', 'Secret address in iCal format',
    // names the machine reports, so they are data and not interface text
    'UMIDIGI BISON', 'samsung SM-X200',
    // literal values the API accepts, shown inside <code>: translating them would break the examples
    '(ok / warn / error)', '(ok / warn / error),',
  ].concat(
    // English names of the other languages in the picker
    ['English', 'Deutsch', 'Français', 'Español', 'Italiano', 'Nederlands', 'Polski', 'Русский', '中文', 'Türkçe', 'Português'],
    // the sample config: page, rule, shortcut, adapter and device names come from the file, not from the UI
    DEMO_NAMES,
  ));

  expect(leftoversKept, `untranslated text still on the settings page:\n${leftoversKept.join('\n')}`).toEqual([]);
});

// Checked for every language with a complete widget/template catalog (CATALOG.<lang> covers every widgets.json and
// templates.json string): today that is Vietnamese and Portuguese (Brazil).
for (const lang of ['vi', 'pt-BR']) {
test(`every string in widgets.json and templates.json has a ${lang} catalog entry`, async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async (lang) => {
    const I18n = window.PhoneBoxI18n;
    const cat = await (await fetch('/widgets.json', { cache: 'no-store' })).json();
    const tpl = await (await fetch('/templates.json', { cache: 'no-store' })).json();
    const widgets = Array.isArray(cat) ? cat : (cat.widgets || []);
    const templates = Array.isArray(tpl) ? tpl : (tpl.templates || []);
    const dict = I18n.CATALOG[lang] || {};

    const wanted = new Set();
    for (const w of widgets) {
      for (const f of ['name', 'desc', 'category']) if (w[f]) wanted.add(w[f]);
      for (const o of w.options || []) {
        if (o.label) wanted.add(o.label);
        for (const c of o.choices || []) if (c[1]) wanted.add(c[1]);
      }
    }
    for (const t of templates) {
      for (const f of ['title', 'desc']) if (t[f]) wanted.add(t[f]);
    }
    // A choice whose label is a plain number ("1", "2", "3" for the feeds line count) is the value itself, not text.
    const isValue = (s) => /^[\d.,%\-/+ ]+$/.test(s);
    // TRY is the Turkish lira code, used as a currency label: translating it would turn the code into a word.
    const NOT_TRANSLATED = new Set(['TRY']);
    const missing = [...wanted].filter((s) => !(s in dict) && !NOT_TRANSLATED.has(s) && !isValue(s));
    return { widgets: widgets.length, templates: templates.length, wanted: wanted.size, missing };
  }, lang);

  expect(out.widgets).toBeGreaterThan(0);
  expect(out.missing, `catalog entries missing for:\n${out.missing.join('\n')}`).toEqual([]);
});
}

test('settings.html keeps no English literal in a position that reaches the screen', async () => {
  const file = path.join(__dirname, '..', '..', 'agent', 'PhoneBox.Agent', 'settings.html');
  const src = fs.readFileSync(file, 'utf8');
  const marker = src.indexOf('<script>');
  const script = marker === -1 ? '' : src.slice(marker);

  // Positions that put a string straight into the DOM: h('tag', {...}, 'text'), btn('text'), text('key', 'label'),
  // label: 'text', field('label'), and the template-literal form h('tag', {...}, `text`).
  const PATTERNS = [
    /h\('[a-z0-9]+',\s*\{[^{}]*\},\s*'((?:[^'\\]|\\.){4,300})'/g,
    /\bbtn\(\s*'((?:[^'\\]|\\.){4,300})'/g,
    /\btext\('[^']*',\s*'((?:[^'\\]|\\.){4,300})'/g,
    /label:\s*'((?:[^'\\]|\\.){4,300})'/g,
    /\bfield\('((?:[^'\\]|\\.){4,300})'/g,
    /h\('[a-z0-9]+',\s*\{[^{}]*\},\s*`([^`]{4,300})`/g,
  ];

  const hits = [];
  for (const re of PATTERNS) {
    for (const m of script.matchAll(re)) {
      const s = m[1];
      if (/[\u00c0-\u1ef9]/i.test(s)) continue;                // already Vietnamese
      if (!/[A-Za-z]{3,}/.test(s)) continue;
      if (/(=>|\$\{|document\.|querySelector|addEventListener)/.test(s)) continue;
      if (/^[\w.\-+#/:%=]+$/.test(s)) continue;                 // ids and codes
      hits.push(s);
    }
  }

  // The trigger, action and preset tables are looked up through tc()/tl(), so their English text is correct here: it is
  // the key. Everything else must go through t().
  const LOOKED_UP_BY_TEXT = new Set([
    'an AI session needs me', 'an AI session is working', 'music or video plays', 'an app is running',
    'an app is in front', 'the microphone is in use', 'the camera is in use', 'a calendar event is on',
    'OBS is recording or streaming', 'the CPU is busy', 'I am away from the PC', 'it is between',
    'a pushed value', 'an AI quota is above', 'a GitHub review is requested', 'CI fails on my pull request',
    'a device battery is below', 'the network is slow', 'always (use the days and hours below)',
    'show the page', 'show a notification', 'push a value', 'run a shortcut',
    'Control Deck',
  ]);
  const left = [...new Set(hits)].filter((s) => !LOOKED_UP_BY_TEXT.has(s));

  expect(left, `English literals that reach the screen in settings.html:\n${left.join('\n')}`).toEqual([]);
});
