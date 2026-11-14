// Shared by the panel and desktop editor. Omitted values preserve the original look.
(() => {
  'use strict';
  const presets = Object.freeze({
    default: {},
    cards: { radius: 18, gap: 12, padding: 12, border: true, shadow: false },
    flat: { radius: 12, gap: 12, padding: 12, border: false, shadow: false },
    borderless: { radius: 0, gap: 12, padding: 12, border: false, shadow: false },
    seamless: { radius: 0, gap: 0, padding: 12, border: true, shadow: false },
  });
  const fonts = Object.freeze({
    default: '',
    sans: 'Roboto, "Segoe UI", system-ui, sans-serif',
    rounded: '"SF Pro Rounded", "Hiragino Maru Gothic ProN", Quicksand, Nunito, "Trebuchet MS", Roboto, system-ui, sans-serif',
    condensed: '"Roboto Condensed", "Arial Narrow", "Liberation Sans Narrow", sans-serif',
    serif: 'Georgia, "Times New Roman", "Noto Serif", serif',
    mono: '"Roboto Mono", "Cascadia Mono", ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
  });
  const limits = { radius: 32, gap: 24, padding: 24, pagePadding: 32 };
  const scaledSheets = new WeakSet();
  const skipScale = /^(inherit|initial|unset|revert|revert-layer|xx-small|x-small|small|medium|large|x-large|xx-large|smaller|larger|-?\d*\.?\d+(em|%))$/i;

  function scaleRuleList(rules) {
    if (!rules) return;
    for (const rule of rules) {
      if (rule.cssRules) scaleRuleList(rule.cssRules);
      const size = rule.style && rule.style.fontSize;
      if (!size || size.includes('--widget-font-scale') || skipScale.test(size.trim())) continue;
      if (rule.selectorText && /^(?:html|body|:root)(?:[\s,{[]|$)/i.test(rule.selectorText.trim())) continue;
      const priority = rule.style.getPropertyPriority('font-size');
      rule.style.setProperty('font-size', 'calc((' + size + ') * var(--widget-font-scale, 1))', priority);
    }
  }

  function scalePanelSheets() {
    if (typeof document === 'undefined' || !document.styleSheets) return;
    for (const sheet of document.styleSheets) {
      if (scaledSheets.has(sheet)) continue;
      const href = sheet.href || '';
      if (!/\/(?:style|controls)\.css(?:[?#]|$)/i.test(href)) continue;
      try {
        scaleRuleList(sheet.cssRules);
        scaledSheets.add(sheet);
      } catch (_) {}
    }
  }

  if (typeof window !== 'undefined' && window.addEventListener) {
    scalePanelSheets();
    window.addEventListener('load', scalePanelSheets, { once: true });
  }

  function resolve(...layers) {
    let result = {};
    for (const layer of layers) {
      if (!layer || typeof layer !== 'object') continue;
      if (typeof layer.style === 'string' && Object.prototype.hasOwnProperty.call(presets, layer.style)) {
        result = layer.style === 'default' ? { style: 'default' } : { ...result, ...presets[layer.style], style: layer.style };
      }
      if (typeof layer.font === 'string' && Object.prototype.hasOwnProperty.call(fonts, layer.font)) {
        result.font = layer.font;
      }
      if (typeof layer.fontScale === 'number' && Number.isFinite(layer.fontScale)) {
        result.fontScale = Math.max(50, Math.min(200, Math.round(layer.fontScale)));
      }
      for (const [key, max] of Object.entries(limits))
        if (typeof layer[key] === 'number' && Number.isFinite(layer[key])) result[key] = Math.max(0, Math.min(max, Math.round(layer[key])));
      for (const key of ['border', 'shadow']) if (typeof layer[key] === 'boolean') result[key] = layer[key];
    }
    return result;
  }
  function widget(element, appearance = {}) {
    scalePanelSheets();
    const fontStack = appearance.font ? fonts[appearance.font] : '';
    element.classList.toggle('has-widget-font', Boolean(fontStack));
    if (fontStack) element.style.setProperty('--widget-font', fontStack);
    else element.style.removeProperty('--widget-font');
    const scaled = appearance.fontScale != null && appearance.fontScale !== 100;
    element.classList.toggle('has-widget-font-scale', scaled);
    if (scaled) element.style.setProperty('--widget-font-scale', String(appearance.fontScale / 100));
    else element.style.removeProperty('--widget-font-scale');
    for (const key of ['radius', 'padding']) {
      const active = appearance[key] != null;
      element.classList.toggle('has-widget-' + key, active);
      if (active) element.style.setProperty('--widget-' + key, appearance[key] + 'px');
      else element.style.removeProperty('--widget-' + key);
    }
    for (const key of ['border', 'shadow']) {
      if (appearance[key] != null) element.dataset['widget' + key[0].toUpperCase() + key.slice(1)] = String(appearance[key]);
      else delete element.dataset['widget' + key[0].toUpperCase() + key.slice(1)];
    }
    if (appearance.style && appearance.style !== 'default') element.dataset.widgetSurface = appearance.style;
    else delete element.dataset.widgetSurface;
  }
  function page(element, grid, appearance = {}) {
    grid.style.gap = appearance.gap != null ? appearance.gap + 'px' : '';
    if (appearance.pagePadding != null) {
      const n = appearance.pagePadding + 'px';
      element.style.padding = 'max(' + n + ', env(safe-area-inset-top)) max(' + n + ', env(safe-area-inset-right)) max(' + n + ', 17px, env(safe-area-inset-bottom)) max(' + n + ', env(safe-area-inset-left))';
    } else element.style.padding = '';
  }
  globalThis.PhoneBoxAppearance = Object.freeze({ presets, fonts, resolve, widget, page });
})();
