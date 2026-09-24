/* ==========================================================================
   תוצאות
   עץ התיקיות, רשימת הקבצים והגלריה, סינון ותצוגה מקדימה.
   ========================================================================== */

'use strict';

/* =====================================================================
   מסך 3 — תוצאות
   ===================================================================== */

async function renderResults() {
  Steps.set(3);
  const s = State.summary;

  el('content').innerHTML = `
    <div class="results">
      <div class="results-bar">
        <button class="btn" id="btn-home">${Icon.back}<span>מחיצות</span></button>

        <div class="result-stats">
          <span><b>${(s.deleted || 0).toLocaleString('he-IL')}</b> ${plural(s.deleted, 'מחוק', 'מחוקים')}</span>
          <span class="sep">·</span>
          <span><b class="ok-text">${(s.recoverable || 0).toLocaleString('he-IL')}</b> ${plural(s.recoverable, 'ניתן', 'ניתנים')} לשחזור</span>
          ${s.emptied > 0 ? `<span class="sep">·</span>
            <span><b class="danger-text">${s.emptied.toLocaleString('he-IL')}</b> ריקים</span>` : ''}
          ${s.evidence > 0 ? `<span class="sep">·</span>
            <span title="קבצים שאותרו ביומני מערכת הקבצים: שמם ידוע, תוכנם אינו ניתן לאיתור">
              <b>${s.evidence.toLocaleString('he-IL')}</b> עדות בלבד</span>` : ''}
          <span class="sep">·</span>
          <span>${esc(s.mode)} · ${formatDuration(s.duration)}</span>
          ${s.cancelled ? '<span class="chip warn">נעצרה</span>' : ''}
        </div>

        ${s.evidence > 0 ? `
          <label class="toggle" title="רשומות שאותרו ביומני מערכת הקבצים: שמן ידוע, אך תוכנן אינו ניתן לאיתור ולא ניתן לשחזר אותן">
            <input type="checkbox" id="chk-evidence">
            <span>הצג ${s.evidence.toLocaleString('he-IL')} רשומות יומן</span>
          </label>` : ''}

        <button class="btn icon-only" id="btn-save-scan" title="שמירת הסריקה לקובץ — כדי לחזור אליה בלי לסרוק שוב"
                aria-label="שמירת הסריקה">${Icon.disk}</button>

        <div class="search-box">
          ${Icon.search}
          <input type="text" id="search-input" placeholder="חיפוש בשם קובץ…" autocomplete="off" aria-label="חיפוש בשם קובץ">
        </div>
      </div>

      ${s.resumePercent != null ? `<div class="resume-strip">
        ${notice('warn', Icon.pause, `הסריקה נעצרה אחרי ${s.resumePercent.toFixed(1)}% מהמחיצה`,
          'מוצג מה שנמצא עד כה. אפשר להמשיך את הסריקה מאותה נקודה.')}
        <button class="btn btn-primary" id="btn-resume-scan">${Icon.play}<span>המשך הסריקה</span></button>
      </div>` : ''}

      ${(s.warnings || []).length ? notesHtml(s.warnings.map(esc)) : ''}

      <div class="results-grid">
        <aside class="tree" id="tree" role="tree" aria-label="תיקיות — חיצים למעבר, אנטר לפתיחה, רווח לסימון"></aside>
        <div class="filelist-wrap" id="filelist-wrap">
          <div class="list-tools">
            <label class="chk-all" title="סימון כל הקבצים ברשימה, גם אלה שלא נגללו"><input type="checkbox" id="chk-all"><span>הכל</span></label>
            <div class="cat-chips" id="cat-chips"></div>
            <div class="view-switch" role="group" aria-label="אופן התצוגה">
              <button class="icon-btn" data-view="list" title="רשימה" aria-label="רשימה">${Icon.list}</button>
              <button class="icon-btn" data-view="grid" title="גלריה" aria-label="גלריה">${Icon.grid}</button>
            </div>
            <div class="list-filters">
              <select class="filter-select" id="grid-sort" aria-label="מיון">
                <option value="date:1">מהחדש לישן, לפי חודשים</option>
                <option value="date:0">מהישן לחדש, לפי חודשים</option>
                <option value="name:0">לפי שם</option>
                <option value="size:1">מהגדול לקטן</option>
                <option value="quality:0">לפי איכות</option>
              </select>
              <select class="filter-select" id="flt-date" aria-label="סינון לפי תאריך">
                ${DATE_FILTERS.map((f) => `<option value="${f.id}">${f.label}</option>`).join('')}
              </select>
              <span class="date-range" id="flt-range" hidden>
                <input type="date" id="flt-from" aria-label="מתאריך"><span>עד</span><input type="date" id="flt-to" aria-label="עד תאריך">
              </span>
              <select class="filter-select" id="flt-size" aria-label="סינון לפי גודל">
                ${SIZE_FILTERS.map((f) => `<option value="${f.min}">${f.label}</option>`).join('')}
              </select>
              <label class="toggle small"><input type="checkbox" id="chk-recoverable"><span>רק ניתנים לשחזור</span></label>
              <label class="toggle small" title="קבצים עם תוכן זהה בדיוק מוצגים פעם אחת — העותק הטוב ביותר. העותקים שמוסתרים גם לא ישוחזרו.">
                <input type="checkbox" id="chk-dups"><span>הסתר כפילויות</span></label>
            </div>
          </div>
          <div class="filelist-head">
            <span></span>
            ${SORT_COLUMNS.map((c) => `<button class="col-sort col-${c.id}" data-sort="${c.id}">${c.label}</button>`).join('')}
          </div>
          <div class="list-banner" id="list-banner" hidden></div>
          <div class="filelist" id="filelist" role="listbox" tabindex="0" aria-multiselectable="true"
               aria-label="הקבצים — חיצים למעבר, רווח לסימון לשחזור, אנטר לתצוגה מקדימה"></div>
        </div>
        <aside class="preview" id="preview">
          <div class="preview-empty">${Icon.image}<p>בחרו קובץ לתצוגה מקדימה</p></div>
        </aside>
      </div>

      <div class="recover-bar">
        <div class="recover-info" id="recover-info">לא נבחרו קבצים</div>
        <button class="btn btn-primary" id="btn-recover" disabled>${Icon.save}<span>שחזור לכונן אחר</span></button>
      </div>
    </div>`;

  el('btn-home').onclick = loadDisks;
  el('btn-recover').onclick = openRecoverPanel;
  el('btn-save-scan').onclick = saveScanAs;
  if (el('btn-resume-scan')) el('btn-resume-scan').onclick = () => resumeScan(null, s.partition);
  FileList.attach();
  updateRecoverBar();

  const evidenceToggle = el('chk-evidence');
  if (evidenceToggle) {
    evidenceToggle.onchange = async (e) => {
      State.showEvidence = e.target.checked;
      await buildTree(State.currentPath);
    };
  }

  let searchTimer;
  el('search-input').oninput = (e) => {
    clearTimeout(searchTimer);
    const query = e.target.value.trim();
    searchTimer = setTimeout(() => (query ? runSearch(query) : openFolder(State.currentPath)), 250);
  };

  State.currentPath = '';
  await buildTree();
  setStatus(`${countFiles(s.total || 0)} · ${esc(s.partition)}`);
}

