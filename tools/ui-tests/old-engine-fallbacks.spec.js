// Safari before 16 (iOS 15) and Android WebViews before Chromium 105 have no container units, and Safari before 16.2
// and WebViews before Chromium 111 have no color-mix(). style.css carries `@supports not (…)` fallbacks for them. The
// Chromium these tests run in has both, so the tests take the fallback blocks out of style.css and apply them
// unconditionally, the way such an engine would.
const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

const CSS = fs.readFileSync(path.join(__dirname, '../../android/app/src/main/assets/www/style.css'), 'utf8');

/** The body of the top-level `@supports not (…)` block whose condition contains `condition`. */
function fallback(condition) {
  const re = /@supports\s+not\s*\(/g;
  let m;
  while ((m = re.exec(CSS))) {
    const open = CSS.indexOf('{', m.index);
    if (!CSS.slice(m.index, open).includes(condition)) continue;
    let depth = 0, i = open;
    for (; i < CSS.length; i++) {
      if (CSS[i] === '{') depth++;
      else if (CSS[i] === '}' && --depth === 0) break;
    }
    return CSS.slice(open + 1, i);
  }
  throw new Error(`no @supports not block for ${condition}`);
}

const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 10 10"><rect width="10" height="10" fill="#2563eb"/></svg>';

async function open(page, widgets, shortcuts = []) {
  await page.route((url) => url.pathname.endsWith('/agent/api/shortcuts/icon'), (route) => route.fulfill({ contentType: 'image/svg+xml', body: svg }));
  await page.addInitScript((config) => {
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  }, JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts,
    pages: [{ id: 'old-engine', title: 'Old engine', widgets: widgets.map((w, i) => ({ id: 'w' + i, ...w })) }],
  }));
  await page.goto('/panel/');
}

/** The parts of a key's ::before that make up the toggle dot or the cover shade. */
const before = (key) => key.evaluate((el) => {
  const s = getComputedStyle(el, '::before');
  return {
    content: s.content, top: s.top, right: s.right, left: s.left, width: s.width, height: s.height,
    opacity: s.opacity, zIndex: s.zIndex, background: s.backgroundColor, image: s.backgroundImage,
    border: s.borderTopWidth, shadow: s.boxShadow,
  };
});

test('without color-mix() a plain key gets its colour layer, and toggle and cover keys keep their ::before', async ({ page }) => {
  await open(page, [
    { type: 'button', x: 0, y: 0, w: 4, h: 6, options: { id: 'plain' } },
    { type: 'button', x: 4, y: 0, w: 4, h: 6, options: { id: 'lamp' } },
    { type: 'button', x: 8, y: 0, w: 4, h: 6, options: { id: 'art', cover: true } },
  ], [
    { id: 'plain', label: 'Plain', icon: '💡', color: '#e11d48' },
    { id: 'lamp', label: 'Lamp', icon: '💡', color: '#e11d48', toggle: true },
    { id: 'art', label: 'Artwork', icon: '🎨', color: '#e11d48', iconRev: 1, iconUpload: true },
  ]);
  const keys = page.locator('[data-id="old-engine"] .w-button .key');
  const plain = keys.nth(0), toggle = keys.nth(1), cover = keys.nth(2);
  await expect(toggle).toHaveClass(/toggle/);
  await expect(cover).toHaveClass(/k-cover/);
  await expect(cover.locator('img.k-bg')).toBeVisible();

  const toggleBefore = await before(toggle), coverBefore = await before(cover);
  expect(toggleBefore.width).toBe('9px'); // the on/off dot

  await page.addStyleTag({ content: fallback('color-mix') });

  // the toggle's dot and the cover's shade are untouched (#723 review: the layer had moved the dot and covered the picture)
  expect(await before(toggle)).toEqual(toggleBefore);
  expect(await before(cover)).toEqual(coverBefore);
  // both still have a frame
  await expect(toggle).toHaveCSS('border-top-width', '1px');
  await expect(cover).toHaveCSS('border-top-width', '1px');
  // a plain key gets its colour from --k on a faded layer behind its content
  const layer = await before(plain);
  expect(layer.content).toBe('""');
  expect(layer.opacity).toBe('0.3');
  expect(layer.zIndex).toBe('-1');
  expect(layer.border).toBe('1px');
  await expect(plain).toHaveCSS('isolation', 'isolate');
});

test('without container units the Now Playing cover keeps a size', async ({ page }) => {
  await open(page, [{ type: 'media', x: 0, y: 0, w: 12, h: 6 }]);
  const art = page.locator('[data-id="old-engine"] .w-media .art-wrap');
  await expect(art).toBeVisible();

  // An engine without container units drops `height: min(58cqh, 34cqw)`: the cover has no height of its own then.
  await page.addStyleTag({ content: '.art-wrap { height: auto; }' });
  expect((await art.boundingBox()).height).toBe(0);

  await page.addStyleTag({ content: fallback('cqh') });
  const box = await art.boundingBox();
  expect(box.height).toBeGreaterThanOrEqual(56);
  expect(box.height).toBeLessThanOrEqual(160);
  expect(box.width).toBeCloseTo(box.height, 0); // still square
});
