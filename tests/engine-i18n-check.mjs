// בדיקת התרגום של הודעות המנוע: מריצים "node tests/engine-i18n-check.mjs" מתיקיית הפרויקט.
//
// 1. מחרוזת בעברית שאינה בתוך L.T(...) — תוצג בעברית גם בממשק האנגלי.
//    מחרוזת משולבת ($"...{x}...") בעברית — צריך להפוך ל-L.T("...{0}...", x).
// 2. מחרוזת בתוך L.T שאין לה תרגום ב-English.cs.
//
// פטורים: שורה עם "// לא לתרגום", קטע בין "לא לתרגום: מכאן" ל"לא לתרגום: עד כאן",
// והקבצים שבהם תוויות קצרות שהממשק מתרגם בעצמו (ראו EXEMPT).
// --json קובץ — שמירת הטקסטים שחסר להם תרגום, לפי קובץ.

import fs from 'fs';
import path from 'path';
import { pathToFileURL } from 'url';

const SRC = 'src';
const HEB = /[֐-׿]/;
const EXEMPT = new Set([
  'RAF.App/Display.cs',                        // תוויות — הממשק מתרגם (lang-en.js)
  'RAF.Core/Signatures/FileSignatures.cs',     // שמות סוגי קבצים — הממשק מתרגם; גם שמות תיקיות בסריקה מתקדמת
]);

function files(dir) {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (!['bin', 'obj'].includes(e.name)) out.push(...files(p)); }
    else if (e.name.endsWith('.cs')) out.push(p);
  }
  return out;
}

/// המחרוזות בקובץ C#, עם המיקום והסוג: רגילה, משולבת ($), מילולית (@), גולמית (""").
export function strings(src) {
  const out = [];
  let i = 0, line = 1;
  const nl = (a, b) => { for (let k = a; k < b; k++) if (src[k] === '\n') line++; };
  while (i < src.length) {
    const c = src[i];
    if (c === '\n') { line++; i++; continue; }
    if (c === '/' && src[i + 1] === '/') { while (i < src.length && src[i] !== '\n') i++; continue; }
    if (c === '/' && src[i + 1] === '*') { const e = src.indexOf('*/', i + 2); nl(i, e); i = e + 2; continue; }
    if (c === "'") { let j = i + 1; while (src[j] !== "'") { if (src[j] === '\\') j++; j++; } i = j + 1; continue; }

    // קידומות: $, @, $@, @$ — ואחריהן " או """
    let j = i, interp = false, verbatim = false;
    while (src[j] === '$' || src[j] === '@') { if (src[j] === '$') interp = true; else verbatim = true; j++; }
    if (src[j] !== '"' || (j > i && /[\w]/.test(src[i - 1] || ''))) {
      if (j > i) { i = j; continue; }
      if (c !== '"') { i++; continue; }
    }
    const start = i, startLine = line;
    if (src.startsWith('"""', j)) {                                       // גולמית
      const e = src.indexOf('"""', j + 3);
      const text = src.slice(j + 3, e);
      nl(j, e + 3); i = e + 3;
      out.push({ kind: interp ? 'interp' : 'str', text, line: startLine, at: start, end: i });
      continue;
    }
    j++;
    let text = '', depth = 0;
    while (j < src.length) {
      const ch = src[j];
      if (depth === 0) {
        if (verbatim && ch === '"' && src[j + 1] === '"') { text += '"'; j += 2; continue; }
        if (!verbatim && ch === '\\') { text += src[j] + src[j + 1]; j += 2; continue; }
        if (ch === '"') break;
        if (interp && ch === '{') { if (src[j + 1] === '{') { text += '{'; j += 2; continue; } depth = 1; text += '{'; j++; continue; }
        if (ch === '\n') line++;
        text += ch; j++;
      } else {
        // בתוך {...} של מחרוזת משולבת — עד הסוגר המתאים (כולל מחרוזות פנימיות)
        if (ch === '"') { j++; while (src[j] !== '"') { if (src[j] === '\\') j++; j++; } j++; continue; }
        if (ch === '{') depth++;
        if (ch === '}') depth--;
        text += ch; j++;
      }
    }
    i = j + 1;
    out.push({ kind: interp ? 'interp' : 'str', text, line: startLine, at: start, end: i });
  }
  return out;
}

