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
