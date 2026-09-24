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

/// זמן משוער שנותר, במילים. הקצב מוחלק (ממוצע נע), כדי שהמספר לא יקפוץ בכל
/// דיווח — וכשאין עדיין מספיק נתונים, אומרים זאת במקום לנחש.
const Eta = (() => {
  const smoothed = new Map();

  function words(seconds) {
    if (seconds < 60) return 'פחות מדקה';
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return minutes === 1 ? 'כדקה' : `כ-${minutes} דקות`;
    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    const h = hours === 1 ? 'כשעה' : hours === 2 ? 'כשעתיים' : `כ-${hours} שעות`;
    return rest < 5 ? h : `${h} ו-${rest} דקות`;
  }

  /// key מזהה את הפעולה: ערך מוחלק אחד לכל מסך התקדמות. האחוז הוא של השלב
  /// הנוכחי, והזמן שחלף — של הפעולה כולה; לכן הקצב נמדד מתחילת השלב: כשהאחוז
  /// יורד (שלב חדש בסריקה עמוקה, ניסיון חוזר בתמונה) — המדידה מתחילה מחדש.
  function text(key, percent, elapsed) {
    if (percent === null || percent === undefined) {
      smoothed.delete(key);
      return 'מחשב…';
    }
    if (percent >= 100) return '—';

    let s = smoothed.get(key);
    // פעולה חדשה (הזמן חזר לאחור) או שלב חדש (האחוז ירד) — מדידה חדשה.
    if (!s || elapsed < s.lastTime || percent < s.lastPercent - 1)
      s = { baseTime: elapsed, basePercent: percent, value: undefined };
    s.lastPercent = percent;
    s.lastTime = elapsed;
    smoothed.set(key, s);

    const done = percent - s.basePercent;
    const time = elapsed - s.baseTime;
    if (done < 1 || time < 8) return 'מחשב…';

    // החלקה לפי זמן (כ-10 שניות) ולא לפי מספר הדיווחים, שמשתנה בין פעולות.
    // הערך הקודם "מתקדם" בזמן שעבר מאז, לפני שמשקללים אותו עם החדש.
    // תיקון לאופטימיות: נמדד על סריקה מלאה של דיסק-און-קי — הקצב יורד לאורך הסריקה
    // (64 ← 46MB/שנייה בממוצע) וההערכה בדקה הראשונה יצאה קצרה בכ-30% מהזמן בפועל.
    // התיקון גדול בהתחלה ונעלם לקראת הסוף, כשהקצב כבר ידוע.
    const raw = time * (100 - percent) / done * (1 + 0.3 * (1 - percent / 100));
    if (s.value === undefined) s.value = raw;
    else {
      const dt = Math.max(0, elapsed - s.valueAt);
      const keep = Math.exp(-dt / 10);
      s.value = Math.max(0, s.value - dt) * keep + raw * (1 - keep);
    }
    s.valueAt = elapsed;
    return words(s.value);
  }

  return { text };
})();

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
  pause: '<svg viewBox="0 0 24 24"><rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/></svg>',
  play: '<svg viewBox="0 0 24 24"><path d="M16 5v14L5 12z"/></svg>',
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
  lock: '<svg viewBox="0 0 24 24"><rect x="4.5" y="10.5" width="15" height="10.5" rx="2"/><path d="M8 10.5V7a4 4 0 0 1 8 0v3.5"/><path d="M12 15v2"/></svg>',
  help: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M9.5 9.2a2.6 2.6 0 0 1 5 .9c0 1.7-2.5 2.2-2.5 3.9"/><path d="M12 17h.01"/></svg>',
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
el('btn-tray').onclick = () => Bridge.call('window.toTray');
el('btn-max').onclick = () => Bridge.call('window.toggleMaximize');
el('btn-close').onclick = () => Bridge.call('window.close');

/* ------------------------------------------------------- שאלות נפוצות */

