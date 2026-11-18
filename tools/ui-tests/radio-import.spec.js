const { test, expect } = require('@playwright/test');

test('radio import preserves path and query case while deduplicating hosts', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => localStorage.setItem('pb-demo-config', JSON.stringify({
    display: { language: 'en' }, pages: [], controls: { actions: [], profiles: [] },
  })));
  await page.goto('/settings/#general');
  await page.evaluate(() => {
    const demoFetch = window.fetch;
    const countries = [{ code: 'TR', name: 'Türkiye', stationCount: 4 }];
    const stations = [
      { id: 'first', label: 'Upper path', streamUrl: 'https://stream.example.com/Live?channel=A' },
      { id: 'second', label: 'Lower path', streamUrl: 'https://stream.example.com/live?channel=A' },
      { id: 'third', label: 'Lower channel', streamUrl: 'https://stream.example.com/live?channel=a' },
      { id: 'duplicate', label: 'Same upper path', streamUrl: 'HTTPS://STREAM.EXAMPLE.COM:443/Live?channel=A' },
    ];
    window.fetch = (url, options) => {
      const path = String(url);
      if (path === '/api/controls/radio-directory/countries') return Promise.resolve(Response.json({ countries }));
      if (path.startsWith('/api/controls/radio-directory/stations?')) return Promise.resolve(Response.json({ stations }));
      return demoFetch(url, options);
    };
  });
  await page.getByText('Import & verify radio stations (radio-browser.info)', { exact: true }).click();
  await page.getByRole('button', { name: 'Load countries' }).click();
  await page.getByRole('combobox', { name: 'Country', exact: true }).selectOption('TR');
  const importStations = page.getByRole('button', { name: 'Import stations' });
  await importStations.click();
  await expect(page.locator('#controlSettings').getByText(/^Imported 3 new station/)).toContainText('(1 already present)');
  await importStations.click();
  await expect(page.locator('#controlSettings').getByText(/^Imported 0 new station/)).toContainText('(4 already present)');
});
