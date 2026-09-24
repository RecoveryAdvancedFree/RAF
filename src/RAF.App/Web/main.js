/* ==========================================================================
   הפעלה
   נטען אחרון: כל שאר הקבצים כבר מוגדרים.
   ========================================================================== */

'use strict';

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