/// שאלות נפוצות — בשכבה משלהן, כך שאפשר לפתוח אותן גם באמצע סריקה או
/// מעל לוח פתוח, בלי לאבד אותו.
const Help = (() => {
  const FAQ = [
    ['מה אסור לעשות עכשיו?', `
      <ul>
        <li><b>לא לשמור שום דבר</b> על הכונן שממנו משחזרים — לא קבצים, לא תוכנות. כל קובץ חדש עלול לדרוס קבצים שעוד אפשר להציל.</li>
        <li><b>לא לפרמט</b>, גם אם Windows מבקש.</li>
        <li><b>לא להריץ "בדיקת שגיאות"</b> (chkdsk) — היא "מתקנת" על ידי מחיקה של מה שלא מסתדר לה.</li>
        <li><b>לשחזר תמיד לכונן אחר</b> — התוכנה לא תאפשר לשחזר לאותו כונן.</li>
      </ul>`],
    ['באיזו סריקה להתחיל?', `
      <ol>
        <li><b>סריקה מהירה</b> — שניות עד דקות. מוצאת קבצים שנמחקו לאחרונה, עם השמות והתיקיות.</li>
        <li>לא מצאה? <b>סריקה עמוקה</b> — יותר זמן, ומוצאת גם קבצים ישנים יותר. רוב השמות נשמרים.</li>
        <li>אחרי פירמוט או נזק כבד: <b>סריקה מתקדמת</b> — מחפשת קבצים לפי התוכן שלהם, בכל הכונן. היא מוצאת הכי הרבה, אבל בלי שמות ותיקיות.</li>
      </ol>`],
    ['Windows אומר שצריך לפרמט את הכונן. מה עושים?', `
      לוחצים <b>ביטול</b>. ברוב המקרים הקבצים עדיין שם — רק תחילת המחיצה ניזוקה.
      ברשימת הכוננים לוחצים על המחיצה, והתוכנה בודקת מה קרה ומציעה את הדרך הבטוחה: קודם העתקת הקבצים עם השמות, בלי לכתוב לכונן,
      ורק אחר כך, אם רוצים, תיקון של המחיצה עצמה.`],
    ['למה לקבצים מהסריקה המתקדמת אין שמות?', `
      השם והתיקייה של קובץ שמורים בטבלה של מערכת הקבצים, לא בתוך הקובץ עצמו. אחרי פירמוט הטבלה הזו נמחקת,
      והסריקה המתקדמת מוצאת את הקבצים לפי התוכן שלהם — ולכן הם מקבלים מספר במקום שם, ומסודרים בתיקיות לפי הסוג.
      כדאי לעבור עליהם בתצוגת הגלריה.`],
    ['קובץ ששוחזר לא נפתח, או נפתח חלקית. למה?', `
      כנראה שחלק מהמקום שהקובץ תפס כבר נכתב מחדש על ידי קובץ אחר. מה אפשר לעשות:
      <ul>
        <li>ברשימת הכוננים: <b>תיקון קבצים שלא נפתחים</b> — גוררים את הקבצים לחלון.</li>
        <li>סרטון שלא מתנגן אפשר לתקן בעזרת סרטון תקין שצולם באותו מכשיר.</li>
        <li>בתמונה פגומה נשמרת לפעמים גרסה קטנה ושלמה שלה, ליד הקובץ.</li>
      </ul>`],
    ['מה אומרת האיכות שליד כל קובץ?', `
      <ul>
        <li><b>מצוין</b> — המקום שהקובץ תפס עדיין פנוי. הוא צפוי לחזור במלואו.</li>
        <li><b>טוב</b> — ייתכן שחלק קטן נדרס.</li>
        <li><b>פגום חלקית</b> — חלק ניכר נדרס. הקובץ יחזור, אבל כנראה פגום.</li>
        <li><b>לא ניתן לשחזור</b> — ידוע שהקובץ היה קיים, אבל לא נשאר מידע איפה התוכן שלו.</li>
      </ul>`],
    ['למה בכונן SSD כמעט לא מוצאים קבצים שנמחקו?', `
      רוב כונני ה-SSD מוחקים בעצמם את התוכן של קבצים שנמחקו, זמן קצר אחרי המחיקה (התכונה נקראת TRIM).
      אחרי שזה קרה, אף תוכנה לא יכולה להחזיר אותם. ליד כונן כזה מופיע השבב "TRIM פעיל".
      בדיסק-און-קי, בכרטיס זיכרון ובדיסק קשיח רגיל זה בדרך כלל לא קורה.`],
    ['הכונן איטי, משמיע רעשים או נתקע', `
      זה סימן שהכונן נכשל, וכל קריאה נוספת עלולה להחמיר את המצב. במקום לסרוק אותו שוב ושוב:
      לוחצים <b>יצירת תמונת דיסק</b> — התוכנה מעתיקה את הכונן פעם אחת לקובץ על כונן אחר, קודם את מה שנקרא בקלות,
      ואז סורקים את ההעתק כמה שרוצים. אם הכונן מדווח על מצב הבריאות שלו, הוא מופיע ליד שמו.`],
    ['אפשר לעצור סריקה ולהמשיך אחר כך?', `
      כן. בסריקה מתקדמת יש כפתור <b>השהיה</b>, ואפשר להמשיך גם אחרי שסוגרים את התוכנה — או אם הכונן נותק באמצע.
      כל סריקה נשמרת אוטומטית, ומופיעה ב<b>סריקות אחרונות</b> במסך הכוננים.`],
    ['אפשר לשחזר מהטלפון?', `
      מהזיכרון הפנימי של הטלפון — לא. טלפונים לא מאפשרים למחשב לקרוא את הזיכרון שלהם כמו כונן.
      אם בטלפון יש <b>כרטיס זיכרון</b>, מוציאים אותו ומחברים למחשב דרך קורא כרטיסים — ואותו אפשר לסרוק.`],
    ['הכונן נעול ב-BitLocker', `
      קודם פותחים את הנעילה ב-Windows (לחיצה כפולה על הכונן בסייר הקבצים, עם הסיסמה או מפתח השחזור),
      ואז לוחצים על המחיצה ברשימה ובוחרים <b>פתיחה לסריקה</b>. בלי הסיסמה או המפתח אין דרך לקרוא את הקבצים.`],
    ['יש לי גיבוי של Windows או מכונה וירטואלית (קובץ VHD או VHDX)', `
      במסך הכוננים לוחצים <b>פתיחת תמונת דיסק</b> ובוחרים את הקובץ. הוא נפתח ככונן נוסף ברשימה —
      בלי לחבר אותו ל-Windows ובלי לכתוב אליו — ואפשר לסרוק ולשחזר ממנו כמו מכל כונן.`],
    ['קיצורי מקלדת', `
      במסך בחירת הקבצים:
      <ul>
        <li><b>חיצים</b> — מעבר בין הקבצים (בגלריה גם ימינה ושמאלה). <b>Page Up / Page Down</b>, <b>Home / End</b> — קפיצה.</li>
        <li><b>אנטר</b> — תצוגה מקדימה של הקובץ.</li>
        <li><b>רווח</b> — סימון הקובץ לשחזור, או ביטול הסימון.</li>
        <li><b>קונטרול + A</b> — סימון כל הקבצים ברשימה, ובלחיצה נוספת ביטול.</li>
        <li><b>קונטרול + F</b> — חיפוש לפי שם. <b>יציאה (Esc)</b> מנקה את החיפוש.</li>
      </ul>
      ובכל מקום: <b>F1</b> — השאלות האלה.`],
    ['האם התוכנה כותבת משהו לכונן?', `
      לא, חוץ משתי פעולות שמבקשים במפורש: <b>תיקון המחיצה</b> ו<b>החזרה לטבלה</b> של מחיצה שנמחקה.
      לפני כל אחת מהן התוכנה שומרת גיבוי של מה שהיא משנה, ומחזירה אותו אוטומטית אם משהו נכשל.
      סריקה, שחזור ויצירת תמונת דיסק רק קוראים מהכונן.`],
  ];

  function open() {
    el('help-panel').innerHTML = `
      <div class="panel-head">
        <div class="grow">
          <div class="panel-title">שאלות נפוצות</div>
          <div class="panel-sub">לחצו על שאלה כדי לראות את התשובה</div>
        </div>
        <button class="panel-close" id="help-close" aria-label="סגירה">${Icon.close}</button>
      </div>
      <div class="panel-body">
        <div class="faq">
          ${FAQ.map(([q, a]) => `<details><summary>${q}</summary><div class="faq-a">${a}</div></details>`).join('')}
        </div>
      </div>`;
    el('help-overlay').hidden = false;
    el('help-close').onclick = close;
  }

  function close() {
    el('help-overlay').hidden = true;
    el('help-panel').innerHTML = '';
  }

  const isOpen = () => !el('help-overlay').hidden;

  el('btn-help').innerHTML = Icon.help;
  el('btn-help').onclick = () => (isOpen() ? close() : open());
  el('help-overlay').addEventListener('mousedown', (e) => {
    if (e.target === el('help-overlay')) close();
  });
  // Escape סוגר קודם את השאלות, ורק בלחיצה נוספת את הלוח שמתחתן.
  document.addEventListener('keydown', (e) => {
    if (e.key === 'F1') { e.preventDefault(); isOpen() ? close() : open(); }
    else if (e.key === 'Escape' && isOpen()) { e.stopImmediatePropagation(); close(); }
  }, true);

  return { open, close };
})();

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
  health: {},           // בריאות הכוננים (SMART), לפי diskKey — מגיעה אחרי הרשימה
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

/// זהות כונן בין רענונים. מספר הדיסק ב-Windows משתנה כשמנתקים ומחברים, ולכן לא הוא.
const diskKey = (d) => `${d.name}|${d.size}`;

/// quiet — רענון במקום: הרשימה נשארת על המסך, רק סמל הרענון מסתובב, והכונן
/// שנוסף מודגש לרגע. כך נראים חיבור כונן וכפתור "רענון"; מסך טעינה מלא —
/// רק כשאין עדיין רשימה להציג.
async function loadDisks(options = {}) {
  const quiet = !!options.quiet && !!el('btn-refresh');
  Steps.set(1);

  if (quiet) el('btn-refresh').classList.add('is-busy');
  else el('content').innerHTML =
    '<div class="loading"><div class="spinner"></div><p>סורק את אמצעי האחסון במערכת…</p></div>';

  const before = new Set(State.disks.map(diskKey));

  try {
    // ברענון שקט הרשימה מגיעה לרוב תוך עשיריות שנייה — מהר מכדי לראות שמשהו
    // קרה. סיבוב אחד מלא של הסמל (0.8 שניות) מראה שהרשימה אכן נבדקה מחדש.
    const [data] = await Promise.all([
      Bridge.call('disks.list'),
      quiet ? new Promise((r) => setTimeout(r, 800)) : null,
    ]);
    State.disks = data.disks || [];
    State.failed = data.failed || [];
    State.elevated = !!data.elevated;

    const scroll = el('content').scrollTop;
    renderDisks();
    loadHealth();
    if (!quiet) return;

    el('content').scrollTop = scroll;
    State.disks.filter((d) => !before.has(diskKey(d))).forEach((d) =>
      document.querySelector(`.disk[data-disk-card="${d.number}"]`)?.classList.add('is-new'));
  } catch (err) {
    if (quiet) {
      el('btn-refresh')?.classList.remove('is-busy');
      setStatus('לא ניתן לרענן את רשימת הכוננים — ' + err.message);
      return;
    }
    el('content').innerHTML =
      errorNotice('לא ניתן לקרוא את רשימת הכוננים', err);
    setStatus('שגיאה');
  }
}

/* ---------- בריאות הכוננים (SMART) ---------- */

