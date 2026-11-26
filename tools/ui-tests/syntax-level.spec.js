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
  [/(\|\||&&|\?\?)=/g, 'logical assignment (||=, &&=, ??=), Chromium 85'],
  [/(^|[;{}])\s*(static\s+)?(async\s+)?(get\s+|set\s+)?#[A-Za-z_$][\w$]*\s*\(/gm, 'private method, Chromium 84'],
  [/\bstatic\s*\{/g, 'class static block, Chromium 94'],
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
  const regexCanStart = () => !prev || /[(,=:[!&|?{};+\-*%<>~^]/.test(prev) || /\b(return|typeof|case|do|else|in|void|yield|await)$/.test(out.trimEnd());
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

/**
 * Each use of newer syntax in a source: its line (1-based) and what it is. Matched over the whole source, so a
 * construct split over lines (`static` and `{` on two lines) is found too.
 */
function newerSyntax(src) {
  const code = codeOnly(src), found = [];
  for (const [re, what] of NEWER) {
    for (const m of code.matchAll(re)) {
      // the line of the construct itself, past the delimiter and whitespace the pattern took before it
      const at = m.index + Math.max(0, m[0].search(/[^\s;{}]/));
      // `this.` and `#run()` on two lines is a call of a private field (Chromium 74), not a method declaration
      if (what.startsWith('private') && /\.\s*$/.test(code.slice(0, at))) continue;
      found.push({ line: code.slice(0, at).split('\n').length, what });
    }
  }
  return found;
}

test('the lexer keeps code and drops what only looks like it', () => {
  const found = (src) => newerSyntax(src).map((f) => f.what);
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
  // more from CodeRabbit on #655: an indented private method after a field, `of` used as a name, and a static block
  // whose brace is on the next line
  expect(found('class A {\n  value = 1\n  #run() {}\n}')).toHaveLength(1);
  expect(found('let a; const of = 4; of / (a ||= 1) / 2;')).toHaveLength(1);
  expect(found('class A {\n  static\n  {}\n}')).toHaveLength(1);
  expect(found('class A {\n  #run = () => {};\n  call() {\n    this.\n      #run();\n  }\n}')).toEqual([]);
  expect(newerSyntax('const a = 1;\nlet b;\nb ||= 2;')).toEqual([{ line: 3, what: NEWER[0][1] }]);
});

test('the panel uses no syntax newer than Chromium 80', () => {
  const found = [];
  for (const file of files(WWW)) {
    const src = fs.readFileSync(file, 'utf8'), raw = src.split('\n');
    for (const { line, what } of newerSyntax(src)) found.push(`${path.relative(WWW, file)}:${line}: ${what}: ${raw[line - 1].trim()}`);
  }
  expect(found).toEqual([]);
});