/* ---------- עץ התיקיות ---------- */

async function buildTree(restorePath) {
  const root = document.createElement('div');
  root.className = 'tree-root';
  el('tree').innerHTML = '';
  el('tree').appendChild(root);

  root.appendChild(await makeTreeNode('', 'כל הקבצים', 0));

  // פתיחת השורש מיד, אחרת מסך התוצאות נראה ריק עד שהמשתמש לוחץ.
  const first = root.querySelector('.tree-item');
  if (first) await first.expand();

  // בנייה מחדש של העץ אינה אמורה להחזיר את המשתמש לשורש.
  if (restorePath) {
    await expandToPath(restorePath);
  } else {
    // תיקייה ריקה עם תיקיות משנה — כמו בסריקה מתקדמת (הקבצים לפי סוג) או במחיצת
    // EFI (EFI\Microsoft\Boot): יורדים לתיקייה הראשונה עד שיש מה להציג, במקום מסך ריק.
    let item = first;
    for (let depth = 0; item && FileList.total === 0 && depth < 8; depth++) {
      item = item.parentElement.querySelector(':scope > .tree-children > .tree-node > .tree-item');
      if (item) await item.expand();
    }
  }
}

/// פתיחת העץ עד לנתיב נתון, מקטע אחר מקטע.
async function expandToPath(path) {
  const segments = path.split('\\').filter(Boolean);
  let current = '';

  for (const segment of segments) {
    current = current ? current + '\\' + segment : segment;

    // השוואה על dataset במקום בורר CSS: נתיבי NTFS מכילים לוכסנים
    // שדורשים בריחה מורכבת בבורר.
    const node = [...el('tree').querySelectorAll('.tree-item')]
      .find((n) => n.dataset.path === current);

    if (!node) break;
    await node.expand();
  }
}

/// מצב תיבות הסימון בעץ נקבע במנוע, שיודע כמה קבצים מסומנים תחת כל ענף.
const Tree = {
  async refreshStates() {
    const items = [...document.querySelectorAll('#tree .tree-item')];
    if (items.length === 0) return;

    const states = await Bridge.call('scan.folderStates', { paths: items.map((i) => i.dataset.path) });
    for (const item of items) {
      const box = item.querySelector('.tree-chk');
      const state = states[item.dataset.path];
      // ‎-1: אין בענף קבצים ניתנים לשחזור — אין מה לסמן.
      box.style.visibility = state === -1 ? 'hidden' : '';
      box.checked = state === 2;
      box.indeterminate = state === 1;
      if (state === -1) item.removeAttribute('aria-checked');
      else item.setAttribute('aria-checked', state === 2 ? 'true' : state === 1 ? 'mixed' : 'false');
    }
  },
};

async function makeTreeNode(path, label, depth) {
  const node = document.createElement('div');
  node.className = 'tree-node';

  const item = document.createElement('div');
  item.className = 'tree-item';
  item.style.paddingInlineStart = (8 + depth * 14) + 'px';
  item.dataset.path = path;
  item.setAttribute('role', 'treeitem');
  item.setAttribute('aria-level', String(depth + 1));
  item.setAttribute('aria-expanded', 'false');
  item.setAttribute('aria-label', label);
  item.tabIndex = depth === 0 ? 0 : -1;
  item.innerHTML = `<span class="tree-caret">${Icon.chevron}</span>
                    <input type="checkbox" class="tree-chk" tabindex="-1" aria-hidden="true" title="סימון התיקייה וכל מה שבתוכה">
                    <span class="tree-icon">${Icon.folder}</span>
                    <span class="tree-label">${esc(label)}</span>`;

  const children = document.createElement('div');
  children.className = 'tree-children';
  children.setAttribute('role', 'group');
  children.hidden = true;

  let loaded = false;

  /// טעינת תיקיות המשנה פעם אחת, ופתיחת הענף.
  item.expand = async () => {
    document.querySelectorAll('.tree-item').forEach((n) => {
      n.classList.remove('active');
      n.setAttribute('aria-selected', 'false');
      n.tabIndex = -1;
    });
    item.classList.add('active');
    item.setAttribute('aria-selected', 'true');
    item.tabIndex = 0;

    if (!loaded) {
      loaded = true;
      const data = await Bridge.call('scan.children', { path, evidence: State.showEvidence });
      for (const folder of data.folders) {
        children.appendChild(await makeTreeNode(folder.path, folder.name, depth + 1));
      }
      if (data.folders.length === 0) { item.classList.add('leaf'); item.removeAttribute('aria-expanded'); }
      Tree.refreshStates();
    }

    children.hidden = false;
    item.classList.add('open');
    if (!item.classList.contains('leaf')) item.setAttribute('aria-expanded', 'true');

    el('search-input').value = '';
    await openFolder(path);
  };

  item.onclick = async (e) => {
    // תיבת הסימון מסמנת את התיקייה כולה, כולל תיקיות משנה, בלי לפתוח אותה.
    // אחרי הלחיצה הדפדפן כבר הפך את המצב: מסומנת חלקית או לא מסומנת ← מסומנת.
    if (e.target.classList.contains('tree-chk')) {
      e.stopPropagation();
      await FileList.select({ folder: path, on: e.target.checked }, true);
      return;
    }

    // לחיצה על החץ מקפלת ומרחיבה בלבד; לחיצה על השם פותחת גם את התיקייה.
    if (e.target.closest('.tree-caret') && !children.hidden) {
      item.collapse();
      return;
    }

    await item.expand();
  };

  item.collapse = () => {
    children.hidden = true;
    item.classList.remove('open');
    if (!item.classList.contains('leaf')) item.setAttribute('aria-expanded', 'false');
  };
  item.isOpen = () => !children.hidden;

  node.appendChild(item);
  node.appendChild(children);
  return node;
}

