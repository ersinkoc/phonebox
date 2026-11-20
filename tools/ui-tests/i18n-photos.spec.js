// The Photo frame's Immich strings (Settings › General › Photo frame) exist in every language of the dictionary, and
// the one that carries a number keeps its {n}. The panel falls back to English for a missing s.* key, so nothing else
// would notice a language that was left out.
const { test, expect } = require('@playwright/test');

const KEYS = [
  's.photos.source', 's.photos.sourceFolder', 's.photos.sourceImmich', 's.photos.immichHint', 's.photos.immichUrl',
  's.photos.immichKey', 's.photos.immichAlbum', 's.photos.immichLoad',
  's.js.immichLoadFirst', 's.js.immichChoose', 's.js.immichChosen', 's.js.immichUntitled', 's.js.immichLoading',
  's.js.immichCount', 's.js.immichNone', 's.js.immichFailed',
];
const LANGUAGES = ['en', 'tr', 'de', 'fr', 'es', 'it', 'pt', 'pt-BR', 'nl', 'pl', 'ru', 'el', 'vi', 'zh'];

for (const lang of LANGUAGES) {
  test(`the Immich photo source strings are in ${lang}`, async ({ page }) => {
    await page.goto('/panel/');
    const found = await page.evaluate(({ lang, keys }) => {
      const S = window.PhoneBoxI18n.STRINGS[lang];
      return keys.map((k) => [k, S[k]]);
    }, { lang, keys: KEYS });
    expect(found.filter(([, v]) => typeof v !== 'string' || !v.trim()).map(([k]) => k)).toEqual([]);
    expect(Object.fromEntries(found)['s.js.immichCount']).toContain('{n}');
    // the permission names are identifiers, not words: they stay as they are in the hint
    const hint = Object.fromEntries(found)['s.photos.immichHint'];
    for (const permission of ['album.read', 'asset.read', 'asset.view']) expect(hint).toContain(permission);
  });
}
