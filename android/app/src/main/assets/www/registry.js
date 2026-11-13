// The widget registry: W[type] = { needs: [data kinds], bare?, create(box, ctx) -> { update(kind), destroy() } }.
// On its own so the widget modules can fill it while main.js is still loading.
export const W = {};
export const resolveWidget = (type) =>
  W[type] || (typeof type === 'string' && type.startsWith('plugin:') ? W['plugin:*'] : undefined);
