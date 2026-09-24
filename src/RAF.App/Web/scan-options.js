/* ==========================================================================
   בחירת סריקה
   סוג הסריקה, האפשרויות שלה, וסוגי קבצים שהמשתמש מלמד את התוכנה.
   ========================================================================== */

'use strict';

/* =====================================================================
   לוח בחירת סוג הסריקה
   ===================================================================== */

/// desc — מתי לבחור בסריקה, במילים פשוטות. time — מה נשמר וכמה זמן.
/// tech — מה הסריקה עושה בפועל; מוצג בריחוף ובמתקפל "מה ההבדל?".
const SCAN_MODES = [
  {
    id: 1, name: 'סריקה מהירה', icon: Icon.bolt,
    desc: 'נמחק לאחרונה? התחילו כאן.',
    time: 'שמות ותיקיות נשמרים · שניות עד דקות',
    tech: 'קוראת את טבלת הקבצים של המחיצה ומאתרת קבצים שנמחקו אך הרשומה שלהם עדיין קיימת. ' +
          'ב-NTFS זו טבלת ה-MFT, וב-FAT וב-exFAT — רשומות התיקיות.',
  },
  {
    id: 2, name: 'סריקה עמוקה', icon: Icon.layers,
    desc: 'המהירה לא מצאה? נסו את זו.',
    time: 'רוב השמות נשמרים · דקות עד שעה',
    tech: 'עוברת בנוסף על כל המחיצה ומחפשת רשומות יתומות — רשומות של קבצים שהטבלה ' +
          'כבר אינה מצביעה עליהן — וקוראת את יומני מערכת הקבצים.',
  },
  {
    id: 3, name: 'סריקה מתקדמת', icon: Icon.radar,
    desc: 'אחרי פירמוט או נזק כבד.',
    time: 'בלי שמות מקוריים · שעה ומעלה',
    tech: 'קוראת את הכונן כולו ומזהה קבצים לפי חתימות HEX — הבתים הקבועים שבתחילת כל סוג קובץ — ' +
          'בלי תלות במערכת הקבצים. ' +
          'עובדת גם אחרי פירמוט, אבל שמות ותיקיות אינם נשמרים.',
  },
];

/// הסבר טכני על כל סוגי הסריקה, מקופל כברירת מחדל.
function scanTechDetails() {
  return `
    <details class="scan-tech">
      <summary>מה ההבדל בין הסריקות?</summary>
      <dl>${SCAN_MODES.map((m) => `<dt>${m.name}</dt><dd>${m.tech}</dd>`).join('')}</dl>
    </details>`;
}

