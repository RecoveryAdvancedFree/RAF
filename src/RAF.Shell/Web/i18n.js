/* ==========================================================================
   שפת הממשק — עברית (ברירת המחדל) או אנגלית
   הטקסט בקוד נשאר בעברית, והוא גם המפתח: t('סגירה') מחזיר "Close" באנגלית.
   המילון באנגלית — lang-en.js. טקסט שעוד לא תורגם מוצג בעברית, ולא נעלם.
   ========================================================================== */

'use strict';

const I18n = (() => {
  let lang = 'he';
  try { if (localStorage.getItem('raf-lang') === 'en') lang = 'en'; } catch (e) { /* ברירת המחדל */ }

  /// טקסטים שהתבקשו באנגלית ואין להם תרגום — לבדיקה, כדי שלא יישאר עברית בממשק האנגלי.
  const missing = new Set();

  function applyDocument() {
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === 'en' ? 'ltr' : 'rtl';
  }
  applyDocument();

  /// התרגום. {0}, {1}… מוחלפים בערכים שאחרי הטקסט — באותו מקום בשתי השפות.
  function t(he, ...args) {
    let text = he;
    if (lang === 'en') {
      const en = EN[he];
      if (en === undefined) missing.add(he); else text = en;
    }
    return args.length ? text.replace(/\{(\d+)\}/g, (m, i) => (args[i] ?? '')) : text;
  }

  /// טקסט קבוע בדף עצמו (index.html): data-t לתוכן, data-t-label לתיאור ולשם לקורא מסך.
  /// הטקסט העברי המקורי נשמר, כדי שאפשר יהיה לחזור אליו.
  function translateStatic() {
    document.title = t('שחזור מתקדם חינם');
    document.querySelectorAll('[data-t]').forEach((n) => {
      if (n.dataset.he === undefined) n.dataset.he = n.textContent;
      n.textContent = t(n.dataset.he);
    });
    document.querySelectorAll('[data-t-label]').forEach((n) => {
      const he = n.dataset.tLabel;
      n.setAttribute('aria-label', t(he));
      if (n.hasAttribute('title')) n.title = t(n.dataset.tTitle || he);
    });
  }

  function renderButton() {
    const btn = document.getElementById('btn-lang');
    if (!btn) return;
    // הכפתור מראה את השפה שאליה עוברים.
    btn.textContent = lang === 'en' ? 'He' : 'En';
    btn.title = lang === 'en' ? 'עברית' : 'English';                              // לא לתרגום: כל שפה בשמה
    btn.setAttribute('aria-label', lang === 'en' ? 'מעבר לעברית' : 'Switch to English'); // לא לתרגום
    btn.setAttribute('lang', lang === 'en' ? 'he' : 'en');
  }

  /// החלפת שפה: הכיוון, הטקסט הקבוע, והמסך הנוכחי נבנה מחדש. באמצע פעולה ארוכה —
  /// לא: המסך שלה נבנה מההתקדמות שמגיעה מהמנוע, ובנייה מחדש הייתה מאבדת אותו.
  async function toggle() {
    if (typeof Steps !== 'undefined' && Steps.busy) {
      setStatus(lang === 'en' ? 'The language can be changed when the current operation ends.'
                              : 'אפשר להחליף שפה בסיום הפעולה.');   // לא לתרגום: יש כאן את שתי השפות
      return;
    }
    lang = lang === 'en' ? 'he' : 'en';
    try { localStorage.setItem('raf-lang', lang); } catch (e) { /* לא נשמר — רק לפעם הזו */ }
    applyDocument();
    renderButton();
    translateStatic();
    Help.close();
    closePanel();
    Theme.refresh();
    renderStatusInfo();
    Update.refresh();
    Steps.set(Steps.current);
    // המנוע עובר לשפה החדשה לפני שמבקשים ממנו שוב את המסך (למשל ההסברים ברשימת הכוננים)
    await Bridge.call('system.language', { lang }).catch(() => {});
    if (el('filelist') && State.summary) renderResults();
    else loadDisks();
  }

  document.addEventListener('DOMContentLoaded', () => {
    renderButton();
    translateStatic();
    document.getElementById('btn-lang')?.addEventListener('click', toggle);
  });

  return {
    t,
    toggle,
    translateStatic,
    get lang() { return lang; },
    get rtl() { return lang !== 'en'; },
    /// אזור לתצוגת מספרים ותאריכים.
    get locale() { return lang === 'en' ? 'en-US' : 'he-IL'; },
    missing,
  };
})();

const t = I18n.t;
