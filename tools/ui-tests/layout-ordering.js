// Exercise the real fixture helper with the stale bootstrap completing after its storage write.
async function gatedLayout(page, layout, label = 'owned-layout') {
  await page.goto('/panel/style.css');
  await page.evaluate(() => localStorage.clear());
  let release;
  let started;
  const gate = new Promise(resolve => release = resolve);
  const ready = new Promise(resolve => started = resolve);
  let bootstrapping = false;
  let firstNavigation = true;
  await page.route('**/templates.json', async route => { started(); await gate; await route.continue(); });
  const wrapped = new Proxy(page, { get(target, property) {
    if (property === 'goto') return async (...args) => {
      if (firstNavigation) { firstNavigation = false; bootstrapping = args[0] === '/panel/'; }
      return target.goto(...args);
    };
    if (property === 'evaluate') return async (...args) => {
      if (bootstrapping) await ready;
      const result = await target.evaluate(...args);
      if (bootstrapping) {
        release();
        await target.waitForFunction(() => JSON.parse(localStorage.getItem('pb-demo-config') || 'null')?.pages?.length > 1);
        bootstrapping = false;
      }
      return result;
    };
    const value = target[property];
    return typeof value === 'function' ? value.bind(target) : value;
  } });
  try {
    await layout(wrapped, [{ id: 'note', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: label } }]);
    return page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages.map(p => p.id));
  } finally { release(); await page.unroute('**/templates.json'); }
}

module.exports = { gatedLayout };
