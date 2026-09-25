/* ==========================================================================
   עדכונים
   בדיקה אוטומטית בהפעלה ופעם בכמה שעות, וכפתור רענון לבדיקה ידנית. כשיש גרסה
   חדשה מופיעה בשורת הכותרת הודעה קטנה עם "התקן כעת".
   העדכון סוגר את התוכנה ופותח אותה מחדש — ולכן לעולם לא באמצע פעולה ארוכה.
   ========================================================================== */

'use strict';

const Update = (() => {
  const RECHECK_MS = 6 * 60 * 60 * 1000;
  let available = null;          // הגרסה החדשה, כשיש
  let checking = false;
  let hideTimer = 0;

  /// ההודעה בשורת הכותרת: טקסט, ואולי כפתור "התקן כעת". הודעה זמנית נעלמת לבד.
  function pill(text, { install = false, temporary = false, danger = false } = {}) {
    clearTimeout(hideTimer);
    const box = el('update-pill');
    box.hidden = false;
    box.classList.toggle('danger', danger);
    el('update-text').textContent = text;
    el('btn-update-install').hidden = !install;
    if (temporary) hideTimer = setTimeout(restore, 6000);
  }

  /// אחרי הודעה זמנית — חזרה להודעה על הגרסה הזמינה, או הסתרה.
  function restore() {
    if (available) pill(t('גרסה {0} זמינה', available), { install: true });
    else el('update-pill').hidden = true;
  }

  async function check(manual) {
    if (checking) return;
    checking = true;
    el('btn-update').classList.add('spinning');
    try {
      const r = await Bridge.call('update.check', {}, 30000);
      if (r.available) {
        available = r.version;
        restore();
      } else if (manual) {
        pill(t('יש לכם את הגרסה העדכנית ({0})', r.current), { temporary: true });
      }
    } catch (err) {
      if (manual) pill(err.message, { temporary: true, danger: true });
    } finally {
      checking = false;
      el('btn-update').classList.remove('spinning');
    }
  }

  async function openPanel() {
    // לוח פתוח באמצע פעולה (למשל לוח השחזור) מכיל את הדוח שלה — לא מחליפים אותו.
    if (Steps.busy) {
      pill(t('אפשר לעדכן כשהפעולה הנוכחית תסתיים'), { temporary: true });
      return;
    }

    let info;
    try {
      info = await Bridge.call('update.prepare');
    } catch (err) {
      pill(err.message, { temporary: true, danger: true });
      return;
    }

    const drives = info.drives.map((d) => `<span class="ltr-inline">${esc(d)}</span>`).join(', ');
    let session = '';
    if (info.sessionOpen) {
      session = info.sessionSaved
        ? notice('info', Icon.history, t('תוצאות הסריקה נשמרו'),
            t('העדכון יסגור אותן. אחרי העדכון אפשר לפתוח אותן שוב מ"סריקות אחרונות" במסך הכוננים.'), '', 'spaced')
        : notice('warn', Icon.alert, t('תוצאות הסריקה לא נשמרו'),
            t('העדכון יסגור אותן והן יאבדו. כדי לשמור אותן, סגרו את החלון הזה ולחצו על כפתור השמירה במסך התוצאות.'), '', 'spaced');
    }

    el('panel').innerHTML = `
      <div class="panel-head">
        <div class="grow">
          <div class="panel-title">${t('עדכון לגרסה {0}', esc(info.version))}</div>
          <div class="panel-sub">${t('הגרסה הנוכחית: {0}', esc(State.sysInfo?.version || ''))}</div>
        </div>
        <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
      </div>
      <div class="panel-body">
        ${info.busy
          ? notice('danger', Icon.alert, t('פעולה פועלת כעת'),
              t('העדכון סוגר את התוכנה, והפעולה הייתה נעצרת באמצע. אפשר לעדכן כשהיא תסתיים.'))
          : ''}
        ${notice('warn', Icon.drive, t('העדכון כותב לכונן {0}', drives),
          t('יורדים כ-{0}. אם אתם משחזרים קבצים מכונן זה, סיימו את השחזור לפני העדכון, כדי שהכתיבה לא תדרוס אותם.',
            formatSize(info.size)), '', 'spaced')}
        ${session}
        ${notice('info', Icon.refresh, '',
          info.installed
            ? t('התוכנה תיסגר, הגרסה החדשה תותקן, והתוכנה תיפתח מחדש.')
            : t('קובץ התוכנה יוחלף בגרסה החדשה, והתוכנה תיפתח מחדש.'), '', 'spaced')}
        <div class="progress-card" id="update-progress" hidden>
          <div class="progress-stage" id="update-stage">${t('מוריד את העדכון…')}</div>
          <div class="progress-track"><div class="progress-fill" id="update-fill" style="width:0%"></div></div>
        </div>
        <div id="update-error"></div>
      </div>
      <div class="panel-foot">
        <button class="btn btn-primary" id="btn-do-update" ${info.busy ? 'disabled' : ''}>${Icon.save}<span>${t('עדכן עכשיו')}</span></button>
        <button class="btn" id="btn-update-later">${t('לא עכשיו')}</button>
      </div>`;
    el('overlay').hidden = false;
    el('panel-close').onclick = closePanel;
    el('btn-update-later').onclick = closePanel;
    el('btn-do-update').onclick = install;
  }

  async function install() {
    el('btn-do-update').disabled = true;
    el('update-error').innerHTML = '';
    el('update-progress').hidden = false;
    Steps.setBusy(true);
    try {
      await Bridge.call('update.install', {}, 0);
      el('update-stage').textContent = t('מתקין… התוכנה תיסגר ותיפתח מחדש.');
    } catch (err) {
      Steps.setBusy(false);
      el('update-progress').hidden = true;
      el('btn-do-update').disabled = false;
      el('update-error').innerHTML = errorNotice(t('העדכון לא הושלם'), err, 'spaced');
    }
  }

  Bridge.on('update.progress', ({ percent }) => {
    const fill = el('update-fill');
    if (fill) fill.style.width = `${Math.min(100, percent)}%`;
    const stage = el('update-stage');
    if (stage) stage.textContent = t('מוריד את העדכון… {0}%', percent);
  });

  function init(info) {
    if (!info?.updates) return;
    el('btn-update').hidden = false;
    el('btn-update').onclick = () => check(true);
    el('btn-update-install').onclick = openPanel;
    // לא מיד בהפעלה: קודם הכוננים.
    setTimeout(() => check(false), 4000);
    setInterval(() => check(false), RECHECK_MS);
  }

  /// החלפת שפה: ההודעה נכתבת מחדש בשפה החדשה.
  function refresh() { if (!el('update-pill').hidden) restore(); }

  return { init, refresh };
})();