/// מחיצת BitLocker. הדיסק הפיזי מחזיר רק תוכן מוצפן; אחרי שהנעילה נפתחת
/// ב-Windows, התוכנה קוראת את המחיצה דרך Windows — מפוענחת — כדיסק נוסף ברשימה.
function openBitLockerPanel(disk, part) {
  const letter = part.letter ? `<bdi>${esc(part.letter)}</bdi>` : '';
  const body = part.unlocked
    ? `${notice('ok-notice', Icon.lock, 'הנעילה פתוחה',
        `התוכנה תקרא את הכונן ${letter} דרך Windows, שמפענח אותו. הוא יופיע ברשימה ככונן נוסף, ` +
        'ואפשר יהיה להריץ עליו כל סוג סריקה — גם סריקה מתקדמת.',
        'BitLocker מצפין את כל המחיצה, כולל המקום שבו יושבים קבצים שנמחקו. קריאה ישירה מהכונן ' +
        'מחזירה רק תוכן מוצפן; דרך Windows כל אזור נקרא מפוענח.')}
       ${notice('info', Icon.shield, 'קריאה בלבד',
        'שום דבר לא ייכתב לכונן. אל תנעלו אותו מחדש ואל תנתקו אותו עד סוף השחזור.')}`
    : `${notice('warn', Icon.lock, 'הכונן נעול ב-BitLocker',
        'התוכן שלו מוצפן, ולכן אי אפשר לסרוק אותו לפני שפותחים את הנעילה ב-Windows.')}
       <div class="section-label" style="margin-top:14px">איך פותחים את הנעילה</div>
       <ol class="image-steps">
         ${letter
           ? `<li>פתחו את <b>סייר הקבצים</b> ולחצו פעמיים על הכונן <b>${letter}</b>. Windows יבקש סיסמה או מפתח שחזור.</li>`
           : '<li>לכונן אין אות כונן, ולכן Windows לא מציע לפתוח אותו. אם הוא חיצוני — נתקו וחברו אותו מחדש; ' +
             'אחרת פתחו את <b>ניהול דיסקים</b> של Windows והקצו לו אות.</li>'}
         <li>אין סיסמה? <b>מפתח השחזור</b> הוא מספר של 48 ספרות. הוא נשמר בדרך כלל בחשבון Microsoft של
           מי שהגדיר את המחשב (בכתובת <span class="ltr-inline">aka.ms/myrecoverykey</span>), או הודפס ונשמר בקובץ כשההצפנה הופעלה.</li>
         <li>אחרי שהכונן נפתח, חזרו לכאן ולחצו <b>רענון</b>. ליד המחיצה יופיע "נעילה פתוחה".</li>
       </ol>
       ${notice('info', Icon.info, 'בלי המפתח אין דרך לשחזר',
        'ההצפנה נועדה בדיוק לזה: בלי סיסמה או מפתח שחזור אף תוכנה לא יכולה לקרוא את הקבצים.',
        'אם Windows לא מצליח לפתוח את הכונן גם עם המפתח הנכון, כנראה שאזור הניהול של ההצפנה ניזוק. ' +
        'במקרה כזה כדאי ליצור תמונת דיסק ולפנות למעבדת שחזור.', 'spaced')}`;

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · BitLocker · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">${body}<div id="bitlocker-status"></div></div>
    <div class="panel-foot">
      ${part.unlocked
        ? `<button class="btn btn-primary" id="btn-bitlocker-open">${Icon.lock}<span>פתיחה לסריקה</span></button>`
        : `<button class="btn btn-primary" id="btn-bitlocker-refresh">${Icon.refresh}<span>רענון</span></button>`}
      <button class="btn" id="btn-cancel-bitlocker">ביטול</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-bitlocker').onclick = closePanel;

  const refresh = el('btn-bitlocker-refresh');
  if (refresh) refresh.onclick = async () => { closePanel(); await loadDisks(); };

  const open = el('btn-bitlocker-open');
  if (open) open.onclick = async () => {
    open.disabled = true;
    try {
      const r = await Bridge.call('bitlocker.open', { disk: disk.number, part: part.index }, 0);
      closePanel();
      State.openDisks.add(r.number);
      await loadDisks();
      openScanPanel(r.number, 0);
    } catch (err) {
      open.disabled = false;
      el('bitlocker-status').innerHTML = errorNotice('לא ניתן לפתוח את הכונן', err, 'spaced');
    }
  };
}

function findPart(diskNumber, partIndex) {
  const disk = State.disks.find((d) => d.number === diskNumber);
  if (!disk) return null;
  const part = disk.partitions.find((p) => p.index === partIndex);
  return part ? { disk, part } : null;
}

function partTitle(part) {
  return part.label || (part.letter ? 'כונן ' + part.letter : 'מחיצה ' + part.index);
}

