// Web kiosk lite runtime (tools/kiosk-lite/polyfill.js): fills in what Firefox 68 cannot evaluate in the panel's CSS,
// from the table build.mjs writes into window.PF_TABLE:
//   values   [selector, property, expression]  → --pf-N on each matching element (cqw/cqh, min/max/clamp, color-mix)
//   queries  [[[min|max, width|height, px]…], selector] → data-pf-cq~="N" when the element's container matches
//   has      [selector, inner]                  → class pf-has-N when the element contains inner
//   ratios   [selector, ratio, 'w' | 'h']       → inline width (from height) or height (from width)
// Plus the few DOM / JS APIs the panel uses that Firefox 68 lacks. Classic script, runs before the panel's module.
(function () {
  'use strict';

  // ---------------------------------------------------------------- JS / DOM APIs missing in Firefox 68
  function replaceChildren() {
    while (this.lastChild) this.removeChild(this.lastChild);
    this.append.apply(this, arguments);
  }
  [Element, Document, DocumentFragment].forEach(function (C) {
    if (!C.prototype.replaceChildren) Object.defineProperty(C.prototype, 'replaceChildren', { value: replaceChildren, configurable: true, writable: true });
  });
  function at(i) { i = Math.trunc(i) || 0; if (i < 0) i += this.length; return i < 0 || i >= this.length ? undefined : this[i]; }
  [Array, String].forEach(function (C) {
    if (!C.prototype.at) Object.defineProperty(C.prototype, 'at', { value: at, configurable: true, writable: true });
  });

  var T = window.PF_TABLE;
  if (!T) return;

  // ---------------------------------------------------------------- expression evaluation
  var ctx = null; // { el, prop, cw, ch, style }

  function cssOf(el) { return el ? getComputedStyle(el) : null; }
  function customProp(name) {
    var s = ctx.style || (ctx.style = cssOf(ctx.el));
    return s.getPropertyValue(name).trim();
  }
  function fontSizeOf(el) { return parseFloat(cssOf(el).fontSize) || 16; }
  function percentBase() {
    var p = ctx.el.parentElement || document.documentElement;
    if (ctx.prop === 'font-size') return fontSizeOf(p);
    if (/height|top|bottom|^inset-block/.test(ctx.prop)) return p.clientHeight;
    return p.clientWidth;
  }

  /** Index of the parenthesis closing the one at i. */
  function closing(s, i) {
    for (var d = 0, j = i; j < s.length; j++) {
      if (s[j] === '(') d++;
      else if (s[j] === ')' && --d === 0) return j;
    }
    return s.length - 1;
  }
  function splitArgs(s) {
    var out = [], d = 0, cur = '';
    for (var i = 0; i < s.length; i++) {
      var c = s[i];
      if (c === '(') d++; else if (c === ')') d--;
      if (c === ',' && d === 0) { out.push(cur.trim()); cur = ''; } else cur += c;
    }
    if (cur.trim() || out.length) out.push(cur.trim());
    return out;
  }

  // A numeric value: { v: number, u: 'px' | '' }. Lengths are resolved to px.
  function unitToPx(n, u) {
    switch (u) {
      case '': return { v: n, u: '' };
      case 'px': return { v: n, u: 'px' };
      case '%': return { v: n * percentBase() / 100, u: 'px' };
      case 'cqw': case 'cqi': return { v: n * ctx.cw / 100, u: 'px' };
      case 'cqh': case 'cqb': return { v: n * ctx.ch / 100, u: 'px' };
      case 'cqmin': return { v: n * Math.min(ctx.cw, ctx.ch) / 100, u: 'px' };
      case 'cqmax': return { v: n * Math.max(ctx.cw, ctx.ch) / 100, u: 'px' };
      case 'vw': return { v: n * innerWidth / 100, u: 'px' };
      case 'vh': return { v: n * innerHeight / 100, u: 'px' };
      case 'vmin': return { v: n * Math.min(innerWidth, innerHeight) / 100, u: 'px' };
      case 'vmax': return { v: n * Math.max(innerWidth, innerHeight) / 100, u: 'px' };
      case 'em': return { v: n * (ctx.prop === 'font-size' ? fontSizeOf(ctx.el.parentElement || ctx.el) : fontSizeOf(ctx.el)), u: 'px' };
      case 'rem': return { v: n * fontSizeOf(document.documentElement), u: 'px' };
      case 'deg': return { v: n, u: 'deg' };
      case 's': return { v: n, u: 's' };
      case 'ms': return { v: n / 1000, u: 's' };
      default: throw new Error('unit ' + u);
    }
  }

  // Recursive descent over + - * / with functions min / max / clamp / calc / var / env.
  function parseNum(s) {
    var p = { s: s, i: 0 };
    var v = sum(p);
    skip(p);
    if (p.i < p.s.length) throw new Error('unexpected ' + p.s.slice(p.i));
    return v;
  }
  function skip(p) { while (p.i < p.s.length && /\s/.test(p.s[p.i])) p.i++; }
  function sum(p) {
    var a = product(p);
    for (;;) {
      skip(p);
      var c = p.s[p.i];
      if (c !== '+' && c !== '-') return a;
      p.i++;
      var b = product(p);
      a = { v: c === '+' ? a.v + b.v : a.v - b.v, u: a.u || b.u };
    }
  }
  function product(p) {
    var a = atom(p);
    for (;;) {
      skip(p);
      var c = p.s[p.i];
      if (c !== '*' && c !== '/') return a;
      p.i++;
      var b = atom(p);
      a = c === '*' ? { v: a.v * b.v, u: a.u || b.u } : { v: a.v / b.v, u: a.u };
    }
  }
  function atom(p) {
    skip(p);
    var rest = p.s.slice(p.i), m;
    if (rest[0] === '(') {
      var end = closing(p.s, p.i);
      var inner = parseNum(p.s.slice(p.i + 1, end));
      p.i = end + 1;
      return inner;
    }
    if ((m = /^([a-z-]+)\(/i.exec(rest))) {
      var name = m[1].toLowerCase(), open = p.i + m[1].length, close = closing(p.s, open);
      var args = splitArgs(p.s.slice(open + 1, close));
      p.i = close + 1;
      return fn(name, args);
    }
    if ((m = /^([+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?)(%|[a-z]+)?/i.exec(rest))) {
      p.i += m[0].length;
      return unitToPx(parseFloat(m[1]), (m[2] || '').toLowerCase());
    }
    throw new Error('cannot read ' + rest);
  }
  function fn(name, args) {
    switch (name) {
      case 'calc': return parseNum(args.join(','));
      case 'min': return args.map(parseNum).reduce(function (a, b) { return b.v < a.v ? b : a; });
      case 'max': return args.map(parseNum).reduce(function (a, b) { return b.v > a.v ? b : a; });
      case 'clamp': {
        var lo = parseNum(args[0]), val = parseNum(args[1]), hi = parseNum(args[2]);
        return { v: Math.max(lo.v, Math.min(val.v, hi.v)), u: val.u || lo.u || hi.u };
      }
      case 'var': {
        var v = customProp(args[0]);
        if (!v && args.length > 1) v = args.slice(1).join(',');
        return parseNum(v || '0');
      }
      case 'env': return parseNum(args.length > 1 ? args.slice(1).join(',') : '0px');
      default: throw new Error('function ' + name);
    }
  }
  function numText(n) { return (Math.round(n.v * 1000) / 1000) + n.u; }

  // ---------------------------------------------------------------- colors (color-mix in srgb)
  var colorCtx = document.createElement('canvas').getContext('2d');
  function rgba(text) {
    text = text.trim();
    if (text === 'transparent') return [0, 0, 0, 0];
    colorCtx.fillStyle = '#000';
    colorCtx.fillStyle = text;
    var c = colorCtx.fillStyle, m;
    if (c[0] === '#') return [parseInt(c.slice(1, 3), 16), parseInt(c.slice(3, 5), 16), parseInt(c.slice(5, 7), 16), 1];
    if ((m = /rgba?\(([^)]+)\)/.exec(c))) { var p = m[1].split(',').map(parseFloat); return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1]; }
    return [0, 0, 0, 0];
  }
  function colorMix(args) {
    // [in srgb, "A p%", "B q%"]
    function part(s) {
      var m = /\s(\d*\.?\d+)%\s*$/.exec(' ' + s);
      return { c: rgba(resolveVars(m ? s.slice(0, s.length - m[0].length + 1) : s)), p: m ? parseFloat(m[1]) / 100 : null };
    }
    var a = part(args[1]), b = part(args[2]);
    var pa = a.p == null ? (b.p == null ? 0.5 : 1 - b.p) : a.p, pb = b.p == null ? 1 - pa : b.p;
    var sum = pa + pb || 1; pa /= sum; pb /= sum;
    // premultiplied alpha, as in CSS Color 5
    var al = a.c[3] * pa + b.c[3] * pb, ch = [0, 1, 2].map(function (i) {
      return al ? Math.round((a.c[i] * a.c[3] * pa + b.c[i] * b.c[3] * pb) / al) : 0;
    });
    return 'rgba(' + ch.join(', ') + ', ' + (Math.round(al * 1000) / 1000) + ')';
  }
  function resolveVars(s) {
    return s.replace(/var\(\s*(--[\w-]+)\s*(?:,([^()]*(?:\([^()]*\))?[^()]*))?\)/g, function (_, name, fb) {
      var v = customProp(name);
      return v || (fb ? resolveVars(fb.trim()) : '');
    });
  }

  /** Evaluates the parts of a declaration value Firefox 68 does not understand, leaving the rest as written. */
  function evalValue(s) {
    var out = '', i = 0, m;
    while (i < s.length) {
      var rest = s.slice(i);
      if ((m = /^([a-z-]+)\(/i.exec(rest)) && (i === 0 || !/[\w-]/.test(s[i - 1]))) {
        var name = m[1].toLowerCase(), open = i + m[1].length, close = closing(s, open), body = s.slice(open + 1, close);
        if (name === 'color-mix') out += colorMix(splitArgs(body));
        else if (name === 'min' || name === 'max' || name === 'clamp' || (name === 'calc' && /cq|min\(|max\(|clamp\(/.test(body))) out += numText(fn(name, splitArgs(body)));
        else out += name + '(' + evalValue(body) + ')';
        i = close + 1;
        continue;
      }
      if ((m = /^(\d*\.?\d+)(cqw|cqh|cqi|cqb|cqmin|cqmax)\b/.exec(rest)) && (i === 0 || !/[\w.-]/.test(s[i - 1]))) {
        out += numText(unitToPx(parseFloat(m[1]), m[2]));
        i += m[0].length;
        continue;
      }
      out += s[i++];
    }
    return out;
  }

  // ---------------------------------------------------------------- applying the table
  var containers = [], sizes = new Map();
  function containerOf(el) {
    for (var p = el.parentElement; p; p = p.parentElement) if (sizes.has(p)) return p;
    return null;
  }
  function containerSize(el) {
    var c = containerOf(el);
    return c ? sizes.get(c) : { w: innerWidth, h: innerHeight }; // no container: small viewport units
  }
  function all(sel) { try { return document.querySelectorAll(sel); } catch (e) { return []; } }

  var observer, errors = {};
  function report(key, e) {
    if (errors[key]) return;
    errors[key] = true;
    if (window.console) console.warn('kiosk lite: ' + key + ': ' + (e && e.message));
  }

  function measure() {
    sizes = new Map();
    if (!T.containers) return;
    containers = all(T.containers);
    for (var i = 0; i < containers.length; i++) {
      var c = containers[i], s = getComputedStyle(c);
      // size queries use the content box
      sizes.set(c, {
        w: c.clientWidth - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight),
        h: c.clientHeight - parseFloat(s.paddingTop) - parseFloat(s.paddingBottom),
      });
    }
  }

  function apply() {
    measure();
    var writes = [];
    // :has()
    T.has.forEach(function (h, n) {
      var els = all(h[0]);
      for (var i = 0; i < els.length; i++) {
        var want = !!els[i].querySelector(h[1]), cls = 'pf-has-' + n;
        if (els[i].classList.contains(cls) !== want) writes.push(function (el, c, w) { return function () { el.classList.toggle(c, w); }; }(els[i], cls, want));
      }
    });
    // @container
    var tokens = new Map();
    T.queries.forEach(function (q, n) {
      var els = all(q[1]);
      for (var i = 0; i < els.length; i++) {
        var el = els[i], c = containerOf(el), ok = !!c;
        if (ok) {
          var size = sizes.get(c);
          for (var k = 0; k < q[0].length && ok; k++) {
            var cond = q[0][k], v = cond[1] === 'width' ? size.w : size.h;
            ok = cond[0] === 'min' ? v >= cond[2] : v <= cond[2];
          }
        }
        if (!tokens.has(el)) tokens.set(el, []);
        if (ok) tokens.get(el).push(n);
      }
    });
    all('[data-pf-cq]').forEach(function (el) { if (!tokens.has(el)) tokens.set(el, []); });
    tokens.forEach(function (list, el) {
      var text = list.join(' ');
      if ((el.getAttribute('data-pf-cq') || '') !== text) writes.push(function () { if (text) el.setAttribute('data-pf-cq', text); else el.removeAttribute('data-pf-cq'); });
    });
    // values
    T.values.forEach(function (d, n) {
      var els = all(d[0]), name = '--pf-' + n;
      for (var i = 0; i < els.length; i++) {
        var el = els[i], size = containerSize(el);
        ctx = { el: el, prop: d[1], cw: size.w, ch: size.h, style: null };
        try {
          var v = evalValue(d[2]);
          if (el.style.getPropertyValue(name) !== v) writes.push(function (e, val) { return function () { e.style.setProperty(name, val); }; }(el, v));
        } catch (e) { report(d[0] + ' { ' + d[1] + ': ' + d[2] + ' }', e); }
      }
    });
    writes.forEach(function (w) { w(); });
    // aspect-ratio, once the sizes it depends on are in place
    T.ratios.forEach(function (r) {
      var els = all(r[0]);
      for (var i = 0; i < els.length; i++) {
        var el = els[i];
        if (r[2] === 'w') { el.style.width = ''; var h = el.getBoundingClientRect().height; if (h) el.style.width = (h * r[1]) + 'px'; }
        else { el.style.height = ''; var w = el.getBoundingClientRect().width; if (w) el.style.height = (w / r[1]) + 'px'; }
      }
    });
    if (observer) observer.takeRecords(); // our own changes
    return writes.length;
  }

  var pending = 0;
  function schedule() {
    if (pending) return;
    pending = setTimeout(function () {
      pending = 0;
      // values can depend on each other (custom properties, container sizes): settle in a few rounds
      for (var round = 0; round < 3 && apply(); round++);
    }, 30);
  }

  function start() {
    schedule();
    observer = new MutationObserver(schedule);
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style'] });
    addEventListener('resize', schedule);
    addEventListener('orientationchange', schedule);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(schedule);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
  window.__kioskLite = { apply: apply, evalValue: evalValue };
})();
