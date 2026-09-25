/* ==========================================================================
   הפעלה
   נטען אחרון: כל שאר הקבצים כבר מוגדרים.
   ========================================================================== */

'use strict';

/* ---------------------------------------------------------- אתחול */

/// שורת המצב: הגרסה והרשאות המנהל. נבנית מחדש גם כשמחליפים שפה.
function renderStatusInfo() {
  const info = State.sysInfo;
  if (!info) return;
  // השם הלועזי עבר לכאן משורת הכותרת, שבה נשאר רק השם העברי.
  el('status-version').textContent = t('⁦Recovery Advanced Free⁩ · גרסה {0}', info.version);

  const chip = el('status-elevation');
  chip.textContent = t(info.elevated ? 'הרשאות מנהל' : 'ללא הרשאות מנהל');
  chip.className = 'status-chip ' + (info.elevated ? 'ok' : 'danger');
}

async function init() {
  try {
    const info = await Bridge.call('system.info');
    State.sysInfo = info;
    renderStatusInfo();
    // לפני כל קריאה אחרת — כדי שההודעות הראשונות מהמנוע (רשימת הכוננים) כבר יגיעו בשפה הנכונה.
    await Bridge.call('system.language', { lang: I18n.lang }).catch(() => {});

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
      diag.textContent = `${t('בעיית תצוגה · חלון')} ${client} ${t('· ציור')} ${drawn} ${t('· יחס')} ${ratio}`;
      diag.className = 'status-chip danger';
      diag.title = t('אי-התאמה בין גודל החלון לאזור הציור. פירוט בקובץ RAF-diagnostics.txt בתיקיית הזמניים.');
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