/// הבריאות נקראת אחרי הרשימה: כונן גוסס עלול להתעכב בתשובה, והרשימה לא מחכה לו.
async function loadHealth() {
  let list;
  try { list = await Bridge.call('disks.health'); } catch { return; }

  State.health = {};
  for (const h of list) {
    const disk = State.disks.find((d) => d.number === h.disk);
    if (disk) State.health[diskKey(disk)] = h;
  }

  for (const disk of State.disks) {
    const chip = document.querySelector(`[data-health-chip="${disk.number}"]`);
    const note = document.querySelector(`[data-health-note="${disk.number}"]`);
    if (chip) chip.innerHTML = healthChip(disk);
    if (note) {
      note.innerHTML = healthNote(disk);
      note.querySelector('[data-image-disk]')?.addEventListener('click', () => openImagePanel(disk, null));
    }
  }
}

const healthOf = (disk) => State.health[diskKey(disk)];

/// פרטי הבריאות במילים — לריחוף על השבב.
function healthDetails(h) {
  const parts = [];
  if (h.temperature != null) parts.push(`טמפרטורה ${h.temperature}°`);
  if (h.powerOnHours != null) parts.push(`${h.powerOnHours.toLocaleString('he-IL')} שעות פעולה`);
  if (h.percentUsed != null) parts.push(`${h.percentUsed}% מאורך החיים נוצלו`);
  if (h.reallocated != null) parts.push(`${h.reallocated.toLocaleString('he-IL')} סקטורים שהוחלפו`);
  if (h.pending != null) parts.push(`${h.pending.toLocaleString('he-IL')} סקטורים שאינם נקראים`);
  return parts.join(' · ');
}

function healthChip(disk) {
  const h = healthOf(disk);
  if (!h) return '';
  const [cls, text] = h.level === 'Bad' ? ['danger', 'הכונן בסכנה']
    : h.level === 'Caution' ? ['warn', 'סימני שחיקה'] : ['ok', 'בריאות תקינה'];
  const title = [...h.problems, healthDetails(h)].filter(Boolean).join('\n');
  return `<span class="chip ${cls}" title="${esc(title)}">${text}</span>`;
}

