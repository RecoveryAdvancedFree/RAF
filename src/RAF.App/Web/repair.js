/* ==========================================================================
   תיקון מחיצה
   אבחון, מילת האישור לפני כתיבה לכונן, וביטול תיקון קודם.
   ========================================================================== */

'use strict';

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
        <input type="text" id="undo-path" readonly placeholder="לא נבחרה תיקייה" aria-label="תיקיית הגיבוי">
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
        <input type="text" id="undo-file" readonly placeholder="לא נבחר קובץ" aria-label="קובץ הביטול">
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