/// מקלדת בעץ התיקיות: חיצים למעלה ולמטה בין התיקיות הגלויות; בכיוון הקריאה (מימין
/// לשמאל) — החץ השמאלי פותח ויורד פנימה, והימני סוגר ועולה להורה; אנטר פותח את
/// התיקייה ברשימה; רווח מסמן אותה לשחזור.
function treeKey(e) {
  const item = e.target.closest('.tree-item');
  if (!item) return;
  const visible = [...el('tree').querySelectorAll('.tree-item')].filter((n) => n.getClientRects().length > 0);
  const at = visible.indexOf(item);
  const go = (n) => { if (!n) return; visible.forEach((v) => (v.tabIndex = -1)); n.tabIndex = 0; n.focus(); };
  const parentItem = () => item.parentElement.parentElement.closest('.tree-node')?.querySelector(':scope > .tree-item');

  switch (e.key) {
    case 'ArrowDown': go(visible[at + 1]); break;
    case 'ArrowUp': go(visible[at - 1]); break;
    case 'Home': go(visible[0]); break;
    case 'End': go(visible[visible.length - 1]); break;
    case 'ArrowLeft':
      if (item.classList.contains('leaf')) break;
      if (!item.isOpen()) item.expand().then(() => item.focus());
      else go(item.parentElement.querySelector(':scope > .tree-children > .tree-node > .tree-item'));
      break;
    case 'ArrowRight':
      if (item.isOpen() && !item.classList.contains('leaf')) item.collapse();
      else go(parentItem());
      break;
    case 'Enter': item.expand().then(() => item.focus()); break;
    case ' ': {
      const box = item.querySelector('.tree-chk');
      if (box.style.visibility !== 'hidden') box.click();
      break;
    }
    default: return;
  }
  e.preventDefault();
  e.stopPropagation();
}

/* ---------- רשימת הקבצים ---------- */

async function openFolder(path) {
  State.currentPath = path;
  await FileList.open({ path, query: null });
}

async function runSearch(query) {
  await FileList.open({ path: '', query });
}

/// עמודות הרשימה שניתן למיין לפיהן. מיון ראשון לפי גודל, תאריך או איכות — מהגדול, החדש והטוב.
const SORT_COLUMNS = [
  { id: 'name', label: 'שם הקובץ', firstDesc: false },
  { id: 'size', label: 'גודל', firstDesc: true },
  { id: 'date', label: 'שונה', firstDesc: true },
  { id: 'quality', label: 'איכות', firstDesc: false },
];

/// סינון לפי תאריך השינוי. הטווח מחושב בכל פתיחה, כך ש"השנה" נשארת נכונה גם אחרי סוף השנה.
const DATE_FILTERS = [
  { id: 'all', label: 'כל התאריכים' },
  { id: 'month', label: '30 הימים האחרונים' },
  { id: 'year', label: 'השנה' },
  { id: 'lastYear', label: 'השנה שעברה' },
  { id: 'custom', label: 'טווח לבחירה…' },
];

/// סינון לפי גודל מינימלי. קבצים זעירים הם בדרך כלל סמלים ותמונות מוקטנות של מערכת ההפעלה.
const SIZE_FILTERS = [
  { min: 0, label: 'כל הגדלים' },
  { min: 10 * 1024, label: 'מעל 10KB' },
  { min: 100 * 1024, label: 'מעל 100KB' },
  { min: 1024 * 1024, label: 'מעל 1MB' },
  { min: 10 * 1024 * 1024, label: 'מעל 10MB' },
];

/// תאריך מקומי בפורמט שהמנוע מקבל (yyyy-MM-dd).
function isoDay(d) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/// הטווח של סינון התאריך: from כולל, to לא כולל.
function dateRange(prefs) {
  const now = new Date();
  const year = now.getFullYear();
  switch (prefs.date) {
    case 'month': return { from: isoDay(new Date(year, now.getMonth(), now.getDate() - 30)), to: null };
    case 'year': return { from: `${year}-01-01`, to: null };
    case 'lastYear': return { from: `${year - 1}-01-01`, to: `${year}-01-01` };
    case 'custom': {
      // "עד" כולל את היום שנבחר, ולכן הגבול הוא היום שאחריו.
      let to = null;
      if (prefs.to) { const d = new Date(prefs.to + 'T00:00'); d.setDate(d.getDate() + 1); to = isoDay(d); }
      return { from: prefs.from || null, to };
    }
    default: return { from: null, to: null };
  }
}

/// שבבי הסינון, בסדר ההצגה. המזהים תואמים ל-FileCategories במנוע.
const CATEGORIES = [
  { id: 'all', label: 'כל הסוגים' },
  { id: 'images', label: 'תמונות' },
  { id: 'documents', label: 'מסמכים' },
  { id: 'video', label: 'וידאו' },
  { id: 'audio', label: 'שמע' },
  { id: 'archives', label: 'ארכיונים' },
  { id: 'other', label: 'אחר' },
];

