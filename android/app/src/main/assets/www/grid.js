// How many cells a page has. A phone is the 12 × 6 grid the layouts were drawn on (the editor canvas is
// 752 × 333). A tablet is shorter for its width, so the same 6 rows would stretch every widget. Extra rows
// keep each cell about as wide-versus-tall as on the phone, and that space holds more widgets.
// The editor's screen buttons in settings.html (phone 12×6, tablet 12×10, wide tablet 12×8) must stay in step
// with gridForViewport() for 780×360, 1024×768 and 1280×800.

export const PHONE_COLS = 12;
export const PHONE_ROWS = 6;
/** Editor canvas, and the grid inside a 780×360 phone after the page padding. */
export const PHONE_ASPECT = 752 / 333;
/** Android's tablet breakpoint (sw600dp). CSS pixels on the WebView match dp. */
export const TABLET_MIN_PX = 600;
/** Tallest grid a page can be saved as. Taller than this, the cells turn into strips. */
export const MAX_ROWS = 18;
/** .page padding in style.css: 14px each side, 10px top, 17px bottom. Safe areas are extra on notched phones. */
const PAD_X = 28;
const PAD_Y = 27;

/**
 * @param {number} width viewport width, CSS pixels
 * @param {number} height viewport height, CSS pixels
 * @param {{ tabletRows?: boolean }} [options] tabletRows: Settings › Display › More rows on a tablet (off by default).
 *   Off, every screen keeps the 12 × 6 page and portrait packs into 6 columns, as before the setting existed.
 * @returns {{ cols: number, rows: number, tablet: boolean, portrait: boolean }}
 */
export function gridForViewport(width, height, { tabletRows = false } = {}) {
  const w = Math.max(1, Number(width) || 1);
  const h = Math.max(1, Number(height) || 1);
  const portrait = h > w * 1.05;
  const tablet = tabletRows && Math.min(w, h) >= TABLET_MIN_PX;
  // Upright: pack into columns that keep a cell about as wide as on a phone in landscape.
  // A phone stays at 6; a tablet has room for the full 12, so two phone-width widgets sit side by side.
  if (portrait) return { cols: tablet ? PHONE_COLS : 6, rows: PHONE_ROWS, tablet, portrait: true };
  if (!tablet) return { cols: PHONE_COLS, rows: PHONE_ROWS, tablet: false, portrait: false };
  const aspect = Math.max(1, w - PAD_X) / Math.max(1, h - PAD_Y);
  if (aspect >= PHONE_ASPECT * 0.92) return { cols: PHONE_COLS, rows: PHONE_ROWS, tablet: false, portrait: false };
  const rows = Math.max(PHONE_ROWS, Math.min(MAX_ROWS, Math.round(PHONE_ROWS * PHONE_ASPECT / aspect)));
  return { cols: PHONE_COLS, rows, tablet: rows > PHONE_ROWS, portrait: false };
}

/** The landscape grid for this screen, including when the device is held upright. */
export function designGrid(width, height, options) {
  const w = Math.max(1, Number(width) || 1);
  const h = Math.max(1, Number(height) || 1);
  return gridForViewport(Math.max(w, h), Math.min(w, h), options);
}

/** Where a saved widget sits on a cols×rows page. Null when it starts outside the page (a tablet row on a phone). */
export function placeWidget(w, cols, rows) {
  const x = w?.x ?? 0, y = w?.y ?? 0, ww = w?.w ?? 1, hh = w?.h ?? 1;
  if (x >= cols || y >= rows) return null;
  return { x, y, w: Math.min(ww, cols - x), h: Math.min(hh, rows - y) };
}

/** The widgets that sit on a cols×rows page; the rest are on rows this screen does not have. */
export function widgetsOnGrid(widgets, cols, rows) {
  return (widgets || []).filter((w) => placeWidget(w, cols, rows));
}