function openScanPanel(diskNumber, partIndex) {
  const found = findPart(diskNumber, partIndex);
  if (!found) return;
  const { disk, part } = found;

  // מחיצה מוצפנת: אבחון ותיקון לא רלוונטיים — וכתיבה אליה הייתה הורסת אותה.
  if (part.fs === 'BitLocker' && !part.found) {
    openBitLockerPanel(disk, part);
    return;
  }

  // מחיצה שמערכת הקבצים שלה אינה נקראת מקבלת קודם אבחון:
  // ייתכן שניתן לתקן אותה, וזה עדיף על שחזור קבצים בודדים.
  if (!part.scannable) {
    openRepairPanel(disk, part);
    return;
  }

  const options = SCAN_MODES.map((m) => {
    // סריקה מתקדמת אינה תלויה במערכת הקבצים ולכן תמיד זמינה.
    const blocked = !part.scannable && m.id !== 3;

    return `
    <button class="scan-opt" data-mode="${m.id}" title="${esc(m.tech)}"${blocked ? ' disabled' : ''}>
      <div class="scan-opt-icon">${m.icon}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">${m.name}${blocked ? '<span class="chip">לא זמין</span>' : ''}</div>
        <div class="scan-opt-desc">${m.desc}</div>
        <div class="scan-opt-time">${m.time}</div>
      </div>
    </button>`;
  }).join('');

  const fsNotice = !part.scannable
    ? notice('warn', Icon.alert, `מערכת הקבצים ${esc(part.fsLabel)} אינה נתמכת`,
        '<b>סריקה מתקדמת</b> עדיין תעבוד — היא אינה תלויה במערכת הקבצים.',
        'נתמכות: NTFS, exFAT, FAT32, FAT16 ו-FAT12.')
    : '';

  const readThroughNotice = part.readThrough
    ? notice('ok-notice', Icon.shield, 'המחיצה נקראת דרך עותק הגיבוי',
        'בחרו <b>סריקה מהירה</b> — יוצגו כל הקבצים עם השמות, ולא רק קבצים שנמחקו.',
        'תחילת המחיצה (מגזר האתחול) פגומה, והתוכנה קוראת אותה דרך עותק הגיבוי שלה — בזיכרון בלבד. שום דבר לא נכתב לכונן.')
    : '';

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · ${esc(part.fsLabel)} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${readThroughNotice}
      ${fsNotice}
      <div class="section-label">בחרו סוג סריקה</div>
      ${options}
      ${scanTechDetails()}
      ${imageOption(disk)}
      ${restoreOption(disk, part)}
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;

  document.querySelectorAll('.scan-opt[data-mode]:not([disabled])').forEach((btn) => {
    btn.onclick = () => showStrategy(disk, part, +btn.dataset.mode);
  });

  const imageBtn = el('btn-image-part');
  if (imageBtn) imageBtn.onclick = () => openImagePanel(disk, part);

  const restoreBtn = el('btn-restore-part');
  if (restoreBtn) restoreBtn.onclick = () => openRestorePanel(disk, part);
}

/// הצעה לגבות מחיצה לקובץ לפני הסריקה. בדיסק מגנטי היא מודגשת:
/// דיסק מגנטי שמתחיל להיכשל עלול לא לשרוד סריקה ארוכה.
function imageOption(disk) {
  if (disk.isImage || !disk.rawAccessible) return '';

  const hdd = disk.media === 'HardDisk';
  return `
    <div class="section-label" style="margin-top:18px">כונן חלש או שמשמיע רעשים?</div>
    <button class="scan-opt ${hdd ? '' : 'subtle'}" id="btn-image-part">
      <div class="scan-opt-icon">${Icon.copy}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">יצירת תמונה של המחיצה לפני הסריקה</div>
        <div class="scan-opt-desc">מעתיקים את המחיצה פעם אחת לקובץ על כונן אחר, וסורקים את ההעתק —
        בלי לשחוק כונן שעלול להפסיק לעבוד.</div>
      </div>
    </button>`;
}



