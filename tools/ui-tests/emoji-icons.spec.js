// Emoji are drawn as Lucide icons: a mapped emoji on its own becomes an icon, a longer emoji sequence and an emoji with
// no icon stay text, and a page label with an icon still reads as text in the page-switch countdown.
const { test, expect } = require('@playwright/test');

test('rich() swaps a lone mapped emoji and leaves emoji sequences as typed', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const { rich } = await import('/panel/main.js');
    const probe = (text) => { const d = document.createElement('div'); d.append(rich(text)); return { icons: d.querySelectorAll('svg').length, text: d.textContent }; };
    return { bell: probe('\u{1F514} 3'), skin: probe('\u{1F44B}\u{1F3FD}'), family: probe('\u{1F468}\u200D\u{1F469}\u200D\u{1F467}'), unknown: probe('\u{1F984} x'), vs: probe('\u{1F3C3}\uFE0F\u200D\u{1F4BB}'), text: probe('\u2600\uFE0E'), sun: probe('\u2600\uFE0F') };
  });
  expect(out.bell).toEqual({ icons: 1, text: ' 3' });
  expect(out.skin).toEqual({ icons: 0, text: '\u{1F44B}\u{1F3FD}' });
  expect(out.family).toEqual({ icons: 0, text: '\u{1F468}\u200D\u{1F469}\u200D\u{1F467}' });
  expect(out.vs).toEqual({ icons: 0, text: '\u{1F3C3}\uFE0F\u200D\u{1F4BB}' }); // a variation selector before a ZWJ is not given back
  expect(out.text).toEqual({ icons: 0, text: '\u2600\uFE0E' }); // asked for text presentation: left alone
  expect(out.sun).toEqual({ icons: 1, text: '' }); // the emoji form is still an icon
  expect(out.unknown).toEqual({ icons: 0, text: '\u{1F984} x' });
});
