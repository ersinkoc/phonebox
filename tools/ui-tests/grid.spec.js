const { test, expect } = require('@playwright/test');

// grid.js is a pure module; it is loaded in the page because the panel serves it as an ES module.
async function grid(page, fn) {
  await page.goto('/panel/');
  return page.evaluate(async (src) => {
    const g = await import('./grid.js');
    return new Function('g', `return (${src})(g);`)(g);
  }, fn.toString());
}

test('with More rows on a tablet off, every screen keeps the 12 × 6 page', async ({ page }) => {
  const r = await grid(page, (g) => [
    g.gridForViewport(780, 360), g.gridForViewport(360, 780),
    g.gridForViewport(1024, 768), g.gridForViewport(768, 1024), g.gridForViewport(1280, 800),
  ]);
  expect(r).toEqual([
    { cols: 12, rows: 6, tablet: false, portrait: false },
    { cols: 6, rows: 6, tablet: false, portrait: true },
    { cols: 12, rows: 6, tablet: false, portrait: false },
    { cols: 6, rows: 6, tablet: false, portrait: true },
    { cols: 12, rows: 6, tablet: false, portrait: false },
  ]);
});

test('with More rows on a tablet on, the rows follow the tablet screen and match the editor presets', async ({ page }) => {
  const r = await grid(page, (g) => {
    const on = { tabletRows: true };
    return {
      phone: g.gridForViewport(780, 360, on),
      phoneUpright: g.gridForViewport(360, 780, on),
      nexus9: g.gridForViewport(1024, 768, on),
      nexus9Upright: g.gridForViewport(768, 1024, on),
      nexus9Design: g.designGrid(768, 1024, on),
      wide: g.gridForViewport(1280, 800, on),
      hd: g.gridForViewport(1280, 720, on).rows,
      tall: g.gridForViewport(1200, 800, on).rows,
      widePhone: g.gridForViewport(800, 360, on),
      max: g.MAX_ROWS,
    };
  });
  expect(r.phone).toEqual({ cols: 12, rows: 6, tablet: false, portrait: false });
  expect(r.phoneUpright).toEqual({ cols: 6, rows: 6, tablet: false, portrait: true });
  expect(r.nexus9).toEqual({ cols: 12, rows: 10, tablet: true, portrait: false });
  expect(r.nexus9Upright).toEqual({ cols: 12, rows: 6, tablet: true, portrait: true });
  expect(r.nexus9Design).toEqual({ cols: 12, rows: 10, tablet: true, portrait: false });
  expect(r.wide).toEqual({ cols: 12, rows: 8, tablet: true, portrait: false });
  expect([r.hd, r.tall]).toEqual([7, 9]); // the counts the editor's note gives as examples
  expect(r.widePhone).toEqual({ cols: 12, rows: 6, tablet: false, portrait: false });
  expect(r.nexus9.rows).toBeLessThanOrEqual(r.max);
});

test('widgets are placed, clipped or left out by the grid they land on', async ({ page }) => {
  const r = await grid(page, (g) => {
    const page = [{ id: 'top', x: 0, y: 0, w: 4, h: 2 }, { id: 'tablet', x: 0, y: 6, w: 4, h: 2 }, { id: 'edge', x: 4, y: 4, w: 4, h: 4 }];
    return {
      hidden: g.placeWidget({ x: 0, y: 6, w: 4, h: 2 }, 12, 6),
      kept: g.placeWidget({ x: 0, y: 6, w: 4, h: 2 }, 12, 10),
      clamped: g.placeWidget({ x: 10, y: 4, w: 4, h: 4 }, 12, 6),
      phone: g.widgetsOnGrid(page, 12, 6).map((w) => w.id),
      tablet: g.widgetsOnGrid(page, 12, 10).map((w) => w.id),
    };
  });
  expect(r.hidden).toBeNull();
  expect(r.kept).toEqual({ x: 0, y: 6, w: 4, h: 2 });
  expect(r.clamped).toEqual({ x: 10, y: 4, w: 2, h: 2 });
  expect(r.phone).toEqual(['top', 'edge']);
  expect(r.tablet).toEqual(['top', 'tablet', 'edge']);
});
