/* ==========================================================================
   תמונות דיסק
   יצירה (כולל VHD) ופתיחה.
   ========================================================================== */

'use strict';

/* =====================================================================
   תמונת דיסק — יצירה ופתיחה
   ===================================================================== */

/// פתיחת קובץ תמונה והצגתו ברשימה כדיסק נוסף.
async function openImageFile(path) {
  try {
    const r = await Bridge.call('image.open', path ? { path } : {}, 0);
    if (r.number === null || r.number === undefined) return;
    State.openDisks.add(r.number);
    await loadDisks();

    const card = document.querySelector(`[data-close-image="${r.number}"]`);
    if (card) card.closest('.disk').scrollIntoView({ behavior: 'smooth', block: 'center' });
    setStatus('התמונה נפתחה — בחרו מחיצה מתוכה לסריקה');
  } catch (err) {
    el('content').insertAdjacentHTML('afterbegin',
      errorNotice('לא ניתן לפתוח את תמונת הדיסק', err));
  }
}

/// לוח יצירת תמונה — של דיסק שלם (part = null) או של מחיצה אחת.
function openImagePanel(disk, part) {
  const title = part ? partTitle(part) : disk.name;
  const size = part ? part.size : disk.size;
  const request = { disk: disk.number, part: part ? part.index : -1 };

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">יצירת תמונת דיסק</div>
        <div class="panel-sub">${esc(title)}${part ? ' · ' + esc(disk.name) : ''} · ${formatSize(size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      <div class="strategy">
        <p>${part ? 'המחיצה תועתק' : 'הדיסק כולו יועתק'} לקובץ על כונן אחר, וכל הסריקות
        ירוצו על ההעתק — הכונן המקורי כבר לא ייקרא.</p>
        <ol class="image-steps">
          <li><b>מעבר 1 — העתקה מהירה.</b> מדלגים על אזורים פגומים, ואוספים קודם את מה שנקרא בקלות.</li>
          <li><b>מעבר 2 — ניסיון חוזר.</b> חוזרים לאזורים שדולגו, וקוראים אותם בחלקים קטנים ככל האפשר.</li>
          <li><b>מעבר 3 — מהכיוון ההפוך.</b> מה שעדיין לא נקרא נקרא שוב מהסוף להתחלה — כך מצליחים לפעמים להציל עוד סקטורים בקצה של אזור פגום.</li>
        </ol>
        ${part ? '' : '<p>בשמירה אפשר לבחור גם <b>כונן וירטואלי (VHD)</b> — Windows יודע לחבר אותו בלחיצה כפולה, ואז מעתיקים ממנו קבצים בסייר.</p>'}
      </div>

      <div class="section-label">קובץ התמונה</div>
      <div class="target-row">
        <input type="text" id="image-path" readonly placeholder="לא נבחר קובץ" aria-label="קובץ התמונה">
        <button class="btn" id="btn-pick-image">${Icon.folder}<span>בחירה</span></button>
      </div>
      <div id="image-status"></div>

      ${notice('info', Icon.shield, 'קריאה בלבד מהכונן המקורי',
        'אזורים שלא ייקראו יתועדו בקובץ מפה לצד התמונה.',
        'אזור שלא נקרא נשמר בתמונה כאפסים, ואי אפשר להבחין בינו לבין אפסים אמיתיים. המפה מראה בדיוק מה חסר.',
        'spaced')}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start-image" disabled>${Icon.copy}<span>יצירת תמונת דיסק</span></button>
      <button class="btn" id="btn-cancel-image">ביטול</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-image').onclick = closePanel;

  el('btn-pick-image').onclick = async () => {
    const { path } = await Bridge.call('image.pickSave', request, 0);
    if (!path) return;

    el('image-path').value = path;
    const v = await Bridge.call('image.validate', { ...request, path });

    el('btn-start-image').disabled = !v.valid;
    el('image-status').innerHTML = !v.valid
      ? `<div class="notice danger tiny-notice">${Icon.alert}<div>${esc(v.error)}</div></div>`
      : v.existing ? existingImageChoice(v.existing)
      : `<div class="notice ok-notice tiny-notice">${Icon.check}
           <div>התמונה תתפוס ${formatSize(v.size)}. פנויים בכונן היעד ${formatSize(v.freeSpace)}.${
             /\.vhd$/i.test(path) ? ' זה כונן וירטואלי: אחרי ההעתקה אפשר לחבר אותו ב-Windows בלחיצה כפולה.' : ''}</div></div>`;
  };

  el('btn-start-image').onclick = () => {
    // בנתיב שיש בו תמונה קודמת — ההמשך הוא ברירת המחדל, כדי לא לקרוא שוב כונן גוסס.
    const mode = document.querySelector('input[name="image-mode"]:checked')?.value || 'new';
    startImaging(disk, part, {
      ...request, path: el('image-path').value,
      resume: mode !== 'new',
      retryUnreadable: mode === 'retry' || !!el('opt-retry-unreadable')?.checked,
    });
  };
}

/// בחירה מה לעשות עם תמונה קודמת באותו נתיב: להמשיך ממנה, לנסות שוב את
/// הסקטורים שלא נקראו, או להתחיל מחדש ולדרוס אותה.
function existingImageChoice(e) {
  if (!e.canResume) {
    return notice('warn', Icon.alert, 'בנתיב הזה כבר יש תמונה', esc(e.reason), '', 'tiny-notice');
  }

  const option = (value, checked, title, text) => `
    <label class="radio-opt">
      <input type="radio" name="image-mode" value="${value}"${checked ? ' checked' : ''}>
      <span><b>${title}</b><br><span class="faint">${text}</span></span>
    </label>`;

  // תמונה שהושלמה ונשארו בה רק סקטורים פגומים — אין מה "להמשיך", רק לנסות שוב.
  const onlyRetry = e.notCopied === 0;

  return `
    ${notice('info', Icon.info, 'בנתיב הזה יש תמונה קודמת של אותו מקור',
      onlyRetry
        ? `התמונה הושלמה, אבל ${formatSize(e.unreadable)} לא נקראו בה.`
        : `ההעתקה נעצרה לפני הסוף. עוד לא הועתקו: ${formatSize(e.notCopied)}.`,
      `התמונה הקודמת נוצרה מ: <bdi>${esc(e.source)}</bdi>. ודאו שזה אותו כונן.`, 'tiny-notice')}
    <div class="radio-group">
      ${onlyRetry
        ? option('retry', true, 'ניסיון חוזר באזורים שלא נקראו',
            'רק הם נקראים שוב. לפעמים כונן מצליח לקרוא אזור פגום בניסיון מאוחר יותר.')
        : option('resume', true, 'המשך מהנקודה שנעצרה',
            'רק מה שלא הועתק נקרא מהכונן. מה שכבר בתמונה נשאר כפי שהוא.')}
      ${option('new', false, 'התחלה מחדש', 'התמונה הקודמת תידרס, והכונן כולו ייקרא שוב.')}
    </div>
    ${!onlyRetry && e.unreadable > 0 ? `
      <label class="switch" style="margin-top:8px">
        <input type="checkbox" id="opt-retry-unreadable">
        <span>לנסות שוב גם את ${formatSize(e.unreadable)} שלא נקראו בפעם הקודמת</span>
      </label>` : ''}`;
}

async function startImaging(disk, part, request) {
  closePanel();
  const title = part ? partTitle(part) : disk.name;

  el('content').innerHTML = `
    <div class="scanning">
      <div class="scan-head">
        <div class="scan-icon">${Icon.copy}</div>
        <div>
          <div class="page-title">יצירת תמונת דיסק</div>
          <div class="page-desc">${esc(title)} ← <span class="ltr-inline">${esc(request.path)}</span></div>
        </div>
      </div>

      <div class="progress-card">
        <div class="progress-stage" id="img-stage">מתחיל…</div>
        <div class="progress-track"><div class="progress-fill" id="img-fill" style="width:0%"></div></div>
        <div class="progress-numbers">
          <span id="img-percent">0%</span>
          <span id="img-speed"></span>
        </div>

        ${SectorMapView.html('image')}

        <div class="kv" style="margin-top:18px">
          <div><dt>הועתק</dt><dd id="img-done">0 B</dd></div>
          <div><dt>טרם נקרא בהצלחה</dt><dd id="img-problems">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="img-elapsed">0:00</dd></div>
          <div><dt>זמן משוער שנותר</dt><dd id="img-eta" class="words">מחשב…</dd></div>
          <div><dt>מצב</dt><dd style="direction:rtl" id="img-state">פועל</dd></div>
        </div>
      </div>

      <div class="scan-actions">
        <button class="btn" id="btn-stop-image">${Icon.stop}<span>עצירה</span></button>
      </div>

      ${notice('info', Icon.info, 'אפשר לעצור בכל רגע',
        'מה שהועתק יישמר, וגם תמונה חלקית ניתנת לסריקה.')}
    </div>`;

  el('btn-stop-image').onclick = () => {
    el('img-state').textContent = 'עוצר…';
    Bridge.call('image.cancel');
  };

  setStatus('יוצר תמונה…');

  let r;
  try {
    r = await longCall('image.create', request);
  } catch (err) {
    el('content').innerHTML = `
      <div class="page-head"><div>
        <div class="page-title">יצירת התמונה נכשלה</div>
        <div class="page-desc">${esc(title)}</div>
      </div>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button></div>
      ${errorNotice('', err)}`;
    el('btn-home').onclick = loadDisks;
    setStatus('שגיאה');
    return;
  }

  showImageResult(title, r);
}

Bridge.on('image.progress', (p) => {
  const stage = el('img-stage');
  if (!stage) return;

  if (stage.textContent !== p.stage) stage.textContent = p.stage;
  const pct = Math.min(100, p.percent || 0);
  el('img-fill').style.width = pct + '%';
  el('img-percent').textContent = pct.toFixed(1) + '%';
  el('img-speed').textContent = p.speed > 0 ? formatSize(p.speed) + '/שנייה' : '';
  el('img-done').textContent = p.pass === 1
    ? `${formatSize(p.done)} מתוך ${formatSize(p.total)}`
    : `${p.pass === 2 ? 'ניסיון חוזר' : 'מהכיוון ההפוך'}: ${formatSize(p.done)} מתוך ${formatSize(p.total)}`;
  el('img-problems').textContent = formatSize(p.problems);
  el('img-problems').classList.toggle('warn-text', p.problems > 0);
  el('img-elapsed').textContent = formatDuration(p.elapsed);
  el('img-eta').textContent = Eta.text('img', p.percent, p.elapsed);
  SectorMapView.update('image', p.map);
});

function showImageResult(title, r) {
  const cls = r.cancelled ? 'warn' : r.unreadable > 0 ? 'warn' : 'ok-notice';
  const icon = cls === 'ok-notice' ? Icon.check : Icon.alert;

  el('content').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">${r.cancelled ? 'התמונה נעצרה' : 'התמונה נוצרה'}</div>
        <div class="page-desc">${esc(title)}</div>
      </div>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button>
    </div>

    <div class="notice ${cls}">${icon}<div>${esc(r.message)}</div></div>

    <div class="progress-card">
      <div class="kv">
        <div><dt>גודל התמונה</dt><dd>${formatSize(r.size)}</dd></div>
        <div><dt>לא נקרא מהכונן</dt><dd>${r.unreadable > 0 ? formatSize(r.unreadable) : 'אין'}</dd></div>
        <div><dt>לא הועתק</dt><dd>${r.notCopied > 0 ? formatSize(r.notCopied) : 'אין'}</dd></div>
        <div><dt>משך</dt><dd>${formatDuration(r.duration)}</dd></div>
      </div>
      <div class="image-files">
        <div><span>תמונה</span><span class="ltr-inline">${esc(r.path)}</span></div>
        <div><span>מפה</span><span class="ltr-inline">${esc(r.map)}</span></div>
      </div>
    </div>

    ${/\.vhd$/i.test(r.path) ? notice('info', Icon.info, 'לחבר את התמונה ב-Windows',
      'לחיצה כפולה על קובץ התמונה בסייר הקבצים מחברת אותו ככונן, והקבצים שבו נפתחים כרגיל — ' +
      'אפשר להעתיק מהם בלי לגעת שוב בכונן המקורי. כשמסיימים: לחיצה ימנית על הכונן בסייר ← הוצאה.',
      'Windows מחבר את התמונה לקריאה ולכתיבה. כדי לשמור אותה כמו שהיא, עדיף להעתיק ממנה ולא לשנות בה דבר.', 'spaced') : ''}

    <div class="scan-actions">
      <button class="btn btn-primary" id="btn-open-created">${Icon.open}<span>פתיחת התמונה לסריקה</span></button>
    </div>`;

  el('btn-home').onclick = loadDisks;
  el('btn-open-created').onclick = () => openImageFile(r.path);
  setStatus(r.cancelled ? 'התמונה נעצרה' : 'התמונה נוצרה');
}
