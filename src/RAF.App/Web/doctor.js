/* ==========================================================================
   תיקון קבצים שלא נפתחים
   ========================================================================== */

'use strict';

/* =====================================================================
   תיקון קבצים שלא נפתחים — זיהוי לפי תוכן הקובץ (חתימות HEX)
   ===================================================================== */

const Doctor = { files: [] };

async function openDoctorPanel(source) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${t('תיקון קבצים שלא נפתחים')}</div>
        <div class="panel-sub">${t('הבדיקה לפי מה שיש בתוך הקובץ, לא לפי השם שלו')}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
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
    setStatus(t('אי אפשר לבדוק קבצים באמצע פעולה. נסו שוב כשהיא תסתיים.'));
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
    ${notice('info', Icon.shield, t('הקבצים המקוריים לא משתנים'),
      t('התיקון נכתב לעותק חדש, ונבדק שוב אחרי הכתיבה.'),
      t('הבדיקה משווה בין חתימת הפתיחה של כל קובץ (הבתים הראשונים שמזהים את סוגו), הסיומת שלו, ' +
      'והאורך שמבנה הקובץ מצהיר עליו. כל פער ביניהם הוא בעיה מזוהה.'))}
    <div class="section-label">${t('מה אפשר לתקן')}</div>
    <div class="strategy"><p>
      ${t('תחילת קובץ שנמחקה או נפגעה · נתונים מיותרים בסוף הקובץ · סוף קובץ חסר · סיומת שגויה (למשל תמונה שנשמרה בשם ‎.doc) · מסמך Word, Excel או PowerPoint (או ZIP) שלא נפתח — תוכן העניינים שלו נבנה מחדש · מסמך PDF שלא נפתח, או נפתח רק עם אזהרה — טבלת המיקומים שלו נבנית מחדש · סרטון שההקלטה שלו נקטעה ולא נפתח — בעזרת סרטון תקין אחד מאותו מכשיר · הקלטת WAV שנקטעה ומתנגנת ריקה · שיר MP3 שנגנים לא מזהים בגלל נתונים זרים בתחילתו · מסד נתונים SQLite שהכותרת שלו נפגעה.')}</p>
      <p>${t('קובץ שחסרים בו נתונים, או שאינו תואם לשום פורמט מוכר, לא יתוקן — התוכנה לא ממציאה נתונים.')}</p></div>
    <p class="doc-hint">${t('אפשר גם לגרור קבצים או תיקייה אל החלון.')}</p>`;

  el('doctor-foot').innerHTML = `
    <button class="btn btn-primary" id="btn-doctor-pick">${Icon.file}<span>${t('בחירת קבצים')}</span></button>
    <button class="btn" id="btn-doctor-close">${t('סגירה')}</button>`;

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
    `<div class="loading" style="height:180px"><div class="spinner"></div><p>${t('בודק את הקבצים…')}</p></div>`;
  el('doctor-foot').innerHTML = '';

  try {
    const data = await request();
    Doctor.files = data.files || [];
    renderDoctorList();
  } catch (err) {
    el('doctor-body').innerHTML = errorNotice(t('לא ניתן לבדוק את הקבצים'), err);
  }
}

function renderDoctorList() {
  const files = Doctor.files;
  const healthy = files.filter((f) => f.healthy).length;
  const fixable = files.filter((f) => !f.healthy && f.canRepair);
  const videos = files.filter((f) => f.referenceKind === 'video');
  const photos = files.filter((f) => f.referenceKind === 'photo');
  const databases = files.filter((f) => f.referenceKind === 'database');
  const hopeless = files.length - healthy - fixable.length - [...videos, ...photos, ...databases].filter((f) => !f.canRepair).length;

  const rows = files.map((f) => {
    const [cls, label] = f.healthy ? ['ok', 'תקין']                                  // מתורגם בהצגה
      : f.canRepair ? ['warn', 'ניתן לתקן']                                              // מתורגם בהצגה
      : f.referenceKind === 'video' ? ['warn', 'צריך סרטון לדוגמה']                      // מתורגם בהצגה
      : f.referenceKind === 'photo' ? ['warn', 'צריך תמונה לדוגמה']                      // מתורגם בהצגה
      : f.referenceKind === 'database' ? ['warn', 'צריך מסד לדוגמה'] : ['danger', 'לא ניתן לתקן'];  // מתורגם בהצגה

    const issues = f.issues.length
      ? `<ul class="doc-issues">${f.issues.map((i) =>
          `<li class="${i.fixable || ['VideoIndexMissing', 'PhotoHeaderLost', 'RawHeaderLost', 'DatabaseSchemaLost'].includes(i.kind) ? '' : 'nofix'}">${esc(i.description)}</li>`).join('')}</ul>`
      : '';

    return `
      <div class="doc-row">
        <div class="doc-top">
          <span class="doc-name" title="${esc(f.path)}"><bdi>${esc(f.name)}</bdi></span>
          <span class="doc-size">${formatSize(f.size)}</span>
          <span class="chip ${cls} tiny">${t(label)}</span>
        </div>
        ${f.detected ? `<div class="doc-meta">${t('זוהה:')} ${esc(t(f.detected))}</div>` : ''}
        ${issues}
        ${f.referenceKind === 'video' ? `
          <button class="btn btn-sm doc-action" data-rebuild="${esc(f.path)}">${Icon.play}<span>${t('בחירת סרטון תקין מאותו מכשיר…')}</span></button>` : ''}
        ${f.referenceKind === 'photo' ? `
          <button class="btn btn-sm doc-action" data-rebuild-photo="${esc(f.path)}">${Icon.wrench}<span>${t('בחירת תמונה תקינה מאותה מצלמה…')}</span></button>` : ''}
        ${f.referenceKind === 'database' ? `
          <button class="btn btn-sm doc-action" data-rebuild-photo="${esc(f.path)}" data-kind="database">${Icon.wrench}<span>${t('בחירת מסד תקין של אותה אפליקציה…')}</span></button>` : ''}
      </div>`;
  }).join('');

  el('doctor-body').innerHTML = `
    <div class="doc-summary">
      <span><b class="ok-text">${healthy}</b> ${t('תקינים')}</span>
      <span class="sep">·</span>
      <span><b>${fixable.length}</b> ${t('ניתנים לתיקון')}</span>
      ${videos.length ? `<span class="sep">·</span>
      <span><b>${videos.length}</b> ${plural(videos.length, 'סרטון שצריך סרטון לדוגמה', 'סרטונים שצריכים סרטון לדוגמה')}</span>` : ''}
      ${photos.length ? `<span class="sep">·</span>
      <span><b>${photos.length}</b> ${plural(photos.length, 'תמונה שצריכה תמונה לדוגמה', 'תמונות שצריכות תמונה לדוגמה')}</span>` : ''}
      ${databases.length ? `<span class="sep">·</span>
      <span><b>${databases.length}</b> ${plural(databases.length, 'מסד שצריך מסד לדוגמה', 'מסדים שצריכים מסד לדוגמה')}</span>` : ''}
      <span class="sep">·</span>
      <span><b class="danger-text">${hopeless}</b> ${t('לא ניתנים לתיקון')}</span>
    </div>
    <div class="doc-list">${rows || `<div class="empty small"><h3>${t('אין קבצים')}</h3></div>`}</div>`;

  el('doctor-foot').innerHTML = `
    ${fixable.length ? `<button class="btn btn-primary" id="btn-doctor-fix">${Icon.wrench}<span>${t('תיקון')} ${countFiles(fixable.length)}</span></button>` : ''}
    <button class="btn" id="btn-doctor-more">${t('בחירת קבצים אחרים')}</button>
    <button class="btn" id="btn-doctor-close">${t('סגירה')}</button>`;

  const fix = el('btn-doctor-fix');
  if (fix) fix.onclick = () => runDoctorRepair(fixable.map((f) => f.path));
  document.querySelectorAll('[data-rebuild]').forEach((b) => {
    b.onclick = () => rebuildVideo(b.dataset.rebuild);
  });
  document.querySelectorAll('[data-rebuild-photo]').forEach((b) => {
    b.onclick = () => rebuildPhoto(b.dataset.rebuildPhoto, b.dataset.kind || 'photo');
  });
  el('btn-doctor-more').onclick = pickDoctorFiles;
  el('btn-doctor-close').onclick = closePanel;
}

async function runDoctorRepair(paths) {
  const { path: output } = await Bridge.call('doctor.pickFolder', {}, 0);
  if (!output) return;

  el('doctor-body').innerHTML =
    `<div class="loading" style="height:180px"><div class="spinner"></div><p>${t('מתקן ובודק מחדש…')}</p></div>`;
  el('doctor-foot').innerHTML = '';

  let data;
  try {
    data = await Bridge.call('doctor.repair', { paths, output }, 0);
  } catch (err) {
    el('doctor-body').innerHTML = errorNotice(t('התיקון לא הושלם'), err);
    return;
  }

  const rows = data.results.map((r) => `
    <div class="doc-row">
      <div class="doc-top">
        <span class="doc-name"><bdi>${esc(r.name)}</bdi></span>
        <span class="chip ${r.healthyAfter ? 'ok' : r.succeeded ? 'warn' : 'danger'} tiny">
          ${t(r.healthyAfter ? 'תוקן — תקין' : r.succeeded ? 'תוקן חלקית' : 'לא תוקן')}</span>
      </div>
      ${r.applied.length ? `<ul class="doc-issues fixed">${r.applied.map((a) => `<li>${esc(a)}</li>`).join('')}</ul>` : ''}
      <div class="doc-meta">${esc(r.message)}</div>
    </div>`).join('');

  el('doctor-body').innerHTML = `
    ${notice('ok-notice', Icon.check, `${countFiles(data.repaired)} ${plural(data.repaired, 'נכתב אל:', 'נכתבו אל:')}`,
      `<span style="direction:ltr;display:inline-block">${esc(data.output)}</span>`,
      t('כל עותק מתוקן נבדק שוב אחרי הכתיבה, והתוצאה המוצגת היא של הבדיקה החוזרת. הקבצים המקוריים לא שונו.'))}
    <div class="doc-list">${rows}</div>`;

  el('doctor-foot').innerHTML = `<button class="btn btn-primary" id="btn-doctor-done">${t('סיום')}</button>`;
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
      <p>${t('בונה אינדקס חדש לסרטון — תמונה אחר תמונה…')}</p>
      <p class="doc-meta" id="rebuild-percent"></p></div>`;
  el('doctor-foot').innerHTML = '';

  let r;
  try {
    r = await Bridge.call('doctor.rebuildVideo', { path, reference: ref.path, output }, 0);
  } catch (err) {
    el('doctor-body').innerHTML = errorNotice(t('בניית האינדקס לא הושלמה'), err);
    el('doctor-foot').innerHTML = `<button class="btn" id="btn-doctor-back">${t('חזרה לרשימה')}</button>`;
    el('btn-doctor-back').onclick = renderDoctorList;
    return;
  }

  el('doctor-body').innerHTML = `
    ${r.succeeded
      ? notice('ok-notice', Icon.check, t('הסרטון תוקן'), esc(r.message))
      : notice(r.output ? 'warn' : 'danger', Icon.alert, t(r.output ? 'הסרטון תוקן חלקית' : 'הסרטון לא תוקן'), esc(r.message))}
    <div class="doc-list"><div class="doc-row">
      <div class="doc-top"><span class="doc-name"><bdi>${esc(r.name)}</bdi></span></div>
      ${r.applied.length ? `<ul class="doc-issues fixed">${r.applied.map((a) => `<li>${esc(a)}</li>`).join('')}</ul>` : ''}
      ${r.output ? `<div class="doc-meta"><span class="ltr-inline">${esc(r.output)}</span></div>` : ''}
    </div></div>
    <p class="doc-hint">${t('הסרטון המקורי לא שונה. אם התמונה בסרטון המתוקן משובשת, כנראה שסרטון הדוגמה צולם בהגדרות אחרות (רזולוציה או קצב תמונות) — נסו סרטון אחר מאותו מכשיר.')}</p>`;

  el('doctor-foot').innerHTML = `
    ${r.folder ? `<button class="btn btn-primary" id="btn-doctor-open">${Icon.folder}<span>${t('פתיחת התיקייה')}</span></button>` : ''}
    <button class="btn" id="btn-doctor-back">${t('חזרה לרשימה')}</button>`;
  if (r.folder) el('btn-doctor-open').onclick = () => Bridge.call('recover.openFolder', { path: r.folder });
  el('btn-doctor-back').onclick = renderDoctorList;
}