/// אזהרה מתחת לכונן שמראה סימני כשל — עם ההמלצה ליצור תמונה ולסרוק ממנה.
function healthNote(disk, withButton = true) {
  const h = healthOf(disk);
  if (!h || h.level === 'Good') return '';
  const bad = h.level === 'Bad';
  const button = withButton && disk.rawAccessible
    ? ` <button class="link-btn" data-image-disk="${disk.number}">יצירת תמונה של הכונן</button>` : '';
  return `<div class="disk-note ${bad ? 'danger' : 'warn'}">${Icon.alert}<span>
    <b>${bad ? 'הכונן מראה סימני כשל' : 'הכונן מראה סימני שחיקה'}:</b> ${esc(h.problems.join('; '))}.
    ${bad
      ? 'מומלץ ליצור קודם תמונה של הכונן — להעתיק אותו פעם אחת לקובץ — ולסרוק מהתמונה: כל קריאה נוספת מהכונן עלולה להחמיר את מצבו.'
      : 'כדאי לשחזר את הקבצים החשובים בהקדם. אם הסריקה נתקעת או איטית מאוד — עדיף ליצור תמונה של הכונן ולסרוק ממנה.'}${button}
  </span></div>`;
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
        <button class="btn" id="btn-undo-repair">${Icon.back}<span>ביטול תיקון קודם</span></button>
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

  el('btn-refresh').onclick = () => loadDisks({ quiet: true });
  el('btn-doctor').onclick = () => openDoctorPanel();
  el('btn-undo-repair').onclick = () => openUndoPanel();
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
    // מחיצת "שמור למערכת" (MSR) ריקה מלכתחילה, ואינה מחיצה ש-Windows מבקש לפרמט.
    const raw = disks.flatMap((d) => d.partitions
      .filter((p) => !p.scannable && !p.found && p.typeName !== 'שמור למערכת')
      .map((p) => [d, p]));
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
  panel.querySelector('[data-guide-refresh]')?.addEventListener('click', () => { closePanel(); loadDisks({ quiet: true }); });
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

/// כונן חובר או נותק: רשימת הכוננים מתעדכנת מעצמה — אבל רק כשהיא מוצגת ואין
/// חלונית פתוחה, כדי לא לקטוע סריקה, בחירת קבצים או הדרכה באמצע.
Bridge.on('disks.changed', () => {
  if (Steps.busy || !el('overlay').hidden || !el('btn-refresh')) return;
  loadDisks({ quiet: true });
});

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

  const sub = disk.isVolume
    ? `כונן מוצפן שנקרא דרך Windows · ${formatSize(disk.size)}`
    : disk.isImage
    ? `<span class="ltr-inline">${esc(disk.imagePath)}</span> · ${formatSize(disk.size)} · ${countText}`
    : `דיסק ${disk.number} · ${esc(disk.mediaLabel)} · ${disk.size > 0 ? formatSize(disk.size) : 'גודל לא ידוע'} · ${countText}`;

  const actions = [];
  if (disk.rawAccessible && disk.size > 0) {
    actions.push(`<button class="btn btn-sm" data-hunt-disk="${disk.number}" title="חיפוש מחיצות שנמחקו או שאבדו בכל הכונן">${Icon.search}<span>סריקת כונן</span></button>`);
  }
  if (disk.isImage) {
    actions.push(`<button class="btn btn-sm" data-close-image="${disk.number}">${Icon.close}<span>${disk.isVolume ? 'סגירה' : 'סגירת התמונה'}</span></button>`);
  } else if (disk.rawAccessible) {
    actions.push(`<button class="btn btn-sm" data-image-disk="${disk.number}" title="העתקת הדיסק כולו לקובץ, וסריקה מתוכו">${Icon.copy}<span>יצירת תמונת דיסק</span></button>`);
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
          <span class="health-slot" data-health-chip="${disk.number}">${disk.isImage ? '' : healthChip(disk)}</span>
        </div>
        <div class="disk-actions">${actions.join('')}</div>
      </div>
      ${notes.join('')}
      <div data-health-note="${disk.number}">${disk.isImage ? '' : healthNote(disk)}</div>
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
  if (p.fs === 'BitLocker') {
    tags.push(p.unlocked
      ? '<span class="chip ok" title="הנעילה נפתחה ב-Windows — אפשר לסרוק">נעילה פתוחה</span>'
      : '<span class="chip warn" title="הכונן מוצפן. פתחו אותו ב-Windows כדי לסרוק">נעול</span>');
  }
  if (p.bootable) tags.push('<span class="chip">אתחול</span>');
  if (!p.found && !p.scannable && p.fs !== 'Unknown' && p.fs !== 'BitLocker') tags.push('<span class="chip">לא נתמכת לסריקה</span>');

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
            ${s.resumePercent != null
              ? `<span class="chip warn" title="נעצרה באמצע — אפשר להמשיך מאותה נקודה">נעצרה ב-${s.resumePercent.toFixed(0)}%</span>`
              : s.partial ? '<span class="chip warn" title="נשמרה באמצע סריקה — לא כל המחיצה נסרקה">חלקית</span>' : ''}
            ${s.connected ? '<span class="chip ok">הכונן מחובר</span>'
                          : '<span class="chip" title="אפשר לעיין ברשימה, אבל לא לשחזר">הכונן לא מחובר</span>'}
          </div>
        </button>
        ${s.resumePercent != null && s.connected
          ? `<button class="btn recent-resume" data-resume="${esc(s.path)}" data-title="${esc(s.title)}"
               title="נעצרה אחרי ${s.resumePercent.toFixed(1)}% — המשך מאותה נקודה">${Icon.play}<span>המשך</span></button>`
          : ''}
        <button class="recent-remove" data-forget="${esc(s.path)}" title="הסרה מהרשימה" aria-label="הסרה מהרשימה">${Icon.close}</button>
        </div>`).join('')}
    </div>`;

  box.querySelectorAll('[data-path]').forEach((b) => { b.onclick = () => openSavedScan(b.dataset.path); });
  box.querySelectorAll('[data-forget]').forEach((b) => { b.onclick = () => forgetScans(b.dataset.forget); });
  box.querySelectorAll('[data-resume]').forEach((b) => { b.onclick = () => resumeScan(b.dataset.resume, b.dataset.title); });

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
        <p>מחפש מחיצות שנמחקו או שאבדו — אחרי מחיקה בטעות, התקנה מחדש,
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

        ${SectorMapView.html('hunt')}
        <div class="kv" style="margin-top:18px">
          <div><dt>מחיצות שנמצאו</dt><dd id="hunt-found">0</dd></div>
          <div><dt>נקרא מהכונן</dt><dd id="hunt-done">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="hunt-elapsed">0:00</dd></div>
          <div><dt>זמן משוער שנותר</dt><dd id="hunt-eta" class="words">מחשב…</dd></div>
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
  el('hunt-eta').textContent = Eta.text('hunt', p.percent, p.elapsed);
  SectorMapView.update('hunt', p.map);
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
    <div id="restore-status"></div>

    ${confirmWordField('restore-confirm')}`;

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-restore" disabled>${Icon.layers}<span>החזרה לטבלה</span></button>
      <button class="btn" id="btn-back-restore">חזרה</button>
    </div>`);

  el('btn-back-restore').onclick = () => openScanPanel(disk.number, part.index);

  const ready = () => { el('btn-do-restore').disabled = !el('restore-undo').value || !confirmTyped('restore-confirm'); };
  el('restore-confirm').oninput = ready;

  el('btn-pick-restore-undo').onclick = async () => {
    const { path } = await Bridge.call('repair.pickFolder', {}, 0);
    if (!path) return;
    el('restore-undo').value = path;
    ready();
    el('restore-status').innerHTML =
      `<div class="notice ok-notice tiny-notice">${Icon.check}<div>הגיבוי יישמר כאן.</div></div>`;
  };

  el('btn-do-restore').onclick = async () => {
    const undoFolder = el('restore-undo').value;
    const confirm = el('restore-confirm').value.trim();
    el('panel').querySelector('.panel-body').innerHTML =
      `<div class="loading" style="height:180px"><div class="spinner"></div><p>מחזיר את המחיצה לטבלה…</p></div>`;
    el('panel').querySelector('.panel-foot').innerHTML = '';

    let r;
    try {
      r = await Bridge.call('partition.restore', { disk: disk.number, part: part.index, undoFolder, confirm }, 0);
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

/// מחיצת BitLocker. הדיסק הפיזי מחזיר רק תוכן מוצפן; אחרי שהנעילה נפתחת
/// ב-Windows, התוכנה קוראת את המחיצה דרך Windows — מפוענחת — כדיסק נוסף ברשימה.
function openBitLockerPanel(disk, part) {
  const letter = part.letter ? `<bdi>${esc(part.letter)}</bdi>` : '';
  const body = part.unlocked
    ? `${notice('ok-notice', Icon.lock, 'הנעילה פתוחה',
        `התוכנה תקרא את הכונן ${letter} דרך Windows, שמפענח אותו. הוא יופיע ברשימה ככונן נוסף, ` +
        'ואפשר יהיה להריץ עליו כל סוג סריקה — גם סריקה מתקדמת.',
        'BitLocker מצפין את כל המחיצה, כולל המקום שבו יושבים קבצים שנמחקו. קריאה ישירה מהכונן ' +
        'מחזירה רק תוכן מוצפן; דרך Windows כל אזור נקרא מפוענח.')}
       ${notice('info', Icon.shield, 'קריאה בלבד',
        'שום דבר לא ייכתב לכונן. אל תנעלו אותו מחדש ואל תנתקו אותו עד סוף השחזור.')}`
    : `${notice('warn', Icon.lock, 'הכונן נעול ב-BitLocker',
        'התוכן שלו מוצפן, ולכן אי אפשר לסרוק אותו לפני שפותחים את הנעילה ב-Windows.')}
       <div class="section-label" style="margin-top:14px">איך פותחים את הנעילה</div>
       <ol class="image-steps">
         ${letter
           ? `<li>פתחו את <b>סייר הקבצים</b> ולחצו פעמיים על הכונן <b>${letter}</b>. Windows יבקש סיסמה או מפתח שחזור.</li>`
           : '<li>לכונן אין אות כונן, ולכן Windows לא מציע לפתוח אותו. אם הוא חיצוני — נתקו וחברו אותו מחדש; ' +
             'אחרת פתחו את <b>ניהול דיסקים</b> של Windows והקצו לו אות.</li>'}
         <li>אין סיסמה? <b>מפתח השחזור</b> הוא מספר של 48 ספרות. הוא נשמר בדרך כלל בחשבון Microsoft של
           מי שהגדיר את המחשב (בכתובת <span class="ltr-inline">aka.ms/myrecoverykey</span>), או הודפס ונשמר בקובץ כשההצפנה הופעלה.</li>
         <li>אחרי שהכונן נפתח, חזרו לכאן ולחצו <b>רענון</b>. ליד המחיצה יופיע "נעילה פתוחה".</li>
       </ol>
       ${notice('info', Icon.info, 'בלי המפתח אין דרך לשחזר',
        'ההצפנה נועדה בדיוק לזה: בלי סיסמה או מפתח שחזור אף תוכנה לא יכולה לקרוא את הקבצים.',
        'אם Windows לא מצליח לפתוח את הכונן גם עם המפתח הנכון, כנראה שאזור הניהול של ההצפנה ניזוק. ' +
        'במקרה כזה כדאי ליצור תמונת דיסק ולפנות למעבדת שחזור.', 'spaced')}`;

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · BitLocker · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">${body}<div id="bitlocker-status"></div></div>
    <div class="panel-foot">
      ${part.unlocked
        ? `<button class="btn btn-primary" id="btn-bitlocker-open">${Icon.lock}<span>פתיחה לסריקה</span></button>`
        : `<button class="btn btn-primary" id="btn-bitlocker-refresh">${Icon.refresh}<span>רענון</span></button>`}
      <button class="btn" id="btn-cancel-bitlocker">ביטול</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-bitlocker').onclick = closePanel;

  const refresh = el('btn-bitlocker-refresh');
  if (refresh) refresh.onclick = async () => { closePanel(); await loadDisks(); };

  const open = el('btn-bitlocker-open');
  if (open) open.onclick = async () => {
    open.disabled = true;
    try {
      const r = await Bridge.call('bitlocker.open', { disk: disk.number, part: part.index }, 0);
      closePanel();
      State.openDisks.add(r.number);
      await loadDisks();
      openScanPanel(r.number, 0);
    } catch (err) {
      open.disabled = false;
      el('bitlocker-status').innerHTML = errorNotice('לא ניתן לפתוח את הכונן', err, 'spaced');
    }
  };
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

  // מחיצה מוצפנת: אבחון ותיקון לא רלוונטיים — וכתיבה אליה הייתה הורסת אותה.
  if (part.fs === 'BitLocker' && !part.found) {
    openBitLockerPanel(disk, part);
    return;
  }

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
      ${d.canRepair ? 'אפשרות 3 — ' : ''}סריקה מתקדמת
    </div>
    <div class="strategy">
      <p>סריקה מתקדמת מחפשת קבצים לפי התוכן שלהם, ועובדת גם במחיצה שאינה נקראת כלל. ${d.canRepair
        ? 'אבל הקבצים יימצאו בלי שמות — השתמשו בה רק אם אפשרות 1 לא מצאה את מה שחיפשתם.'
        : 'לא נמצא עותק גיבוי, ולכן זו הדרך להציל את הקבצים — בלי שמות מקוריים ובלי תיקיות.'}</p>
    </div>
    ${d.canRepair ? '' : notice('info', Icon.shield, 'קריאה בלבד',
      'הכונן לא ישתנה, והקבצים יועתקו לכונן אחר.')}`;

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      ${d.canRepair ? `<button class="btn btn-primary" id="btn-read-through">${Icon.copy}<span>העתקת קבצים עם שמות</span></button>` : ''}
      ${d.canRepair ? `<button class="btn" id="btn-repair">${Icon.wrench}<span>תיקון המחיצה</span></button>` : ''}
      <button class="btn ${d.canRepair ? '' : 'btn-primary'}" id="btn-recover-raw">${Icon.radar}<span>סריקה מתקדמת</span></button>
      ${disk.isImage || !disk.rawAccessible ? '' : `<button class="btn" id="btn-image-raw">${Icon.copy}<span>יצירת תמונת דיסק</span></button>`}
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

/// המילה שמקלידים לפני כתיבה לכונן — חייבת להתאים ל-ConfirmWord שבגשר.
const CONFIRM_WORD = 'מאשר';

/// שדה מילת האישור: לחיצה אחת בטעות לא תכתוב לכונן.
function confirmWordField(id) {
  return `
    <div class="section-label">אישור אחרון</div>
    <p class="confirm-hint" id="${id}-hint">כדי לכתוב לכונן, הקלידו את המילה <b>${CONFIRM_WORD}</b>:</p>
    <input type="text" class="confirm-word" id="${id}" autocomplete="off" spellcheck="false"
           aria-describedby="${id}-hint">`;
}

const confirmTyped = id => el(id).value.trim() === CONFIRM_WORD;

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

      ${confirmWordField('repair-confirm')}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-repair" disabled>ביצוע התיקון</button>
      <button class="btn" id="btn-back-repair">חזרה</button>
    </div>`;

  el('panel-close').onclick = closePanel;
  el('btn-back-repair').onclick = () => openRepairPanel(disk, part);

  const ready = () => { el('btn-do-repair').disabled = !el('undo-path').value || !confirmTyped('repair-confirm'); };
  el('repair-confirm').oninput = ready;

  el('btn-pick-undo').onclick = async () => {
    const { path } = await Bridge.call('repair.pickFolder', {}, 0);
    if (!path) return;

    el('undo-path').value = path;
    ready();
    el('undo-status').innerHTML =
      `<div class="notice ok-notice tiny-notice">${Icon.check}<div>הגיבוי יישמר כאן.</div></div>`;
  };

  el('btn-do-repair').onclick = () => runRepair(disk, part);
}

async function runRepair(disk, part) {
  const undoFolder = el('undo-path').value;
  const confirm = el('repair-confirm').value.trim();

  el('panel').querySelector('.panel-body').innerHTML =
    `<div class="loading" style="height:180px"><div class="spinner"></div><p>מתקן את המחיצה…</p></div>`;
  el('panel').querySelector('.panel-foot').innerHTML = '';

  let r;
  try {
    r = await Bridge.call('repair.apply',
      { disk: disk.number, part: part.index, undoFolder, confirm }, 0);
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

/// ביטול תיקון קודם מתוך קובץ הביטול שנשמר בזמן התיקון. המנוע מוודא שזה
/// הכונן הנכון ושהוא עדיין במצב שהתיקון השאיר — רק אז אפשר לאשר.
function openUndoPanel() {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">ביטול תיקון קודם</div>
        <div class="panel-sub">החזרת הכונן למצב שלפני תיקון מחיצה או החזרת מחיצה לטבלה</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('info', Icon.info, '',
        'בכל תיקון התוכנה שומרת קובץ ביטול בתיקיית הגיבוי שבחרתם. שמו מתחיל ב-RAF-undo.')}

      <div class="section-label">קובץ הביטול</div>
      <div class="target-row">
        <input type="text" id="undo-file" readonly placeholder="לא נבחר קובץ">
        <button class="btn" id="btn-pick-undo-file">${Icon.file}<span>בחירה</span></button>
      </div>
      <div id="undo-check"></div>
      <div id="undo-confirm-row" hidden>${confirmWordField('undo-confirm')}</div>
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-undo" disabled>${Icon.back}<span>ביטול התיקון</span></button>
      <button class="btn" id="btn-close-undo">סגירה</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-close-undo').onclick = closePanel;

  let checked = false;
  const ready = () => { el('btn-do-undo').disabled = !checked || !confirmTyped('undo-confirm'); };
  el('undo-confirm').oninput = ready;

  el('btn-pick-undo-file').onclick = async () => {
    const { path } = await Bridge.call('undo.pickFile', {}, 0);
    if (!path) return;

    el('undo-file').value = path;
    checked = false;
    ready();
    el('undo-check').innerHTML =
      `<div class="loading" style="height:80px"><div class="spinner"></div><p>בודק את הכונן…</p></div>`;

    let c;
    try {
      c = await Bridge.call('undo.check', { path }, 0);
    } catch (err) {
      el('undo-check').innerHTML = errorNotice('לא ניתן לבדוק את קובץ הביטול', err, 'spaced');
      return;
    }

    checked = c.canUndo;
    const details = c.what ? `
      <div class="strategy"><p>
        <b>הפעולה:</b> ${esc(c.what)}<br>
        ${c.created ? `<b>מתי:</b> ${esc(new Date(c.created).toLocaleString('he-IL'))}<br>` : ''}
        ${c.diskName ? `<b>הכונן:</b> ${esc(c.diskName)} · ${formatSize(c.diskSize)}` : ''}
      </p></div>` : '';
    el('undo-check').innerHTML = details +
      `<div class="notice ${c.canUndo ? 'ok-notice' : 'warn'} tiny-notice">
        ${c.canUndo ? Icon.check : Icon.alert}<div>${esc(c.message)}</div></div>`;
    el('undo-confirm-row').hidden = !c.canUndo;
    ready();
  };

  el('btn-do-undo').onclick = async () => {
    const path = el('undo-file').value;
    const confirm = el('undo-confirm').value.trim();

    el('panel').querySelector('.panel-body').innerHTML =
      `<div class="loading" style="height:180px"><div class="spinner"></div><p>מחזיר את הכונן למצב הקודם…</p></div>`;
    el('panel').querySelector('.panel-foot').innerHTML = '';

    let r;
    try {
      r = await Bridge.call('undo.apply', { path, confirm }, 0);
    } catch (err) {
      r = { succeeded: false, error: err };
    }

    const cls = r.succeeded ? 'ok-notice' : r.rolledBack ? 'warn' : 'danger';
    el('panel').querySelector('.panel-body').innerHTML = r.error
      ? errorNotice('ביטול התיקון לא הושלם', r.error)
      : `<div class="notice ${cls}">${r.succeeded ? Icon.check : Icon.alert}<div>${esc(r.message)}</div></div>`;
    el('panel').querySelector('.panel-foot').innerHTML =
      `<button class="btn btn-primary" id="btn-done-undo">סיום</button>`;
    el('btn-done-undo').onclick = () => { closePanel(); loadDisks(); };
  };
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
        <div class="panel-title">יצירת תמונת דיסק</div>
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
          <li><b>מעבר 3 — מהכיוון ההפוך.</b> מה שעדיין לא נקרא נקרא שוב מהסוף להתחלה — כך מצליחים לפעמים להציל עוד סקטורים בקצה של אזור פגום.</li>
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
      <button class="btn btn-primary" id="btn-start-image" disabled>${Icon.copy}<span>יצירת תמונת דיסק</span></button>
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
          <div class="page-title">יצירת תמונת דיסק</div>
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

        ${SectorMapView.html('image')}

        <div class="kv" style="margin-top:18px">
          <div><dt>הועתק</dt><dd id="img-done">0 B</dd></div>
          <div><dt>טרם נקרא בהצלחה</dt><dd id="img-problems">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="img-elapsed">0:00</dd></div>
          <div><dt>זמן משוער שנותר</dt><dd id="img-eta" class="words">מחשב…</dd></div>
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
    : `${p.pass === 2 ? 'ניסיון חוזר' : 'מהכיוון ההפוך'}: ${formatSize(p.done)} מתוך ${formatSize(p.total)}`;
  el('img-problems').textContent = formatSize(p.problems);
  el('img-problems').classList.toggle('warn-text', p.problems > 0);
  el('img-elapsed').textContent = formatDuration(p.elapsed);
  el('img-eta').textContent = Eta.text('img', p.percent, p.elapsed);
  SectorMapView.update('image', p.map);
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
  } else if (source && source.paths) {
    await diagnoseInto(() => Bridge.call('doctor.diagnose', { paths: source.paths }, 0));
  } else {
    renderDoctorEmpty();
  }
}

/// קבצים או תיקיות שנגררו אל החלון נבדקים בתיקון הקבצים. כשהחלונית כבר
/// פתוחה הם מצטרפים לרשימה; באמצע סריקה או שחזור — הגרירה אינה מפריעה.
Bridge.on('files.dropped', async ({ paths }) => {
  if (Steps.busy) {
    setStatus('אי אפשר לבדוק קבצים באמצע פעולה. נסו שוב כשהיא תסתיים.');
    return;
  }

  if (!el('doctor-body')) {
    await openDoctorPanel({ paths });
    return;
  }

  const before = Doctor.files;
  await diagnoseInto(async () => {
    const data = await Bridge.call('doctor.diagnose', { paths }, 0);
    const known = new Set(before.map((f) => f.path));
    return { files: before.concat((data.files || []).filter((f) => !known.has(f.path))) };
  });
});

// קובץ שנגרר אל אזור הממשק לא ינווט אליו — הגרירה מטופלת בחלון (ראו FileDrop.cs).
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => e.preventDefault());

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
      מסמך Word, Excel או PowerPoint (או ZIP) שלא נפתח — תוכן העניינים שלו נבנה מחדש ·
      מסמך PDF שלא נפתח, או נפתח רק עם אזהרה — טבלת המיקומים שלו נבנית מחדש ·
      סרטון שההקלטה שלו נקטעה ולא נפתח — בעזרת סרטון תקין אחד מאותו מכשיר ·
      הקלטת WAV שנקטעה ומתנגנת ריקה · שיר MP3 שנגנים לא מזהים בגלל נתונים זרים בתחילתו ·
      מסד נתונים SQLite שהכותרת שלו נפגעה.</p>
      <p>קובץ שחסרים בו נתונים, או שאינו תואם לשום פורמט מוכר, לא יתוקן — התוכנה לא ממציאה נתונים.</p></div>
    <p class="doc-hint">אפשר גם לגרור קבצים או תיקייה אל החלון.</p>`;

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
  const videos = files.filter((f) => f.needsReference);
  const hopeless = files.length - healthy - fixable.length - videos.filter((f) => !f.canRepair).length;

  const rows = files.map((f) => {
    const [cls, label] = f.healthy ? ['ok', 'תקין']
      : f.canRepair ? ['warn', 'ניתן לתקן']
      : f.needsReference ? ['warn', 'צריך סרטון לדוגמה'] : ['danger', 'לא ניתן לתקן'];

    const issues = f.issues.length
      ? `<ul class="doc-issues">${f.issues.map((i) =>
          `<li class="${i.fixable || i.kind === 'VideoIndexMissing' ? '' : 'nofix'}">${esc(i.description)}</li>`).join('')}</ul>`
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
        ${f.needsReference ? `
          <button class="btn btn-sm doc-action" data-rebuild="${esc(f.path)}">${Icon.play}<span>בחירת סרטון תקין מאותו מכשיר…</span></button>` : ''}
      </div>`;
  }).join('');

  el('doctor-body').innerHTML = `
    <div class="doc-summary">
      <span><b class="ok-text">${healthy}</b> תקינים</span>
      <span class="sep">·</span>
      <span><b>${fixable.length}</b> ניתנים לתיקון</span>
      ${videos.length ? `<span class="sep">·</span>
      <span><b>${videos.length}</b> ${plural(videos.length, 'סרטון שצריך', 'סרטונים שצריכים')} סרטון לדוגמה</span>` : ''}
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
  document.querySelectorAll('[data-rebuild]').forEach((b) => {
    b.onclick = () => rebuildVideo(b.dataset.rebuild);
  });
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

/// סרטון שחסר בו האינדקס: סרטון ייחוס מאותו מכשיר, תיקיית יעד, ובנייה.
async function rebuildVideo(path) {
  const ref = await Bridge.call('doctor.pickReference', {}, 0);
  if (!ref.path) return;
  if (ref.problem) {
    setStatus('');
    const row = document.querySelector(`[data-rebuild="${CSS.escape(path)}"]`)?.closest('.doc-row');
    row?.querySelector('.doc-ref-problem')?.remove();
    row?.insertAdjacentHTML('beforeend', `<div class="doc-meta danger-text doc-ref-problem">${esc(ref.problem)}</div>`);
    return;
  }

  const { path: output } = await Bridge.call('doctor.pickFolder', {}, 0);
  if (!output) return;

  el('doctor-body').innerHTML = `
    <div class="loading" style="height:200px"><div class="spinner"></div>
      <p>בונה אינדקס חדש לסרטון — תמונה אחר תמונה…</p>
      <p class="doc-meta" id="rebuild-percent"></p></div>`;
  el('doctor-foot').innerHTML = '';

  let r;
  try {
    r = await Bridge.call('doctor.rebuildVideo', { path, reference: ref.path, output }, 0);
  } catch (err) {
    el('doctor-body').innerHTML = errorNotice('בניית האינדקס לא הושלמה', err);
    el('doctor-foot').innerHTML = `<button class="btn" id="btn-doctor-back">חזרה לרשימה</button>`;
    el('btn-doctor-back').onclick = renderDoctorList;
    return;
  }

  el('doctor-body').innerHTML = `
    ${r.succeeded
      ? notice('ok-notice', Icon.check, 'הסרטון תוקן', esc(r.message))
      : notice(r.output ? 'warn' : 'danger', Icon.alert, r.output ? 'הסרטון תוקן חלקית' : 'הסרטון לא תוקן', esc(r.message))}
    <div class="doc-list"><div class="doc-row">
      <div class="doc-top"><span class="doc-name"><bdi>${esc(r.name)}</bdi></span></div>
      ${r.applied.length ? `<ul class="doc-issues fixed">${r.applied.map((a) => `<li>${esc(a)}</li>`).join('')}</ul>` : ''}
      ${r.output ? `<div class="doc-meta"><span class="ltr-inline">${esc(r.output)}</span></div>` : ''}
    </div></div>
    <p class="doc-hint">הסרטון המקורי לא שונה. אם התמונה בסרטון המתוקן משובשת, כנראה שסרטון הדוגמה צולם בהגדרות אחרות
      (רזולוציה או קצב תמונות) — נסו סרטון אחר מאותו מכשיר.</p>`;

  el('doctor-foot').innerHTML = `
    ${r.folder ? `<button class="btn btn-primary" id="btn-doctor-open">${Icon.folder}<span>פתיחת התיקייה</span></button>` : ''}
    <button class="btn" id="btn-doctor-back">חזרה לרשימה</button>`;
  if (r.folder) el('btn-doctor-open').onclick = () => Bridge.call('recover.openFolder', { path: r.folder });
  el('btn-doctor-back').onclick = renderDoctorList;
}

Bridge.on('doctor.progress', ({ percent }) => {
  const box = el('rebuild-percent');
  if (box) box.textContent = `${Math.min(100, percent).toFixed(0)}%`;
});

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

  const health = healthOf(disk);
  const healthWarning = health && health.level !== 'Good'
    ? `<div class="health-warning">${healthNote(disk, false)}
        ${health.level === 'Bad' && disk.rawAccessible
          ? `<button class="btn btn-sm" id="btn-image-first">${Icon.copy}<span>יצירת תמונה במקום סריקה ישירה</span></button>` : ''}
      </div>` : '';

  el('panel').querySelector('.panel-body').innerHTML = `
    ${healthWarning}
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

    ${modeId === 3 ? `
    <div class="section-label" style="margin-top:16px">אילו סוגי קבצים לחפש</div>
    <div class="type-picks" id="type-picks">
      ${CATEGORIES.filter((c) => c.id !== 'all').map((c) => `
        <button type="button" class="type-pick on" data-type="${c.id}">${Icon.check}<span>${c.label}</span></button>`).join('')}
    </div>
    <p class="switch-note">בחירה של סוגים מסוימים מקצרת את רשימת התוצאות ומתמקדת במה שמחפשים.</p>` : ''}

    ${modeId === 3 && part.scannable ? `
    <label class="switch">
      <input type="checkbox" id="opt-free-only" checked>
      <span>לסרוק רק את המקום הפנוי — מהיר בהרבה</span>
    </label>
    <p class="switch-note">קבצים שנמחקו נמצאים במקום שמערכת הקבצים סימנה כפנוי, והמקום התפוס מכיל את הקבצים
      הקיימים. בכונן מלא ברובו הסריקה מהירה פי כמה. כבו אם מבנה המחיצה פגום.</p>` : ''}

    <label class="switch">
      <input type="checkbox" id="opt-include-existing"${part.readThrough ? ' checked' : ''}>
      <span>הצג גם קבצים קיימים, ולא רק קבצים שנמחקו</span>
    </label>

    ${notice('info', Icon.shield, 'קריאה בלבד מהדיסק המקור',
      'השחזור יתאפשר רק לכונן אחר.')}`;
  el('btn-image-first')?.addEventListener('click', () => openImagePanel(disk, null));

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start">${Icon.bolt}<span>התחלת סריקה</span></button>
      <button class="btn" id="btn-back">חזרה</button>
    </div>`);

  el('btn-back').onclick = () => openScanPanel(disk.number, part.index);
  // סוגי הקבצים: לחיצה מדליקה ומכבה; אי אפשר לכבות את כולם.
  document.querySelectorAll('.type-pick').forEach((b) => {
    b.onclick = () => {
      if (b.classList.contains('on') && document.querySelectorAll('.type-pick.on').length === 1) return;
      b.classList.toggle('on');
    };
  });

  el('btn-start').onclick = () => startScan(disk, part, modeId, el('opt-include-existing').checked,
    !!el('opt-free-only')?.checked,
    [...document.querySelectorAll('.type-pick.on')].map((b) => b.dataset.type));
}