async function showStrategy(disk, part, modeId) {
  const mode = SCAN_MODES.find((m) => m.id === modeId);

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${mode.name}</div>
        <div class="panel-sub">${esc(partTitle(part))} · ${esc(disk.name)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body"><div class="loading" style="height:180px">
      <div class="spinner"></div><p>מחשב אסטרטגיית שחזור…</p></div></div>`;
  el('panel-close').onclick = closePanel;

  let profile;
  try {
    profile = await Bridge.call('scan.profile', { disk: disk.number, mode: modeId });
  } catch (err) {
    el('panel').querySelector('.panel-body').innerHTML = errorNotice('לא ניתן להכין את הסריקה', err);
    return;
  }

  const cls = profile.outlook >= 70 ? 'good' : profile.outlook >= 40 ? 'mid' : 'low';
  const word = profile.outlook >= 70 ? 'גבוהים' : profile.outlook >= 40 ? 'בינוניים' : 'נמוכים';
  const warning = profile.warning
    ? `<div class="notice warn">${Icon.alert}<div>${esc(profile.warning)}</div></div>` : '';

  const health = healthOf(disk);
  const healthWarning = health && health.level !== 'Good'
    ? `<div class="health-warning">${healthNote(disk, false)}
        ${health.level === 'Bad' && disk.rawAccessible
          ? `<button class="btn btn-sm" id="btn-image-first">${Icon.copy}<span>יצירת תמונה במקום סריקה ישירה</span></button>` : ''}
      </div>` : '';

  el('panel').querySelector('.panel-body').innerHTML = `
    ${healthWarning}
    ${warning}
    <div class="section-label">אסטרטגיה שנבחרה אוטומטית</div>
    <div class="strategy">
      <p>${esc(profile.rationale)}</p>
      <div class="kv">
        <div><dt>גודל בלוק קריאה</dt><dd>${profile.blockSizeKb} KB</dd></div>
        <div><dt>ערוצי קריאה מקבילים</dt><dd>${profile.parallelism}</dd></div>
        <div><dt>סדר סריקה</dt><dd style="direction:rtl">${profile.sequential ? 'רציף' : 'חופשי'}</dd></div>
        <div><dt>סוג אמצעי אחסון</dt><dd style="direction:rtl">${esc(profile.mediaLabel)}</dd></div>
      </div>
      <div class="meter">
        <div class="meter-head">
          <span>הערכת סיכויי שחזור: <b>${word}</b></span>
          <span style="direction:ltr;color:var(--text-faint)">${profile.outlook}%</span>
        </div>
        <div class="meter-track" role="meter" aria-label="הערכת סיכויי שחזור" aria-valuemin="0" aria-valuemax="100"
             aria-valuenow="${profile.outlook}" aria-valuetext="${word}, ${profile.outlook}%"><div class="meter-fill ${cls}" style="width:${profile.outlook}%"></div></div>
      </div>
    </div>

    ${modeId === 3 ? `
    <div class="section-label" style="margin-top:16px">אילו סוגי קבצים לחפש</div>
    <div class="type-picks" id="type-picks">
      ${CATEGORIES.filter((c) => c.id !== 'all').map((c) => `
        <button type="button" class="type-pick on" data-type="${c.id}">${Icon.check}<span>${c.label}</span></button>`).join('')}
    </div>
    <p class="switch-note">בחירה של סוגים מסוימים מקצרת את רשימת התוצאות ומתמקדת במה שמחפשים.</p>
    <div class="custom-types-line" id="custom-types-line"></div>` : ''}

    ${modeId === 3 && part.scannable ? `
    <label class="switch">
      <input type="checkbox" id="opt-free-only" checked>
      <span>לסרוק רק את המקום הפנוי — מהיר בהרבה</span>
    </label>
    <p class="switch-note">קבצים שנמחקו נמצאים במקום שמערכת הקבצים סימנה כפנוי, והמקום התפוס מכיל את הקבצים
      הקיימים. בכונן מלא ברובו הסריקה מהירה פי כמה. כבו אם מבנה המחיצה פגום.</p>` : ''}

    <label class="switch">
      <input type="checkbox" id="opt-include-existing"${part.readThrough ? ' checked' : ''}>
      <span>הצג גם קבצים קיימים, ולא רק קבצים שנמחקו</span>
    </label>

    ${notice('info', Icon.shield, 'קריאה בלבד מהדיסק המקור',
      'השחזור יתאפשר רק לכונן אחר.')}`;
  el('btn-image-first')?.addEventListener('click', () => openImagePanel(disk, null));

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start">${Icon.bolt}<span>התחלת סריקה</span></button>
      <button class="btn" id="btn-back">חזרה</button>
    </div>`);

  el('btn-back').onclick = () => openScanPanel(disk.number, part.index);
  // סוגי הקבצים: לחיצה מדליקה ומכבה; אי אפשר לכבות את כולם.
  document.querySelectorAll('.type-pick').forEach((b) => {
    b.onclick = () => {
      if (b.classList.contains('on') && document.querySelectorAll('.type-pick.on').length === 1) return;
      b.classList.toggle('on');
    };
  });

  if (modeId === 3) renderCustomTypesLine(disk, part, modeId);

  el('btn-start').onclick = () => startScan(disk, part, modeId, el('opt-include-existing').checked,
    !!el('opt-free-only')?.checked,
    [...document.querySelectorAll('.type-pick.on')].map((b) => b.dataset.type));
}


/* =====================================================================
   סוגי קבצים שהמשתמש מלמד את התוכנה
   ===================================================================== */

