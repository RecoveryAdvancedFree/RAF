/* ==========================================================================
   מסך הכוננים
   רשימת הכוננים והמחיצות, "מה קרה?" והסריקות האחרונות.
   ========================================================================== */

'use strict';

/* =====================================================================
   מסך 1 — רשימת המחיצות
   ===================================================================== */

/// זהות כונן בין רענונים. מספר הדיסק ב-Windows משתנה כשמנתקים ומחברים, ולכן לא הוא.
const diskKey = (d) => `${d.name}|${d.size}`;

/// quiet — רענון במקום: הרשימה נשארת על המסך, רק סמל הרענון מסתובב, והכונן
/// שנוסף מודגש לרגע. כך נראים חיבור כונן וכפתור "רענון"; מסך טעינה מלא —
/// רק כשאין עדיין רשימה להציג.
async function loadDisks(options = {}) {
  const quiet = !!options.quiet && !!el('btn-refresh');
  Steps.set(1);

  if (quiet) el('btn-refresh').classList.add('is-busy');
  else el('content').innerHTML =
    `<div class="loading"><div class="spinner"></div><p>${t('סורק את אמצעי האחסון במערכת…')}</p></div>`;

  const before = new Set(State.disks.map(diskKey));

  try {
    // ברענון שקט הרשימה מגיעה לרוב תוך עשיריות שנייה — מהר מכדי לראות שמשהו
    // קרה. סיבוב אחד מלא של הסמל (0.8 שניות) מראה שהרשימה אכן נבדקה מחדש.
    const [data] = await Promise.all([
      Bridge.call('disks.list'),
      quiet ? new Promise((r) => setTimeout(r, 800)) : null,
    ]);
    State.disks = data.disks || [];
    State.failed = data.failed || [];
    State.elevated = !!data.elevated;

    const scroll = el('content').scrollTop;
    renderDisks();
    loadHealth();
    if (!quiet) return;

    el('content').scrollTop = scroll;
    State.disks.filter((d) => !before.has(diskKey(d))).forEach((d) =>
      document.querySelector(`.disk[data-disk-card="${d.number}"]`)?.classList.add('is-new'));
  } catch (err) {
    if (quiet) {
      el('btn-refresh')?.classList.remove('is-busy');
      setStatus(t('לא ניתן לרענן את רשימת הכוננים — ') + err.message);
      return;
    }
    el('content').innerHTML =
      errorNotice(t('לא ניתן לקרוא את רשימת הכוננים'), err);
    setStatus(t('שגיאה'));
  }
}

/* ---------- בריאות הכוננים (SMART) ---------- */

/// הבריאות נקראת אחרי הרשימה: כונן גוסס עלול להתעכב בתשובה, והרשימה לא מחכה לו.
async function loadHealth() {
  let list;
  try { list = await Bridge.call('disks.health'); } catch { return; }

  State.health = {};
  for (const h of list) {
    const disk = State.disks.find((d) => d.number === h.disk);
    if (disk) State.health[diskKey(disk)] = h;
  }

  for (const disk of State.disks) {
    const chip = document.querySelector(`[data-health-chip="${disk.number}"]`);
    const note = document.querySelector(`[data-health-note="${disk.number}"]`);
    if (chip) chip.innerHTML = healthChip(disk);
    if (note) {
      note.innerHTML = healthNote(disk);
      note.querySelector('[data-image-disk]')?.addEventListener('click', () => openImagePanel(disk, null));
    }
  }
}

const healthOf = (disk) => State.health[diskKey(disk)];

/// פרטי הבריאות במילים — לריחוף על השבב.
function healthDetails(h) {
  const parts = [];
  if (h.temperature != null) parts.push(t('טמפרטורה {0}°', h.temperature));
  if (h.powerOnHours != null) parts.push(t('{0} שעות פעולה', num(h.powerOnHours)));
  if (h.percentUsed != null) parts.push(t('{0}% מאורך החיים נוצלו', h.percentUsed));
  if (h.reallocated != null) parts.push(t('{0} סקטורים שהוחלפו', num(h.reallocated)));
  if (h.pending != null) parts.push(t('{0} סקטורים שאינם נקראים', num(h.pending)));
  return parts.join(' · ');
}

function healthChip(disk) {
  const h = healthOf(disk);
  if (!h) return '';
  const [cls, text] = h.level === 'Bad' ? ['danger', 'הכונן בסכנה']          // מתורגם בהצגה
    : h.level === 'Caution' ? ['warn', 'סימני שחיקה'] : ['ok', 'בריאות תקינה']; // מתורגם בהצגה
  const title = [...h.problems.map((p) => t(p)), healthDetails(h)].filter(Boolean).join('\n');
  return `<span class="chip ${cls}" title="${esc(title)}">${t(text)}</span>`;
}