/* =====================================================================
   מסך 2 — סריקה מתבצעת
   ===================================================================== */

async function startScan(disk, part, modeId, includeExisting, freeSpaceOnly = false, types = null) {
  closePanel();
  State.scan = { disk, part, mode: modeId };
  showScanScreen(modeId, `${partTitle(part)} · ${disk.name}`);
  await runScan('scan.start', {
    disk: disk.number, part: part.index, mode: modeId, includeExisting, freeSpaceOnly, types,
  }, partTitle(part));
}

/// המשך של סריקה מתקדמת שנעצרה — מהסריקה הנוכחית (בלי נתיב) או מ"סריקות אחרונות".
async function resumeScan(path, title) {
  closePanel();
  showScanScreen(3, `${title} · המשך מהנקודה שבה נעצרה`);
  await runScan('scan.resume', path ? { path } : {}, title);
}

/// מסך הסריקה. בסריקה מתקדמת יש גם השהיה: הסריקה נשמרת ואפשר להמשיך אחר כך.
function showScanScreen(modeId, subtitle) {
  Steps.set(2);
  State.selection = { count: 0, bytes: 0 };
  const mode = SCAN_MODES.find((m) => m.id === modeId);
  const pausable = modeId === 3;

  el('content').innerHTML = `
    <div class="scanning">
      <div class="scan-head">
        <div class="scan-icon">${mode.icon}</div>
        <div>
          <div class="page-title">${mode.name}</div>
          <div class="page-desc">${esc(subtitle)}</div>
        </div>
      </div>

      <div class="progress-card">
        <div class="progress-stage" id="scan-stage">מתחיל…</div>
        <div class="progress-track"><div class="progress-fill" id="scan-fill" style="width:0%"></div></div>
        <div class="progress-numbers">
          <span id="scan-percent">0%</span>
          <span id="scan-speed"></span>
        </div>

        ${SectorMapView.html('scan')}

        <div class="kv" style="margin-top:18px">
          <div><dt>קבצים שנמצאו</dt><dd id="scan-files">0</dd></div>
          <div><dt>נקרא מהדיסק</dt><dd id="scan-bytes">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="scan-elapsed">0:00</dd></div>
          <div><dt>זמן משוער שנותר</dt><dd id="scan-eta" class="words">מחשב…</dd></div>
          <div><dt>מצב</dt><dd style="direction:rtl" id="scan-state">פועל</dd></div>
        </div>
      </div>

      <div class="scan-actions">
        ${pausable ? `<button class="btn" id="btn-pause-scan">${Icon.pause}<span>השהיה</span></button>` : ''}
        <button class="btn" id="btn-cancel-scan">${Icon.stop}<span>עצירת הסריקה</span></button>
      </div>

      ${pausable
        ? notice('info', Icon.info, 'אפשר להשהות ולהמשיך אחר כך',
            'בהשהיה הסריקה נשמרת עם הנקודה שבה עצרה — אפשר להמשיך עכשיו, או גם אחרי סגירת התוכנה, ' +
            'מ"סריקות אחרונות". בעצירה מוצג מה שנמצא עד אז.')
        : notice('info', Icon.info, 'אפשר לעצור בכל רגע',
            'מה שנמצא עד אז יוצג, ואפשר יהיה לשחזר אותו.')}
    </div>`;

  el('btn-cancel-scan').onclick = () => {
    el('scan-state').textContent = 'עוצר…';
    Bridge.call('scan.cancel');
  };
  if (pausable) {
    el('btn-pause-scan').onclick = () => {
      el('scan-state').textContent = 'משהה…';
      el('btn-pause-scan').disabled = true;
      Bridge.call('scan.pause');
    };
  }

  setStatus('סורק…');
}