/// רשימת קבצים וירטואלית, ברשימה או בגלריה: רק מה שעל המסך קיים ב-DOM,
/// והנתונים נמשכים מהמנוע בעמודים לפי הגלילה. כך תיקייה של מאות אלפי
/// קבצים נפתחת מיד — סריקה מתקדמת שמה את כל קבצי ה-JPEG בתיקייה אחת.
///
/// הסינון, המיון והבחירה נעשים במנוע; הממשק שולח את "התצוגה" בכל בקשה.
const FileList = (() => {
  const PAGE = 200;        // קבצים בכל בקשה למנוע
  const OVERSCAN = 6;      // שורות נוספות מעל ומתחת לאזור הנראה

  /// מידות הפריסה. ברשימה כל שורה היא קובץ; בגלריה כל שורה היא שורת אריחים.
  /// הגבהים תואמים ל-.frow ול-.tile ב-views.css.
  const LAYOUT = {
    list: { rowHeight: 40, perRow: () => 1 },
    grid: { rowHeight: 196, perRow: (width) => Math.max(1, Math.floor((width - 12) / 164)) },
  };

  // העדפות תצוגה — נשמרות בין תיקיות ובין סריקות.
  const prefs = {
    category: 'all', recoverableOnly: false, sort: 'name', desc: false, mode: 'list',
    date: 'all', from: '', to: '', minSize: 0,
  };
  try { prefs.mode = localStorage.getItem('raf-view') === 'grid' ? 'grid' : 'list'; } catch (e) {}
  if (prefs.mode === 'grid') { prefs.sort = 'date'; prefs.desc = true; }

  let target = null;       // { path, query }
  let total = 0;
  let pages = new Map();   // מספר עמוד ← מערך קבצים
  let pending = new Set(); // עמודים שבקשתם בדרך
  let byId = new Map();    // קבצים שנטענו, לתצוגה מקדימה ולסימון
  let generation = 0;      // תשובה מתצוגה קודמת נזרקת
  let activeId = null;
  let activeIndex = -1;    // מיקום הקובץ הפעיל ברשימה — לניווט במקלדת
  let groups = [];         // חודשים לכותרות בגלריה, כשממיינים לפי תאריך
  let model = null;        // פריסת הגלריה עם כותרות: שורות בגבהים שונים

  /// התצוגה כפי שהמנוע מכיר אותה (ViewQuery).
  const view = () => ({
    path: target.path, query: target.query, evidence: State.showEvidence,
    category: prefs.category, recoverableOnly: prefs.recoverableOnly,
    sort: prefs.sort, desc: prefs.desc,
    ...dateRange(prefs), minSize: prefs.minSize,
  });

  async function open(next) {
    if (next) target = next;
    if (!target) return;

    const gen = ++generation;
    total = 0;
    pages = new Map();
    pending = new Set();
    byId = new Map();
    activeIndex = -1;
    model = null;

    const list = el('filelist');
    list.scrollTop = 0;

    const first = await Bridge.call('scan.list', { view: view(), offset: 0, count: PAGE });
    if (gen !== generation) return;

    total = first.total;
    groups = first.groups || [];
    store(0, first.files);
    applySelection(first.selection);
    renderChips(first.counts);
    renderHead();

    list.classList.toggle('is-grid', prefs.mode === 'grid');
    el('filelist-wrap').classList.toggle('is-grid', prefs.mode === 'grid');
    list.innerHTML = total === 0
      ? `<div class="empty small"><h3>אין קבצים להצגה</h3><p>${emptyReason()}</p></div>`
      : `<div class="vlist"><div class="vlist-rows"></div></div>`;

    const notes = [];
    if (target.query) notes.push(`נמצאו ${total.toLocaleString('he-IL')} תוצאות עבור "${target.query}".`);
    if (first.undated > 0) {
      notes.push(`${first.undated.toLocaleString('he-IL')} ${plural(first.undated, 'קובץ', 'קבצים')} בלי תאריך ` +
                 `${plural(first.undated, 'אינו מוצג', 'אינם מוצגים')} בסינון לפי תאריך.`);
    }
    if (dupNote) notes.push(dupNote);
    setBanner(notes.join(' '));

    render();

    // מי שעובד ברשימה במקלדת ועבר לתיקייה אחרת — ממשיך מהקובץ הראשון בה, וקורא המסך מקריא אותו.
    if (document.activeElement === list && total > 0) moveTo(0);
  }

  function emptyReason() {
    if (prefs.category !== 'all' || prefs.recoverableOnly || prefs.date !== 'all' || prefs.minSize > 0)
      return 'אין קבצים שמתאימים לסינון.';
    if (target.query) return 'לא נמצאו תוצאות לחיפוש.';

    // בסריקה מתקדמת כל הקבצים בתיקיות לפי סוג, והשורש ריק — "התיקייה ריקה" נשמע
    // כמו "לא נמצא כלום".
    const node = [...document.querySelectorAll('#tree .tree-item')].find((n) => n.dataset.path === target.path);
    return node && !node.classList.contains('leaf')
      ? 'הקבצים נמצאים בתיקיות המשנה — בחרו תיקייה בעץ.'
      : 'התיקייה הזו ריקה.';
  }

  /// רענון הנתונים באותה תצוגה, בלי לאבד את מיקום הגלילה — אחרי סימון תיקייה או "הכל".
  function refresh() {
    pages = new Map();
    pending = new Set();
    generation++;
    render();
  }

  function store(pageIndex, files) {
    pages.set(pageIndex, files);
    for (const f of files) byId.set(f.id, f);
  }

  async function fetchPage(pageIndex) {
    if (pages.has(pageIndex) || pending.has(pageIndex)) return;
    pending.add(pageIndex);
    const gen = generation;
    try {
      const data = await Bridge.call('scan.list', { view: view(), offset: pageIndex * PAGE, count: PAGE });
      if (gen !== generation) return;
      store(pageIndex, data.files);
      render();
    } finally {
      if (gen === generation) pending.delete(pageIndex);
    }
  }

  function fileAt(index) {
    const page = pages.get(Math.floor(index / PAGE));
    return page ? page[index % PAGE] : undefined;
  }

  function render() {
    const list = el('filelist');
    const vlist = list && list.querySelector('.vlist');
    if (!vlist) return;

    const layout = LAYOUT[prefs.mode];
    const perRow = layout.perRow(list.clientWidth);
    if (grouped()) { renderGrouped(list, vlist, perRow); return; }
    const rowCount = Math.ceil(total / perRow);
    vlist.style.height = rowCount * layout.rowHeight + 'px';

    const firstRow = Math.max(0, Math.floor(list.scrollTop / layout.rowHeight) - OVERSCAN);
    const lastRow = Math.min(rowCount, Math.ceil((list.scrollTop + list.clientHeight) / layout.rowHeight) + OVERSCAN);
    const first = firstRow * perRow;
    const last = Math.min(total, lastRow * perRow);

    let html = '';
    for (let i = first; i < last; i++) {
      const f = fileAt(i);
      if (f) {
        html += prefs.mode === 'grid' ? tileHtml(f, i) : rowHtml(f, i);
      } else {
        html += `<div class="${prefs.mode === 'grid' ? 'tile' : 'frow'} placeholder" data-index="${i}"></div>`;
        fetchPage(Math.floor(i / PAGE));
      }
    }

    const rows = vlist.querySelector('.vlist-rows');
    rows.style.transform = `translateY(${firstRow * layout.rowHeight}px)`;
    rows.style.setProperty('--per-row', perRow);
    rows.innerHTML = html;
    announceActive(list);

    if (prefs.mode === 'grid') Thumbs.fill(rows);
  }

  /// הקובץ הפעיל — לקורא המסך, שמקריא אותו כשהמיקוד על הרשימה.
  function announceActive(list) {
    const row = activeIndex >= 0 && document.getElementById('file-' + activeIndex);
    if (row) list.setAttribute('aria-activedescendant', row.id);
    else list.removeAttribute('aria-activedescendant');
  }

  // ------------------------------------------------- גלריה עם כותרות חודש

  const HEAD_PITCH = 44;   // כותרת (32) + רווח (12), תואם ל-.grid-group
  const grouped = () => prefs.mode === 'grid' && prefs.sort === 'date' && groups.length > 0;

  /// שורות הגלריה: כותרת לכל חודש ואחריה שורות האריחים שלו. נבנה מחדש רק כשרוחב השורה משתנה.
  function rowModel(perRow) {
    if (model && model.perRow === perRow) return model;
    const rows = [];
    let top = 0;
    for (const g of groups) {
      rows.push({ top, head: g });
      top += HEAD_PITCH;
      for (let i = 0; i < g.count; i += perRow) {
        rows.push({ top, start: g.start + i, n: Math.min(perRow, g.count - i) });
        top += LAYOUT.grid.rowHeight;
      }
    }
    model = { perRow, rows, height: top };
    return model;
  }

  /// השורה האחרונה שמתחילה מעל גובה נתון (חיפוש בינארי).
  function rowAt(rows, y) {
    let lo = 0, hi = rows.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (rows[mid].top <= y) lo = mid; else hi = mid - 1;
    }
    return lo;
  }

  function renderGrouped(list, vlist, perRow) {
    const { rows, height } = rowModel(perRow);
    vlist.style.height = height + 'px';

    const overscan = 3 * LAYOUT.grid.rowHeight;
    const first = rowAt(rows, Math.max(0, list.scrollTop - overscan));
    const last = rowAt(rows, list.scrollTop + list.clientHeight + overscan);

    // שורה חלקית בסוף חודש אינה בעיה: הכותרת הבאה תופסת שורה שלמה (grid-column: 1 / -1).
    let html = '';
    for (let r = first; r <= last; r++) {
      const row = rows[r];
      if (row.head) {
        html += `<div class="grid-group"><b>${esc(row.head.label)}</b><span>${countFiles(row.head.count)}</span></div>`;
        continue;
      }
      for (let i = row.start; i < row.start + row.n; i++) {
        const f = fileAt(i);
        if (f) html += tileHtml(f, i);
        else { html += `<div class="tile placeholder" data-index="${i}"></div>`; fetchPage(Math.floor(i / PAGE)); }
      }
    }

    const rowsEl = vlist.querySelector('.vlist-rows');
    rowsEl.style.transform = `translateY(${rows[first].top}px)`;
    rowsEl.style.setProperty('--per-row', perRow);
    rowsEl.innerHTML = html;
    announceActive(list);
    Thumbs.fill(rowsEl);
  }

  /// המיקום (למעלה) של קובץ ברשימה — לגלילה אליו מהמקלדת.
  function topOf(index, perRow) {
    if (grouped()) {
      const row = rowModel(perRow).rows.find((r) => !r.head && index >= r.start && index < r.start + r.n);
      return row ? row.top : 0;
    }
    return Math.floor(index / perRow) * LAYOUT[prefs.mode].rowHeight;
  }

  const isActive = (f, index) => (activeIndex >= 0 ? index === activeIndex : f.id === activeId);

  // ---------------------------------------------------------------- מקלדת

  let previewTimer;

  /// מעבר לקובץ אחר ברשימה: סימון, גלילה אליו, ותצוגה מקדימה אחרי עצירה קצרה —
  /// כדי שמעבר מהיר על עשרות קבצים לא יקרא כל אחד מהם מהדיסק.
  function moveTo(index, previewNow) {
    const list = el('filelist');
    if (!list || total === 0) return;
    index = Math.max(0, Math.min(total - 1, index));
    activeIndex = index;
    activeId = fileAt(index)?.id ?? null;

    const perRow = LAYOUT[prefs.mode].perRow(list.clientWidth);
    const height = LAYOUT[prefs.mode].rowHeight;
    const top = topOf(index, perRow);
    if (top < list.scrollTop) list.scrollTop = top;
    else if (top + height > list.scrollTop + list.clientHeight) list.scrollTop = top + height - list.clientHeight;
    render();

    clearTimeout(previewTimer);
    const show = () => { const f = fileAt(activeIndex); if (f) showPreview(f.id); };
    if (previewNow) show(); else previewTimer = setTimeout(show, 350);
  }

  /// מקשי הרשימה. מחזיר true אם המקש טופל.
  function key(e) {
    const list = el('filelist');
    const perRow = LAYOUT[prefs.mode].perRow(list.clientWidth);
    const page = Math.max(1, Math.floor(list.clientHeight / LAYOUT[prefs.mode].rowHeight)) * perRow;
    const at = activeIndex;
    const grid = prefs.mode === 'grid';

    switch (e.key) {
      case 'ArrowDown': moveTo(at < 0 ? 0 : at + perRow); return true;
      case 'ArrowUp': moveTo(at < 0 ? 0 : at - perRow); return true;
      // מימין לשמאל: החץ השמאלי מתקדם לקובץ הבא.
      case 'ArrowLeft': if (!grid) return false; moveTo(at + 1); return true;
      case 'ArrowRight': if (!grid) return false; moveTo(Math.max(0, at - 1)); return true;
      case 'PageDown': moveTo(at + page); return true;
      case 'PageUp': moveTo(at - page); return true;
      case 'Home': moveTo(0); return true;
      case 'End': moveTo(total - 1); return true;
      case 'Enter': if (at >= 0) moveTo(at, true); return at >= 0;
      case ' ': {
        const f = at >= 0 && fileAt(at);
        if (!f || !f.recoverable) return at >= 0;
        f.selected = !f.selected;
        render();
        select({ ids: [f.id], on: f.selected }, false);
        return true;
      }
      default: return false;
    }
  }

  /// קונטרול+A: כמו תיבת "הכל" — מסמן את כל הרשימה, ובלחיצה נוספת מבטל.
  function toggleAll() {
    const all = el('chk-all');
    if (!all || all.disabled) return;
    all.checked = !all.checked;
    select({ all: true, on: all.checked }, true);
  }

  function qualityChip(f) {
    const q = f.quality === 'Excellent' ? 'ok' : f.quality === 'Good' ? '' :
              f.quality === 'Poor' ? 'warn' : 'danger';

    // קובץ שאומת כריק מקבל תווית מפורשת, ולא דירוג איכות שמרמז על אפשרות שחזור.
    // רשומה שמקורה ביומן היא עדות לקיום הקובץ בלבד, ללא מיקום תוכן.
    const label = f.evidence ? 'עדות בלבד'
      : f.emptyContent ? 'ריק — נמחק'
      : f.qualityLabel;
    return `<span class="chip ${q} tiny" title="${esc(f.qualityReason || '')}">${esc(label)}</span>`;
  }

  function checkbox(f) {
    return `<input type="checkbox" tabindex="-1" aria-hidden="true"${f.selected ? ' checked' : ''}${f.recoverable ? '' : ' disabled'}>`;
  }

  /// הקובץ כפריט ברשימה לקורא מסך: שם, גודל, סיכוי השחזור, ואם סומן לשחזור.
  function optionAttrs(f, index) {
    const quality = f.evidence ? 'עדות בלבד' : f.emptyContent ? 'ריק' : f.qualityLabel;
    const label = `${f.name}, ${formatSize(f.size).replace(/[\u2066\u2069]/g, '')}, ${quality}${f.deleted ? ', נמחק' : ''}` +
                  (f.recoverable ? '' : ', לא ניתן לשחזור');
    return `id="file-${index}" role="option" aria-selected="${!!f.selected}" aria-posinset="${index + 1}" ` +
           `aria-setsize="${total}" aria-label="${esc(label)}"`;
  }

  function rowHtml(f, index) {
    return `
      <div class="frow${f.recoverable ? '' : ' unrecoverable'}${isActive(f, index) ? ' active' : ''}"
           data-id="${f.id}" data-index="${index}" ${optionAttrs(f, index)}>
        <label class="frow-chk">${checkbox(f)}</label>
        <div class="frow-name">
          <span class="frow-icon">${Icon.file}</span>
          <span class="frow-text" title="${esc(f.path ? f.path + '\\' + f.name : f.name)}"><bdi>${esc(f.name)}</bdi></span>
          ${f.deleted ? '<span class="chip warn tiny">נמחק</span>' : ''}
          ${f.compressed ? '<span class="chip tiny">דחוס</span>' : ''}
          ${f.verified && f.recoverable
            // קובץ שנדרס מכיל נתונים — אבל של קובץ אחר. "אומת" ליד "לא ניתן לשחזור" היה סותר.
            ? '<span class="chip ok tiny" title="נדגם תוכן אמיתי מהדיסק">אומת</span>' : ''}
          ${f.evidence ? `<span class="chip tiny" title="${esc(f.source)}">${esc(f.source)}</span>` : ''}
          ${f.recycledAt ? `<span class="chip accent tiny"
            title="נמחק דרך סל המחזור ב-${esc(f.recycledAt)}. השם והתיקייה המקוריים הוחזרו מתוך הסל."
            >מסל המחזור</span>` : ''}
          ${f.namePartial ? `<span class="chip warn tiny"
            title="ב-FAT מחיקה דורסת את האות הראשונה של שם קצר. התוכן שלם, השם חסר אות אחת."
            >שם חלקי</span>` : ''}
        </div>
        <div class="frow-size">${formatSize(f.size)}</div>
        <div class="frow-date">${esc(f.modified || '—')}</div>
        <div class="frow-quality">${qualityChip(f)}</div>
      </div>`;
  }

  function tileHtml(f, index) {
    const thumb = f.thumb && Thumbs.get(f.id);
    const picture = thumb
      ? `<img src="${thumb}" alt="">`
      : `<span class="tile-icon${f.thumb && thumb !== false ? ' loading' : ''}">${f.thumb ? Icon.image : Icon.file}</span>`;

    return `
      <div class="tile${f.recoverable ? '' : ' unrecoverable'}${isActive(f, index) ? ' active' : ''}"
           data-id="${f.id}" data-index="${index}"${f.thumb && thumb === undefined ? ' data-thumb="1"' : ''} ${optionAttrs(f, index)}>
        <label class="tile-chk">${checkbox(f)}</label>
        <div class="tile-pic">${picture}</div>
        <div class="tile-name" title="${esc(f.path ? f.path + '\\' + f.name : f.name)}"><bdi>${esc(f.name)}</bdi></div>
        <div class="tile-meta"><span>${formatSize(f.size)}</span>${qualityChip(f)}</div>
      </div>`;
  }

  // ------------------------------------------------------------ כלי הרשימה

  function renderChips(counts) {
    if (!counts) return;
    el('cat-chips').innerHTML = CATEGORIES
      // קטגוריה ריקה אינה מוצגת — אלא אם היא הנבחרת, כדי שאפשר יהיה לצאת ממנה.
      .filter((c) => c.id === 'all' || counts[c.id] > 0 || c.id === prefs.category)
      .map((c) => `
        <button class="cat-chip${c.id === prefs.category ? ' on' : ''}" data-category="${c.id}">
          ${c.label}<span>${(counts[c.id] || 0).toLocaleString('he-IL')}</span>
        </button>`).join('');
  }

  function renderHead() {
    document.querySelectorAll('.col-sort').forEach((b) => {
      const on = b.dataset.sort === prefs.sort;
      b.classList.toggle('on', on);
      b.classList.toggle('desc', on && prefs.desc);
      b.setAttribute('aria-sort', on ? (prefs.desc ? 'descending' : 'ascending') : 'none');
    });
    document.querySelectorAll('.view-switch [data-view]').forEach((b) =>
      b.classList.toggle('on', b.dataset.view === prefs.mode));
    el('chk-recoverable').checked = prefs.recoverableOnly;
    el('flt-date').value = prefs.date;
    el('flt-range').hidden = prefs.date !== 'custom';
    el('flt-from').value = prefs.from;
    el('flt-to').value = prefs.to;
    el('flt-size').value = String(prefs.minSize);
    const sortBox = el('grid-sort');
    sortBox.hidden = prefs.mode !== 'grid';
    const current = `${prefs.sort}:${prefs.desc ? 1 : 0}`;
    if (![...sortBox.options].some((o) => o.value === current)) {
      sortBox.insertAdjacentHTML('beforeend', `<option value="${current}">מיון מהרשימה</option>`);
    }
    sortBox.value = current;
  }

  function setBanner(text) {
    const banner = el('list-banner');
    banner.hidden = !text;
    banner.textContent = text || '';
  }

  /// הודעת הכפילויות נשארת מעל הרשימה גם כשעוברים תיקייה, עד שמכבים את ההסתרה.
  let dupNote = '';

  /// הסתרת כפילויות: בפעם הראשונה המנוע משווה קבצים, וזה עשוי לקחת זמן.
  async function hideDuplicates(on) {
    const box = el('chk-dups');
    box.disabled = true;
    if (on) setBanner('מחפש קבצים כפולים…');
    try {
      const r = await longCall('scan.duplicates', { on });
      dupNote = !on ? ''
        : r.hidden === 0 ? 'לא נמצאו קבצים כפולים.'
        : `הוסתרו ${countFiles(r.hidden)} כפולים (${formatSize(r.bytes)}) — מכל קובץ מוצג העותק הטוב ביותר.` +
          (r.deselected === 1 ? ' עותק אחד שסומן הוסר מהבחירה.'
            : r.deselected > 1 ? ` ${r.deselected.toLocaleString('he-IL')} עותקים שסומנו הוסרו מהבחירה.` : '');
      applySelection(r.selection);
      await open();
    } catch (err) {
      box.checked = !on;
      const banner = el('list-banner');
      banner.hidden = false;
      banner.innerHTML = errorNotice('לא ניתן לחפש כפילויות', err, 'tiny-notice');
    } finally {
      box.disabled = false;
      Tree.refreshStates();
    }
  }

  /// האזנה אחת לכל הרשימה: השורות נבנות מחדש בכל גלילה, ולכן אין טעם לחבר אירועים לכל שורה.
  function attach() {
    const list = el('filelist');

    // ב-Chromium אירוע גלילה נשלח לכל היותר פעם אחת בכל פריים, ולכן אין צורך בוויסות נוסף.
    list.addEventListener('scroll', () => render());

    list.addEventListener('click', (e) => {
      const item = e.target.closest('[data-id]');
      if (!item || e.target.closest('.frow-chk, .tile-chk')) return;
      activeId = +item.dataset.id;
      activeIndex = +item.dataset.index;
      list.querySelectorAll('.active').forEach((r) => r.classList.remove('active'));
      item.classList.add('active');
      showPreview(activeId);
    });

    list.addEventListener('change', async (e) => {
      const item = e.target.closest('[data-id]');
      const file = item && byId.get(+item.dataset.id);
      if (!file) return;
      file.selected = e.target.checked;
      await select({ ids: [file.id], on: file.selected }, false);
    });

    el('chk-all').onchange = (e) => select({ all: true, on: e.target.checked }, true);

    el('cat-chips').addEventListener('click', (e) => {
      const chip = e.target.closest('[data-category]');
      if (!chip || chip.dataset.category === prefs.category) return;
      prefs.category = chip.dataset.category;
      open();
    });

    el('chk-recoverable').onchange = (e) => { prefs.recoverableOnly = e.target.checked; open(); };

    el('flt-date').onchange = (e) => {
      prefs.date = e.target.value;
      el('flt-range').hidden = prefs.date !== 'custom';
      if (prefs.date !== 'custom' || prefs.from || prefs.to) open();
    };
    el('flt-from').onchange = (e) => { prefs.from = e.target.value; open(); };
    el('flt-to').onchange = (e) => { prefs.to = e.target.value; open(); };
    el('flt-size').onchange = (e) => { prefs.minSize = +e.target.value; open(); };
    el('chk-dups').onchange = (e) => hideDuplicates(e.target.checked);
    el('grid-sort').onchange = (e) => {
      const [sort, desc] = e.target.value.split(':');
      prefs.sort = sort;
      prefs.desc = desc === '1';
      open();
    };

    document.querySelectorAll('.col-sort').forEach((b) => {
      b.onclick = () => {
        const col = SORT_COLUMNS.find((c) => c.id === b.dataset.sort);
        prefs.desc = prefs.sort === col.id ? !prefs.desc : col.firstDesc;
        prefs.sort = col.id;
        open();
      };
    });

    document.querySelectorAll('.view-switch [data-view]').forEach((b) => {
      b.onclick = () => {
        if (prefs.mode === b.dataset.view) return;
        prefs.mode = b.dataset.view;
        // הגלריה נפתחת לפי תאריך, עם כותרת לכל חודש — אלא אם כבר נבחר מיון אחר מלבד השם.
        if (prefs.mode === 'grid' && prefs.sort === 'name') { prefs.sort = 'date'; prefs.desc = true; }
        try { localStorage.setItem('raf-view', prefs.mode); } catch (e) {}
        open();
      };
    });

    new ResizeObserver(() => render()).observe(list);
  }

  // ------------------------------------------------------------------ בחירה

  /// שליחת פעולת סימון למנוע. אחרי סימון של יותר מקובץ אחד (תיקייה או "הכל")
  /// השורות הטעונות אינן מעודכנות, ולכן הן נטענות מחדש.
  async function select(request, reload) {
    const summary = await Bridge.call('scan.select', { ...request, view: target ? view() : null });
    applySelection(summary);
    if (reload) refresh();
    Tree.refreshStates();
  }

  function applySelection(s) {
    if (!s) return;
    State.selection = { count: s.count, bytes: s.bytes };
    updateRecoverBar();

    const all = el('chk-all');
    if (!all) return;
    all.checked = s.viewSelectable > 0 && s.viewSelected === s.viewSelectable;
    all.indeterminate = s.viewSelected > 0 && s.viewSelected < s.viewSelectable;
    all.disabled = s.viewSelectable === 0;
  }

  return {
    open, attach, select, key, toggleAll,
    get: (id) => byId.get(id),
    get total() { return total; },
    render,
  };
})();