/// אזהרה מתחת לכונן שמראה סימני כשל — עם ההמלצה ליצור תמונה ולסרוק ממנה.
function healthNote(disk, withButton = true) {
  const h = healthOf(disk);
  if (!h || h.level === 'Good') return '';
  const bad = h.level === 'Bad';
  const button = withButton && disk.rawAccessible
    ? ` <button class="link-btn" data-image-disk="${disk.number}">${t('יצירת תמונה של הכונן')}</button>` : '';
  return `<div class="disk-note ${bad ? 'danger' : 'warn'}">${Icon.alert}<span>
    <b>${t(bad ? 'הכונן מראה סימני כשל' : 'הכונן מראה סימני שחיקה')}:</b> ${esc(h.problems.map((p) => t(p)).join('; '))}.
    ${t(bad
      ? 'מומלץ ליצור קודם תמונה של הכונן — להעתיק אותו פעם אחת לקובץ — ולסרוק מהתמונה: כל קריאה נוספת מהכונן עלולה להחמיר את מצבו.'
      : 'כדאי לשחזר את הקבצים החשובים בהקדם. אם הסריקה נתקעת או איטית מאוד — עדיף ליצור תמונה של הכונן ולסרוק ממנה.')}${button}
  </span></div>`;
}

function renderDisks() {
  const totalParts = State.disks.reduce((n, d) => n + d.partitions.length, 0);

  let html = `
    <div class="page-head">
      <div>
        <div class="page-title">${t('הכוננים במערכת')}</div>
        <div class="page-desc">${t('לחצו על כונן כדי לראות את המחיצות שבו')}</div>
      </div>
      <div class="head-actions">
        <button class="btn" id="btn-open-scan">${Icon.history}<span>${t('פתיחת סריקה שמורה')}</span></button>
        <button class="btn" id="btn-open-image">${Icon.open}<span>${t('פתיחת תמונת דיסק')}</span></button>
        <button class="btn" id="btn-hw-raid" title="${t('כוננים שהוצאו ממחשב או משרת עם כרטיס RAID — התוכנה מזהה את המבנה ומרכיבה')}">${Icon.layers}<span>${t('הרכבת מערך')}</span></button>
        <button class="btn" id="btn-doctor">${Icon.wrench}<span>${t('תיקון קבצים שלא נפתחים')}</span></button>
        <button class="btn" id="btn-undo-repair">${Icon.back}<span>${t('ביטול תיקון קודם')}</span></button>
        <button class="btn" id="btn-refresh">${Icon.refresh}<span>${t('רענון')}</span></button>
      </div>
    </div>`;

  // הודעה חד-פעמית מפעולה קודמת (למשל תוצאת סריקת כונן).
  if (State.flash) {
    html += State.flash;
    State.flash = null;
  }

  if (!State.elevated) {
    html += notice('danger', Icon.alert,
      t('אין הרשאות מנהל — השחזור לא יעבוד'),
      t('סגרו את התוכנה והפעילו אותה מחדש: לחיצה ימנית ← "הפעל כמנהל".'),
      t('בלי הרשאות מנהל Windows מאפשר לראות רק את הקבצים הקיימים. קבצים שנמחקו נמצאים מתחת לרשימת הקבצים, ' +
      'ורק קריאה ישירה של הכונן מגיעה אליהם.'));
  }

  html += situationCards();

  if (State.disks.length === 0 && State.failed.length === 0) {
    html += `<div class="empty"><h3>${t('לא נמצאו אמצעי אחסון')}</h3><p>${t('ודאו שהדיסק מחובר ונסו לרענן.')}</p></div>`;
  } else {
    html += State.disks.map(renderDisk).join('');
  }

  html += renderFailedDevices();
  html += '<div id="recent-scans"></div>';

  el('content').innerHTML = html;

  el('btn-refresh').onclick = () => loadDisks({ quiet: true });
  el('btn-doctor').onclick = () => openDoctorPanel();
  el('btn-undo-repair').onclick = () => openUndoPanel();
  document.querySelectorAll('[data-situation]').forEach((btn) => {
    btn.onclick = () => openSituation(btn.dataset.situation);
  });
  el('btn-open-image').onclick = () => openImageFile();
  el('btn-hw-raid').onclick = () => openHardwareRaidPanel();
  el('btn-open-scan').onclick = () => openSavedScan();
  renderRecentScans();

  // לחיצה על שורת כונן פותחת או סוגרת את המחיצות שלו.
  document.querySelectorAll('[data-toggle-disk]').forEach((head) => {
    head.onclick = (e) => {
      if (e.target.closest('button')) return;
      toggleDisk(+head.dataset.toggleDisk);
    };
  });

  const byNumber = (n) => State.disks.find((d) => d.number === n);

  document.querySelectorAll('[data-image-disk]').forEach((btn) => {
    btn.onclick = () => { const d = byNumber(+btn.dataset.imageDisk); if (d) openImagePanel(d, null); };
  });

  document.querySelectorAll('[data-hunt-disk]').forEach((btn) => {
    btn.onclick = () => { const d = byNumber(+btn.dataset.huntDisk); if (d) openHuntPanel(d); };
  });

  document.querySelectorAll('[data-close-image]').forEach((btn) => {
    btn.onclick = async () => {
      await Bridge.call('image.close', { disk: +btn.dataset.closeImage });
      loadDisks();
    };
  });

  document.querySelectorAll('.part[data-disk]').forEach((row) => {
    row.onclick = () => openScanPanel(+row.dataset.disk, +row.dataset.part);
  });

  setStatus(t('{0} כוננים · {1} מחיצות', State.disks.length, totalParts));
}

