/* ==========================================================================
   שחזור מתקדם חינם — RAF
   שכבת הממשק. מתקשרת עם מנוע הליבה דרך גשר הודעות.
   ========================================================================== */

'use strict';

/* ----------------------------------------------------------------- הגשר */

const Bridge = (() => {
  const pending = new Map();
  const listeners = new Map();
  let counter = 0;

  window.chrome.webview.addEventListener('message', (e) => {
    let msg;
    try { msg = JSON.parse(e.data); } catch { return; }

    // הודעה ללא מזהה היא אירוע יזום מהמנוע, למשל דיווח התקדמות.
    if (msg.event) {
      (listeners.get(msg.event) || []).forEach((fn) => fn(msg.data));
      return;
    }

    const entry = pending.get(msg.id);
    if (!entry) return;
    pending.delete(msg.id);

    if (msg.ok) { entry.resolve(msg.data); return; }

    // השגיאה מגיעה מתורגמת: מה קרה, מה לעשות, והפירוט הטכני המקורי בנפרד.
    const err = new Error(msg.error || 'שגיאה לא ידועה');
    err.advice = msg.advice;
    err.detail = msg.detail;
    err.sourceUntouched = msg.sourceUntouched;
    entry.reject(err);
  });

  function call(method, params, timeoutMs) {
    const id = 'r' + (++counter);
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage(JSON.stringify({ id, method, params: params || {} }));

      // סריקה ושחזור עשויים להימשך שעות; שאר הבקשות מוגבלות בזמן.
      const limit = timeoutMs === 0 ? 0 : (timeoutMs || 120000);
      if (limit > 0) {
        setTimeout(() => {
          if (pending.has(id)) {
            pending.delete(id);
            const err = new Error('הפעולה לא הסתיימה בזמן הצפוי.');
            err.advice = 'ייתכן שהכונן איטי או תקוע. בדקו שהוא מחובר ונסו שוב.';
            reject(err);
          }
        }, limit);
      }
    });
  }

  function on(event, handler) {
    if (!listeners.has(event)) listeners.set(event, []);
    listeners.get(event).push(handler);
  }

  return { call, on };
})();

/* ------------------------------------------------------------ כלי עזר */

/// הגודל עטוף בבידוד כיווניות (LRI…PDI): בתוך משפט עברי "216 GB" היה מתהפך ל-"GB 216".
/// הבידוד עובד גם ב-textContent וגם ב-HTML, ואינו משפיע בהקשר משמאל לימין.
function formatSize(bytes) {
  if (bytes === null || bytes === undefined || bytes < 0) return '—';
  if (bytes === 0) return '⁦0 B⁩';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let v = bytes, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return '⁦' + v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2) + ' ' + units[i] + '⁩';
}

/// "קובץ אחד" / "3 קבצים" — בעברית המספר אחד בא אחרי שם העצם, ביחיד.
function countFiles(n) {
  return n === 1 ? 'קובץ אחד' : `${n.toLocaleString('he-IL')} קבצים`;
}

/// פועל שמתאים למספר: plural(n, 'שוחזר', 'שוחזרו').
function plural(n, one, many) {
  return n === 1 ? one : many;
}

