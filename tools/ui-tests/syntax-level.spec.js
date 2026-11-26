// The panel runs on old Android System WebViews (index.html lets Chromium 80 in; compat.js fills in the newer DOM
// APIs). Newer syntax can't be filled in: one use stops the whole module from parsing, and the panel stays black with
// "Unexpected token" (#654, a `||=` on Chromium 81). This keeps syntax newer than Chromium 80 out of the panel's code.
const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

const WWW = path.join(__dirname, '../../android/app/src/main/assets/www');
const files = (dir) => fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
  e.isDirectory() ? files(path.join(dir, e.name)) : e.name.endsWith('.js') ? [path.join(dir, e.name)] : []);

// syntax and the first Chromium that parses it
const NEWER = [
  [/(\|\||&&|\?\?)=/, 'logical assignment (||=, &&=, ??=), Chromium 85'],
  [/^\s*(static\s+)?(async\s+)?#[A-Za-z_$][\w$]*\s*\(/, 'private method, Chromium 84'],
  [/\bstatic\s*\{/, 'class static block, Chromium 94'],
  [/^\s*await\b/, 'top-level await, Chromium 89'],
];

/** The code of a line without its strings and its // comment (a rough cut, enough for these patterns). */
const code = (line) => line.replace(/\./g, '').replace(/'[^']*'|"[^"]*"|`[^`]*`/g, '""').replace(/\/\/.*$/, '');

test('the panel uses no syntax newer than Chromium 80', () => {
  const found = [];
  for (const file of files(WWW)) {
    let inComment = false, depth = 0;
    fs.readFileSync(file, 'utf8').split('\n').forEach((raw, i) => {
      let line = raw;
      if (inComment) { const end = line.indexOf('*/'); if (end < 0) return; line = line.slice(end + 2); inComment = false; }
      line = line.replace(/\/\*.*?\*\//g, '');
      const open = line.indexOf('/*');
      if (open >= 0) { line = line.slice(0, open); inComment = true; }
      const c = code(line);
      for (const [re, what] of NEWER) {
        // top-level await only at the module's top level
        if (what.startsWith('top-level') && depth > 0) continue;
        if (re.test(c)) found.push(`${path.relative(WWW, file)}:${i + 1}: ${what}: ${raw.trim()}`);
      }
      for (const ch of c) depth += ch === '{' ? 1 : ch === '}' ? -1 : 0;
    });
  }
  expect(found).toEqual([]);
});