/// קיצורי המקלדת במסך התוצאות. לא כשלוח או חלון השאלות פתוחים, ולא בזמן הקלדה —
/// חוץ מקונטרול+F, ומקש היציאה שמנקה את החיפוש.
document.addEventListener('keydown', (e) => {
  if (!el('filelist') || !el('overlay').hidden || !el('help-overlay').hidden) return;

  const search = el('search-input');
  const ctrl = e.ctrlKey || e.metaKey;

  if (ctrl && (e.key === 'f' || e.key === 'F' || e.code === 'KeyF')) {
    e.preventDefault();
    search.focus();
    search.select();
    return;
  }
  const target = e.target instanceof Element ? e.target : document.body;
  if (target === search) {
    if (e.key === 'Escape' && search.value) {
      search.value = '';
      search.dispatchEvent(new Event('input'));
    }
    if (e.key === 'Escape' || e.key === 'ArrowDown') { search.blur(); if (e.key === 'ArrowDown') FileList.key(e); }
    return;
  }
  if (target.closest('input[type="text"], input[type="date"], select, textarea')) return;
  // רווח ואנטר על כפתור או תיבת סימון שבפוקוס — הפעולה הרגילה שלהם, לא של הרשימה.
  if ((e.key === ' ' || e.key === 'Enter') && target.closest('button, input, label')) return;

  if (ctrl && (e.key === 'a' || e.key === 'A' || e.code === 'KeyA')) {
    e.preventDefault();
    FileList.toggleAll();
    return;
  }
  if (!ctrl && !e.altKey && FileList.key(e)) e.preventDefault();
});