/// האם המחרוזת היא ארגומנט של L.T(...) — הסוגר הפתוח הקרוב שלפניה שייך לה.
export function insideLT(src, at) {
  let depth = 0;
  for (let j = at - 1; j >= Math.max(0, at - 2000); j--) {
    const c = src[j];
    if (c === ')' || c === ']' || c === '}') depth++;
    else if (c === '(' || c === '[' || c === '{') {
      if (depth === 0) return c === '(' && /\bL\.T\s*$/.test(src.slice(Math.max(0, j - 8), j));
      depth--;
    }
  }
  return false;
}

/// שרשראות: מחרוזות צמודות שמחוברות ב-+ ("חלק א " + "חלק ב") הן טקסט אחד.
export function chains(src, list) {
  const out = [];
  for (const s of list) {
    const last = out[out.length - 1];
    if (last && /^\s*\+\s*$/.test(src.slice(last[last.length - 1].end, s.at))) last.push(s);
    else out.push([s]);
  }
  return out;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) main();

function main() {
// המילון
const dictSrc = fs.readFileSync(path.join(SRC, 'RAF.Core', 'Text', 'English.cs'), 'utf8');
const dict = new Set();
for (const m of dictSrc.matchAll(/\[\s*"((?:[^"\\]|\\.)*)"\s*\]\s*=/g)) dict.add(m[1]);   // השוואה בצורה שבקוד (עם \" ו-\n)

let bare = 0;
const missing = new Map();
for (const f of files(SRC)) {
  const rel = path.relative(SRC, f).replace(/\\/g, '/');
  if (EXEMPT.has(rel) || rel.endsWith('Text/English.cs')) continue;
  const src = fs.readFileSync(f, 'utf8');
  const lines = src.split('\n');
  const exempt = new Set();
  for (let n = 0, on = false; n < lines.length; n++) {
    if (/לא לתרגום: מכאן/.test(lines[n])) on = true;
    if (on) exempt.add(n + 1);
    if (/לא לתרגום: עד כאן/.test(lines[n])) on = false;
  }
  for (const chain of chains(src, strings(src))) {
    const s = chain[0];
    const text = chain.map((c) => c.text).join('');
    if (!HEB.test(text) || chain.some((c) => exempt.has(c.line) || /לא לתרגום/.test(lines[c.line - 1] || ''))) continue;
    if (chain.some((c) => c.kind === 'interp')) {
      bare++;
      console.log(`מחרוזת משולבת בעברית: ${rel}:${s.line}  «${text.replace(/\s+/g, ' ').slice(0, 80)}»`);
      continue;
    }
    if (!insideLT(src, s.at)) {
      bare++;
      console.log(`מחרוזת בלי L.T: ${rel}:${s.line}  «${text.replace(/\s+/g, ' ').slice(0, 80)}»`);
      continue;
    }
    if (!dict.has(text)) missing.set(text, `${rel}:${s.line}`);
  }
}

for (const [text, where] of missing) console.log(`אין תרגום: ${where}  «${text.slice(0, 90)}»`);
console.log(`\nסיכום: ${bare} מחרוזות בעברית בלי L.T, ${missing.size} בלי תרגום.`);

const jsonAt = process.argv.indexOf('--json');
if (jsonAt > 0) {
  const byFile = {};
  for (const [text, where] of missing) (byFile[where.split(':')[0]] ??= []).push(text);
  fs.writeFileSync(process.argv[jsonAt + 1], JSON.stringify(byFile, null, 1), 'utf8');
}
process.exitCode = bare + missing.size > 0 ? 1 : 0;
}
