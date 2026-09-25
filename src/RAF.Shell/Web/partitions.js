/* ==========================================================================
   מחיצות שאבדו
   סריקת כונן לאיתור מחיצות, והחזרת מחיצה שנמצאה לטבלה.
   ========================================================================== */

'use strict';

/* =====================================================================
   סריקת כונן — איתור מחיצות שנמחקו או שאבדו
   ===================================================================== */

function openHuntPanel(disk) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${t('סריקת כונן')}</div>
        <div class="panel-sub">${esc(disk.name)} · ${formatSize(disk.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body">
      <div class="strategy">
        <p>${t('מחפש מחיצות שנמחקו או שאבדו — אחרי מחיקה בטעות, התקנה מחדש, או כשהכונן מופיע פתאום "לא מאותחל".')}</p>
        <p>${t('מחיצה שנמחקה מהטבלה עדיין על הכונן, עם כל הקבצים. מה שיימצא יופיע ברשימה, ואפשר יהיה להעתיק ממנו קבצים או להחזיר אותו לטבלה.')}</p>
      </div>
      ${notice('info', Icon.shield, t('קריאה בלבד — שום דבר לא נכתב לכונן'),
        t('בכונן גדול זה לוקח זמן. אפשר לעצור בכל רגע, ומה שנמצא עד אז יוצג.'))}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start-hunt">${Icon.search}<span>${t('התחלת סריקה')}</span></button>
      <button class="btn" id="btn-cancel-hunt">${t('ביטול')}</button>
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
          <div class="page-title">${t('סריקת כונן')}</div>
          <div class="page-desc">${esc(disk.name)} · ${formatSize(disk.size)}</div>
        </div>
      </div>

      <div class="progress-card">
        <div class="progress-stage">${t('מחפש מחיצות בכל הכונן')}</div>
        <div class="progress-track"><div class="progress-fill" id="hunt-fill" style="width:0%"></div></div>
        <div class="progress-numbers">
          <span id="hunt-percent">0%</span>
          <span id="hunt-speed"></span>
        </div>

        ${SectorMapView.html('hunt')}
        <div class="kv" style="margin-top:18px">
          <div><dt>${t('מחיצות שנמצאו')}</dt><dd id="hunt-found">0</dd></div>
          <div><dt>${t('נקרא מהכונן')}</dt><dd id="hunt-done">0 B</dd></div>
          <div><dt>${t('זמן שחלף')}</dt><dd id="hunt-elapsed">0:00</dd></div>
          <div><dt>${t('זמן משוער שנותר')}</dt><dd id="hunt-eta" class="words">${t('מחשב…')}</dd></div>
          <div><dt>${t('מצב')}</dt><dd class="words" id="hunt-state">${t('פועל')}</dd></div>
        </div>
      </div>

      <div class="scan-actions">
        <button class="btn" id="btn-stop-hunt">${Icon.stop}<span>${t('עצירה')}</span></button>
      </div>
    </div>`;

  el('btn-stop-hunt').onclick = () => {
    el('hunt-state').textContent = t('עוצר…');
    Bridge.call('disk.huntCancel');
  };

  setStatus(t('סורק את הכונן…'));

  try {
    const r = await longCall('disk.hunt', { disk: disk.number });

    const i = State.disks.findIndex((d) => d.number === disk.number);
    if (i >= 0) State.disks[i] = r.disk;
    State.openDisks.add(disk.number);

    const stopped = r.cancelled ? t(' (הסריקה נעצרה לפני סופה)') : '';

    // שאריות בתוך מחיצות אחרות אינן מוצגות — רק מוזכרות, כדי שיהיה ברור שנבדקו.
    const hidden = r.hidden > 0
      ? (r.hidden === 1
          ? t('נמצאה גם שארית אחת של מערכת קבצים בתוך מחיצה אחרת — בדרך כלל קובץ ISO שנשמר על הכונן. היא אינה מחיצה, ולכן לא הוצגה.')
          : t('נמצאו גם {0} שאריות של מערכות קבצים בתוך מחיצות אחרות — בדרך כלל קבצי ISO שנשמרו על הכונן. הן אינן מחיצות, ולכן לא הוצגו.', r.hidden))
      : '';
    // מחיצה רשומה שלא נקראה (Windows מבקש לפרמט), וסריקת הכונן בנתה מחדש את מגזר האתחול שלה.
    const rebuilt = r.rebuiltInPlace > 0
      ? notice('ok-notice', Icon.check,
          r.rebuiltInPlace === 1 ? t('מחיצה שלא נקראה נבנתה מחדש') : t('{0} מחיצות שלא נקראו נבנו מחדש', r.rebuiltInPlace),
          t('מגזר האתחול אבד, ולכן Windows לא מזהה את המחיצה ומבקש לפרמט. הוא חושב מחדש מרשומות הקבצים — בזיכרון בלבד, בלי לכתוב לכונן. עכשיו אפשר לסרוק אותה ולשחזר קבצים עם השמות והתיקיות.'))
      : '';
    State.flash = rebuilt + (r.found > 0
      ? notice('ok-notice', Icon.check,
          (r.found === 1 ? t('נמצאה מחיצה אחת ב{0}', esc(disk.name)) : t('נמצאו {0} מחיצות ב{1}', r.found, esc(disk.name))) + stopped,
          t('הן מסומנות "נמצאה בסריקה". לחצו על מחיצה כדי להעתיק ממנה קבצים או להחזיר אותה לטבלה.'),
          hidden)
      : rebuilt ? '' : notice('warn', Icon.info,
          t('לא נמצאו מחיצות אבודות ב{0}', esc(disk.name)) + stopped,
          t('הקבצים עדיין חסרים? נסו <b>סריקה מתקדמת</b> על אחת המחיצות — היא מוצאת קבצים לפי סוגם.'),
          hidden));

    Steps.set(1);  // מה שנמצא מוצג כמחיצות לבחירה
    renderDisks();
  } catch (err) {
    el('content').innerHTML = `
      <div class="page-head"><div>
        <div class="page-title">${t('סריקת הכונן נכשלה')}</div>
        <div class="page-desc">${esc(disk.name)}</div>
      </div>
      <button class="btn" id="btn-home">${Icon.back}<span>${t('חזרה לכוננים')}</span></button></div>
      ${errorNotice('', err)}`;
    el('btn-home').onclick = loadDisks;
    setStatus(t('שגיאה'));
  }
}

Bridge.on('hunt.progress', (p) => {
  const fill = el('hunt-fill');
  if (!fill) return;

  const pct = Math.min(100, p.percent || 0);
  fill.style.width = pct + '%';
  el('hunt-percent').textContent = pct.toFixed(1) + '%';
  el('hunt-speed').textContent = p.speed > 0 ? formatSize(p.speed) + t('/שנייה') : '';
  el('hunt-found').textContent = num(p.found);
  el('hunt-done').textContent = t('{0} מתוך {1}', formatSize(p.done), formatSize(p.total));
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
    <div class="section-label" style="margin-top:18px">${t('החזרת המחיצה')}</div>
    <button class="scan-opt subtle" id="btn-restore-part">
      <div class="scan-opt-icon">${Icon.layers}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">${t('החזרת המחיצה לטבלת המחיצות')}</div>
        <div class="scan-opt-desc">${t('כדי ש-Windows יראה אותה שוב, עם אות כונן. כותב לכונן — כדאי להעתיק קודם את הקבצים החשובים.')}</div>
      </div>
    </button>`;
}

async function openRestorePanel(disk, part) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${t('החזרת מחיצה לטבלה')}</div>
        <div class="panel-sub">${esc(partTitle(part))} · ${esc(disk.name)} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body"><div class="loading" style="height:180px">
      <div class="spinner"></div><p>${t('בודק את טבלת המחיצות של הכונן…')}</p></div></div>`;

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
      ? errorNotice(t('לא ניתן לבדוק אם אפשר להחזיר את המחיצה'), plan.error)
      : `<div class="notice warn">${Icon.alert}<div>${esc(plan.explanation)}</div></div>`;
    el('panel').insertAdjacentHTML('beforeend', `
      <div class="panel-foot"><button class="btn" id="btn-back-restore">${t('חזרה')}</button></div>`);
    el('btn-back-restore').onclick = () => openScanPanel(disk.number, part.index);
    return;
  }

  el('panel').querySelector('.panel-body').innerHTML = `
    <div class="notice ok-notice">${Icon.check}<div>${esc(plan.explanation)}</div></div>
    <div class="strategy"><p>${esc(plan.whatWillChange)}</p></div>
    ${notice('warn', Icon.alert, t('הפעולה כותבת לכונן'),
      t('יש במחיצה קבצים חשובים? העתיקו אותם קודם: סגרו את החלון ובחרו סריקה.'),
      t('לפני הכתיבה נשמר גיבוי של כל מה שעומד להשתנות בכונן. אם משהו ישתבש, התוכנה תחזיר את המצב הקודם אוטומטית.'))}

    <div class="section-label">${t('תיקיית גיבוי — על כונן אחר')}</div>
    <div class="target-row">
      <input type="text" id="restore-undo" readonly placeholder="${t('לא נבחרה תיקייה')}" aria-label="${t('תיקיית הגיבוי')}">
      <button class="btn" id="btn-pick-restore-undo">${Icon.folder}<span>${t('בחירה')}</span></button>
    </div>
    <div id="restore-status"></div>

    ${confirmWordField('restore-confirm')}`;

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-do-restore" disabled>${Icon.layers}<span>${t('החזרה לטבלה')}</span></button>
      <button class="btn" id="btn-back-restore">${t('חזרה')}</button>
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
      `<div class="notice ok-notice tiny-notice">${Icon.check}<div>${t('הגיבוי יישמר כאן.')}</div></div>`;
  };

  el('btn-do-restore').onclick = async () => {
    const undoFolder = el('restore-undo').value;
    const confirm = el('restore-confirm').value.trim();
    el('panel').querySelector('.panel-body').innerHTML =
      `<div class="loading" style="height:180px"><div class="spinner"></div><p>${t('מחזיר את המחיצה לטבלה…')}</p></div>`;
    el('panel').querySelector('.panel-foot').innerHTML = '';

    let r;
    try {
      r = await Bridge.call('partition.restore', { disk: disk.number, part: part.index, undoFolder, confirm }, 0);
    } catch (err) {
      r = { succeeded: false, error: err };
    }

    const cls = r.succeeded ? 'ok-notice' : r.rolledBack ? 'warn' : 'danger';
    el('panel').querySelector('.panel-body').innerHTML = r.error
      ? errorNotice(t('החזרת המחיצה לא הושלמה'), r.error)
      : `<div class="notice ${cls}">${r.succeeded ? Icon.check : Icon.alert}<div>${esc(r.message)}</div></div>`;
    el('panel').querySelector('.panel-foot').innerHTML =
      `<button class="btn btn-primary" id="btn-done-restore">${t('סיום')}</button>`;
    el('btn-done-restore').onclick = () => { closePanel(); State.openDisks.add(disk.number); loadDisks(); };
  };
}