/// תמונות ממוזערות לגלריה. המנוע מקטין כל תמונה, ולכן הבקשות מוגבלות
/// לשלוש במקביל ורק לאריחים שעל המסך — גלילה מהירה לא תציף את הדיסק.
const Thumbs = (() => {
  const LIMIT = 3;
  const MAX_CACHE = 600;
  const cache = new Map();  // מזהה ← data URL, או false אם אין תמונה
  let running = 0;

  function get(id) { return cache.get(id); }

  function remember(id, value) {
    cache.set(id, value);
    if (cache.size > MAX_CACHE) cache.delete(cache.keys().next().value);
  }

  /// מילוי האריחים שעל המסך. אריח שנגלל החוצה עד שהגיע תורו — מדולג.
  function fill(container) {
    while (running < LIMIT) {
      const tile = container.querySelector('.tile[data-thumb="1"]');
      if (!tile) return;
      tile.removeAttribute('data-thumb');
      load(+tile.dataset.id, container);
    }
  }

  async function load(id, container) {
    running++;
    try {
      const r = await Bridge.call('scan.thumb', { id, size: 200 });
      remember(id, r.ok ? 'data:image/jpeg;base64,' + r.data : false);
    } catch {
      remember(id, false);
    } finally {
      running--;
    }

    const tile = container.isConnected && container.querySelector(`.tile[data-id="${id}"] .tile-pic`);
    if (tile) {
      const src = cache.get(id);
      tile.innerHTML = src ? `<img src="${src}" alt="">` : `<span class="tile-icon">${Icon.image}</span>`;
    }
    if (container.isConnected) fill(container);
  }

  return { get, fill };
})();

