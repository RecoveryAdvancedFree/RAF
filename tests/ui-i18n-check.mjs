// בדיקת התרגום של הממשק: מריצים "node tests/ui-i18n-check.mjs" מתיקיית הפרויקט.
//
// 1. עברית שעוד לא עוברת דרך t() — טקסט בתוך תבנית (`...`) שאינו בתוך ${...},
//    או מחרוזת רגילה בעברית שאינה ארגומנט של t / plural / מפתח במערך תוויות.
// 2. מחרוזות בעברית שאין להן תרגום במילון (lang-en.js).
//
// הבדיקה לא מבינה JavaScript עד הסוף — היא קוראת את הקוד כטקסט, ומדלגת על הערות.
// מחרוזת שמשמשת רק להשוואה (לא להצגה) מסומנת בהערה "// לא לתרגום" באותה שורה.

import fs from 'fs';
import path from 'path';
import vm from 'vm';

const WEB = path.join('src', 'RAF.App', 'Web');
const HEB = /[֐-׿]/;

const ctx = {};
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(path.join(WEB, 'lang-en.js'), 'utf8') + '\nthis.EN = EN;', ctx);
const EN = ctx.EN;

/// פירוק הקוד: מחרוזות ('…', "…", `…`) עם מיקומן, בלי הערות. בתבנית — גם הקטעים שמחוץ ל-${}.
function tokens(src) {
  const out = [];
  let i = 0, line = 1;
  const stack = [];                                     // עומק ${ בתוך תבניות
  function template() {
    // בתוך `...`: אוספים טקסט עד ` או ${
    let text = '', startLine = line;
    while (i < src.length) {
      const c = src[i];
      if (c === '\\') { text += src[i] + src[i + 1]; i += 2; continue; }
      if (c === '\n') line++;
      if (c === '`') { i++; out.push({ kind: 'tpl', text, line: startLine }); return false; }
      if (c === '$' && src[i + 1] === '{') { i += 2; out.push({ kind: 'tpl', text, line: startLine }); return true; }
      text += c; i++;
    }
    return false;
  }
  while (i < src.length) {
    const c = src[i];
    if (c === '\n') { line++; i++; continue; }
    if (c === '/' && src[i + 1] === '/') { while (i < src.length && src[i] !== '\n') i++; continue; }
    if (c === '/' && src[i + 1] === '*') { const e = src.indexOf('*/', i + 2); line += src.slice(i, e).split('\n').length - 1; i = e + 2; continue; }
    // ביטוי חיפוש (/…/): מדלגים עליו — יש בו גרשיים שאינם מחרוזות. מזהים אותו לפי מה שלפניו.
    if (c === '/' && /[(,=:[!&|?{};+]\s*$|return\s*$/.test(src.slice(Math.max(0, i - 12), i))) {
      let j = i + 1, inClass = false;
      while (j < src.length && (src[j] !== '/' || inClass)) {
        if (src[j] === '\\') j++;
        else if (src[j] === '[') inClass = true;
        else if (src[j] === ']') inClass = false;
        j++;
      }
      i = j + 1; continue;
    }
    if (c === "'" || c === '"') {
      const q = c; let j = i + 1, text = '';
      while (j < src.length && src[j] !== q) { if (src[j] === '\\') { text += src[j + 1] === "'" ? "'" : src[j + 1] === '"' ? '"' : src[j] + src[j + 1]; j += 2; continue; } text += src[j]; j++; }
      out.push({ kind: 'str', text, line, at: i, end: j + 1 });
      i = j + 1; continue;
    }
    if (c === '`') { i++; if (template()) stack.push(0); continue; }
    if (c === '{' && stack.length) { stack[stack.length - 1]++; i++; continue; }
    if (c === '}' && stack.length) {
      if (stack[stack.length - 1] === 0) { stack.pop(); i++; if (template()) stack.push(0); continue; }
      stack[stack.length - 1]--; i++; continue;
    }
    i++;
  }
  return out;
}

/// מחרוזות שמחוברות ב-+ נחשבות מחרוזת אחת — כך t('א' + 'ב') נבדק כ-'אב'.
function joined(src, toks) {
  const res = [];
  for (let k = 0; k < toks.length; k++) {
    const tk = toks[k];
    if (tk.kind !== 'str') { res.push(tk); continue; }
    let text = tk.text, end = tk.end;
    while (k + 1 < toks.length && toks[k + 1].kind === 'str' && /^\s*\+\s*$/.test(src.slice(end, toks[k + 1].at))) {
      k++; text += toks[k].text; end = toks[k].end;
    }
    res.push({ ...tk, text, end });
  }
  return res;
}

/// האם המחרוזת בתוך קריאה ל-t() או ל-plural() — הסוגר הפתוח הקרוב ביותר לפניה שייך לאחת מהן.
function insideT(src, at) {
  let depth = 0;
  for (let j = at - 1; j >= Math.max(0, at - 1500); j--) {
    const c = src[j];
    if (c === ')' || c === ']' || c === '}') depth++;
    else if (c === '(' || c === '[' || c === '{') {
      if (depth === 0) return c === '(' && /(?:^|[^\w.])(t|plural|chip|option)\s*$/.test(src.slice(Math.max(0, j - 10), j));
      depth--;
    }
  }
  return false;
}

const files = fs.readdirSync(WEB).filter((f) => f.endsWith('.js') && f !== 'lang-en.js');
let bare = 0;
let untouched = 0;
const missing = new Map();

for (const f of files) {
  const src = fs.readFileSync(path.join(WEB, f), 'utf8');
  const lines = src.split('\n');
  // קטע שלם שפטור מבדיקה: בין "לא לתרגום: מכאן" ל"לא לתרגום: עד כאן" (למשל טקסט שיש לו גרסה נפרדת באנגלית).
  const exempt = new Set(), atDisplay = new Set();
  for (let n = 0, on = false, shown = false; n < lines.length; n++) {
    if (/לא לתרגום: מכאן/.test(lines[n])) on = true;
    if (/מתורגם בהצגה: מכאן/.test(lines[n])) shown = true;
    if (on) exempt.add(n + 1);
    if (shown) atDisplay.add(n + 1);
    if (/לא לתרגום: עד כאן/.test(lines[n])) on = false;
    if (/מתורגם בהצגה: עד כאן/.test(lines[n])) shown = false;
  }
  for (const tk of joined(src, tokens(src))) {
    if (!HEB.test(tk.text)) continue;
    if (exempt.has(tk.line) || /לא לתרגום/.test(lines[tk.line - 1] || '')) continue;
    if (tk.kind === 'tpl') {
      untouched++;
      console.log(`עברית בתוך תבנית, בלי t(): ${f}:${tk.line}  «${tk.text.trim().replace(/\s+/g, ' ').slice(0, 70)}»`);
      continue;
    }
    // מחרוזת מחוץ ל-t(): תוצג בעברית גם בממשק האנגלי. מותר רק כשהיא מתורגמת במקום אחר —
    // טקסט במערך תוויות, שמתורגם כשמציגים אותו — ואז השורה מסומנת "מתורגם בהצגה".
    if (!insideT(src, tk.at) && !atDisplay.has(tk.line) && !/מתורגם בהצגה/.test(lines[tk.line - 1] || '')) {
      bare++;
      console.log(`מחרוזת בלי t(): ${f}:${tk.line}  «${tk.text.replace(/\s+/g, ' ').slice(0, 70)}»`);
    }
    if (!(tk.text in EN)) missing.set(tk.text, `${f}:${tk.line}`);
  }
}

for (const [text, where] of missing) console.log(`אין תרגום: ${where}  «${text.replace(/\s+/g, ' ').slice(0, 90)}»`);

// --json קובץ: הטקסטים המלאים שחסר להם תרגום, לפי הקובץ שבו הם מופיעים — להשלמת המילון.
const jsonAt = process.argv.indexOf('--json');
if (jsonAt > 0) {
  const byFile = {};
  for (const [text, where] of missing) (byFile[where.split(':')[0]] ??= []).push(text);
  fs.writeFileSync(process.argv[jsonAt + 1], JSON.stringify(byFile, null, 1), 'utf8');
}
console.log(`\nסיכום: ${untouched} קטעי עברית בתבניות בלי t(), ${bare} מחרוזות בלי t(), ${missing.size} מחרוזות בלי תרגום.`);
process.exitCode = untouched + bare + missing.size > 0 ? 1 : 0;
