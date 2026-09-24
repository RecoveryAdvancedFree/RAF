/* ==========================================================================
   שחזור
   בחירת היעד, ההעתקה והדוח.
   ========================================================================== */

'use strict';

/* =====================================================================
   שחזור
   ===================================================================== */

function openRecoverPanel() {
  Steps.set(4);
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${t('שחזור קבצים')}</div>
        <div class="panel-sub">${countFiles(State.selection.count)} · ${formatSize(Math.max(0, State.selection.bytes))}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('warn', Icon.alert, t('יעד על כונן אחר בלבד'),
        t('שחזור לאותו כונן ידרוס קבצים שעוד לא שוחזרו.'),
        t('קובץ שנמחק עדיין יושב באזור שמסומן "פנוי". כל קובץ חדש שנכתב לאותו כונן עלול לתפוס בדיוק את האזור הזה. התוכנה חוסמת זאת אוטומטית.'))}

      <div class="section-label">${t('תיקיית יעד')}</div>
      <div class="target-row">
        <input type="text" id="target-path" readonly placeholder="${t('לא נבחרה תיקייה')}" aria-label="${t('תיקיית היעד לשחזור')}">
        <button class="btn" id="btn-pick">${Icon.folder}<span>${t('בחירה')}</span></button>
      </div>
      <div id="target-status"></div>

      <label class="switch" style="margin-top:16px">
        <input type="checkbox" id="opt-preserve" checked>
        <span>${t('שמירה על מבנה התיקיות המקורי')}</span>
      </label>
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-recover" disabled>${Icon.save}<span>${t('שחזור')}</span></button>
      <button class="btn" id="btn-cancel">${t('ביטול')}</button>
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
  status.innerHTML = `<div class="target-check">${t('בודק…')}</div>`;

  const check = await Bridge.call('recover.validate', { target: path });

  if (check.valid) {
    const enough = check.freeSpace >= State.selection.bytes;
    status.innerHTML = `
      <div class="notice ${enough ? 'ok-notice' : 'warn'} tiny-notice">
        ${enough ? Icon.check : Icon.alert}
        <div>${t(enough ? 'תיקיית היעד תקינה' : 'ייתכן שאין מספיק מקום פנוי')} · ${t('פנוי: {0}', formatSize(check.freeSpace))}</div>
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
      <div class="panel-title">${t('משחזר קבצים…')}</div>
      <div class="panel-sub" id="rec-file">${t('מתחיל')}</div>
    </div></div>
    <div class="panel-body">
      <div class="progress-card">
        <div class="progress-track"><div class="progress-fill" id="rec-fill" style="width:0%"></div></div>
        <div class="progress-numbers"><span id="rec-count">0 / 0</span><span id="rec-bytes">0 B</span></div>
      </div>
    </div>
    <div class="panel-foot">
      <button class="btn" id="btn-stop-rec">${Icon.stop}<span>${t('עצירה')}</span></button>
    </div>`;

  el('btn-stop-rec').onclick = () => Bridge.call('recover.cancel');

  try {
    const report = await longCall('recover.start', { target, preservePaths });
    showRecoveryReport(report);
  } catch (err) {
    el('panel').querySelector('.panel-body').innerHTML = errorNotice(t('השחזור נעצר'), err);
    el('panel').querySelector('.panel-foot').innerHTML =
      `<button class="btn" id="btn-rec-close">${t('סגירה')}</button>`;
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
    ? `<div class="section-label" style="margin-top:18px">${t('קבצים שנכשלו')}</div>
       <div class="fail-list">${r.failures.map((f) =>
         `<div class="fail-row"><b>${esc(f.file)}</b><span>${esc(f.reason)}</span></div>`).join('')}</div>`
    : '';

  const partial = r.partial && r.partial.length
    ? notice('warn', Icon.alert, `${countFiles(r.partial.length)} ${plural(r.partial.length, 'שוחזר חלקית', 'שוחזרו חלקית')}`,
        t('הם הועברו לתיקייה <b>_חלקיים</b>, כדי שיהיה ברור על אילו קבצים לא לסמוך.'),
        t('חלק מהנתונים שלהם כבר נדרס, או שלא ניתן היה לקרוא אותם מהדיסק. ייתכן שלא ייפתחו כראוי.'))
    : '';

  // הדוח מפרט כל קובץ — גם מה שנכשל — עם גיבוב SHA-256 של מה שנכתב.
  const reportNote = r.reportPath
    ? notice('info', Icon.file, t('נשמר דוח שחזור'),
        t('{0} בתיקיית היעד — נפתח ב-Excel.', `<span class="ltr-inline">${esc(r.reportPath.split('\\').pop())}</span>`),
        t('שורה לכל קובץ: הנתיב המקורי, לאן נכתב, איכות, תוצאה, וגיבוב SHA-256 של מה שנכתב — כדי לוודא בעתיד שהקובץ לא השתנה.'))
    : '';

  el('panel').innerHTML = `
    <div class="panel-head"><div class="grow">
      <div class="panel-title">${t(r.cancelled ? 'השחזור נעצר' : 'השחזור הושלם')}</div>
      <div class="panel-sub">${formatDuration(r.duration)}</div>
    </div>
    <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button></div>
    <div class="panel-body">
      <div class="notice ${r.succeeded > 0 ? 'ok-notice' : 'warn'}">
        ${r.succeeded > 0 ? Icon.check : Icon.alert}
        <div><b>${countFiles(r.succeeded)} ${plural(r.succeeded, 'שוחזר בהצלחה', 'שוחזרו בהצלחה')}</b><br>
        ${t('{0} נכתבו אל:', formatSize(r.bytes))}<br>
        <span style="direction:ltr;display:inline-block">${esc(r.target)}</span></div>
      </div>
      ${partial}
      ${r.previews > 0 ? notice('info', Icon.image,
        r.previews === 1 ? t('נשמרה תמונה מוקטנת אחת מתוך תמונות פגומות')
                         : t('נשמרו {0} תמונות מוקטנות מתוך תמונות פגומות', num(r.previews)),
        t('בתוך רוב התמונות ממצלמה או מטלפון שמורה גרסה מוקטנת. כשהתמונה עצמה חזרה פגומה, הגרסה המוקטנת נשמרה לצדה — ' +
        'בשם "(תמונה מוקטנת)" — ונבדקה שהיא שלמה.')) : ''}
      ${reportNote}
      ${r.empty > 0 ? notice('danger', Icon.alert, `${countFiles(r.empty)} ${plural(r.empty, 'לא נכתב', 'לא נכתבו')}`,
        t('התוכן שלהם כבר לא קיים על הדיסק.'),
        t('אזור הנתונים שלהם מכיל אפסים בלבד. לא נוצר עבורם קובץ, כדי שלא יתקבלו קבצים ריקים שנראים תקינים.')) : ''}
      ${r.failed > r.empty ? `<div class="notice danger">${Icon.alert}
        <div>${countFiles(r.failed - r.empty)} ${plural(r.failed - r.empty, 'נכשל מסיבות אחרות.', 'נכשלו מסיבות אחרות.')}</div></div>` : ''}
      ${failures}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-done">${t('סיום')}</button>
      ${r.succeeded > 0 ? `<button class="btn" id="btn-open-target">${Icon.open}<span>${t('פתיחת תיקיית היעד')}</span></button>` : ''}
      ${r.succeeded > 0 ? `<button class="btn" id="btn-check-recovered">${Icon.wrench}<span>${t('בדיקת הקבצים ששוחזרו')}</span></button>` : ''}
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