async function runScan(method, params, title) {
  try {
    // ללא מגבלת זמן: סריקה עמוקה על דיסק גדול עשויה להימשך שעות.
    const summary = await longCall(method, params);

    if (summary.paused) { showPaused(summary, title); return; }

    State.summary = summary;
    await renderResults();
  } catch (err) {
    const resuming = method === 'scan.resume';
    el('content').innerHTML = `
      <div class="page-head"><div>
        <div class="page-title">${resuming ? 'אי אפשר להמשיך את הסריקה כרגע' : 'הסריקה נכשלה'}</div>
        <div class="page-desc">${esc(title)}</div>
      </div>
      <div class="head-actions">
        ${resuming ? `<button class="btn btn-primary" id="btn-retry">${Icon.play}<span>ניסיון נוסף</span></button>` : ''}
        <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button>
      </div></div>
      ${errorNotice('', err)}
      ${resuming ? '<p class="doc-hint">הסריקה עצמה שמורה, עם הנקודה שבה נעצרה — אפשר להמשיך גם אחר כך, מ"סריקות אחרונות".</p>' : ''}`;
    el('btn-home').onclick = loadDisks;
    if (resuming) el('btn-retry').onclick = () => resumeScan(params.path || null, title);
    setStatus(resuming ? 'אי אפשר להמשיך כרגע' : 'שגיאה');
  }
}

