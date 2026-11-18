// The Israel place picker on the settings page, the zmanim widget (Hebcal) and the work clock. Hebcal is answered from
// a fixture: the tests check what the panel asks for and what it draws, not Hebcal's arithmetic.
const { test, expect } = require('@playwright/test');

test.use({ timezoneId: 'Asia/Jerusalem' });

const DAY = '2026-11-18';
const at = (hm, day = DAY) => `${day}T${hm}:00+02:00`;
const TIMES = {
  alotHaShachar: at('04:53'), misheyakir: at('05:21'), sunrise: at('06:10'), sofZmanShmaMGA: at('08:10'),
  sofZmanShma: at('08:46'), sofZmanTfilla: at('09:38'), chatzot: at('11:23'), minchaGedola: at('11:49'),
  minchaKetana: at('14:26'), plagHaMincha: at('15:31'), sunset: at('16:37'), tzeit7083deg: at('17:05'),
  tzeit72min: at('17:49'),
};

async function panel(page, widgets, requests = []) {
  await page.clock.install({ time: new Date(`${DAY}T12:00:00+02:00`) });
  await page.route('https://www.hebcal.com/**', (route) => {
    const url = new URL(route.request().url());
    requests.push(url);
    if (url.pathname === '/zmanim') {
      const day = url.searchParams.get('date');
      const times = Object.fromEntries(Object.entries(TIMES).map(([k, v]) => [k, v.replace(DAY, day)]));
      return route.fulfill({ json: { date: day, times } });
    }
    return route.fulfill({ json: { items: [
      { title: 'Candle lighting: 16:01', date: at('16:01', '2026-11-20'), category: 'candles', hebrew: 'הדלקת נרות' },
      { title: 'Parashat Vayetzei', date: '2026-11-21', category: 'parashat', hebrew: 'פרשת ויצא' },
      { title: 'Havdalah: 17:15', date: at('17:15', '2026-11-21'), category: 'havdalah', hebrew: 'הבדלה' },
    ] } });
  });
  await page.addInitScript((widgets) => {
    const config = JSON.stringify({
      weather: { city: 'ירושלים', lat: 31.769, lon: 35.216 },
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'il', title: 'Israel', widgets }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  }, widgets);
  await page.goto('/panel/');
}

test('choosing a place in Israel fills in the weather name and coordinates', async ({ page }) => {
  await page.goto('/settings/#general');
  const picker = page.locator('#ilPlace');
  await expect(picker.locator('option')).not.toHaveCount(1);
  // the regions are groups, and every place is listed once
  expect(await picker.locator('optgroup').count()).toBe(7);
  const places = await page.evaluate(() => window.PhoneBoxIL.PLACES.map((p) => `${p.lat},${p.lon}`));
  expect(new Set(places).size).toBe(places.length);
  expect(await picker.locator('option').count()).toBe(places.length + 1);

  await picker.selectOption({ label: 'Jerusalem' });
  await expect(page.locator('[data-bind="weather.city"]')).toHaveValue('Jerusalem');
  await expect(page.locator('[data-bind="weather.lat"]')).toHaveValue('31.769');
  await expect(page.locator('[data-bind="weather.lon"]')).toHaveValue('35.216');
  // the picker keeps showing the place the settings hold
  await expect(picker.locator('option:checked')).toHaveText('Jerusalem');
});

test('the zmanim widget shows the next time, the Hebrew date and the Shabbat times for the weather location', async ({ page }) => {
  const requests = [];
  await panel(page, [{ id: 'zm', type: 'zmanim', x: 0, y: 0, w: 12, h: 6, options: {} }], requests);
  const widget = page.locator('.w-zmanim');
  await expect(widget).toHaveAttribute('dir', 'rtl');
  // 12:00: Mincha Gedola (11:49) has passed, Plag HaMincha is next
  await expect(widget.locator('.zm-nl')).toHaveText('פלג המנחה');
  await expect(widget.locator('.zm-nt')).toHaveText('15:31');
  await expect(widget.locator('.zm-i.on')).toHaveText('פלג המנחה15:31');
  await expect(widget.locator('.zm-i.past')).toHaveCount(5); // without the four "all the times" rows
  await expect(widget.locator('.w-sub')).toHaveText(/^[א-ת]+[״׳][א-ת]? ב[א-ת]+ תשפ״ז$/);
  await expect(widget.locator('.zm-shab')).toHaveText('🕯️ הדלקת נרות 16:01 · פרשת ויצא · ✨ הבדלה 17:15');

  const zmanim = requests.find((u) => u.pathname === '/zmanim');
  expect(zmanim.searchParams.get('latitude')).toBe('31.769');
  expect(zmanim.searchParams.get('longitude')).toBe('35.216');
  expect(zmanim.searchParams.get('tzid')).toBe('Asia/Jerusalem');
  expect(zmanim.searchParams.get('date')).toBe(DAY);
  expect(requests.find((u) => u.pathname === '/shabbat').searchParams.get('b')).toBe('18');
});

test('the zmanim widget uses transliterated names and every time when asked to', async ({ page }) => {
  await panel(page, [{ id: 'zm', type: 'zmanim', x: 0, y: 0, w: 12, h: 6, options: { hebrew: false, full: true, shabbat: false } }]);
  const widget = page.locator('.w-zmanim');
  await expect(widget.locator('.zm-nl')).toHaveText('Mincha Ketana');
  await expect(widget.locator('.zm-i')).toHaveCount(13);
  await expect(widget.locator('.w-sub')).toHaveText(/ 5787$/);
  await expect(widget.locator('.zm-shab')).toBeHidden();
});

test('Hebrew numerals are written the way dates are', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const { gematria } = await import('/panel/widgets/israel.js');
    return [1, 15, 16, 28, 30, 5787, 5800].map(gematria);
  });
  expect(out).toEqual(['א׳', 'ט״ו', 'ט״ז', 'כ״ח', 'ל׳', 'תשפ״ז', 'ת״ת']);
});

test('the work clock counts the running entry and adds it to today', async ({ page }) => {
  await panel(page, [{ id: 'wk', type: 'workclock', x: 0, y: 0, w: 12, h: 6, options: { target: 8 } }]);
  const widget = page.locator('.w-workclock');
  await expect(widget.locator('.push-empty')).toContainText('POST /api/push/workclock');

  const push = (body) => page.evaluate(async (body) => {
    const { API } = await import('/panel/main.js');
    await fetch(API + '/push/workclock', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  }, body);
  await push({ running: true, startedAt: `${DAY}T11:00:00+02:00`, client: 'לתת פה', project: 'מערכת תרומות', todaySec: 5400 });
  await page.clock.runFor(5000);
  await expect(widget).toHaveClass(/\brun\b/);
  await expect(widget.locator('.wk-t')).toHaveText(/^1:00:\d\d$/);
  await expect(widget.locator('.w-title')).toContainText('לתת פה');
  await expect(widget.locator('.wk-p')).toHaveText('מערכת תרומות');
  await expect(widget.locator('.wk-day')).toHaveText('Today 2:30 / 8:00'); // 1:30 logged + 1:00 running

  await push({ running: false, client: 'לתת פה', todaySec: 9000 });
  await page.clock.runFor(5000);
  await expect(widget).not.toHaveClass(/\brun\b/);
  await expect(widget.locator('.wk-t')).toHaveText('2:30');
});
