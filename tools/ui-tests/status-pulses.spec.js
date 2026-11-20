const { test, expect } = require('@playwright/test');

// The status pulses (session dots, OBS LED, on-air sign, dictation mic, radio equalizer) used to be infinite CSS animations.
// A running animation makes the browser produce a frame on every display refresh (120 a second on a 120 Hz phone) for as
// long as the element is on screen, which cost the panel 50 to 100 percentage points of a CPU core each. They are now
// stepped by one shared beat: main.js flips a few classes on <html> from its existing 250 ms tick, and only while something
// that pulses is on screen and the panel is visible.
const FIXTURES = {
  'waiting session dot': { html: '<div class="sess waiting"><span class="st"></span></div>', sel: '.sess .st', prop: 'opacity', values: ['1', '0.35'] },
  'busy session dot': { html: '<div class="sess busy"><span class="st"></span></div>', sel: '.sess .st', prop: 'opacity', values: ['1', '0.35'] },
  'shell session dot': { html: '<div class="sess shell"><span class="st"></span></div>', sel: '.sess .st', prop: 'opacity', values: ['1', '0.35'] },
  'OBS LED': { html: '<div class="obs-pill on"><span class="obs-led"></span></div>', sel: '.obs-led', prop: 'boxShadow' },
  'on-air sign': { html: '<div class="w-onair on"><div class="oa-sign">ON AIR</div></div>', sel: '.oa-sign', prop: 'boxShadow' },
  'dictation mic': { html: '<button class="dc-mic on" type="button">M</button>', sel: '.dc-mic', prop: 'opacity', values: ['1', '0.7'] },
};
const RADIO = '<div class="radio-card is-playing"><div class="radio-logo-box"><span class="radio-eq"><i></i><i></i><i></i></span></div></div>';

async function load(page) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.evaluate(() => import('/panel/main.js'));
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z')); // time moves only with runFor, so a loaded machine cannot skew it
}
const mount = (page, html, place = 'left: 0; top: 0') => page.evaluate(([h, at]) => {
  document.getElementById('pulse-fixture')?.remove();
  const box = document.createElement('div');
  box.id = 'pulse-fixture';
  box.style.cssText = 'position: fixed; width: 300px; height: 150px; ' + at;
  box.innerHTML = h;
  document.body.append(box);
}, [html, place]);
// what the element looks like at each of the next `n` ticks of 250 ms
async function sample(page, sel, prop, n = 24) {
  const out = [];
  for (let i = 0; i < n; i++) {
    await page.clock.runFor(250);
    out.push(await page.evaluate(([s, p]) => getComputedStyle(document.querySelector('#pulse-fixture ' + s))[p], [sel, prop]));
  }
  return out;
}

for (const [name, f] of Object.entries(FIXTURES)) {
  test(`${name}: no running animation, and it still pulses in steps`, async ({ page }) => {
    await load(page);
    await mount(page, f.html);
    const running = await page.evaluate(() => [...document.querySelectorAll('#pulse-fixture, #pulse-fixture *')].flatMap((e) => e.getAnimations()).filter((a) => a.playState === 'running' && a.effect.getComputedTiming().iterations === Infinity).length);
    expect(running, 'EXPECTED: no infinite CSS animation | ACTUAL: ' + running).toBe(0);
    const seen = await sample(page, f.sel, f.prop);
    const distinct = [...new Set(seen)];
    expect(distinct, 'EXPECTED: exactly two looks, in turn | ACTUAL: ' + JSON.stringify(distinct)).toHaveLength(2);
    if (f.values) expect(distinct.sort()).toEqual([...f.values].sort());
    expect(seen.filter((v, i) => i && v !== seen[i - 1]).length, 'it must keep changing').toBeGreaterThanOrEqual(3);
  });
}

test('the radio equalizer bars have no running animation and move apart from each other', async ({ page }) => {
  await load(page);
  await mount(page, RADIO);
  const running = await page.evaluate(() => [...document.querySelectorAll('#pulse-fixture, #pulse-fixture *')].flatMap((e) => e.getAnimations()).filter((a) => a.playState === 'running').length);
  expect(running, 'EXPECTED: no CSS animation on the bars | ACTUAL: ' + running).toBe(0);
  const bars = [[], [], []];
  for (let i = 0; i < 24; i++) {
    await page.clock.runFor(250);
    const now = await page.evaluate(() => [...document.querySelectorAll('#pulse-fixture .radio-eq i')].map((e) => getComputedStyle(e).transform));
    now.forEach((t, k) => bars[k].push(t));
  }
  for (const seq of bars) expect(new Set(seq).size, 'each bar moves between two heights').toBe(2);
  expect(new Set(bars.map((b) => b.join('|'))).size, 'the bars do not move in lockstep').toBe(3);
});

test('the beat does not run while nothing on screen pulses', async ({ page }) => {
  await load(page);
  await mount(page, '<div class="sess idle"><span class="st"></span></div>');
  const before = await page.evaluate(() => document.documentElement.className);
  await page.clock.runFor(5000);
  expect(await page.evaluate(() => document.documentElement.className)).toBe(before);
});

// every page is mounted at once (they sit side by side), and a stack shows one card at a time, so an element that pulses can be
// in the page but not on screen: the beat must wait for it as well
for (const [name, place] of [['on another page', 'left: 2000px; top: 0'], ['in a card that is not shown', 'left: 0; top: 0; display: none'], ['below the screen', 'left: 0; top: 5000px']]) {
  test(`the beat does not run for a pulse ${name}`, async ({ page }) => {
    await load(page);
    await mount(page, FIXTURES['waiting session dot'].html, place);
    const before = await page.evaluate(() => document.documentElement.className);
    await page.clock.runFor(5000);
    expect(await page.evaluate(() => document.documentElement.className)).toBe(before);
  });
}

// a stack keeps its other cards in place at opacity 0, with a real size and inside the viewport
const STACK = (a, b) => `<div class="w-stack"><div class="stack-wrap"><div class="stack-item active">${a}</div><div class="stack-item">${b}</div></div></div>`;
const DOT = FIXTURES['waiting session dot'].html;
test('the beat does not run for a pulse in a stack card that is not the active one', async ({ page }) => {
  await load(page);
  await mount(page, STACK('<div class="sess idle"><span class="st"></span></div>', DOT));
  const before = await page.evaluate(() => document.documentElement.className);
  await page.clock.runFor(5000);
  expect(await page.evaluate(() => document.documentElement.className)).toBe(before);
});
test('the beat runs for a pulse in the active stack card', async ({ page }) => {
  await load(page);
  await mount(page, STACK(DOT, '<div class="sess idle"><span class="st"></span></div>'));
  const before = await page.evaluate(() => document.documentElement.className);
  await page.clock.runFor(1000);
  expect(await page.evaluate(() => document.documentElement.className)).not.toBe(before);
});

test('the beat waits while the panel is not visible', async ({ page }) => {
  await load(page);
  await mount(page, FIXTURES['waiting session dot'].html);
  await page.evaluate(() => window.onAppPause(true));
  const before = await page.evaluate(() => document.documentElement.className);
  await page.clock.runFor(5000);
  expect(await page.evaluate(() => document.documentElement.className)).toBe(before);
  await page.evaluate(() => window.onAppPause(false));
  await page.clock.runFor(750); // three ticks: beat-2 and beat-3 are on by then
  expect(await page.evaluate(() => document.documentElement.className)).not.toBe(before);
});