/// "שמור בשם": הסריקה עם הבחירה הנוכחית, לכל כונן שאינו הכונן שנסרק.
async function saveScanAs() {
  try {
    const r = await Bridge.call('scan.save', {}, 0);
    if (r.path) setStatus('הסריקה נשמרה: ' + r.path);
  } catch (err) {
    showResultsNotice(errorNotice('הסריקה לא נשמרה', err));
  }
}

/// הודעה בראש מסך התוצאות, מתחת לשורת הכלים — מחליפה הודעה קודמת מאותו סוג.
function showResultsNotice(html) {
  const bar = document.querySelector('.results-bar');
  if (!bar) return;
  document.querySelector('.results-flash')?.remove();
  bar.insertAdjacentHTML('afterend', `<div class="results-flash">${html}</div>`);
}

/* ---------- הערות על הסריקה ---------- */

/// כל ההערות בשורה אחת מקופלת: שורת אזהרה מלאה לכל הערה דחקה את רשימת הקבצים
/// לחצי המסך התחתון. הרשימה היא העיקר; ההערות — למי שרוצה לדעת.
function notesHtml(items) {
  return `<details class="results-notes" id="results-notes">
    <summary>${Icon.info}<span id="notes-title">${notesTitle(items.length)}</span>
      <span class="notes-toggle"></span></summary>
    <div class="results-warnings" id="notes-list">${items.map(noteItem).join('')}</div>
  </details>`;
}
const notesTitle = (n) => n === 1 ? 'הערה אחת על הסריקה' : `${n.toLocaleString('he-IL')} הערות על הסריקה`;
const noteItem = (html) => `<div class="results-warning">${Icon.alert}<div>${html}</div></div>`;

