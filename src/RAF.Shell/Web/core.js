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
    const err = new Error(msg.error || t('שגיאה לא ידועה'));
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
            const err = new Error(t('הפעולה לא הסתיימה בזמן הצפוי.'));
            err.advice = t('ייתכן שהכונן איטי או תקוע. בדקו שהוא מחובר ונסו שוב.');
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
  if (I18n.lang === 'en') return n === 1 ? '1 file' : `${n.toLocaleString('en-US')} files`;
  return n === 1 ? 'קובץ אחד' : `${n.toLocaleString('he-IL')} קבצים`;   // לא לתרגום: הגרסה העברית
}

/// מספר לתצוגה, בפורמט של שפת הממשק.
function num(n) {
  return (n || 0).toLocaleString(I18n.locale);
}

/// פועל שמתאים למספר: plural(n, 'שוחזר', 'שוחזרו'). כל צורה מתורגמת בנפרד.
function plural(n, one, many) {
  return t(n === 1 ? one : many);
}

/// זמן משוער שנותר, במילים. הקצב מוחלק (ממוצע נע), כדי שהמספר לא יקפוץ בכל
/// דיווח — וכשאין עדיין מספיק נתונים, אומרים זאת במקום לנחש.
const Eta = (() => {
  const smoothed = new Map();

  function words(seconds) {
    if (I18n.lang === 'en') return wordsEn(seconds);
    if (seconds < 60) return 'פחות מדקה';                                 // לא לתרגום: הגרסה העברית
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return minutes === 1 ? 'כדקה' : `כ-${minutes} דקות`; // לא לתרגום
    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    const h = hours === 1 ? 'כשעה' : hours === 2 ? 'כשעתיים' : `כ-${hours} שעות`; // לא לתרגום
    return rest < 5 ? h : `${h} ו-${rest} דקות`;                          // לא לתרגום
  }

  function wordsEn(seconds) {
    if (seconds < 60) return 'less than a minute';
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return minutes === 1 ? 'about a minute' : `about ${minutes} minutes`;
    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    const h = hours === 1 ? 'about an hour' : `about ${hours} hours`;
    return rest < 5 ? h : `${h} and ${rest} minutes`;
  }

  /// key מזהה את הפעולה: ערך מוחלק אחד לכל מסך התקדמות. האחוז הוא של השלב
  /// הנוכחי, והזמן שחלף — של הפעולה כולה; לכן הקצב נמדד מתחילת השלב: כשהאחוז
  /// יורד (שלב חדש בסריקה עמוקה, ניסיון חוזר בתמונה) — המדידה מתחילה מחדש.
  function text(key, percent, elapsed) {
    if (percent === null || percent === undefined) {
      smoothed.delete(key);
      return t('מחשב…');
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
    if (done < 1 || time < 8) return t('מחשב…');

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
  const more = why ? `<details class="why"><summary>${t('למה?')}</summary><div>${why}</div></details>` : '';
  return `<div class="notice ${cls}${extra ? ' ' + extra : ''}">${icon}<div>${head}${sep}${text || ''}${more}</div></div>`;
}

/// שגיאה במבנה אחיד: מה קרה (הכותרת וההודעה), מה זה אומר על הקבצים,
/// ומה לעשות עכשיו. ההודעה הטכנית המקורית מקופלת תחת "פרטים טכניים".
function errorNotice(title, err, extra) {
  const lines = [esc(err.message)];
  if (err.sourceUntouched) lines.push(t('הקבצים המקוריים לא השתנו.'));
  if (err.advice) lines.push(`<b>${t('מה לעשות:')}</b> ${esc(err.advice)}`);
  const detail = err.detail
    ? `<details class="why"><summary>${t('פרטים טכניים')}</summary><div dir="ltr">${esc(err.detail)}</div></details>`
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

/// סמלים שמצביעים לכיוון ("חזרה", חץ פתיחה, המשך) — משמאל לימין הם מתהפכים.
for (const name of ['back', 'chevron', 'play']) {
  const rtlIcon = Icon[name];
  const ltrIcon = rtlIcon.replace('<svg ', '<svg style="transform:scaleX(-1)" ');
  Object.defineProperty(Icon, name, { get: () => (I18n.rtl ? rtlIcon : ltrIcon) });
}

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
  const LABEL = {   // מתורגם בהצגה: מכאן
    system: 'ערכת נושא: לפי הגדרות Windows',
    light: 'ערכת נושא: בהירה',
    dark: 'ערכת נושא: כהה',
  };                // מתורגם בהצגה: עד כאן
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
    btn.title = t(LABEL[pref]);
    btn.setAttribute('aria-label', t(LABEL[pref]));

    // גם רקע החלון עצמו מתעדכן, כדי שבשינוי גודל לא יבצבץ צבע אחר.
    Bridge.call('window.theme', { dark }).catch(() => {});
  }

  el('btn-theme').onclick = () => {
    pref = ORDER[(ORDER.indexOf(pref) + 1) % ORDER.length];
    try { localStorage.setItem('raf-theme', pref); } catch (e) {}
    apply();
    setStatus(t(LABEL[pref]));
  };

  // במצב "לפי המערכת", שינוי בהגדרות Windows מתעדכן מיד.
  media.addEventListener('change', () => { if (pref === 'system') apply(); });

  apply();
  return { get preference() { return pref; }, refresh: apply };
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
  // לא לתרגום: מכאן — הגרסה האנגלית היא EN_FAQ ב-lang-en.js.
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
      לוחצים על המחיצה ברשימה ומקלידים את מפתח השחזור (48 ספרות) או את הסיסמה. התוכנה מפענחת את הכונן בעצמה,
      והוא מופיע ברשימה ככונן נוסף — גם כש-Windows לא מצליח לפתוח אותו, גם מחיצה שנמחקה וגם תמונת דיסק.
      אם הנעילה כבר פתוחה ב-Windows, בוחרים <b>פתיחה לסריקה</b>. בלי הסיסמה או המפתח אין דרך לקרוא את הקבצים.`],
    ['יש לי גיבוי של Windows או מכונה וירטואלית (קובץ VHD, VHDX או VMDK)', `
      במסך הכוננים לוחצים <b>פתיחת תמונת דיסק</b> ובוחרים את הקובץ. הוא נפתח ככונן נוסף ברשימה —
      בלי לחבר אותו ל-Windows ובלי לכתוב אליו — ואפשר לסרוק ולשחזר ממנו כמו מכל כונן.
      <ul>
        <li><b>VirtualBox ו-VMware</b> — בוחרים את קובץ ה-VMDK הראשי (בלי ‎-s001 או ‎-flat בשם).
        כשהכונן מפוצל לכמה קבצים, כולם צריכים להיות באותה תיקייה.</li>
        <li>למכונה עם תמונת מצב (snapshot) — פותחים את הכונן המקורי, לא את קובץ השינויים.</li>
      </ul>`],
    ['קיבלתי תמונת דיסק ממעבדה או מטכנאי (קובץ E01)', `
      זה הפורמט של כלי חקירה כמו FTK Imager ו-EnCase. במסך הכוננים לוחצים <b>פתיחת תמונת דיסק</b> ובוחרים את קובץ ה-E01.
      כשהתמונה מחולקת לכמה קבצים (E01, E02, E03…), כולם צריכים להיות באותה תיקייה — התוכנה פותחת אותם יחד.`],
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
  // לא לתרגום: עד כאן

  function open() {
    el('help-panel').innerHTML = `
      <div class="panel-head">
        <div class="grow">
          <div class="panel-title">${t('שאלות נפוצות')}</div>
          <div class="panel-sub">${t('לחצו על שאלה כדי לראות את התשובה')}</div>
        </div>
        <button class="panel-close" id="help-close" aria-label="${t('סגירה')}">${Icon.close}</button>
      </div>
      <div class="panel-body">
        <div class="faq">
          ${(I18n.lang === 'en' ? EN_FAQ : FAQ).map(([q, a]) => `<details><summary>${q}</summary><div class="faq-a">${a}</div></details>`).join('')}
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
  const STEPS = ['מחיצה', 'סריקה', 'בחירת קבצים', 'שחזור'];   // מתורגם בהצגה
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
                <span class="step-num">${done ? Icon.check : n}</span>${t(label)}</button>`;
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


/* ----------------------------------------------------------- חלונות */

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