/* ------------------------------------------------------ מה קרה? */

/// מי שהגיע לתוכנה בלחץ לא יודע אם צריך "סריקה עמוקה" או "סריקת כונן".
/// הוא יודע מה קרה לו — והכרטיסים מתרגמים את זה לפעולה הנכונה.
const SITUATIONS = [   // מתורגם בהצגה: מכאן
  { id: 'deleted',   icon: 'trash',  title: 'מחקתי קבצים',        sub: 'גם מסל המחזור' },
  { id: 'formatted', icon: 'eraser', title: 'פרמטתי כונן',        sub: 'או כרטיס זיכרון' },
  { id: 'asks',      icon: 'alert',  title: 'Windows מבקש לפרמט', sub: 'הכונן לא נפתח' },
  { id: 'missing',   icon: 'search', title: 'מחיצה נעלמה',        sub: 'הכונן נראה ריק' },
  { id: 'broken',    icon: 'wrench', title: 'קובץ לא נפתח',       sub: 'תמונה, מסמך, סרטון' },
  { id: 'unseen',    icon: 'unplug', title: 'הכונן לא מופיע',     sub: 'מחובר אבל לא מזוהה' },
];                     // מתורגם בהצגה: עד כאן

function situationCards() {
  return `
    <div class="section-label">${t('מה קרה?')}</div>
    <div class="situations">
      ${SITUATIONS.map((s) => `
        <button class="situation" data-situation="${s.id}">
          <span class="situation-icon">${Icon[s.icon]}</span>
          <span class="situation-text"><b>${t(s.title)}</b><small>${t(s.sub)}</small></span>
        </button>`).join('')}
    </div>`;
}

/// מחיצה ברשימה, כפתור שפותח אותה — לשימוש בתוך ההדרכה.
function partButton(disk, part) {
  const name = part.label || (part.letter ? t('כונן {0}', part.letter) : t(part.typeName || 'מחיצה'));
  return `<button class="btn" data-guide-part="${disk.number}:${part.index}">
            ${diskIcon(disk.media).html}<span><bdi>${esc(name)}</bdi> · <bdi>${esc(disk.name)}</bdi> · ${formatSize(part.size)}</span>
          </button>`;
}

