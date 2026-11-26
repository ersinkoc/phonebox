// The panel runs on old Android System WebViews (index.html lets Chromium 80 in; compat.js fills in the newer DOM
// APIs). Newer syntax can't be filled in: one use stops the whole module from parsing, and the panel stays black with
// "Unexpected token" (#654, a `||=` on Chromium 81). This keeps syntax newer than Chromium 80 out of the panel's code.
// There is no JavaScript parser among the test dependencies, so a small lexer blanks out comments, strings, regular
// expressions and template text (keeping the code inside `${…}`), and the patterns below run on what is left.
// Top-level await (Chromium 89) is not checked: telling it from an await inside a function needs a real parser.
const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

const WWW = path.join(__dirname, '../../android/app/src/main/assets/www');
const files = (dir) => fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
  e.isDirectory() ? files(path.join(dir, e.name)) : e.name.endsWith('.js') ? [path.join(dir, e.name)] : []);

// syntax and the first Chromium that parses it
const NEWER = [
  [/(\|\||&&|\?\?)=/, 'logical assignment (||=, &&=, ??=), Chromium 85'],
  [/(^|[;{}]\s*)(static\s+)?(async\s+)?(get\s+|set\s+)?#[A-Za-z_$][\w$]*\s*\(/m, 'private method, Chromium 84'],
  [/\bstatic\s*\{/, 'class static block, Chromium 94'],
];

/**
 * The source with every comment, string, regular expression and piece of template text replaced by spaces (newlines
 * kept, so line numbers stay), and the code inside a template's `${…}` kept as code.
 */
function codeOnly(src) {
  let out = '';
  let i = 0;
  const stack = []; // one entry per open `${`: the brace depth at which it closes
  let depth = 0;
  let prev = ''; // the last significant code character, to tell a regular expression from a division
  const blank = (s) => s.replace(/[^\n]/g, ' ');
  const regexCanStart = () => !prev || /[(,=:[!&|?{};+\-*%<>~^]/.test(prev) || /\b(return|typeof|case|do|else|in|of|void|yield|await)$/.test(out.trimEnd());
  const template = () => { // from just after a backtick (or a closing `}` of `${`) to the closing backtick or the next `${`
    let s = '';
    while (i < src.length) {
      const c = src[i];
      if (c === '\\') { s += src.slice(i, i + 2); i += 2; continue; }
      if (c === '`') { out += blank(s) + ' '; i++; prev = 'x'; return; }
      if (c === '$' && src[i + 1] === '{') { out += blank(s) + '  '; i += 2; stack.push(depth); depth++; prev = '{'; return; }
      s += c; i++;
    }
    out += blank(s);
  };
  while (i < src.length) {
    const c = src[i], n = src[i + 1];
    if (c === '/' && n === '/') { const end = src.indexOf('\n', i); const stop = end < 0 ? src.length : end; out += blank(src.slice(i, stop)); i = stop; continue; }
    if (c === '/' && n === '*') { const end = src.indexOf('*/', i + 2); const stop = end < 0 ? src.length : end + 2; out += blank(src.slice(i, stop)); i = stop; continue; }
    if (c === '"' || c === "'") {
      let j = i + 1;
      while (j < src.length && src[j] !== c && src[j] !== '\n') j += src[j] === '\\' ? 2 : 1;
      out += blank(src.slice(i, j + 1)); i = j + 1; prev = 'x'; continue;
    }
    if (c === '`') { i++; out += ' '; template(); continue; }
    if (c === '/' && regexCanStart()) {
      let j = i + 1, inClass = false;
      while (j < src.length && src[j] !== '\n' && (inClass || src[j] !== '/')) {
        if (src[j] === '\\') j++;
        else if (src[j] === '[') inClass = true;
        else if (src[j] === ']') inClass = false;
        j++;
      }
      j++;
      while (/[a-z]/i.test(src[j] || '')) j++;
      out += blank(src.slice(i, j)); i = j; prev = 'x'; continue;
    }
    if (c === '{') depth++;
    if (c === '}') {
      depth--;
      if (stack.length && stack[stack.length - 1] === depth) { stack.pop(); out += ' '; i++; template(); continue; }
    }
    out += c; i++;
    if (!/\s/.test(c)) prev = c;
  }
  return out;
}

test('the lexer keeps code and drops what only looks like it', () => {
  const found = (src) => NEWER.filter(([re]) => re.test(codeOnly(src))).map(([, what]) => what);
  // what CodeRabbit pointed out on #655: a `/*` in a string, and code inside `${…}`
  expect(found("const mime = 'image/*';\nlet a; a ||= 1;")).toHaveLength(1);
  expect(found('const label = `${a ||= Date.now()}`;')).toHaveLength(1);
  expect(found('const t = `${`${b ??= 2}`}`;')).toHaveLength(1);
  // and what must not count
  expect(found("const s = 'a ||= b'; // x ??= y\n/* c &&= d */ const r = /||=/; const q = `x ||= ${1}`;")).toEqual([]);
  expect(found('const n = a / b; const m = c /d/ e; f(x ||= 1)')).toHaveLength(1);
  expect(found('class A { #run() {} }')).toHaveLength(1);
  expect(found('class A { static { } }')).toHaveLength(1);
  expect(found("const x = obj['#run'](); const y = '#run(';")).toEqual([]);
});

test('the panel uses no syntax newer than Chromium 80', () => {
  const found = [];
  for (const file of files(WWW)) {
    const raw = fs.readFileSync(file, 'utf8').split('\n');
    codeOnly(fs.readFileSync(file, 'utf8')).split('\n').forEach((line, i) => {
      for (const [re, what] of NEWER) if (re.test(line)) found.push(`${path.relative(WWW, file)}:${i + 1}: ${what}: ${raw[i].trim()}`);
    });
  }
  expect(found).toEqual([]);
});