/// הערה שמגיעה אחרי שהמסך הוצג (למשל: השמירה האוטומטית דולגה).
function addResultsNote(html) {
  if (!el('results-notes')) {
    const bar = document.querySelector('.results-bar');
    if (!bar) return;
    bar.insertAdjacentHTML('afterend', notesHtml([]));
  }
  el('notes-list').insertAdjacentHTML('afterbegin', noteItem(html));
  el('notes-title').textContent = notesTitle(el('notes-list').children.length);
}

// השמירה האוטומטית רצה ברקע אחרי הסריקה, ומודיעה כשהסתיימה — או למה לא נשמרה.
Bridge.on('scan.saved', (s) => {
  if (s.path) {
    setStatus(s.partial ? 'נקודת ביניים של הסריקה נשמרה' : 'הסריקה נשמרה אוטומטית');
  } else if (!s.partial) {
    setStatus('הסריקה לא נשמרה אוטומטית');
    addResultsNote(`<b>הסריקה לא נשמרה אוטומטית:</b> ${esc(s.skipped)} ` +
      'כדי לחזור אליה בלי לסרוק שוב, שמרו אותה בכפתור השמירה לכונן אחר.');
  }
});

function updateRecoverBar() {
  const { count, bytes } = State.selection;
  el('recover-info').textContent = count === 0
    ? 'לא נבחרו קבצים'
    : `${plural(count, 'נבחר', 'נבחרו')} ${countFiles(count)} · ${formatSize(Math.max(0, bytes))}`;
  // סריקה שנפתחה מקובץ בלי שהכונן שלה מחובר — אפשר לסמן, אבל לא לשחזר.
  const offline = !!(State.summary && State.summary.offline);
  el('btn-recover').disabled = count === 0 || offline;
  el('btn-recover').title = offline ? 'הכונן שנסרק אינו מחובר' : '';
}

/* ---------- תצוגה מקדימה ---------- */

async function showPreview(id) {
  const panel = el('preview');
  panel.innerHTML = `<div class="loading" style="height:160px"><div class="spinner"></div><p>קורא…</p></div>`;

  let p;
  try {
    p = await Bridge.call('scan.preview', { id });
  } catch (err) {
    panel.innerHTML = `<div style="margin:12px">${errorNotice('לא ניתן להציג את הקובץ', err)}</div>`;
    return;
  }

  // אי-התאמה בין הסיומת לתוכן היא סימן מובהק לקובץ פגום או לשם שגוי.
  const mismatch = p.matchesExtension === false
    ? `<div class="notice warn tiny-notice">${Icon.alert}
         <div>תוכן הקובץ אינו תואם לסיומת שלו. זוהה בפועל: <b>${esc(p.signature || 'לא ידוע')}</b></div>
       </div>` : '';

  let body;
  if (p.kind === 'media') {
    // הנגן מבקש מהמנוע רק את הקטעים שמנגנים — גם בסרטון של כמה ג'יגה.
    const tag = p.video ? 'video' : 'audio';
    // בלי תפריט שלוש הנקודות של הדפדפן ("הורדה", מהירות, תמונה בתוך תמונה):
    // שמירת קובץ נעשית בשחזור — שבודק אותו ומתעד אותו בדוח — ולא בהורדה מהנגן.
    body = `<${tag} class="preview-media ${tag}" controls preload="metadata"
              controlslist="nodownload noplaybackrate noremoteplayback" disablepictureinpicture
              disableremoteplayback src="${esc(p.url)}"></${tag}>`;
  } else if (p.kind === 'image') {
    body = `<img class="preview-img" src="data:${esc(p.mime)};base64,${p.data}" alt="">`;
  } else if (p.kind === 'text') {
    body = `<pre class="preview-text">${esc(p.text)}</pre>`;
  } else if (p.kind === 'none') {
    body = `<div class="preview-empty">${Icon.alert}<p>${esc(p.reason)}</p></div>`;
  } else {
    body = `<div class="preview-empty">${Icon.file}<p>אין תצוגה מקדימה לסוג קובץ זה</p></div>`;
  }

  const meta = FileList.get(id);
  const reason = meta && meta.qualityReason
    ? `<div class="notice ${meta.recoverable ? 'info' : 'danger'} tiny-notice">
         ${meta.recoverable ? Icon.shield : Icon.alert}
         <div>${esc(meta.qualityReason)}</div>
       </div>` : '';

  panel.innerHTML = `
    <div class="preview-head">
      <div class="preview-name" title="${esc(p.name)}"><bdi>${esc(p.name)}</bdi></div>
      ${p.signature ? `<div class="preview-sig">${esc(p.signature)}</div>` : ''}
    </div>
    ${reason}
    ${mismatch}
    <div class="preview-body">${body}</div>
    ${p.hex ? `
      <details class="hex-box">
        <summary>${Icon.hash}<span>התוכן הגולמי (HEX)</span></summary>
        <pre class="hex-dump">${esc(p.hex)}</pre>
      </details>` : ''}`;

  // קובץ שהנגן אינו מצליח לפענח — פגום, או בקידוד שהנגן אינו מכיר — מקבל הסבר
  // במקום מסך שחור. השחזור עצמו אינו תלוי בזה.
  const player = panel.querySelector('.preview-media');
  if (player) {
    player.addEventListener('error', () => {
      player.outerHTML = `<div class="preview-empty">${Icon.alert}
        <p>הנגן לא מצליח לנגן את הקובץ. ייתכן שהוא פגום, או שהוא בפורמט שהנגן המובנה אינו מכיר
        (למשל חלק מקובצי MKV ו-MOV). אפשר לשחזר אותו ולנסות לפתוח אותו בנגן אחר.</p></div>`;
    }, { once: true });
  }
}