function formatDuration(seconds) {
  if (!seconds || seconds < 0) return '—';
  const s = Math.floor(seconds % 60);
  const m = Math.floor((seconds / 60) % 60);
  const h = Math.floor(seconds / 3600);
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

function esc(text) {
  return String(text ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

function el(id) { return document.getElementById(id); }

/// הודעה במבנה אחיד: כותרת קצרה, משפט אחד, והנימוק המלא מקופל תחת "למה?".
/// הפרמטרים הם HTML — מי שקורא לפונקציה אחראי לבריחת טקסט חיצוני.
function notice(cls, icon, title, text, why, extra) {
  const head = title ? `<b>${title}</b>` : '';
  const sep = title && text ? '<br>' : '';
  const more = why ? `<details class="why"><summary>למה?</summary><div>${why}</div></details>` : '';
  return `<div class="notice ${cls}${extra ? ' ' + extra : ''}">${icon}<div>${head}${sep}${text || ''}${more}</div></div>`;
}

/// שגיאה במבנה אחיד: מה קרה (הכותרת וההודעה), מה זה אומר על הקבצים,
/// ומה לעשות עכשיו. ההודעה הטכנית המקורית מקופלת תחת "פרטים טכניים".
function errorNotice(title, err, extra) {
  const lines = [esc(err.message)];
  if (err.sourceUntouched) lines.push('הקבצים המקוריים לא השתנו.');
  if (err.advice) lines.push(`<b>מה לעשות:</b> ${esc(err.advice)}`);
  const detail = err.detail
    ? `<details class="why"><summary>פרטים טכניים</summary><div dir="ltr">${esc(err.detail)}</div></details>`
    : '';
  const head = title ? `<b>${title}</b><br>` : '';
  return `<div class="notice danger${extra ? ' ' + extra : ''}">${Icon.alert}<div>${head}${lines.join('<br>')}${detail}</div></div>`;
}

/* ------------------------------------------------------------- סמלים */

const Icon = {
  trash: '<svg viewBox="0 0 24 24"><path d="M4 7h16"/><path d="M9.5 7V4.5h5V7"/><path d="M6 7l1 13a1.5 1.5 0 0 0 1.5 1.4h7a1.5 1.5 0 0 0 1.5-1.4L18 7"/><path d="M10 11v6M14 11v6"/></svg>',
  eraser: '<svg viewBox="0 0 24 24"><path d="m7 21-4.3-4.3a1.5 1.5 0 0 1 0-2.1l10-10a1.5 1.5 0 0 1 2.1 0l5.6 5.6a1.5 1.5 0 0 1 0 2.1L13 21Z"/><path d="M21 21H7"/><path d="m5 12 7 7"/></svg>',
  unplug: '<svg viewBox="0 0 24 24"><path d="m19 5 3-3"/><path d="m2 22 3-3"/><path d="M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z"/><path d="M7.5 13.5 10 11"/><path d="M10.5 16.5 13 14"/><path d="m12 6 6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0Z"/></svg>',
  drive: '<svg viewBox="0 0 24 24"><path d="M3 6.5c0-1.4 4-2.5 9-2.5s9 1.1 9 2.5S17 9 12 9 3 7.9 3 6.5Z"/><path d="M21 6.5v11c0 1.4-4 2.5-9 2.5s-9-1.1-9-2.5v-11"/><path d="M3 12c0 1.4 4 2.5 9 2.5s9-1.1 9-2.5"/></svg>',
  hdd: '<svg viewBox="0 0 24 24"><rect x="2.5" y="5" width="19" height="14" rx="2.5"/><circle cx="12" cy="12" r="3.5"/><circle cx="12" cy="12" r="0.6"/></svg>',
  usb: '<svg viewBox="0 0 24 24"><path d="M9 8.5V3.5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v5"/><path d="M11 5h.01M13 5h.01"/><rect x="6.5" y="8.5" width="11" height="13" rx="2.5"/><path d="M10.5 17.5h3"/></svg>',
  card: '<svg viewBox="0 0 24 24"><path d="M7 2.5h7.5L18.5 6.5v13.5a1.5 1.5 0 0 1-1.5 1.5H7A1.5 1.5 0 0 1 5.5 20V4A1.5 1.5 0 0 1 7 2.5Z"/><path d="M8.75 5.75v3M11.25 5.75v3M13.75 5.75v3"/><path d="M8.5 15.5h7"/></svg>',
  bolt: '<svg viewBox="0 0 24 24"><path d="M13 2 4.5 13.5H11l-1 8.5 8.5-11.5H12l1-8.5Z"/></svg>',
  layers: '<svg viewBox="0 0 24 24"><path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 13 9 5 9-5"/><path d="m3 17.5 9 5 9-5"/></svg>',
  radar: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="5"/><path d="M12 3v9l6.5 4"/></svg>',
  chevron: '<svg viewBox="0 0 24 24"><path d="m14 6-6 6 6 6"/></svg>',
  refresh: '<svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 4v5h-5"/></svg>',
  close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  alert: '<svg viewBox="0 0 24 24"><path d="M12 3.5 22 20H2L12 3.5Z"/><path d="M12 10v4"/><path d="M12 17h.01"/></svg>',
  info: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 11v5"/><path d="M12 8h.01"/></svg>',
  shield: '<svg viewBox="0 0 24 24"><path d="M12 2.5 20 6v6c0 5-3.4 8.4-8 9.5-4.6-1.1-8-4.5-8-9.5V6l8-3.5Z"/><path d="m9 12 2 2 4-4"/></svg>',
  folder: '<svg viewBox="0 0 24 24"><path d="M3 7.5A1.5 1.5 0 0 1 4.5 6h4l2 2.5h7A1.5 1.5 0 0 1 19 10v7.5A1.5 1.5 0 0 1 17.5 19h-13A1.5 1.5 0 0 1 3 17.5Z"/></svg>',
  file: '<svg viewBox="0 0 24 24"><path d="M6 3h8l5 5v13a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z"/><path d="M14 3v5h5"/></svg>',
  // "חזרה" בממשק מימין לשמאל מצביעה ימינה — אל הכיוון שממנו באנו.
  back: '<svg viewBox="0 0 24 24"><path d="M19 12H5"/><path d="m12 5 7 7-7 7"/></svg>',
  search: '<svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>',
  save: '<svg viewBox="0 0 24 24"><path d="M12 3v12"/><path d="m7 11 5 5 5-5"/><path d="M4 18v2a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-2"/></svg>',
  stop: '<svg viewBox="0 0 24 24"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="m5 13 4 4L19 7"/></svg>',
  image: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="8.5" cy="9.5" r="1.5"/><path d="m4 17 5-5 4 4 3-2 4 4"/></svg>',
  hash: '<svg viewBox="0 0 24 24"><path d="M5 9h14M5 15h14M10 4 8 20M16 4l-2 16"/></svg>',
  disc: '<svg viewBox="0 0 24 24"><path d="M6 3h8l5 5v13a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z"/><circle cx="12" cy="14" r="4"/><circle cx="12" cy="14" r="0.8"/></svg>',
  copy: '<svg viewBox="0 0 24 24"><rect x="8" y="8" width="12" height="13" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h2"/></svg>',
  open: '<svg viewBox="0 0 24 24"><path d="M3 7.5A1.5 1.5 0 0 1 4.5 6h4l2 2.5h7A1.5 1.5 0 0 1 19 10v1"/><path d="M3 7.5v10A1.5 1.5 0 0 0 4.5 19h12.3a1.5 1.5 0 0 0 1.4-1l2.6-6.2a.8.8 0 0 0-.7-1.1H7.4a1.5 1.5 0 0 0-1.4 1L3 19"/></svg>',
  wrench: '<svg viewBox="0 0 24 24"><path d="M14.7 6.3a4 4 0 0 0-5.4 5.1L4 16.7V20h3.3l5.3-5.3a4 4 0 0 0 5.1-5.4l-2.6 2.6-2.4-.6-.6-2.4 2.6-2.6Z"/></svg>',
  history: '<svg viewBox="0 0 24 24"><path d="M3.5 12a8.5 8.5 0 1 0 2.5-6"/><path d="M3 4v4.5h4.5"/><path d="M12 7.5V12l3 2"/></svg>',
  disk: '<svg viewBox="0 0 24 24"><path d="M5 3.5h11l3.5 3.5v12a1.5 1.5 0 0 1-1.5 1.5H6A1.5 1.5 0 0 1 4.5 19V5A1.5 1.5 0 0 1 6 3.5Z"/><path d="M8 3.5v5h7v-5"/><rect x="7.5" y="13" width="9" height="7.5" rx="1"/></svg>',
  list: '<svg viewBox="0 0 24 24"><path d="M8 6h13M8 12h13M8 18h13"/><path d="M3.5 6h.01M3.5 12h.01M3.5 18h.01"/></svg>',
  grid: '<svg viewBox="0 0 24 24"><rect x="3.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.5"/></svg>',
};

function diskIcon(media) {
  if (media === 'HardDisk') return { html: Icon.hdd, cls: 'is-hdd' };
  if (media === 'UsbFlash') return { html: Icon.usb, cls: 'is-usb' };
  if (media === 'MemoryCard') return { html: Icon.card, cls: 'is-usb' };
  if (media === 'Image') return { html: Icon.disc, cls: 'is-image' };
  return { html: Icon.drive, cls: '' };
}

/* --------------------------------------------------------- ערכת נושא */

/// כפתור אחד שעובר במחזור: לפי המערכת ← בהירה ← כהה.
/// הסמל מראה את הבחירה הנוכחית, והתיאור מסביר אותה במילים.
const Theme = (() => {
  const ORDER = ['system', 'light', 'dark'];
  const LABEL = {
    system: 'ערכת נושא: לפי הגדרות Windows',
    light: 'ערכת נושא: בהירה',
    dark: 'ערכת נושא: כהה',
  };
  const ICON = {
    system: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M12 4v12" /><path d="M12 4h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-7Z" fill="currentColor" stroke="none"/><path d="M8 20h8M12 16v4"/></svg>',
    light: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M4.6 4.6 6 6M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4"/></svg>',
    dark: '<svg viewBox="0 0 24 24"><path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z"/></svg>',
  };

  const media = matchMedia('(prefers-color-scheme: dark)');

  function load() {
    try { return ORDER.includes(localStorage.getItem('raf-theme')) ? localStorage.getItem('raf-theme') : 'system'; }
    catch (e) { return 'system'; }
  }

  let pref = load();

  function apply() {
    const dark = pref === 'dark' || (pref === 'system' && media.matches);
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';

    const btn = el('btn-theme');
    btn.innerHTML = ICON[pref];
    btn.title = LABEL[pref];
    btn.setAttribute('aria-label', LABEL[pref]);

    // גם רקע החלון עצמו מתעדכן, כדי שבשינוי גודל לא יבצבץ צבע אחר.
    Bridge.call('window.theme', { dark }).catch(() => {});
  }

  el('btn-theme').onclick = () => {
    pref = ORDER[(ORDER.indexOf(pref) + 1) % ORDER.length];
    try { localStorage.setItem('raf-theme', pref); } catch (e) {}
    apply();
    setStatus(LABEL[pref]);
  };

  // במצב "לפי המערכת", שינוי בהגדרות Windows מתעדכן מיד.
  media.addEventListener('change', () => { if (pref === 'system') apply(); });

  apply();
  return { get preference() { return pref; } };
})();

/* --------------------------------------------------------- שליטת חלון */

el('btn-min').onclick = () => Bridge.call('window.minimize');
el('btn-max').onclick = () => Bridge.call('window.toggleMaximize');
el('btn-close').onclick = () => Bridge.call('window.close');

// כפתורים בשורת הכותרת — שליטת חלון ושלבים שאפשר לחזור אליהם — אינם גוררים את החלון.
const NOT_DRAG = '.win-btn, .step.link';

el('titlebar').addEventListener('mousedown', (e) => {
  if (e.button !== 0 || e.target.closest(NOT_DRAG)) return;
  Bridge.call('window.beginDrag', { hit: 2 });
});
el('titlebar').addEventListener('dblclick', (e) => {
  if (!e.target.closest(NOT_DRAG)) Bridge.call('window.toggleMaximize');
});

document.querySelectorAll('.resize-edge').forEach((edge) => {
  edge.addEventListener('mousedown', (e) => {
    if (e.button === 0) Bridge.call('window.beginDrag', { hit: parseInt(edge.dataset.hit, 10) });
  });
});

document.addEventListener('contextmenu', (e) => e.preventDefault());
document.addEventListener('dragstart', (e) => e.preventDefault());

/* ------------------------------------------------------------- מצב */

const State = {
  disks: [],
  elevated: false,
  scan: null,          // { disk, part, mode }
  summary: null,
  currentPath: '',
  selection: { count: 0, bytes: 0 }, // סיכום הבחירה; הבחירה עצמה נשמרת במנוע
  showEvidence: false, // רשומות יומן מוסתרות כברירת מחדל
  openDisks: new Set(), // כוננים שהמחיצות שלהם פתוחות
  failed: [],           // התקנים ש-Windows לא הצליח להפעיל
  flash: null,          // הודעה חד-פעמית לראש מסך הכוננים
};

function setStatus(text) { el('status-text').textContent = text; }

/// פס השלבים בשורת הכותרת. שלב קודם הוא קישור חזרה — אלא בזמן פעולה ארוכה
/// (סריקה, יצירת תמונה, שחזור), שתוצאתה הייתה דורסת את המסך שאליו עברו.
/// "בחירת קבצים" זמין גם ממסך הכוננים כל עוד יש תוצאות סריקה: הבחירה נשמרת במנוע.
const Steps = (() => {
  const STEPS = ['מחיצה', 'סריקה', 'בחירת קבצים', 'שחזור'];
  let current = 1;
  let busy = false;

  function canGo(n) {
    if (busy || n === current) return false;
    if (n === 1) return current >= 3;
    if (n === 3) return (current === 1 || current === 4) && !!State.summary;
    return false;
  }

  function render() {
    el('steps').innerHTML = STEPS.map((label, i) => {
      const n = i + 1;
      const done = n < current;
      const cls = ['step', done ? 'done' : '', n === current ? 'current' : '', canGo(n) ? 'link' : ''].join(' ');
      const sep = n > 1 ? `<span class="step-sep${done || n === current ? ' done' : ''}"></span>` : '';
      return `${sep}<button class="${cls}" data-step="${n}"${canGo(n) ? '' : ' tabindex="-1"'}
                ${n === current ? 'aria-current="step"' : ''}>
                <span class="step-num">${done ? Icon.check : n}</span>${label}</button>`;
    }).join('');
  }

  el('steps').addEventListener('click', (e) => {
    const step = e.target.closest('[data-step]');
    if (!step || !canGo(+step.dataset.step)) return;
    closePanel();
    if (+step.dataset.step === 1) loadDisks();
    else if (!el('filelist')) renderResults();
  });

  render();
  return {
    set(n) { current = n; render(); },
    get current() { return current; },
    get busy() { return busy; },
    /// פעולה ארוכה נועלת את הניווט עד שהיא מסתיימת.
    setBusy(on) { busy = on; render(); },
  };
})();

/// קריאה ארוכה למנוע (סריקה, תמונה, שחזור): ללא מגבלת זמן, והניווט נעול עד סופה.
async function longCall(method, params) {
  Steps.setBusy(true);
  try {
    return await Bridge.call(method, params, 0);
  } finally {
    Steps.setBusy(false);
  }
}

/* =====================================================================
   מסך 1 — רשימת המחיצות
   ===================================================================== */

async function loadDisks() {
  Steps.set(1);
  el('content').innerHTML =
    '<div class="loading"><div class="spinner"></div><p>סורק את אמצעי האחסון במערכת…</p></div>';

  try {
    const data = await Bridge.call('disks.list');
    State.disks = data.disks || [];
    State.failed = data.failed || [];
    State.elevated = !!data.elevated;
    renderDisks();
  } catch (err) {
    el('content').innerHTML =
      errorNotice('לא ניתן לקרוא את רשימת הכוננים', err);
    setStatus('שגיאה');
  }
}

function renderDisks() {
  const totalParts = State.disks.reduce((n, d) => n + d.partitions.length, 0);

  let html = `
    <div class="page-head">
      <div>
        <div class="page-title">הכוננים במערכת</div>
        <div class="page-desc">לחצו על כונן כדי לראות את המחיצות שבו</div>
      </div>
      <div class="head-actions">
        <button class="btn" id="btn-open-scan">${Icon.history}<span>פתיחת סריקה שמורה</span></button>
        <button class="btn" id="btn-open-image">${Icon.open}<span>פתיחת תמונת דיסק</span></button>
        <button class="btn" id="btn-doctor">${Icon.wrench}<span>תיקון קבצים שלא נפתחים</span></button>
        <button class="btn" id="btn-refresh">${Icon.refresh}<span>רענון</span></button>
      </div>
    </div>`;

  // הודעה חד-פעמית מפעולה קודמת (למשל תוצאת סריקת כונן).
  if (State.flash) {
    html += State.flash;
    State.flash = null;
  }

  if (!State.elevated) {
    html += notice('danger', Icon.alert,
      'אין הרשאות מנהל — השחזור לא יעבוד',
      'סגרו את התוכנה והפעילו אותה מחדש: לחיצה ימנית ← "הפעל כמנהל".',
      'בלי הרשאות מנהל Windows מאפשר לראות רק את הקבצים הקיימים. קבצים שנמחקו נמצאים מתחת לרשימת הקבצים, ' +
      'ורק קריאה ישירה של הכונן מגיעה אליהם.');
  }

  html += situationCards();

  if (State.disks.length === 0 && State.failed.length === 0) {
    html += `<div class="empty"><h3>לא נמצאו אמצעי אחסון</h3><p>ודאו שהדיסק מחובר ונסו לרענן.</p></div>`;
  } else {
    html += State.disks.map(renderDisk).join('');
  }

  html += renderFailedDevices();
  html += '<div id="recent-scans"></div>';

  el('content').innerHTML = html;

  el('btn-refresh').onclick = loadDisks;
  el('btn-doctor').onclick = () => openDoctorPanel();
  document.querySelectorAll('[data-situation]').forEach((btn) => {
    btn.onclick = () => openSituation(btn.dataset.situation);
  });
  el('btn-open-image').onclick = () => openImageFile();
  el('btn-open-scan').onclick = () => openSavedScan();
  renderRecentScans();

  // לחיצה על שורת כונן פותחת או סוגרת את המחיצות שלו.
  document.querySelectorAll('[data-toggle-disk]').forEach((head) => {
    head.onclick = (e) => {
      if (e.target.closest('button')) return;
      toggleDisk(+head.dataset.toggleDisk);
    };
  });

  const byNumber = (n) => State.disks.find((d) => d.number === n);

  document.querySelectorAll('[data-image-disk]').forEach((btn) => {
    btn.onclick = () => { const d = byNumber(+btn.dataset.imageDisk); if (d) openImagePanel(d, null); };
  });

  document.querySelectorAll('[data-hunt-disk]').forEach((btn) => {
    btn.onclick = () => { const d = byNumber(+btn.dataset.huntDisk); if (d) openHuntPanel(d); };
  });

  document.querySelectorAll('[data-close-image]').forEach((btn) => {
    btn.onclick = async () => {
      await Bridge.call('image.close', { disk: +btn.dataset.closeImage });
      loadDisks();
    };
  });

  document.querySelectorAll('.part[data-disk]').forEach((row) => {
    row.onclick = () => openScanPanel(+row.dataset.disk, +row.dataset.part);
  });

  setStatus(`${State.disks.length} כוננים · ${totalParts} מחיצות`);
}

/* ------------------------------------------------------ מה קרה? */

/// מי שהגיע לתוכנה בלחץ לא יודע אם צריך "סריקה עמוקה" או "סריקת כונן".
/// הוא יודע מה קרה לו — והכרטיסים מתרגמים את זה לפעולה הנכונה.
const SITUATIONS = [
  { id: 'deleted',   icon: 'trash',  title: 'מחקתי קבצים',        sub: 'גם מסל המחזור' },
  { id: 'formatted', icon: 'eraser', title: 'פרמטתי כונן',        sub: 'או כרטיס זיכרון' },
  { id: 'asks',      icon: 'alert',  title: 'Windows מבקש לפרמט', sub: 'הכונן לא נפתח' },
  { id: 'missing',   icon: 'search', title: 'מחיצה נעלמה',        sub: 'הכונן נראה ריק' },
  { id: 'broken',    icon: 'wrench', title: 'קובץ לא נפתח',       sub: 'תמונה, מסמך, סרטון' },
  { id: 'unseen',    icon: 'unplug', title: 'הכונן לא מופיע',     sub: 'מחובר אבל לא מזוהה' },
];

function situationCards() {
  return `
    <div class="section-label">מה קרה?</div>
    <div class="situations">
      ${SITUATIONS.map((s) => `
        <button class="situation" data-situation="${s.id}">
          <span class="situation-icon">${Icon[s.icon]}</span>
          <span class="situation-text"><b>${s.title}</b><small>${s.sub}</small></span>
        </button>`).join('')}
    </div>`;
}

/// מחיצה ברשימה, כפתור שפותח אותה — לשימוש בתוך ההדרכה.
function partButton(disk, part) {
  const name = part.label || (part.letter ? 'כונן ' + part.letter : part.typeName || 'מחיצה');
  return `<button class="btn" data-guide-part="${disk.number}:${part.index}">
            ${diskIcon(disk.media).html}<span><bdi>${esc(name)}</bdi> · <bdi>${esc(disk.name)}</bdi> · ${formatSize(part.size)}</span>
          </button>`;
}

function openSituation(id) {
  if (id === 'broken') { openDoctorPanel(); return; }

  const s = SITUATIONS.find((x) => x.id === id);
  const steps = (items) => `<ol class="guide-steps">${items.map((i) => `<li>${i}</li>`).join('')}</ol>`;
  const disks = State.disks.filter((d) => !d.unresponsive && d.rawAccessible);
  let body = '', actions = '';

  if (id === 'deleted') {
    body = notice('warn', Icon.alert, 'אל תשמרו שום דבר על הכונן שממנו נמחקו הקבצים',
        'כל קובץ חדש — גם הורדה או התקנה — עלול להיכתב בדיוק במקום של הקבצים שנמחקו.') +
      steps([
        'פתחו את הכונן ברשימה ולחצו על <b>המחיצה שבה היו הקבצים</b> (לרוב C: או D:).',
        'בחרו <b>סריקה מהירה</b>. היא לוקחת שניות עד דקות, ושומרת שמות ותיקיות.',
        'לא מצאתם? חזרו לאותה מחיצה ובחרו <b>סריקה עמוקה</b>.',
        'סמנו את הקבצים ושחזרו אותם — <b>לכונן אחר</b>.',
      ]);
    actions = `<button class="btn btn-primary" data-guide-go>${Icon.drive}<span>לרשימת הכוננים</span></button>`;
  } else if (id === 'formatted') {
    body = notice('warn', Icon.alert, 'אל תעתיקו קבצים חדשים לכונן שפורמט',
        'עד שהשחזור מסתיים, כל מה שנכתב אליו עלול לדרוס את מה שאפשר עוד להציל.',
        'פירמוט מהיר מוחק רק את רשימת הקבצים, והתוכן נשאר. פירמוט מלא (לא מהיר) ב-Windows 10 ומעלה ' +
        'כותב אפסים על כל הכונן, ואחריו אין מה לשחזר.') +
      steps([
        'לחצו על <b>המחיצה שפורמטה</b>.',
        'בחרו <b>סריקה עמוקה</b> — לעיתים היא מוצאת גם שמות ותיקיות מלפני הפירמוט.',
        'לא נמצא מספיק? בחרו <b>סריקה מתקדמת</b>. היא מזהה קבצים לפי התוכן שלהם ועובדת גם אחרי פירמוט, ' +
        'אבל בלי השמות המקוריים.',
      ]);
    actions = `<button class="btn btn-primary" data-guide-go>${Icon.drive}<span>לרשימת הכוננים</span></button>`;
  } else if (id === 'asks') {
    const raw = disks.flatMap((d) => d.partitions.filter((p) => !p.scannable && !p.found).map((p) => [d, p]));
    body = notice('danger', Icon.alert, 'אל תאשרו את הפירמוט',
        'Windows מבקש לפרמט כשתחילת המחיצה נפגעה — אבל הקבצים בדרך כלל עדיין שם, שלמים.') +
      steps([
        'לחצו על המחיצה ש-Windows לא מצליח לפתוח.',
        'התוכנה תבדוק אותה ותציע, לפי הסדר: <b>להעתיק את הקבצים בלי לכתוב לכונן</b>, לתקן את המחיצה, ' +
        'או לחפש קבצים לפי התוכן שלהם.',
      ]) +
      (raw.length
        ? `<div class="section-label">מחיצות שהתוכנה לא מצליחה לקרוא</div>
           <div class="guide-parts">${raw.map(([d, p]) => partButton(d, p)).join('')}</div>`
        : notice('info', Icon.info, 'כרגע כל המחיצות נקראות',
            'אם הכונן עדיין לא נפתח ב-Windows, ייתכן שהמחיצה נמחקה מהטבלה — נסו את "מחיצה נעלמה".'));
  } else if (id === 'missing') {
    body = steps([
        'ליד הכונן לחצו <b>סריקת כונן</b>.',
        'התוכנה תעבור על כל הכונן ותחפש מחיצות שנמחקו מטבלת המחיצות — גם כשתחילתן נהרסה.',
        'מחיצה שנמצאה תופיע ברשימה. אפשר להעתיק ממנה קבצים בלי לכתוב לכונן, או להחזיר אותה לטבלה.',
      ]) +
      (disks.length
        ? `<div class="section-label">סריקת כונן</div>
           <div class="guide-parts">${disks.map((d) => `
             <button class="btn" data-guide-hunt="${d.number}">
               ${diskIcon(d.media).html}<span><bdi>${esc(d.name)}</bdi> · ${formatSize(d.size)}</span>
             </button>`).join('')}</div>`
        : '');
  } else if (id === 'unseen') {
    body = (State.failed.length
        ? notice('info', Icon.info,
            `Windows מרגיש ${State.failed.length === 1 ? 'בהתקן אחד' : State.failed.length + ' התקנים'} שלא הופעל`,
            'הם מופיעים בתחתית רשימת הכוננים, עם הסבר ועצות.')
        : '') +
      steps([
        'חברו את הכונן ישירות למחשב, בלי מפצל USB, ונסו יציאה אחרת — עדיף בגב המחשב.',
        'כונן חיצוני גדול: נסו כבל אחר, וודאו שספק הכוח שלו מחובר אם יש לו.',
        'כרטיס זיכרון: נסו קורא כרטיסים אחר.',
        'לחצו <b>רענון</b>.',
      ]) +
      notice('danger', Icon.alert, 'כונן שמשמיע נקישות או רעשים — נתקו אותו מיד',
        'כל הפעלה נוספת עלולה להרוס את מה שנשאר. זה מקרה למעבדת שחזור.',
        'כונן שהמחשב כלל אינו מרגיש שחובר אינו נראה לשום תוכנה; הבעיה בחומרה.');
    actions = `<button class="btn btn-primary" data-guide-refresh>${Icon.refresh}<span>רענון</span></button>`;
  }

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${s.title}</div>
        <div class="panel-sub">מה עושים עכשיו</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">${body}</div>
    <div class="panel-foot">${actions}<button class="btn" id="btn-guide-close">סגירה</button></div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-guide-close').onclick = closePanel;

  const panel = el('panel');
  panel.querySelector('[data-guide-go]')?.addEventListener('click', () => {
    closePanel();
    State.disks.forEach((d) => State.openDisks.add(d.number));
    renderDisks();
    document.querySelector('.disk')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
  panel.querySelector('[data-guide-refresh]')?.addEventListener('click', () => { closePanel(); loadDisks(); });
  panel.querySelectorAll('[data-guide-part]').forEach((btn) => {
    btn.onclick = () => {
      const [d, p] = btn.dataset.guidePart.split(':').map(Number);
      closePanel();
      openScanPanel(d, p);
    };
  });
  panel.querySelectorAll('[data-guide-hunt]').forEach((btn) => {
    btn.onclick = () => {
      const d = State.disks.find((x) => x.number === +btn.dataset.guideHunt);
      closePanel();
      if (d) openHuntPanel(d);
    };
  });
}

function toggleDisk(number) {
  if (State.openDisks.has(number)) State.openDisks.delete(number);
  else State.openDisks.add(number);

  const card = document.querySelector(`.disk[data-disk-card="${number}"]`);
  if (card) card.classList.toggle('open', State.openDisks.has(number));
}

function renderDisk(disk) {
  const icon = diskIcon(disk.media);
  const open = State.openDisks.has(disk.number);

  // TRIM הוא המונח המוכר, ולכן הוא נשאר על השבב; ההסבר במילים פשוטות — בריחוף.
  const trimChip = disk.trim === 'Enabled'
    ? '<span class="chip warn" title="הכונן מוחק מעצמו את התוכן של קבצים שנמחקו, ולכן סיכויי השחזור נמוכים יותר.">TRIM פעיל</span>'
    : disk.trim === 'NotSupported'
      ? '<span class="chip ok" title="הכונן אינו מוחק מעצמו את התוכן של קבצים שנמחקו — מצב טוב לשחזור.">ללא TRIM</span>'
      : '';
  const stateChip = disk.unresponsive ? '<span class="chip danger">לא מגיב</span>'
    : disk.rawAccessible ? '' : '<span class="chip danger">אין גישה גולמית</span>';

  const count = disk.partitions.length;
  const foundCount = disk.partitions.filter((p) => p.found).length;
  const countText = count === 0 ? 'ללא מחיצות'
    : count === 1 ? 'מחיצה אחת' : `${count} מחיצות`;

  const sub = disk.isImage
    ? `<span class="ltr-inline">${esc(disk.imagePath)}</span> · ${formatSize(disk.size)} · ${countText}`
    : `דיסק ${disk.number} · ${esc(disk.mediaLabel)} · ${disk.size > 0 ? formatSize(disk.size) : 'גודל לא ידוע'} · ${countText}`;

  const actions = [];
  if (disk.rawAccessible && disk.size > 0) {
    actions.push(`<button class="btn btn-sm" data-hunt-disk="${disk.number}" title="חיפוש מחיצות שנמחקו או שאבדו בכל הכונן">${Icon.search}<span>סריקת כונן</span></button>`);
  }
  if (disk.isImage) {
    actions.push(`<button class="btn btn-sm" data-close-image="${disk.number}">${Icon.close}<span>סגירת התמונה</span></button>`);
  } else if (disk.rawAccessible) {
    actions.push(`<button class="btn btn-sm" data-image-disk="${disk.number}" title="העתקת הדיסק כולו לקובץ, וסריקה מתוכו">${Icon.copy}<span>יצירת תמונה</span></button>`);
  }

  const notes = [];
  if (disk.unresponsive && disk.problem) {
    notes.push(`<div class="disk-note danger">${Icon.alert}<span>${esc(disk.problem)}</span></div>`);
  }
  if (disk.isImage && disk.imageNote) {
    notes.push(`<div class="disk-note ${disk.imageDamaged ? 'warn' : ''}">${disk.imageDamaged ? Icon.alert : Icon.info}<span>${esc(disk.imageNote)}</span></div>`);
  }
  if (!disk.isImage && !disk.unresponsive && disk.rawAccessible && disk.size === 0) {
    notes.push(`<div class="disk-note warn">${Icon.alert}<span>הכונן מדווח על גודל 0. בקורא כרטיסים זה אומר בדרך כלל שאין כרטיס בפנים, או שהכרטיס אינו נקרא.</span></div>`);
  }

  let parts = disk.partitions.map((p) => renderPartition(disk, p)).join('');
  if (!parts) {
    parts = `<div class="part-empty">לא נמצאו מחיצות בטבלת המחיצות של הכונן.
      ${disk.rawAccessible && disk.size > 0 ? 'היו בו מחיצות שנמחקו? לחצו על <b>סריקת כונן</b>.' : ''}</div>`;
  }

  return `
    <section class="disk ${open ? 'open' : ''}" data-disk-card="${disk.number}">
      <div class="disk-head" data-toggle-disk="${disk.number}" title="לחצו להצגת המחיצות">
        <div class="disk-toggle">${Icon.chevron}</div>
        <div class="disk-icon ${icon.cls}">${icon.html}</div>
        <div class="disk-meta">
          <div class="disk-name">${esc(disk.name)}</div>
          <div class="disk-sub">${sub}</div>
        </div>
        <div class="chips">
          ${foundCount ? `<span class="chip accent">${foundCount === 1 ? 'נמצאה מחיצה אחת' : `נמצאו ${foundCount} מחיצות`}</span>` : ''}
          <span class="chip accent ltr">${esc(disk.mediaShort)}</span>
          ${disk.isImage ? '' : `<span class="chip ltr">${esc(disk.bus)}</span>`}
          <span class="chip ltr">${esc(disk.scheme)}</span>
          ${disk.isImage ? '' : trimChip}${stateChip}
        </div>
        <div class="disk-actions">${actions.join('')}</div>
      </div>
      ${notes.join('')}
      <div class="parts">${parts}</div>
    </section>`;
}

function renderPartition(disk, p) {
  // ללא אות כונן — כוכבית, כמו בכלי הדיסקים של Windows.
  const letter = p.letter
    ? `<div class="part-letter">${esc(p.letter)}</div>`
    : disk.isImage
      ? `<div class="part-letter is-image">IMG</div>`
      : `<div class="part-letter unmounted" title="למחיצה אין אות כונן">*</div>`;

  const name = p.label ? esc(p.label)
    : (p.letter ? 'כונן ' + esc(p.letter) : esc(p.typeName) || 'מחיצה');

  const tags = [`<span class="chip ltr">${esc(p.found && p.foundInfo ? p.foundInfo.fsLabel : p.fsLabel)}</span>`];

  if (p.found) {
    tags.push('<span class="chip accent">נמצאה בסריקה</span>');
    if (p.foundInfo && p.foundInfo.damaged) tags.push('<span class="chip warn">תחילתה פגומה</span>');
    if (p.foundInfo && p.foundInfo.overlaps) tags.push('<span class="chip warn">חופפת למחיצה קיימת</span>');
    if (p.foundInfo && p.foundInfo.inside) tags.push('<span class="chip warn">בתוך מחיצה אחרת</span>');
  } else if (p.readThrough) {
    tags.push('<span class="chip ok">נקראת דרך הגיבוי</span>');
  } else if (p.unmounted) {
    tags.push('<span class="chip warn">לא מחוברת</span>');
  }
  if (p.found && p.readThrough) tags.push('<span class="chip ok">נקראת דרך הגיבוי</span>');
  if (p.bootable) tags.push('<span class="chip">אתחול</span>');
  if (!p.found && !p.scannable && p.fs !== 'Unknown') tags.push('<span class="chip">לא נתמכת לסריקה</span>');

  let bar = '<div class="part-bar-wrap"><div class="part-bar-text">נפח לא זמין</div></div>';
  if (p.used !== null && p.used !== undefined && p.size > 0) {
    const pct = Math.min(100, Math.round((p.used / p.size) * 100));
    const cls = pct >= 92 ? 'full' : pct >= 78 ? 'high' : '';
    bar = `<div class="part-bar-wrap">
             <div class="part-bar"><i class="${cls}" style="width:${pct}%"></i></div>
             <div class="part-bar-text">${formatSize(p.used)} בשימוש · ${pct}%</div>
           </div>`;
  }

  return `
    <div class="part ${p.found ? 'is-found' : ''}" data-disk="${disk.number}" data-part="${p.index}">
      ${letter}
      <div class="part-info">
        <div class="part-title">${name}</div>
        <div class="part-tags">${tags.join('')}</div>
      </div>
      ${bar}
      <div class="part-size">${formatSize(p.size)}<small>${esc(p.typeName)}</small></div>
      <div class="part-arrow">${Icon.chevron}</div>
    </div>`;
}

/// סריקות שנשמרו אוטומטית. נטענות אחרי רשימת הכוננים, כי "מחובר" נקבע לפיה.
async function renderRecentScans() {
  let scans;
  try { scans = await Bridge.call('scan.recent'); } catch { return; }
  const box = el('recent-scans');
  if (!box) return;
  if (!scans.length) { box.innerHTML = ''; return; }

  box.innerHTML = `
    <div class="recent-head">
      <div class="section-label">סריקות אחרונות</div>
      <div class="recent-clear" id="recent-clear">
        <button class="link-btn" data-clear="ask">ניקוי הרשימה</button>
      </div>
    </div>
    <div class="recent-list">
      ${scans.map((s) => `
        <div class="recent-row">
        <button class="recent-scan" data-path="${esc(s.path)}">
          <div class="recent-icon">${Icon.history}</div>
          <div class="recent-body">
            <div class="recent-title">${esc(s.title)} · ${esc(s.mode)}</div>
            <div class="recent-sub">${esc(s.savedAt)} · ${countFiles(s.files)},
              ${s.recoverable.toLocaleString('he-IL')} ${plural(s.recoverable, 'ניתן', 'ניתנים')} לשחזור${s.diskName ? ` · <bdi>${esc(s.diskName)}</bdi>` : ''}</div>
          </div>
          <div class="chips">
            ${s.partial ? '<span class="chip warn" title="נשמרה באמצע סריקה — לא כל המחיצה נסרקה">חלקית</span>' : ''}
            ${s.connected ? '<span class="chip ok">הכונן מחובר</span>'
                          : '<span class="chip" title="אפשר לעיין ברשימה, אבל לא לשחזר">הכונן לא מחובר</span>'}
          </div>
        </button>
        <button class="recent-remove" data-forget="${esc(s.path)}" title="הסרה מהרשימה" aria-label="הסרה מהרשימה">${Icon.close}</button>
        </div>`).join('')}
    </div>`;

  box.querySelectorAll('[data-path]').forEach((b) => { b.onclick = () => openSavedScan(b.dataset.path); });
  box.querySelectorAll('[data-forget]').forEach((b) => { b.onclick = () => forgetScans(b.dataset.forget); });

  // ניקוי הכול — אישור במקום, בלי חלון: שתי מילים ליד הקישור.
  const clear = el('recent-clear');
  clear.onclick = (e) => {
    const action = e.target.closest('[data-clear]')?.dataset.clear;
    if (action === 'ask') {
      clear.innerHTML = `<span>למחוק את כל הסריקות השמורות?</span>
        <button class="link-btn danger" data-clear="yes">מחיקה</button>
        <button class="link-btn" data-clear="no">ביטול</button>`;
    } else if (action === 'no') {
      clear.innerHTML = '<button class="link-btn" data-clear="ask">ניקוי הרשימה</button>';
    } else if (action === 'yes') {
      forgetScans(null);
    }
  };
}

/// הסרת סריקה שמורה מהרשימה (או כולן) — מוחקת את קובץ הסריקה בלבד, לא את הכונן ולא קבצים ששוחזרו.
async function forgetScans(path) {
  let error = null;
  try { await Bridge.call('scan.forget', path ? { path } : {}); }
  catch (err) { error = err; }
  await renderRecentScans();
  if (error) el('recent-scans')?.insertAdjacentHTML('afterbegin',
    errorNotice('לא ניתן להסיר את הסריקה', error));
}

/// פתיחת סריקה שמורה — מהרשימה, או מקובץ שבוחרים. הבחירה שנשמרה איתה חוזרת.
async function openSavedScan(path) {
  el('content').innerHTML =
    '<div class="loading"><div class="spinner"></div><p>פותח את הסריקה השמורה…</p></div>';

  try {
    const summary = await longCall('scan.load', path ? { path } : {});
    if (summary.cancelled) { renderDisks(); return; }
    State.summary = summary;
    State.selection = { count: summary.selection.count, bytes: summary.selection.bytes };
    await renderResults();
  } catch (err) {
    State.flash = errorNotice('לא ניתן לפתוח את הסריקה', err);
    renderDisks();
  }
}

/// התקנים ש-Windows מרגיש שהם מחוברים אך לא הצליח להפעיל — ולכן אינם כוננים ברשימה.
function renderFailedDevices() {
  if (!State.failed.length) return '';

  return `
    <div class="section-label" style="margin:26px 0 10px;text-transform:none">התקנים מחוברים ש-Windows לא הצליח להפעיל</div>
    ${State.failed.map((f) => `
      <section class="disk failed-device">
        <div class="disk-head">
          <div class="disk-icon is-failed">${Icon.alert}</div>
          <div class="disk-meta">
            <div class="disk-name">${esc(f.name)}</div>
            <div class="disk-sub">${esc(f.problem)}</div>
          </div>
          <div class="chips"><span class="chip danger">לא זמין לקריאה</span></div>
        </div>
        <div class="disk-note">${Icon.info}<span><b>מה אפשר לנסות:</b> ${esc(f.advice)}
          ${f.windowsName && f.windowsName !== f.name ? `<br><span class="faint">במנהל ההתקנים הוא מופיע בשם: ${esc(f.windowsName)}</span>` : ''}</span></div>
      </section>`).join('')}`;
}

/// כונן שענה באיחור: עדכון השורה שלו בלבד, בלי לאבד את מצב המסך.
Bridge.on('disks.updated', ({ disk }) => {
  const i = State.disks.findIndex((d) => d.number === disk.number);
  if (i < 0) return;
  State.disks[i] = disk;

  const card = document.querySelector(`.disk[data-disk-card="${disk.number}"]`);
  if (card && el('btn-refresh')) renderDisks();
});

/* =====================================================================
   סריקת כונן — איתור מחיצות שנמחקו או שאבדו
   ===================================================================== */

function openHuntPanel(disk) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">סריקת כונן</div>
        <div class="panel-sub">${esc(disk.name)} · ${formatSize(disk.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      <div class="strategy">
        <p>מחפשת מחיצות שנמחקו או שאבדו — אחרי מחיקה בטעות, התקנה מחדש,
        או כשהכונן מופיע פתאום "לא מאותחל".</p>
        <p>מחיצה שנמחקה מהטבלה עדיין על הכונן, עם כל הקבצים. מה שיימצא יופיע ברשימה,
        ואפשר יהיה להעתיק ממנו קבצים או להחזיר אותו לטבלה.</p>
      </div>
      ${notice('info', Icon.shield, 'קריאה בלבד — שום דבר לא נכתב לכונן',
        'בכונן גדול זה לוקח זמן. אפשר לעצור בכל רגע, ומה שנמצא עד אז יוצג.')}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start-hunt">${Icon.search}<span>התחלת סריקה</span></button>
      <button class="btn" id="btn-cancel-hunt">ביטול</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-hunt').onclick = closePanel;
  el('btn-start-hunt').onclick = () => startHunt(disk);
}

async function startHunt(disk) {
  closePanel();
  Steps.set(2);  // סריקה רצה — גם כשמה שמחפשים הוא מחיצה

  el('content').innerHTML = `
    <div class="scanning">
      <div class="scan-head">
        <div class="scan-icon">${Icon.search}</div>
        <div>
          <div class="page-title">סריקת כונן</div>
          <div class="page-desc">${esc(disk.name)} · ${formatSize(disk.size)}</div>
        </div>
      </div>

      <div class="progress-card">
        <div class="progress-stage">מחפש מחיצות בכל הכונן</div>
        <div class="progress-track"><div class="progress-fill" id="hunt-fill" style="width:0%"></div></div>
        <div class="progress-numbers">
          <span id="hunt-percent">0%</span>
          <span id="hunt-speed"></span>
        </div>
        <div class="kv" style="margin-top:18px">
          <div><dt>מחיצות שנמצאו</dt><dd id="hunt-found">0</dd></div>
          <div><dt>נקרא מהכונן</dt><dd id="hunt-done">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="hunt-elapsed">0:00</dd></div>
          <div><dt>מצב</dt><dd style="direction:rtl" id="hunt-state">פועל</dd></div>
        </div>
      </div>

      <div class="scan-actions">
        <button class="btn" id="btn-stop-hunt">${Icon.stop}<span>עצירה</span></button>
      </div>
    </div>`;

  el('btn-stop-hunt').onclick = () => {
    el('hunt-state').textContent = 'עוצר…';
    Bridge.call('disk.huntCancel');
  };

  setStatus('סורק את הכונן…');

  try {
    const r = await longCall('disk.hunt', { disk: disk.number });

    const i = State.disks.findIndex((d) => d.number === disk.number);
    if (i >= 0) State.disks[i] = r.disk;
    State.openDisks.add(disk.number);

    const stopped = r.cancelled ? ' (הסריקה נעצרה לפני סופה)' : '';

    // שאריות בתוך מחיצות אחרות אינן מוצגות — רק מוזכרות, כדי שיהיה ברור שנבדקו.
    const hidden = r.hidden > 0
      ? `נמצאו גם ${r.hidden === 1 ? 'שארית אחת' : r.hidden + ' שאריות'} של מערכות קבצים בתוך מחיצות אחרות —
         בדרך כלל קבצי ISO שנשמרו על הכונן. הן אינן מחיצות, ולכן לא הוצגו.`
      : '';
    State.flash = r.found > 0
      ? notice('ok-notice', Icon.check,
          `נמצאו ${r.found === 1 ? 'מחיצה אחת' : r.found + ' מחיצות'} ב${esc(disk.name)}${stopped}`,
          'הן מסומנות "נמצאה בסריקה". לחצו על מחיצה כדי להעתיק ממנה קבצים או להחזיר אותה לטבלה.',
          hidden)
      : notice('warn', Icon.info,
          `לא נמצאו מחיצות אבודות ב${esc(disk.name)}${stopped}`,
          'הקבצים עדיין חסרים? נסו <b>סריקה מתקדמת</b> על אחת המחיצות — היא מוצאת קבצים לפי סוגם.',
          hidden);

    Steps.set(1);  // מה שנמצא מוצג כמחיצות לבחירה
    renderDisks();
  } catch (err) {
    el('content').innerHTML = `
      <div class="page-head"><div>
        <div class="page-title">סריקת הכונן נכשלה</div>
        <div class="page-desc">${esc(disk.name)}</div>
      </div>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button></div>
      ${errorNotice('', err)}`;
    el('btn-home').onclick = loadDisks;
    setStatus('שגיאה');
  }
}

Bridge.on('hunt.progress', (p) => {
  const fill = el('hunt-fill');
  if (!fill) return;

  const pct = Math.min(100, p.percent || 0);
  fill.style.width = pct + '%';
  el('hunt-percent').textContent = pct.toFixed(1) + '%';
  el('hunt-speed').textContent = p.speed > 0 ? formatSize(p.speed) + '/שנייה' : '';
  el('hunt-found').textContent = (p.found || 0).toLocaleString('he-IL');
  el('hunt-done').textContent = `${formatSize(p.done)} מתוך ${formatSize(p.total)}`;
  el('hunt-elapsed').textContent = formatDuration(p.elapsed);
});

/* =====================================================================
   החזרת מחיצה שנמצאה לטבלת המחיצות
   ===================================================================== */

/// הצעה בלוחות של מחיצה שנמצאה.
function restoreOption(disk, part) {
  if (!part.found) return '';
  return `
    <div class="section-label" style="margin-top:18px">החזרת המחיצה</div>
    <button class="scan-opt subtle" id="btn-restore-part">
      <div class="scan-opt-icon">${Icon.layers}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">החזרת המחיצה לטבלת המחיצות</div>
        <div class="scan-opt-desc">כדי ש-Windows יראה אותה שוב, עם אות כונן.
        כותב לכונן — כדאי להעתיק קודם את הקבצים החשובים.</div>
      </div>
    </button>`;
}

async function openRestorePanel(disk, part) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">החזרת מחיצה לטבלה</div>
        <div class="panel-sub">${esc(partTitle(part))} · ${esc(disk.name)} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body"><div class="loading" style="height:180px">
      <div class="spinner"></div><p>בודק את טבלת המחיצות של הכונן…</p></div></div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;

  let plan;
  try {
    plan = await Bridge.call('partition.restorePlan', { disk: disk.number, part: part.index });
  } catch (err) {
    plan = { canRestore: false, error: err };
  }

  if (!plan.canRestore) {
    el('panel').querySelector('.panel-body').innerHTML = plan.error
      ? errorNotice('לא ניתן לבדוק אם אפשר להחזיר את המחיצה', plan.error)
      : `<div class="notice warn">${Icon.alert}<div>${esc(plan.explanation)}</div></div>`;
    el('panel').insertAdjacentHTML('beforeend', `
      <div class="panel-foot"><button class="btn" id="btn-back-restore">חזרה</button></div>`);
    el('btn-back-restore').onclick = () => openScanPanel(disk.number, part.index);
    return;
  }

  el('panel').querySelector('.panel-body').innerHTML = `
    <div class="notice ok-notice">${Icon.check}<div>${esc(plan.explanation)}</div></div>
    <div class="strategy"><p>${esc(plan.whatWillChange)}</p></div>
    ${notice('warn', Icon.alert, 'הפעולה כותבת לכונן',
      'יש במחיצה קבצים חשובים? העתיקו אותם קודם: סגרו את החלון ובחרו סריקה.',
      'לפני הכתיבה נשמר גיבוי של כל מה שעומד להשתנות בכונן. אם משהו ישתבש, התוכנה תחזיר את המצב הקודם אוטומטית.')}

    <div class="section-label">תיקיית גיבוי — על כונן אחר</div>
    <div class="target-row">
      <input type="text" id="restore-undo" readonly placeholder="לא נבחרה תיקייה">
      <button class="btn" id="btn-pick-restore-undo">${Icon.folder}<span>בחירה</span></button>
    </div>
    <div id="restore-status"></div>`;

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-restore" disabled>${Icon.layers}<span>החזרה לטבלה</span></button>
      <button class="btn" id="btn-back-restore">חזרה</button>
    </div>`);

  el('btn-back-restore').onclick = () => openScanPanel(disk.number, part.index);

  el('btn-pick-restore-undo').onclick = async () => {
    const { path } = await Bridge.call('repair.pickFolder', {}, 0);
    if (!path) return;
    el('restore-undo').value = path;
    el('btn-do-restore').disabled = false;
    el('restore-status').innerHTML =
      `<div class="notice ok-notice tiny-notice">${Icon.check}<div>הגיבוי יישמר כאן.</div></div>`;
  };

  el('btn-do-restore').onclick = async () => {
    const undoFolder = el('restore-undo').value;
    el('panel').querySelector('.panel-body').innerHTML =
      `<div class="loading" style="height:180px"><div class="spinner"></div><p>מחזיר את המחיצה לטבלה…</p></div>`;
    el('panel').querySelector('.panel-foot').innerHTML = '';

    let r;
    try {
      r = await Bridge.call('partition.restore', { disk: disk.number, part: part.index, undoFolder }, 0);
    } catch (err) {
      r = { succeeded: false, error: err };
    }

    const cls = r.succeeded ? 'ok-notice' : r.rolledBack ? 'warn' : 'danger';
    el('panel').querySelector('.panel-body').innerHTML = r.error
      ? errorNotice('החזרת המחיצה לא הושלמה', r.error)
      : `<div class="notice ${cls}">${r.succeeded ? Icon.check : Icon.alert}<div>${esc(r.message)}</div></div>`;
    el('panel').querySelector('.panel-foot').innerHTML =
      `<button class="btn btn-primary" id="btn-done-restore">סיום</button>`;
    el('btn-done-restore').onclick = () => { closePanel(); State.openDisks.add(disk.number); loadDisks(); };
  };
}

/* =====================================================================
   לוח בחירת סוג הסריקה
   ===================================================================== */

/// desc — מתי לבחור בסריקה, במילים פשוטות. time — מה נשמר וכמה זמן.
/// tech — מה הסריקה עושה בפועל; מוצג בריחוף ובמתקפל "מה ההבדל?".
const SCAN_MODES = [
  {
    id: 1, name: 'סריקה מהירה', icon: Icon.bolt,
    desc: 'נמחק לאחרונה? התחילו כאן.',
    time: 'שמות ותיקיות נשמרים · שניות עד דקות',
    tech: 'קוראת את טבלת הקבצים של המחיצה ומאתרת קבצים שנמחקו אך הרשומה שלהם עדיין קיימת. ' +
          'ב-NTFS זו טבלת ה-MFT, וב-FAT וב-exFAT — רשומות התיקיות.',
  },
  {
    id: 2, name: 'סריקה עמוקה', icon: Icon.layers,
    desc: 'המהירה לא מצאה? נסו את זו.',
    time: 'רוב השמות נשמרים · דקות עד שעה',
    tech: 'עוברת בנוסף על כל המחיצה ומחפשת רשומות יתומות — רשומות של קבצים שהטבלה ' +
          'כבר אינה מצביעה עליהן — וקוראת את יומני מערכת הקבצים.',
  },
  {
    id: 3, name: 'סריקה מתקדמת', icon: Icon.radar,
    desc: 'אחרי פירמוט או נזק כבד.',
    time: 'בלי שמות מקוריים · שעה ומעלה',
    tech: 'קוראת את הכונן כולו ומזהה קבצים לפי חתימות HEX — הבתים הקבועים שבתחילת כל סוג קובץ — ' +
          'בלי תלות במערכת הקבצים. ' +
          'עובדת גם אחרי פירמוט, אבל שמות ותיקיות אינם נשמרים.',
  },
];

/// הסבר טכני על כל סוגי הסריקה, מקופל כברירת מחדל.
function scanTechDetails() {
  return `
    <details class="scan-tech">
      <summary>מה ההבדל בין הסריקות?</summary>
      <dl>${SCAN_MODES.map((m) => `<dt>${m.name}</dt><dd>${m.tech}</dd>`).join('')}</dl>
    </details>`;
}

function findPart(diskNumber, partIndex) {
  const disk = State.disks.find((d) => d.number === diskNumber);
  if (!disk) return null;
  const part = disk.partitions.find((p) => p.index === partIndex);
  return part ? { disk, part } : null;
}

function partTitle(part) {
  return part.label || (part.letter ? 'כונן ' + part.letter : 'מחיצה ' + part.index);
}

function openScanPanel(diskNumber, partIndex) {
  const found = findPart(diskNumber, partIndex);
  if (!found) return;
  const { disk, part } = found;

  // מחיצה שמערכת הקבצים שלה אינה נקראת מקבלת קודם אבחון:
  // ייתכן שניתן לתקן אותה, וזה עדיף על שחזור קבצים בודדים.
  if (!part.scannable) {
    openRepairPanel(disk, part);
    return;
  }

  const options = SCAN_MODES.map((m) => {
    // סריקה מתקדמת אינה תלויה במערכת הקבצים ולכן תמיד זמינה.
    const blocked = !part.scannable && m.id !== 3;

    return `
    <button class="scan-opt" data-mode="${m.id}" title="${esc(m.tech)}"${blocked ? ' disabled' : ''}>
      <div class="scan-opt-icon">${m.icon}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">${m.name}${blocked ? '<span class="chip">לא זמין</span>' : ''}</div>
        <div class="scan-opt-desc">${m.desc}</div>
        <div class="scan-opt-time">${m.time}</div>
      </div>
    </button>`;
  }).join('');

  const fsNotice = !part.scannable
    ? notice('warn', Icon.alert, `מערכת הקבצים ${esc(part.fsLabel)} אינה נתמכת`,
        '<b>סריקה מתקדמת</b> עדיין תעבוד — היא אינה תלויה במערכת הקבצים.',
        'נתמכות: NTFS, exFAT, FAT32, FAT16 ו-FAT12.')
    : '';

  const readThroughNotice = part.readThrough
    ? notice('ok-notice', Icon.shield, 'המחיצה נקראת דרך עותק הגיבוי',
        'בחרו <b>סריקה מהירה</b> — יוצגו כל הקבצים עם השמות, ולא רק קבצים שנמחקו.',
        'תחילת המחיצה (מגזר האתחול) פגומה, והתוכנה קוראת אותה דרך עותק הגיבוי שלה — בזיכרון בלבד. שום דבר לא נכתב לכונן.')
    : '';

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · ${esc(part.fsLabel)} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${readThroughNotice}
      ${fsNotice}
      <div class="section-label">בחרו סוג סריקה</div>
      ${options}
      ${scanTechDetails()}
      ${imageOption(disk)}
      ${restoreOption(disk, part)}
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;

  document.querySelectorAll('.scan-opt[data-mode]:not([disabled])').forEach((btn) => {
    btn.onclick = () => showStrategy(disk, part, +btn.dataset.mode);
  });

  const imageBtn = el('btn-image-part');
  if (imageBtn) imageBtn.onclick = () => openImagePanel(disk, part);

  const restoreBtn = el('btn-restore-part');
  if (restoreBtn) restoreBtn.onclick = () => openRestorePanel(disk, part);
}

/// הצעה לגבות מחיצה לקובץ לפני הסריקה. בדיסק מגנטי היא מודגשת:
/// דיסק מגנטי שמתחיל להיכשל עלול לא לשרוד סריקה ארוכה.
function imageOption(disk) {
  if (disk.isImage || !disk.rawAccessible) return '';

  const hdd = disk.media === 'HardDisk';
  return `
    <div class="section-label" style="margin-top:18px">כונן חלש או שמשמיע רעשים?</div>
    <button class="scan-opt ${hdd ? '' : 'subtle'}" id="btn-image-part">
      <div class="scan-opt-icon">${Icon.copy}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">יצירת תמונה של המחיצה לפני הסריקה</div>
        <div class="scan-opt-desc">מעתיקים את המחיצה פעם אחת לקובץ על כונן אחר, וסורקים את ההעתק —
        בלי לשחוק כונן שעלול להפסיק לעבוד.</div>
      </div>
    </button>`;
}

/* =====================================================================
   אבחון ותיקון מחיצה
   ===================================================================== */

async function openRepairPanel(disk, part) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · ${esc(part.fsLabel)} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body"><div class="loading" style="height:180px">
      <div class="spinner"></div><p>מאבחן את המחיצה…</p></div></div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;

  let d;
  try {
    d = await Bridge.call('repair.diagnose', { disk: disk.number, part: part.index });
  } catch (err) {
    el('panel').querySelector('.panel-body').innerHTML = errorNotice('לא ניתן לבדוק את המחיצה', err);
    return;
  }

  const verdict = d.canRepair
    ? `<div class="notice ok-notice">${Icon.check}<div>${esc(d.summary)}</div></div>`
    : `<div class="notice warn">${Icon.alert}<div>${esc(d.summary)}</div></div>`;

  // כשיש עותק גיבוי, הדרך המומלצת היא להעתיק את הקבצים דרכו — עם שמות
  // ותיקיות, ובלי לכתוב לכונן. התיקון עצמו מוצע כאפשרות שנייה.
  const readThroughBlock = d.canRepair ? `
    <div class="section-label">אפשרות 1 — העתקת הקבצים בלי לגעת בכונן (מומלץ)</div>
    <div class="strategy">
      <p>Windows מבקש לפרמט כי תחילת המחיצה נפגעה, אבל הקבצים עצמם בדרך כלל שלמים.
      התוכנה תציג את כולם <b>עם השמות והתיקיות המקוריים</b>, להעתקה לכונן אחר.</p>
    </div>
    ${notice('info', Icon.shield, 'שום דבר לא נכתב לכונן',
      'התיקון קיים רק בזיכרון של התוכנה.',
      'התוכנה קוראת את המחיצה דרך עותק הגיבוי של תחילתה (מגזר האתחול), והכונן נשאר בדיוק כפי שהוא.')}` : '';

  const repairBlock = d.canRepair ? `
    <div class="section-label" style="margin-top:18px">אפשרות 2 — תיקון המחיצה</div>
    <div class="strategy">
      <p>${esc(d.whatWillChange)}</p>
    </div>
    ${disk.isImage
      ? notice('info', Icon.shield, 'התיקון ייכתב לקובץ התמונה בלבד',
          'הכונן המקורי לא נוגע בתהליך — זו הדרך הבטוחה ביותר לנסות תיקון.')
      : notice('warn', Icon.alert, 'הפעולה היחידה שכותבת לדיסק המקור',
          'אם התיקון יצליח, כל הקבצים יחזרו להיות נגישים כרגיל.',
          'אם התיקון ייכשל, התוכנה תחזיר את המצב הקודם אוטומטית מהגיבוי שנשמר לפני הכתיבה.')}` : '';

  el('panel').querySelector('.panel-body').innerHTML = `
    ${verdict}
    ${readThroughBlock}
    ${repairBlock}

    <div class="section-label" style="margin-top:18px">
      ${d.canRepair ? 'אפשרות 3 — ' : ''}חיפוש קבצים לפי סוגם
    </div>
    <div class="strategy">
      <p>סריקה מתקדמת עובדת גם במחיצה שאינה נקראת כלל. ${d.canRepair
        ? 'אבל הקבצים יימצאו בלי שמות — השתמשו בה רק אם אפשרות 1 לא מצאה את מה שחיפשתם.'
        : 'לא נמצא עותק גיבוי, ולכן זו הדרך להציל את הקבצים — בלי שמות מקוריים ובלי תיקיות.'}</p>
    </div>
    ${d.canRepair ? '' : notice('info', Icon.shield, 'קריאה בלבד',
      'הכונן לא ישתנה, והקבצים יועתקו לכונן אחר.')}`;

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      ${d.canRepair ? `<button class="btn btn-primary" id="btn-read-through">${Icon.copy}<span>העתקת קבצים עם שמות</span></button>` : ''}
      ${d.canRepair ? `<button class="btn" id="btn-repair">${Icon.wrench}<span>תיקון המחיצה</span></button>` : ''}
      <button class="btn ${d.canRepair ? '' : 'btn-primary'}" id="btn-recover-raw">${Icon.radar}<span>חיפוש לפי סוג קובץ</span></button>
      ${disk.isImage || !disk.rawAccessible ? '' : `<button class="btn" id="btn-image-raw">${Icon.copy}<span>יצירת תמונה</span></button>`}
      ${part.found ? `<button class="btn" id="btn-restore-raw">${Icon.layers}<span>החזרה לטבלה</span></button>` : ''}
      <button class="btn" id="btn-cancel-repair">ביטול</button>
    </div>`);

  const imageRaw = el('btn-image-raw');
  if (imageRaw) imageRaw.onclick = () => openImagePanel(disk, part);

  const restoreRaw = el('btn-restore-raw');
  if (restoreRaw) restoreRaw.onclick = () => openRestorePanel(disk, part);

  el('btn-cancel-repair').onclick = closePanel;
  el('btn-recover-raw').onclick = () => showStrategy(disk, part, 3);

  const repairBtn = el('btn-repair');
  if (repairBtn) repairBtn.onclick = () => confirmRepair(disk, part, d);

  const readBtn = el('btn-read-through');
  if (readBtn) readBtn.onclick = () => startReadThrough(disk, part);
}

/// הפעלת הקריאה דרך עותק הגיבוי, ומעבר ישיר לבחירת סריקה.
async function startReadThrough(disk, part) {
  el('panel').querySelector('.panel-body').innerHTML =
    `<div class="loading" style="height:180px"><div class="spinner"></div><p>קורא את המחיצה דרך עותק הגיבוי…</p></div>`;
  el('panel').querySelector('.panel-foot').innerHTML = '';

  let r;
  try {
    r = await Bridge.call('repair.readThrough', { disk: disk.number, part: part.index }, 0);
  } catch (err) {
    r = { ok: false, error: err };
  }

  if (!r.ok) {
    el('panel').querySelector('.panel-body').innerHTML = r.error
      ? errorNotice('לא ניתן לקרוא את המחיצה דרך עותק הגיבוי', r.error)
      : `<div class="notice danger">${Icon.alert}<div>${esc(r.message)}</div></div>`;
    el('panel').querySelector('.panel-foot').innerHTML =
      `<button class="btn" id="btn-back-rt">חזרה</button>`;
    el('btn-back-rt').onclick = () => openRepairPanel(disk, part);
    return;
  }

  // רשימת המחיצות מתעדכנת — המחיצה מופיעה עכשיו עם מערכת הקבצים שלה.
  State.openDisks.add(disk.number);
  await loadDisks();
  openScanPanel(disk.number, part.index);
}

/// אישור אחרון לפני כתיבה לדיסק, כולל בחירת תיקיית הגיבוי.
function confirmRepair(disk, part, diagnosis) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">תיקון מחיצה</div>
        <div class="panel-sub">${esc(partTitle(part))} · ${esc(disk.name)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('warn', Icon.alert, 'הפעולה כותבת לדיסק', esc(diagnosis.whatWillChange))}

      <div class="section-label">תיקיית גיבוי — על כונן אחר</div>
      <div class="target-row">
        <input type="text" id="undo-path" readonly placeholder="לא נבחרה תיקייה">
        <button class="btn" id="btn-pick-undo">${Icon.folder}<span>בחירה</span></button>
      </div>
      <div id="undo-status"></div>

      ${notice('info', Icon.shield, 'אפשר לחזור אחורה',
        'לפני הכתיבה יישמר כאן עותק של כל מה שעומד להשתנות בכונן. אם התיקון ייכשל, המצב הקודם יוחזר אוטומטית.',
        '', 'spaced')}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-repair" disabled>ביצוע התיקון</button>
      <button class="btn" id="btn-back-repair">חזרה</button>
    </div>`;

  el('panel-close').onclick = closePanel;
  el('btn-back-repair').onclick = () => openRepairPanel(disk, part);

  el('btn-pick-undo').onclick = async () => {
    const { path } = await Bridge.call('repair.pickFolder', {}, 0);
    if (!path) return;

    el('undo-path').value = path;
    el('btn-do-repair').disabled = false;
    el('undo-status').innerHTML =
      `<div class="notice ok-notice tiny-notice">${Icon.check}<div>הגיבוי יישמר כאן.</div></div>`;
  };

  el('btn-do-repair').onclick = () => runRepair(disk, part);
}

async function runRepair(disk, part) {
  const undoFolder = el('undo-path').value;

  el('panel').querySelector('.panel-body').innerHTML =
    `<div class="loading" style="height:180px"><div class="spinner"></div><p>מתקן את המחיצה…</p></div>`;
  el('panel').querySelector('.panel-foot').innerHTML = '';

  let r;
  try {
    r = await Bridge.call('repair.apply',
      { disk: disk.number, part: part.index, undoFolder }, 0);
  } catch (err) {
    r = { succeeded: false, error: err };
  }

  const cls = r.succeeded ? 'ok-notice' : r.rolledBack ? 'warn' : 'danger';
  const icon = r.succeeded ? Icon.check : Icon.alert;

  el('panel').querySelector('.panel-body').innerHTML = `
    ${r.error ? errorNotice('תיקון המחיצה לא הושלם', r.error)
      : `<div class="notice ${cls}">${icon}<div>${esc(r.message)}</div></div>`}
    ${r.undoFile ? `<div class="notice info tiny-notice">${Icon.info}
      <div>קובץ ביטול: <span style="direction:ltr;display:inline-block">${esc(r.undoFile)}</span></div>
    </div>` : ''}`;

  el('panel').querySelector('.panel-foot').innerHTML = `
    <button class="btn btn-primary" id="btn-done-repair">סיום</button>`;

  el('btn-done-repair').onclick = () => { closePanel(); loadDisks(); };
}

/* =====================================================================
   תמונת דיסק — יצירה ופתיחה
   ===================================================================== */

/// פתיחת קובץ תמונה והצגתו ברשימה כדיסק נוסף.
async function openImageFile(path) {
  try {
    const r = await Bridge.call('image.open', path ? { path } : {}, 0);
    if (r.number === null || r.number === undefined) return;
    State.openDisks.add(r.number);
    await loadDisks();

    const card = document.querySelector(`[data-close-image="${r.number}"]`);
    if (card) card.closest('.disk').scrollIntoView({ behavior: 'smooth', block: 'center' });
    setStatus('התמונה נפתחה — בחרו מחיצה מתוכה לסריקה');
  } catch (err) {
    el('content').insertAdjacentHTML('afterbegin',
      errorNotice('לא ניתן לפתוח את תמונת הדיסק', err));
  }
}

/// לוח יצירת תמונה — של דיסק שלם (part = null) או של מחיצה אחת.
function openImagePanel(disk, part) {
  const title = part ? partTitle(part) : disk.name;
  const size = part ? part.size : disk.size;
  const request = { disk: disk.number, part: part ? part.index : -1 };

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">יצירת תמונה</div>
        <div class="panel-sub">${esc(title)}${part ? ' · ' + esc(disk.name) : ''} · ${formatSize(size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      <div class="strategy">
        <p>${part ? 'המחיצה תועתק' : 'הדיסק כולו יועתק'} לקובץ על כונן אחר, וכל הסריקות
        ירוצו על ההעתק — הכונן המקורי כבר לא ייקרא.</p>
        <ol class="image-steps">
          <li><b>מעבר 1 — העתקה מהירה.</b> מדלגים על אזורים פגומים, ואוספים קודם את מה שנקרא בקלות.</li>
          <li><b>מעבר 2 — ניסיון חוזר.</b> חוזרים לאזורים שדולגו, וקוראים אותם בחלקים קטנים ככל האפשר.</li>
        </ol>
      </div>

      <div class="section-label">קובץ התמונה</div>
      <div class="target-row">
        <input type="text" id="image-path" readonly placeholder="לא נבחר קובץ">
        <button class="btn" id="btn-pick-image">${Icon.folder}<span>בחירה</span></button>
      </div>
      <div id="image-status"></div>

      ${notice('info', Icon.shield, 'קריאה בלבד מהכונן המקורי',
        'אזורים שלא ייקראו יתועדו בקובץ מפה לצד התמונה.',
        'אזור שלא נקרא נשמר בתמונה כאפסים, ואי אפשר להבחין בינו לבין אפסים אמיתיים. המפה מראה בדיוק מה חסר.',
        'spaced')}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start-image" disabled>${Icon.copy}<span>יצירת תמונה</span></button>
      <button class="btn" id="btn-cancel-image">ביטול</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-image').onclick = closePanel;

  el('btn-pick-image').onclick = async () => {
    const { path } = await Bridge.call('image.pickSave', request, 0);
    if (!path) return;

    el('image-path').value = path;
    const v = await Bridge.call('image.validate', { ...request, path });

    el('btn-start-image').disabled = !v.valid;
    el('image-status').innerHTML = !v.valid
      ? `<div class="notice danger tiny-notice">${Icon.alert}<div>${esc(v.error)}</div></div>`
      : v.existing ? existingImageChoice(v.existing)
      : `<div class="notice ok-notice tiny-notice">${Icon.check}
           <div>התמונה תתפוס ${formatSize(v.size)}. פנויים בכונן היעד ${formatSize(v.freeSpace)}.</div></div>`;
  };

  el('btn-start-image').onclick = () => {
    // בנתיב שיש בו תמונה קודמת — ההמשך הוא ברירת המחדל, כדי לא לקרוא שוב כונן גוסס.
    const mode = document.querySelector('input[name="image-mode"]:checked')?.value || 'new';
    startImaging(disk, part, {
      ...request, path: el('image-path').value,
      resume: mode !== 'new',
      retryUnreadable: mode === 'retry' || !!el('opt-retry-unreadable')?.checked,
    });
  };
}

/// בחירה מה לעשות עם תמונה קודמת באותו נתיב: להמשיך ממנה, לנסות שוב את
/// הסקטורים שלא נקראו, או להתחיל מחדש ולדרוס אותה.
function existingImageChoice(e) {
  if (!e.canResume) {
    return notice('warn', Icon.alert, 'בנתיב הזה כבר יש תמונה', esc(e.reason), '', 'tiny-notice');
  }

  const option = (value, checked, title, text) => `
    <label class="radio-opt">
      <input type="radio" name="image-mode" value="${value}"${checked ? ' checked' : ''}>
      <span><b>${title}</b><br><span class="faint">${text}</span></span>
    </label>`;

  // תמונה שהושלמה ונשארו בה רק סקטורים פגומים — אין מה "להמשיך", רק לנסות שוב.
  const onlyRetry = e.notCopied === 0;

  return `
    ${notice('info', Icon.info, 'בנתיב הזה יש תמונה קודמת של אותו מקור',
      onlyRetry
        ? `התמונה הושלמה, אבל ${formatSize(e.unreadable)} לא נקראו בה.`
        : `ההעתקה נעצרה לפני הסוף. עוד לא הועתקו: ${formatSize(e.notCopied)}.`,
      `התמונה הקודמת נוצרה מ: <bdi>${esc(e.source)}</bdi>. ודאו שזה אותו כונן.`, 'tiny-notice')}
    <div class="radio-group">
      ${onlyRetry
        ? option('retry', true, 'ניסיון חוזר באזורים שלא נקראו',
            'רק הם נקראים שוב. לפעמים כונן מצליח לקרוא אזור פגום בניסיון מאוחר יותר.')
        : option('resume', true, 'המשך מהנקודה שנעצרה',
            'רק מה שלא הועתק נקרא מהכונן. מה שכבר בתמונה נשאר כפי שהוא.')}
      ${option('new', false, 'התחלה מחדש', 'התמונה הקודמת תידרס, והכונן כולו ייקרא שוב.')}
    </div>
    ${!onlyRetry && e.unreadable > 0 ? `
      <label class="switch" style="margin-top:8px">
        <input type="checkbox" id="opt-retry-unreadable">
        <span>לנסות שוב גם את ${formatSize(e.unreadable)} שלא נקראו בפעם הקודמת</span>
      </label>` : ''}`;
}

async function startImaging(disk, part, request) {
  closePanel();
  const title = part ? partTitle(part) : disk.name;

  el('content').innerHTML = `
    <div class="scanning">
      <div class="scan-head">
        <div class="scan-icon">${Icon.copy}</div>
        <div>
          <div class="page-title">יצירת תמונה</div>
          <div class="page-desc">${esc(title)} ← <span class="ltr-inline">${esc(request.path)}</span></div>
        </div>
      </div>

      <div class="progress-card">
        <div class="progress-stage" id="img-stage">מתחיל…</div>
        <div class="progress-track"><div class="progress-fill" id="img-fill" style="width:0%"></div></div>
        <div class="progress-numbers">
          <span id="img-percent">0%</span>
          <span id="img-speed"></span>
        </div>

        <div class="kv" style="margin-top:18px">
          <div><dt>הועתק</dt><dd id="img-done">0 B</dd></div>
          <div><dt>טרם נקרא בהצלחה</dt><dd id="img-problems">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="img-elapsed">0:00</dd></div>
          <div><dt>מצב</dt><dd style="direction:rtl" id="img-state">פועל</dd></div>
        </div>
      </div>

      <div class="scan-actions">
        <button class="btn" id="btn-stop-image">${Icon.stop}<span>עצירה</span></button>
      </div>

      ${notice('info', Icon.info, 'אפשר לעצור בכל רגע',
        'מה שהועתק יישמר, וגם תמונה חלקית ניתנת לסריקה.')}
    </div>`;

  el('btn-stop-image').onclick = () => {
    el('img-state').textContent = 'עוצר…';
    Bridge.call('image.cancel');
  };

  setStatus('יוצר תמונה…');

  let r;
  try {
    r = await longCall('image.create', request);
  } catch (err) {
    el('content').innerHTML = `
      <div class="page-head"><div>
        <div class="page-title">יצירת התמונה נכשלה</div>
        <div class="page-desc">${esc(title)}</div>
      </div>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button></div>
      ${errorNotice('', err)}`;
    el('btn-home').onclick = loadDisks;
    setStatus('שגיאה');
    return;
  }

  showImageResult(title, r);
}

Bridge.on('image.progress', (p) => {
  const stage = el('img-stage');
  if (!stage) return;

  stage.textContent = p.stage;
  const pct = Math.min(100, p.percent || 0);
  el('img-fill').style.width = pct + '%';
  el('img-percent').textContent = pct.toFixed(1) + '%';
  el('img-speed').textContent = p.speed > 0 ? formatSize(p.speed) + '/שנייה' : '';
  el('img-done').textContent = p.pass === 1
    ? `${formatSize(p.done)} מתוך ${formatSize(p.total)}`
    : `ניסיון חוזר: ${formatSize(p.done)} מתוך ${formatSize(p.total)}`;
  el('img-problems').textContent = formatSize(p.problems);
  el('img-problems').classList.toggle('warn-text', p.problems > 0);
  el('img-elapsed').textContent = formatDuration(p.elapsed);
});

function showImageResult(title, r) {
  const cls = r.cancelled ? 'warn' : r.unreadable > 0 ? 'warn' : 'ok-notice';
  const icon = cls === 'ok-notice' ? Icon.check : Icon.alert;

  el('content').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">${r.cancelled ? 'התמונה נעצרה' : 'התמונה נוצרה'}</div>
        <div class="page-desc">${esc(title)}</div>
      </div>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button>
    </div>

    <div class="notice ${cls}">${icon}<div>${esc(r.message)}</div></div>

    <div class="progress-card">
      <div class="kv">
        <div><dt>גודל התמונה</dt><dd>${formatSize(r.size)}</dd></div>
        <div><dt>לא נקרא מהכונן</dt><dd>${r.unreadable > 0 ? formatSize(r.unreadable) : 'אין'}</dd></div>
        <div><dt>לא הועתק</dt><dd>${r.notCopied > 0 ? formatSize(r.notCopied) : 'אין'}</dd></div>
        <div><dt>משך</dt><dd>${formatDuration(r.duration)}</dd></div>
      </div>
      <div class="image-files">
        <div><span>תמונה</span><span class="ltr-inline">${esc(r.path)}</span></div>
        <div><span>מפה</span><span class="ltr-inline">${esc(r.map)}</span></div>
      </div>
    </div>

    <div class="scan-actions">
      <button class="btn btn-primary" id="btn-open-created">${Icon.open}<span>פתיחת התמונה לסריקה</span></button>
    </div>`;

  el('btn-home').onclick = loadDisks;
  el('btn-open-created').onclick = () => openImageFile(r.path);
  setStatus(r.cancelled ? 'התמונה נעצרה' : 'התמונה נוצרה');
}

/* =====================================================================
   תיקון קבצים שלא נפתחים — זיהוי לפי תוכן הקובץ (חתימות HEX)
   ===================================================================== */

const Doctor = { files: [] };

async function openDoctorPanel(source) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">תיקון קבצים שלא נפתחים</div>
        <div class="panel-sub">הבדיקה לפי מה שיש בתוך הקובץ, לא לפי השם שלו</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body" id="doctor-body"></div>
    <div class="panel-foot" id="doctor-foot"></div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;

  if (source && source.folder) {
    await diagnoseInto(() => Bridge.call('doctor.diagnoseFolder', { folder: source.folder }, 0));
  } else {
    renderDoctorEmpty();
  }
}

function renderDoctorEmpty() {
  el('doctor-body').innerHTML = `
    ${notice('info', Icon.shield, 'הקבצים המקוריים לא משתנים',
      'התיקון נכתב לעותק חדש, ונבדק שוב אחרי הכתיבה.',
      'הבדיקה משווה בין חתימת הפתיחה של כל קובץ (הבתים הראשונים שמזהים את סוגו), הסיומת שלו, ' +
      'והאורך שמבנה הקובץ מצהיר עליו. כל פער ביניהם הוא בעיה מזוהה.')}
    <div class="section-label">מה אפשר לתקן</div>
    <div class="strategy"><p>
      תחילת קובץ שנמחקה או נפגעה · נתונים מיותרים בסוף הקובץ · סוף קובץ חסר ·
      סיומת שגויה (למשל תמונה שנשמרה בשם ‎.doc) ·
      מסמך Word, Excel או PowerPoint (או ZIP) שלא נפתח — תוכן העניינים שלו נבנה מחדש.</p>
      <p>קובץ שחסרים בו נתונים, או שאינו תואם לשום פורמט מוכר, לא יתוקן — התוכנה לא ממציאה נתונים.</p></div>`;

  el('doctor-foot').innerHTML = `
    <button class="btn btn-primary" id="btn-doctor-pick">${Icon.file}<span>בחירת קבצים</span></button>
    <button class="btn" id="btn-doctor-close">סגירה</button>`;

  el('btn-doctor-pick').onclick = pickDoctorFiles;
  el('btn-doctor-close').onclick = closePanel;
}

async function pickDoctorFiles() {
  const { paths } = await Bridge.call('doctor.pickFiles', {}, 0);
  if (!paths || paths.length === 0) return;
  await diagnoseInto(() => Bridge.call('doctor.diagnose', { paths }, 0));
}

async function diagnoseInto(request) {
  el('doctor-body').innerHTML =
    `<div class="loading" style="height:180px"><div class="spinner"></div><p>בודק את הקבצים…</p></div>`;
  el('doctor-foot').innerHTML = '';

  try {
    const data = await request();
    Doctor.files = data.files || [];
    renderDoctorList();
  } catch (err) {
    el('doctor-body').innerHTML = errorNotice('לא ניתן לבדוק את הקבצים', err);
  }
}

function renderDoctorList() {
  const files = Doctor.files;
  const healthy = files.filter((f) => f.healthy).length;
  const fixable = files.filter((f) => !f.healthy && f.canRepair);
  const hopeless = files.length - healthy - fixable.length;

  const rows = files.map((f) => {
    const [cls, label] = f.healthy ? ['ok', 'תקין']
      : f.canRepair ? ['warn', 'ניתן לתקן'] : ['danger', 'לא ניתן לתקן'];

    const issues = f.issues.length
      ? `<ul class="doc-issues">${f.issues.map((i) =>
          `<li class="${i.fixable ? '' : 'nofix'}">${esc(i.description)}</li>`).join('')}</ul>`
      : '';

    return `
      <div class="doc-row">
        <div class="doc-top">
          <span class="doc-name" title="${esc(f.path)}"><bdi>${esc(f.name)}</bdi></span>
          <span class="doc-size">${formatSize(f.size)}</span>
          <span class="chip ${cls} tiny">${label}</span>
        </div>
        ${f.detected ? `<div class="doc-meta">זוהה: ${esc(f.detected)}</div>` : ''}
        ${issues}
      </div>`;
  }).join('');

  el('doctor-body').innerHTML = `
    <div class="doc-summary">
      <span><b class="ok-text">${healthy}</b> תקינים</span>
      <span class="sep">·</span>
      <span><b>${fixable.length}</b> ניתנים לתיקון</span>
      <span class="sep">·</span>
      <span><b class="danger-text">${hopeless}</b> לא ניתנים לתיקון</span>
    </div>
    <div class="doc-list">${rows || '<div class="empty small"><h3>אין קבצים</h3></div>'}</div>`;

  el('doctor-foot').innerHTML = `
    ${fixable.length ? `<button class="btn btn-primary" id="btn-doctor-fix">${Icon.wrench}<span>תיקון ${countFiles(fixable.length)}</span></button>` : ''}
    <button class="btn" id="btn-doctor-more">בחירת קבצים אחרים</button>
    <button class="btn" id="btn-doctor-close">סגירה</button>`;

  const fix = el('btn-doctor-fix');
  if (fix) fix.onclick = () => runDoctorRepair(fixable.map((f) => f.path));
  el('btn-doctor-more').onclick = pickDoctorFiles;
  el('btn-doctor-close').onclick = closePanel;
}

async function runDoctorRepair(paths) {
  const { path: output } = await Bridge.call('doctor.pickFolder', {}, 0);
  if (!output) return;

  el('doctor-body').innerHTML =
    `<div class="loading" style="height:180px"><div class="spinner"></div><p>מתקן ובודק מחדש…</p></div>`;
  el('doctor-foot').innerHTML = '';

  let data;
  try {
    data = await Bridge.call('doctor.repair', { paths, output }, 0);
  } catch (err) {
    el('doctor-body').innerHTML = errorNotice('התיקון לא הושלם', err);
    return;
  }

  const rows = data.results.map((r) => `
    <div class="doc-row">
      <div class="doc-top">
        <span class="doc-name"><bdi>${esc(r.name)}</bdi></span>
        <span class="chip ${r.healthyAfter ? 'ok' : r.succeeded ? 'warn' : 'danger'} tiny">
          ${r.healthyAfter ? 'תוקן — תקין' : r.succeeded ? 'תוקן חלקית' : 'לא תוקן'}</span>
      </div>
      ${r.applied.length ? `<ul class="doc-issues fixed">${r.applied.map((a) => `<li>${esc(a)}</li>`).join('')}</ul>` : ''}
      <div class="doc-meta">${esc(r.message)}</div>
    </div>`).join('');

  el('doctor-body').innerHTML = `
    ${notice('ok-notice', Icon.check, `${countFiles(data.repaired)} ${plural(data.repaired, 'נכתב', 'נכתבו')} אל:`,
      `<span style="direction:ltr;display:inline-block">${esc(data.output)}</span>`,
      'כל עותק מתוקן נבדק שוב אחרי הכתיבה, והתוצאה המוצגת היא של הבדיקה החוזרת. הקבצים המקוריים לא שונו.')}
    <div class="doc-list">${rows}</div>`;

  el('doctor-foot').innerHTML = `<button class="btn btn-primary" id="btn-doctor-done">סיום</button>`;
  el('btn-doctor-done').onclick = closePanel;
}

function closePanel() {
  el('overlay').hidden = true;
  el('panel').innerHTML = '';
  // סגירת לוח השחזור מחזירה לבחירת הקבצים.
  if (Steps.current === 4) Steps.set(3);
}

// בזמן פעולה ארוכה הלוח אינו נסגר בלחיצה בחוץ או ב-Escape: דוח השחזור
// נכתב לתוכו בסוף, ולוח שנסגר באמצע היה מאבד אותו.
el('overlay').addEventListener('mousedown', (e) => {
  if (e.target === el('overlay') && !Steps.busy) closePanel();
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !el('overlay').hidden && !Steps.busy) closePanel();
});

async function showStrategy(disk, part, modeId) {
  const mode = SCAN_MODES.find((m) => m.id === modeId);

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${mode.name}</div>
        <div class="panel-sub">${esc(partTitle(part))} · ${esc(disk.name)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body"><div class="loading" style="height:180px">
      <div class="spinner"></div><p>מחשב אסטרטגיית שחזור…</p></div></div>`;
  el('panel-close').onclick = closePanel;

  let profile;
  try {
    profile = await Bridge.call('scan.profile', { disk: disk.number, mode: modeId });
  } catch (err) {
    el('panel').querySelector('.panel-body').innerHTML = errorNotice('לא ניתן להכין את הסריקה', err);
    return;
  }

  const cls = profile.outlook >= 70 ? 'good' : profile.outlook >= 40 ? 'mid' : 'low';
  const word = profile.outlook >= 70 ? 'גבוהים' : profile.outlook >= 40 ? 'בינוניים' : 'נמוכים';
  const warning = profile.warning
    ? `<div class="notice warn">${Icon.alert}<div>${esc(profile.warning)}</div></div>` : '';

  el('panel').querySelector('.panel-body').innerHTML = `
    ${warning}
    <div class="section-label">אסטרטגיה שנבחרה אוטומטית</div>
    <div class="strategy">
      <p>${esc(profile.rationale)}</p>
      <div class="kv">
        <div><dt>גודל בלוק קריאה</dt><dd>${profile.blockSizeKb} KB</dd></div>
        <div><dt>ערוצי קריאה מקבילים</dt><dd>${profile.parallelism}</dd></div>
        <div><dt>סדר סריקה</dt><dd style="direction:rtl">${profile.sequential ? 'רציף' : 'חופשי'}</dd></div>
        <div><dt>סוג אמצעי אחסון</dt><dd style="direction:rtl">${esc(profile.mediaLabel)}</dd></div>
      </div>
      <div class="meter">
        <div class="meter-head">
          <span>הערכת סיכויי שחזור: <b>${word}</b></span>
          <span style="direction:ltr;color:var(--text-faint)">${profile.outlook}%</span>
        </div>
        <div class="meter-track"><div class="meter-fill ${cls}" style="width:${profile.outlook}%"></div></div>
      </div>
    </div>

    <label class="switch">
      <input type="checkbox" id="opt-include-existing"${part.readThrough ? ' checked' : ''}>
      <span>הצג גם קבצים קיימים, ולא רק קבצים שנמחקו</span>
    </label>

    ${notice('info', Icon.shield, 'קריאה בלבד מהדיסק המקור',
      'השחזור יתאפשר רק לכונן אחר.')}`;

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start">${Icon.bolt}<span>התחלת סריקה</span></button>
      <button class="btn" id="btn-back">חזרה</button>
    </div>`);

  el('btn-back').onclick = () => openScanPanel(disk.number, part.index);
  el('btn-start').onclick = () => startScan(disk, part, modeId, el('opt-include-existing').checked);
}

/* =====================================================================
   מסך 2 — סריקה מתבצעת
   ===================================================================== */

async function startScan(disk, part, modeId, includeExisting) {
  closePanel();
  Steps.set(2);
  State.scan = { disk, part, mode: modeId };
  State.selection = { count: 0, bytes: 0 };

  const mode = SCAN_MODES.find((m) => m.id === modeId);

  el('content').innerHTML = `
    <div class="scanning">
      <div class="scan-head">
        <div class="scan-icon">${mode.icon}</div>
        <div>
          <div class="page-title">${mode.name}</div>
          <div class="page-desc">${esc(partTitle(part))} · ${esc(disk.name)}</div>
        </div>
      </div>

      <div class="progress-card">
        <div class="progress-stage" id="scan-stage">מתחיל…</div>
        <div class="progress-track"><div class="progress-fill" id="scan-fill" style="width:0%"></div></div>
        <div class="progress-numbers">
          <span id="scan-percent">0%</span>
          <span id="scan-speed"></span>
        </div>

        <div class="kv" style="margin-top:18px">
          <div><dt>קבצים שנמצאו</dt><dd id="scan-files">0</dd></div>
          <div><dt>נקרא מהדיסק</dt><dd id="scan-bytes">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="scan-elapsed">0:00</dd></div>
          <div><dt>מצב</dt><dd style="direction:rtl" id="scan-state">פועל</dd></div>
        </div>
      </div>

      <div class="scan-actions">
        <button class="btn" id="btn-cancel-scan">${Icon.stop}<span>עצירת הסריקה</span></button>
      </div>

      ${notice('info', Icon.info, 'אפשר לעצור בכל רגע',
        'מה שנמצא עד אז יוצג, ואפשר יהיה לשחזר אותו.')}
    </div>`;

  el('btn-cancel-scan').onclick = () => {
    el('scan-state').textContent = 'עוצר…';
    Bridge.call('scan.cancel');
  };

  setStatus('סורק…');

  try {
    // ללא מגבלת זמן: סריקה עמוקה על דיסק גדול עשויה להימשך שעות.
    const summary = await longCall('scan.start', {
      disk: disk.number, part: part.index, mode: modeId, includeExisting,
    });

    State.summary = summary;
    await renderResults();
  } catch (err) {
    el('content').innerHTML = `
      <div class="page-head"><div>
        <div class="page-title">הסריקה נכשלה</div>
        <div class="page-desc">${esc(partTitle(part))}</div>
      </div>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button></div>
      ${errorNotice('', err)}`;
    el('btn-home').onclick = loadDisks;
    setStatus('שגיאה');
  }
}

Bridge.on('scan.progress', (p) => {
  const stage = el('scan-stage');
  if (!stage) return;

  stage.textContent = p.stage || '';

  const pct = p.percent === null || p.percent === undefined ? null : Math.min(100, p.percent);
  el('scan-fill').style.width = (pct === null ? 100 : pct) + '%';
  el('scan-fill').classList.toggle('indeterminate', pct === null);
  el('scan-percent').textContent = pct === null ? '' : pct.toFixed(1) + '%';

  el('scan-speed').textContent = p.speed > 0 ? formatSize(p.speed) + '/שנייה' : '';
  el('scan-files').textContent = (p.files || 0).toLocaleString('he-IL');
  el('scan-bytes').textContent = formatSize(p.bytes);
  el('scan-elapsed').textContent = formatDuration(p.elapsed);
});

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
          <input type="text" id="search-input" placeholder="חיפוש בשם קובץ…" autocomplete="off">
        </div>
      </div>

      ${(s.warnings || []).length ? notesHtml(s.warnings.map(esc)) : ''}

      <div class="results-grid">
        <aside class="tree" id="tree"></aside>
        <div class="filelist-wrap" id="filelist-wrap">
          <div class="list-tools">
            <label class="chk-all" title="סימון כל הקבצים ברשימה, גם אלה שלא נגללו"><input type="checkbox" id="chk-all"><span>הכל</span></label>
            <div class="cat-chips" id="cat-chips"></div>
            <label class="toggle small"><input type="checkbox" id="chk-recoverable"><span>רק ניתנים לשחזור</span></label>
            <div class="view-switch" role="group" aria-label="אופן התצוגה">
              <button class="icon-btn" data-view="list" title="רשימה" aria-label="רשימה">${Icon.list}</button>
              <button class="icon-btn" data-view="grid" title="גלריה" aria-label="גלריה">${Icon.grid}</button>
            </div>
          </div>
          <div class="filelist-head">
            <span></span>
            ${SORT_COLUMNS.map((c) => `<button class="col-sort col-${c.id}" data-sort="${c.id}">${c.label}</button>`).join('')}
          </div>
          <div class="list-banner" id="list-banner" hidden></div>
          <div class="filelist" id="filelist"></div>
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
  item.innerHTML = `<span class="tree-caret">${Icon.chevron}</span>
                    <input type="checkbox" class="tree-chk" title="סימון התיקייה וכל מה שבתוכה">
                    <span class="tree-icon">${Icon.folder}</span>
                    <span class="tree-label">${esc(label)}</span>`;

  const children = document.createElement('div');
  children.className = 'tree-children';
  children.hidden = true;

  let loaded = false;

  /// טעינת תיקיות המשנה פעם אחת, ופתיחת הענף.
  item.expand = async () => {
    document.querySelectorAll('.tree-item').forEach((n) => n.classList.remove('active'));
    item.classList.add('active');

    if (!loaded) {
      loaded = true;
      const data = await Bridge.call('scan.children', { path, evidence: State.showEvidence });
      for (const folder of data.folders) {
        children.appendChild(await makeTreeNode(folder.path, folder.name, depth + 1));
      }
      if (data.folders.length === 0) item.classList.add('leaf');
      Tree.refreshStates();
    }

    children.hidden = false;
    item.classList.add('open');

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
      children.hidden = true;
      item.classList.remove('open');
      return;
    }

    await item.expand();
  };

  node.appendChild(item);
  node.appendChild(children);
  return node;
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
  const prefs = { category: 'all', recoverableOnly: false, sort: 'name', desc: false, mode: 'list' };
  try { prefs.mode = localStorage.getItem('raf-view') === 'grid' ? 'grid' : 'list'; } catch (e) {}

  let target = null;       // { path, query }
  let total = 0;
  let pages = new Map();   // מספר עמוד ← מערך קבצים
  let pending = new Set(); // עמודים שבקשתם בדרך
  let byId = new Map();    // קבצים שנטענו, לתצוגה מקדימה ולסימון
  let generation = 0;      // תשובה מתצוגה קודמת נזרקת
  let activeId = null;

  /// התצוגה כפי שהמנוע מכיר אותה (ViewQuery).
  const view = () => ({
    path: target.path, query: target.query, evidence: State.showEvidence,
    category: prefs.category, recoverableOnly: prefs.recoverableOnly,
    sort: prefs.sort, desc: prefs.desc,
  });

  async function open(next) {
    if (next) target = next;
    if (!target) return;

    const gen = ++generation;
    total = 0;
    pages = new Map();
    pending = new Set();
    byId = new Map();

    const list = el('filelist');
    list.scrollTop = 0;

    const first = await Bridge.call('scan.list', { view: view(), offset: 0, count: PAGE });
    if (gen !== generation) return;

    total = first.total;
    store(0, first.files);
    applySelection(first.selection);
    renderChips(first.counts);
    renderHead();

    list.classList.toggle('is-grid', prefs.mode === 'grid');
    el('filelist-wrap').classList.toggle('is-grid', prefs.mode === 'grid');
    list.innerHTML = total === 0
      ? `<div class="empty small"><h3>אין קבצים להצגה</h3><p>${emptyReason()}</p></div>`
      : `<div class="vlist"><div class="vlist-rows"></div></div>`;

    const banner = el('list-banner');
    banner.hidden = !target.query;
    if (target.query) {
      banner.textContent = `נמצאו ${total.toLocaleString('he-IL')} תוצאות עבור "${target.query}"`;
    }

    render();
  }

  function emptyReason() {
    if (prefs.category !== 'all' || prefs.recoverableOnly) return 'אין קבצים שמתאימים לסינון.';
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

    if (prefs.mode === 'grid') Thumbs.fill(rows);
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
    return `<input type="checkbox"${f.selected ? ' checked' : ''}${f.recoverable ? '' : ' disabled'}>`;
  }

  function rowHtml(f, index) {
    return `
      <div class="frow${f.recoverable ? '' : ' unrecoverable'}${f.id === activeId ? ' active' : ''}"
           data-id="${f.id}" data-index="${index}">
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
      <div class="tile${f.recoverable ? '' : ' unrecoverable'}${f.id === activeId ? ' active' : ''}"
           data-id="${f.id}" data-index="${index}"${f.thumb && thumb === undefined ? ' data-thumb="1"' : ''}>
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
    open, attach, select,
    get: (id) => byId.get(id),
    get total() { return total; },
    render,
  };
})();

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
  if (p.kind === 'image') {
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
}

/* =====================================================================
   שחזור
   ===================================================================== */

function openRecoverPanel() {
  Steps.set(4);
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">שחזור קבצים</div>
        <div class="panel-sub">${countFiles(State.selection.count)} · ${formatSize(Math.max(0, State.selection.bytes))}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('warn', Icon.alert, 'יעד על כונן אחר בלבד',
        'שחזור לאותו כונן ידרוס קבצים שעוד לא שוחזרו.',
        'קובץ שנמחק עדיין יושב באזור שמסומן "פנוי". כל קובץ חדש שנכתב לאותו כונן עלול לתפוס בדיוק את האזור הזה. התוכנה חוסמת זאת אוטומטית.')}

      <div class="section-label">תיקיית יעד</div>
      <div class="target-row">
        <input type="text" id="target-path" readonly placeholder="לא נבחרה תיקייה">
        <button class="btn" id="btn-pick">${Icon.folder}<span>בחירה</span></button>
      </div>
      <div id="target-status"></div>

      <label class="switch" style="margin-top:16px">
        <input type="checkbox" id="opt-preserve" checked>
        <span>שמירה על מבנה התיקיות המקורי</span>
      </label>
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-recover" disabled>${Icon.save}<span>שחזור</span></button>
      <button class="btn" id="btn-cancel">ביטול</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel').onclick = closePanel;
  el('btn-pick').onclick = pickTarget;
  el('btn-do-recover').onclick = runRecovery;
}

async function pickTarget() {
  const { path } = await Bridge.call('recover.pickFolder', {}, 0);
  if (!path) return;

  el('target-path').value = path;
  const status = el('target-status');
  status.innerHTML = `<div class="target-check">בודק…</div>`;

  const check = await Bridge.call('recover.validate', { target: path });

  if (check.valid) {
    const enough = check.freeSpace >= State.selection.bytes;
    status.innerHTML = `
      <div class="notice ${enough ? 'ok-notice' : 'warn'} tiny-notice">
        ${enough ? Icon.check : Icon.alert}
        <div>${enough ? 'תיקיית היעד תקינה' : 'ייתכן שאין מספיק מקום פנוי'} ·
        פנוי: ${formatSize(check.freeSpace)}</div>
      </div>`;
    el('btn-do-recover').disabled = false;
  } else {
    status.innerHTML = `<div class="notice danger tiny-notice">${Icon.alert}<div>${esc(check.error)}</div></div>`;
    el('btn-do-recover').disabled = true;
  }
}

async function runRecovery() {
  const target = el('target-path').value;
  const preservePaths = el('opt-preserve').checked;

  el('panel').innerHTML = `
    <div class="panel-head"><div class="grow">
      <div class="panel-title">משחזר קבצים…</div>
      <div class="panel-sub" id="rec-file">מתחיל</div>
    </div></div>
    <div class="panel-body">
      <div class="progress-card">
        <div class="progress-track"><div class="progress-fill" id="rec-fill" style="width:0%"></div></div>
        <div class="progress-numbers"><span id="rec-count">0 / 0</span><span id="rec-bytes">0 B</span></div>
      </div>
    </div>
    <div class="panel-foot">
      <button class="btn" id="btn-stop-rec">${Icon.stop}<span>עצירה</span></button>
    </div>`;

  el('btn-stop-rec').onclick = () => Bridge.call('recover.cancel');

  try {
    const report = await longCall('recover.start', { target, preservePaths });
    showRecoveryReport(report);
  } catch (err) {
    el('panel').querySelector('.panel-body').innerHTML = errorNotice('השחזור נעצר', err);
    el('panel').querySelector('.panel-foot').innerHTML =
      `<button class="btn" id="btn-rec-close">סגירה</button>`;
    el('btn-rec-close').onclick = closePanel;
  }
}

Bridge.on('recover.progress', (p) => {
  const fill = el('rec-fill');
  if (!fill) return;
  fill.style.width = (p.percent || 0) + '%';
  el('rec-file').textContent = p.file || '';
  el('rec-count').textContent = `${p.done} / ${p.total}`;
  el('rec-bytes').textContent = formatSize(p.bytes);
});

function showRecoveryReport(r) {
  const failures = r.failures && r.failures.length
    ? `<div class="section-label" style="margin-top:18px">קבצים שנכשלו</div>
       <div class="fail-list">${r.failures.map((f) =>
         `<div class="fail-row"><b>${esc(f.file)}</b><span>${esc(f.reason)}</span></div>`).join('')}</div>`
    : '';

  const partial = r.partial && r.partial.length
    ? notice('warn', Icon.alert, `${countFiles(r.partial.length)} ${plural(r.partial.length, 'שוחזר', 'שוחזרו')} חלקית`,
        'הם הועברו לתיקייה <b>_חלקיים</b>, כדי שיהיה ברור על אילו קבצים לא לסמוך.',
        'חלק מהנתונים שלהם כבר נדרס, או שלא ניתן היה לקרוא אותם מהדיסק. ייתכן שלא ייפתחו כראוי.')
    : '';

  // הדוח מפרט כל קובץ — גם מה שנכשל — עם גיבוב SHA-256 של מה שנכתב.
  const reportNote = r.reportPath
    ? notice('info', Icon.file, 'נשמר דוח שחזור',
        `<span class="ltr-inline">${esc(r.reportPath.split('\\').pop())}</span> בתיקיית היעד — נפתח ב-Excel.`,
        'שורה לכל קובץ: הנתיב המקורי, לאן נכתב, איכות, תוצאה, וגיבוב SHA-256 של מה שנכתב — כדי לוודא בעתיד שהקובץ לא השתנה.')
    : '';

  el('panel').innerHTML = `
    <div class="panel-head"><div class="grow">
      <div class="panel-title">${r.cancelled ? 'השחזור נעצר' : 'השחזור הושלם'}</div>
      <div class="panel-sub">${formatDuration(r.duration)}</div>
    </div>
    <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button></div>
    <div class="panel-body">
      <div class="notice ${r.succeeded > 0 ? 'ok-notice' : 'warn'}">
        ${r.succeeded > 0 ? Icon.check : Icon.alert}
        <div><b>${countFiles(r.succeeded)} ${plural(r.succeeded, 'שוחזר', 'שוחזרו')} בהצלחה</b><br>
        ${formatSize(r.bytes)} נכתבו אל:<br>
        <span style="direction:ltr;display:inline-block">${esc(r.target)}</span></div>
      </div>
      ${partial}
      ${reportNote}
      ${r.empty > 0 ? notice('danger', Icon.alert, `${countFiles(r.empty)} לא ${plural(r.empty, 'נכתב', 'נכתבו')}`,
        'התוכן שלהם כבר לא קיים על הדיסק.',
        'אזור הנתונים שלהם מכיל אפסים בלבד. לא נוצר עבורם קובץ, כדי שלא יתקבלו קבצים ריקים שנראים תקינים.') : ''}
      ${r.failed > r.empty ? `<div class="notice danger">${Icon.alert}
        <div>${countFiles(r.failed - r.empty)} ${plural(r.failed - r.empty, 'נכשל', 'נכשלו')} מסיבות אחרות.</div></div>` : ''}
      ${failures}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-done">סיום</button>
      ${r.succeeded > 0 ? `<button class="btn" id="btn-open-target">${Icon.open}<span>פתיחת תיקיית היעד</span></button>` : ''}
      ${r.succeeded > 0 ? `<button class="btn" id="btn-check-recovered">${Icon.wrench}<span>בדיקת הקבצים ששוחזרו</span></button>` : ''}
    </div>`;

  el('panel-close').onclick = closePanel;
  el('btn-done').onclick = closePanel;

  const openTarget = el('btn-open-target');
  if (openTarget) {
    openTarget.onclick = () => Bridge.call('recover.openFolder', { path: r.target })
      .catch((err) => setStatus(err.message));
  }

  const check = el('btn-check-recovered');
  if (check) check.onclick = () => openDoctorPanel({ folder: r.target });
}

/* ---------------------------------------------------------- אתחול */

async function init() {
  try {
    const info = await Bridge.call('system.info');
    // השם הלועזי עבר לכאן משורת הכותרת, שבה נשאר רק השם העברי.
    el('status-version').textContent = `⁦Recovery Advanced Free⁩ · גרסה ${info.version}`;

    const chip = el('status-elevation');
    chip.textContent = info.elevated ? 'הרשאות מנהל' : 'ללא הרשאות מנהל';
    chip.className = 'status-chip ' + (info.elevated ? 'ok' : 'danger');

    // אבחון פריסה: אם אזור הציור אינו תואם לגודל החלון, זה ייראה כאן מיד.
    const drawn = `${window.innerWidth}×${window.innerHeight}`;
    const client = `${info.clientWidth}×${info.clientHeight}`;
    const ratio = window.devicePixelRatio.toFixed(2);
    const mismatch = Math.abs(window.innerWidth * window.devicePixelRatio - info.clientWidth) > 24;

    // השבב מוצג רק כשיש תקלה. כשהכול תקין אין למשתמש מה לעשות עם המספרים,
    // והם נשמרים ממילא בקובץ האבחון.
    const diag = el('status-diag');
    diag.hidden = !mismatch;
    if (mismatch) {
      diag.textContent = `בעיית תצוגה · חלון ${client} · ציור ${drawn} · יחס ${ratio}`;
      diag.className = 'status-chip danger';
      diag.title = 'אי-התאמה בין גודל החלון לאזור הציור. פירוט בקובץ RAF-diagnostics.txt בתיקיית הזמניים.';
    }
  } catch { /* ממשיכים גם ללא פרטי מערכת */ }

  // כתיבת קובץ אבחון, לאיתור בעיות פריסה ללא תלות בקריאת מספרים מהמסך.
  reportDiagnostics();

  await loadDisks();
}

/// שליחת מדדי העמוד למנוע, שיכתוב אותם לקובץ אבחון.
function reportDiagnostics() {
  const root = document.documentElement;
  const app = document.querySelector('.app');

  Bridge.call('diag.report', {
    innerSize: `${window.innerWidth}x${window.innerHeight}`,
    outerSize: `${window.outerWidth}x${window.outerHeight}`,
    devicePixelRatio: window.devicePixelRatio,
    screenSize: `${window.screen.width}x${window.screen.height}`,
    screenAvail: `${window.screen.availWidth}x${window.screen.availHeight}`,
    documentClient: `${root.clientWidth}x${root.clientHeight}`,
    bodyClient: `${document.body.clientWidth}x${document.body.clientHeight}`,
    appRect: app ? `${Math.round(app.getBoundingClientRect().width)}x${Math.round(app.getBoundingClientRect().height)}` : 'missing',
    visualViewport: window.visualViewport
      ? `${Math.round(window.visualViewport.width)}x${Math.round(window.visualViewport.height)} scale ${window.visualViewport.scale}`
      : 'unavailable',
    direction: getComputedStyle(root).direction,
    userAgent: navigator.userAgent,
  }).catch(() => { /* אבחון אינו קריטי */ });
}

init();