/// תמונה שתחילתה נהרסה: תמונת ייחוס מאותה מצלמה, תיקיית יעד, ובנייה.
async function rebuildPhoto(path, kind) {
  const ext = path.includes('.') ? path.slice(path.lastIndexOf('.') + 1) : '';
  const database = kind === 'database';
  const ref = await Bridge.call('doctor.pickReference', { kind, ext }, 0);
  if (!ref.path) return;
  if (ref.problem) {
    const row = document.querySelector(`[data-rebuild-photo="${CSS.escape(path)}"]`)?.closest('.doc-row');
    row?.querySelector('.doc-ref-problem')?.remove();
    row?.insertAdjacentHTML('beforeend', `<div class="doc-meta danger-text doc-ref-problem">${esc(ref.problem)}</div>`);
    return;
  }

  const { path: output } = await Bridge.call('doctor.pickFolder', {}, 0);
  if (!output) return;

  el('doctor-body').innerHTML = `
    <div class="loading" style="height:200px"><div class="spinner"></div>
      <p>${t(database ? 'משחזר את המסד — טבלה אחר טבלה…' : 'בונה מחדש את תחילת התמונה…')}</p></div>`;
  el('doctor-foot').innerHTML = '';

  let r;
  try {
    r = await Bridge.call('doctor.rebuildPhoto', { path, reference: ref.path, output }, 0);
  } catch (err) {
    el('doctor-body').innerHTML = errorNotice(t(database ? 'שחזור המסד לא הושלם' : 'בניית התמונה לא הושלמה'), err);
    el('doctor-foot').innerHTML = `<button class="btn" id="btn-doctor-back">${t('חזרה לרשימה')}</button>`;
    el('btn-doctor-back').onclick = renderDoctorList;
    return;
  }

  el('doctor-body').innerHTML = `
    ${r.succeeded
      ? notice('ok-notice', Icon.check, t(database ? 'המסד שוחזר' : 'התמונה תוקנה'), esc(r.message))
      : notice(r.output ? 'warn' : 'danger', Icon.alert, database
          ? (r.output ? t('המסד שוחזר חלקית') : t('המסד לא שוחזר'))
          : (r.output ? t('התמונה תוקנה חלקית') : t('התמונה לא תוקנה')), esc(r.message))}
    <div class="doc-list"><div class="doc-row">
      <div class="doc-top"><span class="doc-name"><bdi>${esc(r.name)}</bdi></span></div>
      ${r.applied.length ? `<ul class="doc-issues fixed">${r.applied.map((a) => `<li>${esc(a)}</li>`).join('')}</ul>` : ''}
      ${r.output ? `<div class="doc-meta"><span class="ltr-inline">${esc(r.output)}</span></div>` : ''}
    </div></div>
    <p class="doc-hint">${database ? t('המסד המקורי לא שונה. המסד המשוחזר נבנה מחדש במנוע SQLite ועבר את בדיקת השלמות שלו.')
      : ext.toLowerCase() === 'jpg' || ext.toLowerCase() === 'jpeg'
      ? t('התמונה המקורית לא שונתה. אם הצבעים או הבהירות נראים שונים מהרגיל, המצלמה כנראה משנה את טבלאות הדחיסה מתמונה לתמונה — נסו תמונת דוגמה אחרת, רצוי כזו שצולמה סמוך לתמונה הפגומה.')
      : t('הקובץ המקורי לא שונה. פתחו את הקובץ המתוקן בתוכנת העריכה שלכם כדי לוודא שהוא נפתח.')}</p>`;

  el('doctor-foot').innerHTML = `
    ${r.folder ? `<button class="btn btn-primary" id="btn-doctor-open">${Icon.folder}<span>${t('פתיחת התיקייה')}</span></button>` : ''}
    <button class="btn" id="btn-doctor-back">${t('חזרה לרשימה')}</button>`;
  if (r.folder) el('btn-doctor-open').onclick = () => Bridge.call('recover.openFolder', { path: r.folder });
  el('btn-doctor-back').onclick = renderDoctorList;
}

Bridge.on('doctor.progress', ({ percent }) => {
  const box = el('rebuild-percent');
  if (box) box.textContent = `${Math.min(100, percent).toFixed(0)}%`;
});