/// סריקה מושהית: נשמרה עם נקודת ההמשך. ממשיכים עכשיו, מציגים את מה שנמצא, או חוזרים.
function showPaused(p, title) {
  el('content').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">${p.disconnected ? 'הכונן נותק באמצע הסריקה' : 'הסריקה מושהית'}</div>
        <div class="page-desc">${esc(title)} · נסרקו ${p.percent.toFixed(1)}% · ${countFiles(p.files)} נמצאו עד כה</div>
      </div>
    </div>
    ${p.disconnected
      ? notice('warn', Icon.unplug, 'הסריקה נשמרה עם הנקודה שבה עצרה',
          'חברו את הכונן שוב ולחצו "המשך הסריקה" — היא תמשיך מאותה נקודה. ' +
          'אפשר גם לסגור את התוכנה ולהמשיך אחר כך, מ"סריקות אחרונות" במסך הכוננים.')
      : notice('ok-notice', Icon.check, 'הסריקה נשמרה עם הנקודה שבה עצרה',
          'אפשר להמשיך עכשיו, או לסגור את התוכנה ולהמשיך אחר כך — מ"סריקות אחרונות" במסך הכוננים.')}
    <div class="scan-actions" style="margin-top:16px">
      <button class="btn btn-primary" id="btn-resume">${Icon.play}<span>המשך הסריקה</span></button>
      <button class="btn" id="btn-show-found">${Icon.list}<span>הצגת מה שנמצא עד כה</span></button>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button>
    </div>`;

  el('btn-resume').onclick = () => resumeScan(null, title);
  el('btn-show-found').onclick = async () => {
    State.summary = await Bridge.call('scan.summary');
    await renderResults();
  };
  el('btn-home').onclick = loadDisks;
  setStatus(p.disconnected ? 'הכונן נותק — הסריקה נשמרה' : 'הסריקה מושהית');
}

/* =====================================================================
   מפת הסקטורים — בכל מעבר שעובר סקטור אחרי סקטור: סריקה מתקדמת, סריקת
   עומק, סריקת כונן ויצירת תמונה. כל ריבוע הוא חלק שווה של האזור הנסרק.
   ===================================================================== */

const SectorMapView = (() => {
  // המצבים כפי שהמנוע שולח אותם: תו '0' עד '5' לכל ריבוע.
  const COLORS = ['--border', '--border-strong', '--accent', '--ok', '--warn', '--danger'];
  const LEGENDS = {
    scan: { 2: 'נסרק', 3: 'נמצאו קבצים', 1: 'קבצים קיימים (דולג)', 5: 'לא ניתן לקריאה', 0: 'טרם נסרק' },
    hunt: { 2: 'נבדק', 3: 'נמצאה מחיצה', 5: 'לא ניתן לקריאה', 0: 'טרם נבדק' },
    image: { 2: 'הועתק', 4: 'ממתין לניסיון חוזר', 5: 'לא ניתן לקריאה', 0: 'טרם הועתק' },
  };
  const CELL = 8, GAP = 2, PITCH = CELL + GAP;

  function html(kind) {
    return `
      <div class="sector-map" id="map-${kind}" data-kind="${kind}" hidden>
        <div class="sm-head">
          <span class="section-label">מפת הסקטורים</span>
          <div class="sm-legend"></div>
        </div>
        <div class="sm-canvas-wrap">
          <canvas></canvas>
          <div class="sm-cursor" hidden></div>
        </div>
      </div>`;
  }

  /// מיקום הריבוע: מימין לשמאל ומלמעלה למטה, כמו קריאה בעברית.
  function place(box, i) {
    const col = i % box.cols, row = Math.floor(i / box.cols);
    return { x: box.width - (col + 1) * PITCH + GAP, y: row * PITCH };
  }

  function draw(root) {
    const data = root._map;
    if (!data) return;
    const canvas = root.querySelector('canvas');
    const width = root.querySelector('.sm-canvas-wrap').clientWidth;
    if (width <= 0) return;

    const n = data.cells.length;
    const cols = Math.max(1, Math.floor((width + GAP) / PITCH));
    const rows = Math.ceil(n / cols);
    const height = rows * PITCH - GAP;
    const box = { cols, width, n };
    root._box = box;

    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(box.width * dpr) || canvas.height !== Math.round(height * dpr)) {
      canvas.width = Math.round(box.width * dpr);
      canvas.height = Math.round(height * dpr);
      canvas.style.width = box.width + 'px';
      canvas.style.height = height + 'px';
    }

    const css = getComputedStyle(document.documentElement);
    const colors = COLORS.map((v) => css.getPropertyValue(v).trim());
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, box.width, height);
    for (let i = 0; i < n; i++) {
      const s = data.cells.charCodeAt(i) - 48;
      const { x, y } = place(box, i);
      ctx.fillStyle = colors[s] || colors[0];
      ctx.fillRect(x, y, CELL, CELL);
    }

    const cursor = root.querySelector('.sm-cursor');
    cursor.hidden = !(data.cursor >= 0 && data.cursor < n);
    if (!cursor.hidden) {
      const { x, y } = place(box, data.cursor);
      cursor.style.left = (x - 2) + 'px';
      cursor.style.top = (y - 2) + 'px';
    }

    // מקרא: רק מצבים שיש להם משמעות כאן — "דולג" רק כשבאמת דולג משהו.
    const legend = LEGENDS[root.dataset.kind] || LEGENDS.scan;
    const present = new Set(data.cells);
    const items = Object.entries(legend)
      .filter(([s]) => s !== '1' || present.has('1'))
      .map(([s, label]) => `<span><i style="background:var(${COLORS[s]})"></i>${label}</span>`)
      .join('');
    const legendBox = root.querySelector('.sm-legend');
    if (legendBox._html !== items) legendBox.innerHTML = legendBox._html = items;
  }

  /// ריחוף: איזה אזור בכונן הריבוע מייצג, ומה מצבו.
  function hover(root, e) {
    const box = root._box, data = root._map;
    if (!box || !data) return;
    const rect = e.target.getBoundingClientRect();
    const col = Math.floor((box.width - (e.clientX - rect.left)) / PITCH);
    const row = Math.floor((e.clientY - rect.top) / PITCH);
    const i = row * box.cols + col;
    if (col < 0 || col >= box.cols || i < 0 || i >= box.n) { e.target.title = ''; return; }

    const start = Math.ceil(i * data.length / box.n), end = Math.ceil((i + 1) * data.length / box.n);
    const legend = LEGENDS[root.dataset.kind] || LEGENDS.scan;
    const state = legend[data.cells[i]] || '';
    e.target.title = `${formatSize(start)} – ${formatSize(end)}${state ? ' · ' + state : ''}`;
  }

  /// עדכון מהודעת התקדמות. בלי מפה (שלב שאינו עובר סקטור אחרי סקטור) — המפה מוסתרת.
  function update(kind, map) {
    const root = el('map-' + kind);
    if (!root) return;
    root.hidden = !map;
    if (!map) return;
    root._map = map;

    if (!root._wired) {
      root._wired = true;
      const canvas = root.querySelector('canvas');
      canvas.addEventListener('mousemove', (e) => hover(root, e));
      new ResizeObserver(() => draw(root)).observe(root.querySelector('.sm-canvas-wrap'));
    }
    draw(root);
  }

  return { html, update };
})();

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
  el('scan-eta').textContent = Eta.text('scan', pct, p.elapsed);
  SectorMapView.update('scan', p.map);
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

      ${s.resumePercent != null ? `<div class="resume-strip">
        ${notice('warn', Icon.pause, `הסריקה נעצרה אחרי ${s.resumePercent.toFixed(1)}% מהמחיצה`,
          'מוצג מה שנמצא עד כה. אפשר להמשיך את הסריקה מאותה נקודה.')}
        <button class="btn btn-primary" id="btn-resume-scan">${Icon.play}<span>המשך הסריקה</span></button>
      </div>` : ''}

      ${(s.warnings || []).length ? notesHtml(s.warnings.map(esc)) : ''}

      <div class="results-grid">
        <aside class="tree" id="tree"></aside>
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

    if (prefs.mode === 'grid') Thumbs.fill(rows);
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
    return `<input type="checkbox"${f.selected ? ' checked' : ''}${f.recoverable ? '' : ' disabled'}>`;
  }

  function rowHtml(f, index) {
    return `
      <div class="frow${f.recoverable ? '' : ' unrecoverable'}${isActive(f, index) ? ' active' : ''}"
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
      ${r.previews > 0 ? notice('info', Icon.image,
        `${r.previews === 1 ? 'נשמרה תמונה מוקטנת אחת' : `נשמרו ${r.previews.toLocaleString('he-IL')} תמונות מוקטנות`} מתוך תמונות פגומות`,
        'בתוך רוב התמונות ממצלמה או מטלפון שמורה גרסה מוקטנת. כשהתמונה עצמה חזרה פגומה, הגרסה המוקטנת נשמרה לצדה — ' +
        'בשם "(תמונה מוקטנת)" — ונבדקה שהיא שלמה.') : ''}
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