function openSituation(id) {
  if (id === 'broken') { openDoctorPanel(); return; }

  const s = SITUATIONS.find((x) => x.id === id);
  const steps = (items) => `<ol class="guide-steps">${items.map((i) => `<li>${i}</li>`).join('')}</ol>`;
  const disks = State.disks.filter((d) => !d.unresponsive && d.rawAccessible);
  let body = '', actions = '';

  if (id === 'deleted') {
    body = notice('warn', Icon.alert, t('אל תשמרו שום דבר על הכונן שממנו נמחקו הקבצים'),
        t('כל קובץ חדש — גם הורדה או התקנה — עלול להיכתב בדיוק במקום של הקבצים שנמחקו.')) +
      steps([
        t('פתחו את הכונן ברשימה ולחצו על <b>המחיצה שבה היו הקבצים</b> (לרוב C: או D:).'),
        t('בחרו <b>סריקה מהירה</b>. היא לוקחת שניות עד דקות, ושומרת שמות ותיקיות.'),
        t('לא מצאתם? חזרו לאותה מחיצה ובחרו <b>סריקה עמוקה</b>.'),
        t('סמנו את הקבצים ושחזרו אותם — <b>לכונן אחר</b>.'),
      ]);
    actions = `<button class="btn btn-primary" data-guide-go>${Icon.drive}<span>${t('לרשימת הכוננים')}</span></button>`;
  } else if (id === 'formatted') {
    body = notice('warn', Icon.alert, t('אל תעתיקו קבצים חדשים לכונן שפורמט'),
        t('עד שהשחזור מסתיים, כל מה שנכתב אליו עלול לדרוס את מה שאפשר עוד להציל.'),
        t('פירמוט מהיר מוחק רק את רשימת הקבצים, והתוכן נשאר. פירמוט מלא (לא מהיר) ב-Windows 10 ומעלה ' +
        'כותב אפסים על כל הכונן, ואחריו אין מה לשחזר.')) +
      steps([
        t('לחצו על <b>המחיצה שפורמטה</b>.'),
        t('בחרו <b>סריקה עמוקה</b> — לעיתים היא מוצאת גם שמות ותיקיות מלפני הפירמוט.'),
        t('לא נמצא מספיק? בחרו <b>סריקה מתקדמת</b>. היא מזהה קבצים לפי התוכן שלהם ועובדת גם אחרי פירמוט, ' +
        'אבל בלי השמות המקוריים.'),
      ]);
    actions = `<button class="btn btn-primary" data-guide-go>${Icon.drive}<span>${t('לרשימת הכוננים')}</span></button>`;
  } else if (id === 'asks') {
    // מחיצת "שמור למערכת" (MSR) ריקה מלכתחילה, ואינה מחיצה ש-Windows מבקש לפרמט.
    const raw = disks.flatMap((d) => d.partitions
      .filter((p) => !p.scannable && !p.found && p.typeName !== 'שמור למערכת')   // לא לתרגום: השוואה לערך מהמנוע
      .map((p) => [d, p]));
    body = notice('danger', Icon.alert, t('אל תאשרו את הפירמוט'),
        t('Windows מבקש לפרמט כשתחילת המחיצה נפגעה — אבל הקבצים בדרך כלל עדיין שם, שלמים.')) +
      steps([
        t('לחצו על המחיצה ש-Windows לא מצליח לפתוח.'),
        t('התוכנה תבדוק אותה ותציע, לפי הסדר: <b>להעתיק את הקבצים בלי לכתוב לכונן</b>, לתקן את המחיצה, ' +
        'או לחפש קבצים לפי התוכן שלהם.'),
      ]) +
      (raw.length
        ? `<div class="section-label">${t('מחיצות שהתוכנה לא מצליחה לקרוא')}</div>
           <div class="guide-parts">${raw.map(([d, p]) => partButton(d, p)).join('')}</div>`
        : notice('info', Icon.info, t('כרגע כל המחיצות נקראות'),
            t('אם הכונן עדיין לא נפתח ב-Windows, ייתכן שהמחיצה נמחקה מהטבלה — נסו את "מחיצה נעלמה".')));
  } else if (id === 'missing') {
    body = steps([
        t('ליד הכונן לחצו <b>סריקת כונן</b>.'),
        t('התוכנה תעבור על כל הכונן ותחפש מחיצות שנמחקו מטבלת המחיצות — גם כשתחילתן נהרסה.'),
        t('מחיצה שנמצאה תופיע ברשימה. אפשר להעתיק ממנה קבצים בלי לכתוב לכונן, או להחזיר אותה לטבלה.'),
      ]) +
      (disks.length
        ? `<div class="section-label">${t('סריקת כונן')}</div>
           <div class="guide-parts">${disks.map((d) => `
             <button class="btn" data-guide-hunt="${d.number}">
               ${diskIcon(d.media).html}<span><bdi>${esc(d.name)}</bdi> · ${formatSize(d.size)}</span>
             </button>`).join('')}</div>`
        : '');
  } else if (id === 'unseen') {
    body = (State.failed.length
        ? notice('info', Icon.info,
            State.failed.length === 1 ? t('Windows מרגיש בהתקן אחד שלא הופעל')
                                      : t('Windows מרגיש ב-{0} התקנים שלא הופעלו', State.failed.length),
            t('הם מופיעים בתחתית רשימת הכוננים, עם הסבר ועצות.'))
        : '') +
      steps([
        t('חברו את הכונן ישירות למחשב, בלי מפצל USB, ונסו יציאה אחרת — עדיף בגב המחשב.'),
        t('כונן חיצוני גדול: נסו כבל אחר, וודאו שספק הכוח שלו מחובר אם יש לו.'),
        t('כרטיס זיכרון: נסו קורא כרטיסים אחר.'),
        t('לחצו <b>רענון</b>.'),
      ]) +
      notice('danger', Icon.alert, t('כונן שמשמיע נקישות או רעשים — נתקו אותו מיד'),
        t('כל הפעלה נוספת עלולה להרוס את מה שנשאר. זה מקרה למעבדת שחזור.'),
        t('כונן שהמחשב כלל אינו מרגיש שחובר אינו נראה לשום תוכנה; הבעיה בחומרה.'));
    actions = `<button class="btn btn-primary" data-guide-refresh>${Icon.refresh}<span>${t('רענון')}</span></button>`;
  }

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${t(s.title)}</div>
        <div class="panel-sub">${t('מה עושים עכשיו')}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body">${body}</div>
    <div class="panel-foot">${actions}<button class="btn" id="btn-guide-close">${t('סגירה')}</button></div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-guide-close').onclick = closePanel;

  const panel = el('panel');
  panel.querySelector('[data-guide-go]')?.addEventListener('click', () => {
    closePanel();
    State.disks.forEach((d) => State.openDisks.add(d.number));
    renderDisks();
    document.querySelector('.disk')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
  panel.querySelector('[data-guide-refresh]')?.addEventListener('click', () => { closePanel(); loadDisks({ quiet: true }); });
  panel.querySelectorAll('[data-guide-part]').forEach((btn) => {
    btn.onclick = () => {
      const [d, p] = btn.dataset.guidePart.split(':').map(Number);
      closePanel();
      openScanPanel(d, p);
    };
  });
  panel.querySelectorAll('[data-guide-hunt]').forEach((btn) => {
    btn.onclick = () => {
      const d = State.disks.find((x) => x.number === +btn.dataset.guideHunt);
      closePanel();
      if (d) openHuntPanel(d);
    };
  });
}

/// כונן חובר או נותק: רשימת הכוננים מתעדכנת מעצמה — אבל רק כשהיא מוצגת ואין
/// חלונית פתוחה, כדי לא לקטוע סריקה, בחירת קבצים או הדרכה באמצע.
Bridge.on('disks.changed', () => {
  if (Steps.busy || !el('overlay').hidden || !el('btn-refresh')) return;
  loadDisks({ quiet: true });
});

function toggleDisk(number) {
  if (State.openDisks.has(number)) State.openDisks.delete(number);
  else State.openDisks.add(number);

  const card = document.querySelector(`.disk[data-disk-card="${number}"]`);
  if (card) card.classList.toggle('open', State.openDisks.has(number));
  card?.querySelector('.disk-head')?.setAttribute('aria-expanded', String(State.openDisks.has(number)));
}

function renderDisk(disk) {
  const icon = diskIcon(disk.media);
  const open = State.openDisks.has(disk.number);

  // TRIM הוא המונח המוכר, ולכן הוא נשאר על השבב; ההסבר במילים פשוטות — בריחוף.
  const trimChip = disk.trim === 'Enabled'
    ? `<span class="chip warn" title="${t('הכונן מוחק מעצמו את התוכן של קבצים שנמחקו, ולכן סיכויי השחזור נמוכים יותר.')}">${t('TRIM פעיל')}</span>`
    : disk.trim === 'NotSupported'
      ? `<span class="chip ok" title="${t('הכונן אינו מוחק מעצמו את התוכן של קבצים שנמחקו — מצב טוב לשחזור.')}">${t('ללא TRIM')}</span>`
      : '';
  const stateChip = disk.unresponsive ? `<span class="chip danger">${t('לא מגיב')}</span>`
    : disk.rawAccessible ? '' : `<span class="chip danger">${t('אין גישה גולמית')}</span>`;

  const count = disk.partitions.length;
  const foundCount = disk.partitions.filter((p) => p.found).length;
  const countText = count === 0 ? t('ללא מחיצות')
    : count === 1 ? t('מחיצה אחת') : t('{0} מחיצות', count);

  const sub = disk.raid
    ? t('מערך RAID שהתוכנה הרכיבה · {0} · {1}', formatSize(disk.size), countText)
    : disk.lvm
    ? t('אזור במאגר לוגי · {0}', formatSize(disk.size))
    : disk.decrypted
    ? t('כונן מוצפן שהתוכנה מפענחת · {0}', formatSize(disk.size))
    : disk.isVolume
    ? t('כונן מוצפן שנקרא דרך Windows · {0}', formatSize(disk.size))
    : disk.isImage
    ? `<span class="ltr-inline">${esc(disk.imagePath)}</span> · ${formatSize(disk.size)} · ${countText}`
    : `${t('דיסק {0}', disk.number)} · ${esc(t(disk.mediaLabel))} · ${disk.size > 0 ? formatSize(disk.size) : t('גודל לא ידוע')} · ${countText}`;

  const actions = [];
  if (disk.rawAccessible && disk.size > 0) {
    actions.push(`<button class="btn btn-sm" data-hunt-disk="${disk.number}" title="${t('חיפוש מחיצות שנמחקו או שאבדו בכל הכונן')}">${Icon.search}<span>${t('סריקת כונן')}</span></button>`);
  }
  if (disk.isImage) {
    actions.push(`<button class="btn btn-sm" data-close-image="${disk.number}">${Icon.close}<span>${t(disk.isVolume || disk.raid || disk.lvm ? 'סגירה' : 'סגירת התמונה')}</span></button>`);
  } else if (disk.rawAccessible) {
    actions.push(`<button class="btn btn-sm" data-image-disk="${disk.number}" title="${t('העתקת הדיסק כולו לקובץ, וסריקה מתוכו')}">${Icon.copy}<span>${t('יצירת תמונת דיסק')}</span></button>`);
  }

  const notes = [];
  if (disk.unresponsive && disk.problem) {
    notes.push(`<div class="disk-note danger">${Icon.alert}<span>${esc(disk.problem)}</span></div>`);
  }
  if (disk.isImage && disk.imageNote) {
    notes.push(`<div class="disk-note ${disk.imageDamaged ? 'warn' : ''}">${disk.imageDamaged ? Icon.alert : Icon.info}<span>${esc(disk.imageNote)}</span></div>`);
  }
  if (!disk.isImage && !disk.unresponsive && disk.rawAccessible && disk.size === 0) {
    notes.push(`<div class="disk-note warn">${Icon.alert}<span>${t('הכונן מדווח על גודל 0. בקורא כרטיסים זה אומר בדרך כלל שאין כרטיס בפנים, או שהכרטיס אינו נקרא.')}</span></div>`);
  }

  let parts = disk.partitions.map((p) => renderPartition(disk, p)).join('');
  if (!parts) {
    parts = `<div class="part-empty">${t('לא נמצאו מחיצות בטבלת המחיצות של הכונן.')}
      ${disk.rawAccessible && disk.size > 0 ? t('היו בו מחיצות שנמחקו? לחצו על <b>סריקת כונן</b>.') : ''}</div>`;
  }

  return `
    <section class="disk ${open ? 'open' : ''}" data-disk-card="${disk.number}">
      <div class="disk-head" data-toggle-disk="${disk.number}" title="${t('לחצו להצגת המחיצות')}"
           role="button" tabindex="0" aria-expanded="${open}">
        <div class="disk-toggle">${Icon.chevron}</div>
        <div class="disk-icon ${icon.cls}">${icon.html}</div>
        <div class="disk-meta">
          <div class="disk-name">${esc(disk.name)}</div>
          <div class="disk-sub">${sub}</div>
        </div>
        <div class="chips">
          ${foundCount ? `<span class="chip accent">${foundCount === 1 ? t('נמצאה מחיצה אחת') : t('נמצאו {0} מחיצות', foundCount)}</span>` : ''}
          <span class="chip accent ltr">${esc(disk.mediaShort)}</span>
          ${disk.isImage || !disk.bus ? '' : `<span class="chip ltr">${esc(t(disk.bus))}</span>`}
          <span class="chip ltr">${esc(t(disk.scheme))}</span>
          ${disk.isImage ? '' : trimChip}${stateChip}
          <span class="health-slot" data-health-chip="${disk.number}">${disk.isImage ? '' : healthChip(disk)}</span>
        </div>
        <div class="disk-actions">${actions.join('')}</div>
      </div>
      ${notes.join('')}
      <div data-health-note="${disk.number}">${disk.isImage ? '' : healthNote(disk)}</div>
      <div class="parts">${parts}</div>
    </section>`;
}

function renderPartition(disk, p) {
  // ללא אות כונן — כוכבית, כמו בכלי הדיסקים של Windows.
  const letter = p.letter
    ? `<div class="part-letter">${esc(p.letter)}</div>`
    : disk.isImage
      ? `<div class="part-letter is-image">IMG</div>`
      : `<div class="part-letter unmounted" title="${t('למחיצה אין אות כונן')}">*</div>`;

  const name = p.label ? esc(p.label)
    : (p.letter ? t('כונן {0}', esc(p.letter)) : esc(t(p.typeName)) || t('מחיצה'));

  const tags = [`<span class="chip ltr">${esc(t(p.found && p.foundInfo ? p.foundInfo.fsLabel : p.fsLabel))}</span>`];
  const chip = (cls, text, title) => `<span class="chip${cls ? ' ' + cls : ''}"${title ? ` title="${t(title)}"` : ''}>${t(text)}</span>`;

  if (p.found) {
    tags.push(chip('accent', 'נמצאה בסריקה'));
    if (p.foundInfo && p.foundInfo.damaged) tags.push(chip('warn', 'תחילתה פגומה'));
    if (p.foundInfo && p.foundInfo.overlaps) tags.push(chip('warn', 'חופפת למחיצה קיימת'));
    if (p.foundInfo && p.foundInfo.inside) tags.push(chip('warn', 'בתוך מחיצה אחרת'));
  } else if (p.readThrough) {
    tags.push(chip('ok', 'נקראת דרך הגיבוי'));
  } else if (p.unmounted) {
    tags.push(chip('warn', 'לא מחוברת'));
  }
  if (p.found && p.readThrough) tags.push(chip('ok', 'נקראת דרך הגיבוי'));
  if (p.fs === 'BitLocker') {
    tags.push(p.unlocked
      ? chip('ok', 'נעילה פתוחה', 'הנעילה נפתחה ב-Windows — אפשר לסרוק')
      : chip('warn', 'נעול', 'הכונן מוצפן. פתחו אותו ב-Windows כדי לסרוק'));
  }
  if (p.fs === 'LinuxRaid') tags.push(chip('accent', 'לחצו להרכבת המערך', 'הכונן הזה הוא חלק ממערך RAID של שרת לינוקס או שרת אחסון ביתי'));
  if (p.fs === 'Lvm') tags.push(chip('accent', 'לחצו לפתיחת האזורים', 'המחיצה מחולקת לאזורים בשכבה של לינוקס — כל אזור נפתח ככונן נוסף'));
  if (p.bootable) tags.push(chip('', 'אתחול'));
  if (!p.found && !p.scannable && !['Unknown', 'BitLocker', 'LinuxRaid', 'Lvm'].includes(p.fs)) tags.push(chip('', 'לא נתמכת לסריקה'));

  let bar = `<div class="part-bar-wrap"><div class="part-bar-text">${t('נפח לא זמין')}</div></div>`;
  if (p.used !== null && p.used !== undefined && p.size > 0) {
    const pct = Math.min(100, Math.round((p.used / p.size) * 100));
    const cls = pct >= 92 ? 'full' : pct >= 78 ? 'high' : '';
    bar = `<div class="part-bar-wrap">
             <div class="part-bar"><i class="${cls}" style="width:${pct}%"></i></div>
             <div class="part-bar-text">${t('{0} בשימוש · {1}%', formatSize(p.used), pct)}</div>
           </div>`;
  }

  return `
    <div class="part ${p.found ? 'is-found' : ''}" data-disk="${disk.number}" data-part="${p.index}" role="button" tabindex="0">
      ${letter}
      <div class="part-info">
        <div class="part-title">${name}</div>
        <div class="part-tags">${tags.join('')}</div>
      </div>
      ${bar}
      <div class="part-size">${formatSize(p.size)}<small>${esc(t(p.typeName))}</small></div>
      <div class="part-arrow">${Icon.chevron}</div>
    </div>`;
}

/// סריקות שנשמרו אוטומטית. נטענות אחרי רשימת הכוננים, כי "מחובר" נקבע לפיה.
async function renderRecentScans() {
  let scans;
  try { scans = await Bridge.call('scan.recent'); } catch { return; }
  const box = el('recent-scans');
  if (!box) return;
  if (!scans.length) { box.innerHTML = ''; return; }

  box.innerHTML = `
    <div class="recent-head">
      <div class="section-label">${t('סריקות אחרונות')}</div>
      <div class="recent-clear" id="recent-clear">
        <button class="link-btn" data-clear="ask">${t('ניקוי הרשימה')}</button>
      </div>
    </div>
    <div class="recent-list">
      ${scans.map((s) => `
        <div class="recent-row">
        <button class="recent-scan" data-path="${esc(s.path)}">
          <div class="recent-icon">${Icon.history}</div>
          <div class="recent-body">
            <div class="recent-title">${esc(s.title)} · ${esc(t(s.mode))}</div>
            <div class="recent-sub">${esc(s.savedAt)} · ${countFiles(s.files)},
              ${num(s.recoverable)} ${plural(s.recoverable, 'ניתן לשחזור', 'ניתנים לשחזור')}${s.diskName ? ` · <bdi>${esc(s.diskName)}</bdi>` : ''}</div>
          </div>
          <div class="chips">
            ${s.resumePercent != null
              ? `<span class="chip warn" title="${t('נעצרה באמצע — אפשר להמשיך מאותה נקודה')}">${t('נעצרה ב-{0}%', s.resumePercent.toFixed(0))}</span>`
              : s.partial ? `<span class="chip warn" title="${t('נשמרה באמצע סריקה — לא כל המחיצה נסרקה')}">${t('חלקית')}</span>` : ''}
            ${s.connected ? `<span class="chip ok">${t('הכונן מחובר')}</span>`
                          : `<span class="chip" title="${t('אפשר לעיין ברשימה, אבל לא לשחזר')}">${t('הכונן לא מחובר')}</span>`}
          </div>
        </button>
        ${s.resumePercent != null && s.connected
          ? `<button class="btn recent-resume" data-resume="${esc(s.path)}" data-title="${esc(s.title)}"
               title="${t('נעצרה אחרי {0}% — המשך מאותה נקודה', s.resumePercent.toFixed(1))}">${Icon.play}<span>${t('המשך')}</span></button>`
          : ''}
        <button class="recent-remove" data-forget="${esc(s.path)}" title="${t('הסרה מהרשימה')}" aria-label="${t('הסרה מהרשימה')}">${Icon.close}</button>
        </div>`).join('')}
    </div>`;

  box.querySelectorAll('[data-path]').forEach((b) => { b.onclick = () => openSavedScan(b.dataset.path); });
  box.querySelectorAll('[data-forget]').forEach((b) => { b.onclick = () => forgetScans(b.dataset.forget); });
  box.querySelectorAll('[data-resume]').forEach((b) => { b.onclick = () => resumeScan(b.dataset.resume, b.dataset.title); });

  // ניקוי הכול — אישור במקום, בלי חלון: שתי מילים ליד הקישור.
  const clear = el('recent-clear');
  clear.onclick = (e) => {
    const action = e.target.closest('[data-clear]')?.dataset.clear;
    if (action === 'ask') {
      clear.innerHTML = `<span>${t('למחוק את כל הסריקות השמורות?')}</span>
        <button class="link-btn danger" data-clear="yes">${t('מחיקה')}</button>
        <button class="link-btn" data-clear="no">${t('ביטול')}</button>`;
    } else if (action === 'no') {
      clear.innerHTML = `<button class="link-btn" data-clear="ask">${t('ניקוי הרשימה')}</button>`;
    } else if (action === 'yes') {
      forgetScans(null);
    }
  };
}

/// הסרת סריקה שמורה מהרשימה (או כולן) — מוחקת את קובץ הסריקה בלבד, לא את הכונן ולא קבצים ששוחזרו.
async function forgetScans(path) {
  let error = null;
  try { await Bridge.call('scan.forget', path ? { path } : {}); }
  catch (err) { error = err; }
  await renderRecentScans();
  if (error) el('recent-scans')?.insertAdjacentHTML('afterbegin',
    errorNotice(t('לא ניתן להסיר את הסריקה'), error));
}

/// פתיחת סריקה שמורה — מהרשימה, או מקובץ שבוחרים. הבחירה שנשמרה איתה חוזרת.
async function openSavedScan(path) {
  el('content').innerHTML =
    `<div class="loading"><div class="spinner"></div><p>${t('פותח את הסריקה השמורה…')}</p></div>`;

  try {
    const summary = await longCall('scan.load', path ? { path } : {});
    if (summary.cancelled) { renderDisks(); return; }
    State.summary = summary;
    State.selection = { count: summary.selection.count, bytes: summary.selection.bytes };
    await renderResults();
  } catch (err) {
    State.flash = errorNotice(t('לא ניתן לפתוח את הסריקה'), err);
    renderDisks();
  }
}

/// התקנים ש-Windows מרגיש שהם מחוברים אך לא הצליח להפעיל — ולכן אינם כוננים ברשימה.
function renderFailedDevices() {
  if (!State.failed.length) return '';

  return `
    <div class="section-label" style="margin:26px 0 10px;text-transform:none">${t('התקנים מחוברים ש-Windows לא הצליח להפעיל')}</div>
    ${State.failed.map((f) => `
      <section class="disk failed-device">
        <div class="disk-head">
          <div class="disk-icon is-failed">${Icon.alert}</div>
          <div class="disk-meta">
            <div class="disk-name">${esc(f.name)}</div>
            <div class="disk-sub">${esc(f.problem)}</div>
          </div>
          <div class="chips"><span class="chip danger">${t('לא זמין לקריאה')}</span></div>
        </div>
        <div class="disk-note">${Icon.info}<span><b>${t('מה אפשר לנסות:')}</b> ${esc(f.advice)}
          ${f.windowsName && f.windowsName !== f.name ? `<br><span class="faint">${t('במנהל ההתקנים הוא מופיע בשם: {0}', esc(f.windowsName))}</span>` : ''}</span></div>
      </section>`).join('')}`;
}

/// כונן שענה באיחור: עדכון השורה שלו בלבד, בלי לאבד את מצב המסך.
Bridge.on('disks.updated', ({ disk }) => {
  const i = State.disks.findIndex((d) => d.number === disk.number);
  if (i < 0) return;
  State.disks[i] = disk;

  const card = document.querySelector(`.disk[data-disk-card="${disk.number}"]`);
  if (card && el('btn-refresh')) renderDisks();
});