/// השורה שבאפשרויות הסריקה המתקדמת: מה נוסף, וכפתור להוספה.
async function renderCustomTypesLine(disk, part, modeId) {
  const line = el('custom-types-line');
  if (!line) return;
  let types = [];
  try { types = await Bridge.call('types.list', {}); } catch { /* בלי הרשימה — רק הכפתור */ }

  line.innerHTML = `
    ${types.length ? `<span>גם סוגים שהוספתם: ${types.map((t) => `<b>${esc(t.name)}</b>`).join(', ')}. הם נכללים ב"אחר".</span>` : ''}
    <button type="button" class="btn btn-sm" id="btn-custom-types">${Icon.file}<span>${types.length ? 'ניהול הסוגים שהוספתם' : 'הוספת סוג קובץ שהתוכנה לא מכירה'}</span></button>`;
  el('btn-custom-types').onclick = () => openCustomTypes(() => showStrategy(disk, part, modeId));
}

/// חלון הסוגים: הרשימה, ולימוד סוג חדש מקבצים לדוגמה.
async function openCustomTypes(back) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">סוגי קבצים שהתוכנה לא מכירה</div>
        <div class="panel-sub">מלמדים את הסריקה המתקדמת לחפש אותם</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="סגירה">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('info', Icon.info, 'איך זה עובד',
        'בוחרים כמה קבצים תקינים מאותו סוג — שלושה ומעלה, למשל מגיבוי או ממחשב אחר. ' +
        'התוכנה מוצאת מה זהה בתחילת כולם, ולפי זה הסריקה המתקדמת תמצא קבצים שנמחקו מהסוג הזה.',
        'הקבצים לדוגמה רק נקראים — הם לא משתנים ולא מועתקים לשום מקום.')}
      <div id="learn-result"></div>
      <div class="section-label">הסוגים שהוספתם</div>
      <div id="custom-types-list"></div>
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-learn">${Icon.file}<span>בחירת קבצים לדוגמה</span></button>
      <button class="btn" id="btn-types-back">חזרה</button>
    </div>`;

  el('panel-close').onclick = closePanel;
  el('btn-types-back').onclick = back;

  const renderList = (types) => {
    el('custom-types-list').innerHTML = types.length
      ? types.map((t) => `
        <div class="custom-type-row">
          <div class="grow"><b>${esc(t.name)}</b> <span class="muted">· ‎.${esc(t.extension)} · נלמד מ-${t.samples} קבצים${t.exactLength ? ' · אורך מדויק' : ''}</span></div>
          <button type="button" class="btn btn-sm" data-remove="${esc(t.extension)}">הסרה</button>
        </div>`).join('')
      : '<p class="muted">עדיין לא הוספתם סוגים.</p>';
    document.querySelectorAll('[data-remove]').forEach((b) => {
      b.onclick = async () => renderList(await Bridge.call('types.remove', { extension: b.dataset.remove }));
    });
  };
  renderList(await Bridge.call('types.list', {}));

  el('btn-learn').onclick = async () => {
    let r;
    try {
      r = await Bridge.call('types.learn', {}, 0);
    } catch (err) {
      el('learn-result').innerHTML = errorNotice('לא ניתן ללמוד מהקבצים', err, 'spaced');
      return;
    }
    if (!r.picked) return;

    if (!r.ok) {
      el('learn-result').innerHTML = `<div class="notice warn spaced">${Icon.alert}<div>${esc(r.message)}</div></div>`;
      return;
    }

    el('learn-result').innerHTML = `
      <div class="notice ok-notice spaced">${Icon.check}<div>${esc(r.message)}</div></div>
      <div class="section-label">שם לסוג — כך תיקרא התיקייה של הקבצים שיימצאו</div>
      <div class="target-row">
        <input type="text" class="name-input" id="custom-type-name" maxlength="60" value="${esc(r.name)}" aria-label="שם לסוג">
        <button class="btn btn-primary" id="btn-save-type">${Icon.check}<span>שמירה</span></button>
      </div>`;
    el('btn-save-type').onclick = async () => {
      renderList(await Bridge.call('types.save', { name: el('custom-type-name').value }));
      el('learn-result').innerHTML =
        `<div class="notice ok-notice tiny-notice">${Icon.check}<div>נשמר. מעכשיו הסריקה המתקדמת תחפש גם את הסוג הזה.</div></div>`;
    };
  };
}
