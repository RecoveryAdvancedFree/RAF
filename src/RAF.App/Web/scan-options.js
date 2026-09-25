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
const SCAN_MODES = [   // מתורגם בהצגה: מכאן
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
];                     // מתורגם בהצגה: עד כאן

/// הסבר טכני על כל סוגי הסריקה, מקופל כברירת מחדל.
function scanTechDetails() {
  return `
    <details class="scan-tech">
      <summary>${t('מה ההבדל בין הסריקות?')}</summary>
      <dl>${SCAN_MODES.map((m) => `<dt>${t(m.name)}</dt><dd>${t(m.tech)}</dd>`).join('')}</dl>
    </details>`;
}

/// מחיצת BitLocker. הדיסק הפיזי מחזיר רק תוכן מוצפן. שתי דרכים לקרוא אותה מפוענחת,
/// כדיסק נוסף ברשימה: דרך Windows, כשהנעילה כבר פתוחה שם — או בפענוח של התוכנה
/// עצמה, עם מפתח השחזור או הסיסמה (גם כש-Windows לא מצליח לפתוח אותה).
function openBitLockerPanel(disk, part) {
  if (!part.unlocked) {
    openLockedBitLockerPanel(disk, part);
    return;
  }
  const letter = part.letter ? `<bdi>${esc(part.letter)}</bdi>` : '';
  const body = `${notice('ok-notice', Icon.lock, t('הנעילה פתוחה'),
        t('התוכנה תקרא את הכונן {0} דרך Windows, שמפענח אותו. הוא יופיע ברשימה ככונן נוסף, ' +
          'ואפשר יהיה להריץ עליו כל סוג סריקה — גם סריקה מתקדמת.', letter),
        t('BitLocker מצפין את כל המחיצה, כולל המקום שבו יושבים קבצים שנמחקו. קריאה ישירה מהכונן ' +
        'מחזירה רק תוכן מוצפן; דרך Windows כל אזור נקרא מפוענח.'))}
       ${notice('info', Icon.shield, t('קריאה בלבד'),
        t('שום דבר לא ייכתב לכונן. אל תנעלו אותו מחדש ואל תנתקו אותו עד סוף השחזור.'))}`;

  el('panel').innerHTML = `
    ${bitLockerHead(disk, part)}
    <div class="panel-body">${body}<div id="bitlocker-status"></div></div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-bitlocker-open">${Icon.lock}<span>${t('פתיחה לסריקה')}</span></button>
      <button class="btn" id="btn-cancel-bitlocker">${t('ביטול')}</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-bitlocker').onclick = closePanel;

  const open = el('btn-bitlocker-open');
  open.onclick = async () => {
    open.disabled = true;
    try {
      const r = await Bridge.call('bitlocker.open', { disk: disk.number, part: part.index }, 0);
      await showOpenedVolume(r.number);
    } catch (err) {
      open.disabled = false;
      el('bitlocker-status').innerHTML = errorNotice(t('לא ניתן לפתוח את הכונן'), err, 'spaced');
    }
  };
}

function bitLockerHead(disk, part) {
  return `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · BitLocker · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>`;
}

/// הכונן המפוענח נוסף לרשימה, ומיד נפתחות אפשרויות הסריקה שלו.
async function showOpenedVolume(number) {
  closePanel();
  State.openDisks.add(number);
  await loadDisks();
  openScanPanel(number, 0);
}

/// כונן נעול: מקלידים את מפתח השחזור או את הסיסמה, והתוכנה מפענחת בעצמה — כך נפתח
/// גם כונן ש-Windows לא מצליח לפתוח: מחיצה שנמחקה, תמונת דיסק, או מערכת קבצים פגומה
/// בתוך ההצפנה. הדרך דרך Windows נשארת, מקופלת, למי שמעדיף אותה.
function openLockedBitLockerPanel(disk, part) {
  const letter = part.letter ? `<bdi>${esc(part.letter)}</bdi>` : '';
  const recoveryKeyWhere = t('<b>מפתח השחזור</b> הוא מספר של 48 ספרות. הוא נשמר בדרך כלל בחשבון Microsoft של מי שהגדיר את המחשב (בכתובת {0}), או הודפס ונשמר בקובץ כשההצפנה הופעלה.',
    '<span class="ltr-inline">aka.ms/myrecoverykey</span>');

  el('panel').innerHTML = `
    ${bitLockerHead(disk, part)}
    <div class="panel-body">
      ${notice('warn', Icon.lock, t('הכונן נעול ב-BitLocker'),
        t('התוכן שלו מוצפן. הקלידו את מפתח השחזור או את הסיסמה, והתוכנה תפענח אותו בעצמה — ' +
          'גם אם Windows לא מצליח לפתוח אותו. הכונן המפוענח יופיע ברשימה ככונן נוסף.'))}
      <div id="bitlocker-protectors" class="bitlocker-protectors">${t('בודק את הכונן…')}</div>
      <div class="section-label" style="margin-top:14px"><label for="bitlocker-key">${t('מפתח שחזור או סיסמה')}</label></div>
      <div class="key-field">
        <input type="password" class="bitlocker-key" id="bitlocker-key" dir="ltr" autocomplete="off" spellcheck="false"
               placeholder="000000-000000-000000-000000-000000-000000-000000-000000" aria-describedby="bitlocker-key-hint">
        <button class="btn btn-sm" id="btn-bitlocker-show" type="button" aria-pressed="false">${t('הצגה')}</button>
      </div>
      <p class="confirm-hint" id="bitlocker-key-hint">${recoveryKeyWhere}</p>
      <div id="bitlocker-status"></div>
      <details class="scan-tech">
        <summary>${t('אפשר גם לפתוח את הנעילה ב-Windows')}</summary>
        <ol class="image-steps">
          ${letter
            ? `<li>${t('פתחו את <b>סייר הקבצים</b> ולחצו פעמיים על הכונן <b>{0}</b>. Windows יבקש סיסמה או מפתח שחזור.', letter)}</li>`
            : `<li>${t('לכונן אין אות כונן, ולכן Windows לא מציע לפתוח אותו. אם הוא חיצוני — נתקו וחברו אותו מחדש; ' +
              'אחרת פתחו את <b>ניהול דיסקים</b> של Windows והקצו לו אות.')}</li>`}
          <li>${t('אחרי שהכונן נפתח, חזרו לכאן ולחצו <b>רענון</b>. ליד המחיצה יופיע "נעילה פתוחה".')}</li>
        </ol>
        <button class="btn btn-sm" id="btn-bitlocker-refresh">${Icon.refresh}<span>${t('רענון')}</span></button>
      </details>
      ${notice('info', Icon.info, t('בלי המפתח אין דרך לשחזר'),
        t('ההצפנה נועדה בדיוק לזה: בלי סיסמה או מפתח שחזור אף תוכנה לא יכולה לקרוא את הקבצים.'),
        t('המפתח משמש רק לפענוח בזמן שהתוכנה פתוחה. הוא לא נשמר בשום מקום, ושום דבר לא נכתב לכונן.'), 'spaced')}
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-bitlocker-unlock">${Icon.lock}<span>${t('פתיחה')}</span></button>
      <button class="btn" id="btn-cancel-bitlocker">${t('ביטול')}</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-bitlocker').onclick = closePanel;
  el('btn-bitlocker-refresh').onclick = async () => { closePanel(); await loadDisks(); };

  const input = el('bitlocker-key');
  const show = el('btn-bitlocker-show');
  show.onclick = () => {
    const visible = input.type === 'password';
    input.type = visible ? 'text' : 'password';
    show.setAttribute('aria-pressed', String(visible));
    show.textContent = visible ? t('הסתרה') : t('הצגה');
    input.focus();
  };

  const unlock = el('btn-bitlocker-unlock');
  let suspended = false;
  const status = el('bitlocker-status');

  unlock.onclick = async () => {
    if (!input.value.trim() && !suspended) {
      status.innerHTML = notice('warn', Icon.alert, '', t('הקלידו את מפתח השחזור או את הסיסמה של הכונן.'), null, 'spaced');
      input.focus();
      return;
    }
    unlock.disabled = input.disabled = true;
    status.innerHTML = `<p class="confirm-hint">${t('בודק את המפתח… זה לוקח כמה שניות.')}</p>`;
    try {
      const r = await Bridge.call('bitlocker.unlock', { disk: disk.number, part: part.index, key: input.value }, 0);
      input.value = '';
      await showOpenedVolume(r.number);
    } catch (err) {
      unlock.disabled = input.disabled = false;
      status.innerHTML = errorNotice(t('הכונן לא נפתח'), err, 'spaced');
      input.select();
    }
  };
  input.onkeydown = (e) => { if (e.key === 'Enter') unlock.click(); };
  input.focus();

  Bridge.call('bitlocker.inspect', { disk: disk.number, part: part.index }).then((info) => {
    const box = el('bitlocker-protectors');
    if (!box) return;
    if (info.problem) {
      box.innerHTML = notice('danger', Icon.alert, t('אזור הניהול של ההצפנה לא נקרא'), esc(info.problem),
        t('אם הכונן גוסס, כדאי ליצור ממנו תמונת דיסק ולנסות לפתוח את התמונה. אם גם זה לא עוזר — מעבדת שחזור.'), 'spaced');
      unlock.disabled = input.disabled = true;
      return;
    }
    suspended = info.suspended;
    box.innerHTML = info.suspended
      ? notice('ok-notice', Icon.lock, t('ההגנה על הכונן מושהית'),
          t('אפשר לפתוח אותו בלי מפתח — לחצו <b>פתיחה</b>.'), null, 'spaced')
      : info.protectors.length
      ? `<p class="confirm-hint">${t('הכונן ננעל עם: {0}.', info.protectors.map(esc).join(', '))}</p>` +
        (info.typedKey ? '' : notice('warn', Icon.info, t('אין לכונן הזה מפתח שאפשר להקליד'),
          t('הוא נפתח רק במחשב שבו הוצפן, או בקובץ מפתח. חברו אותו לאותו מחשב ופתחו אותו ב-Windows.'), null, 'spaced'))
      : '';
  }).catch(() => { const box = el('bitlocker-protectors'); if (box) box.textContent = ''; });
}

/// כונן שהוא חלק ממערך RAID של לינוקס (שרת אחסון ביתי): מחפשים את שאר הכוננים של המערך
/// בכל מה שברשימה, מראים מה נמצא ומה חסר, ומרכיבים. המערך יופיע ברשימה ככונן נוסף.
function openRaidPanel(disk, part) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · ${t('חלק ממערך RAID')} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('info', Icon.layers, t('הכונן הזה הוא חלק ממערך RAID'),
        t('שרתי אחסון ביתיים ושרתי לינוקס מפזרים את הקבצים על כמה כוננים. כל כונן לבד מחזיק רק חלקים — ' +
          'צריך להרכיב את המערך מכל הכוננים שלו. התוכנה עושה את זה בעצמה, בלי השרת, ושום דבר לא נכתב לכוננים.'))}
      <div id="raid-list" aria-live="polite"><p class="confirm-hint">${t('מחפש את שאר הכוננים של המערך…')}</p></div>
      <details class="scan-tech">
        <summary>${t('חסר כונן?')}</summary>
        <ol class="image-steps">
          <li>${t('חברו למחשב את כל הכוננים שהוצאו מהשרת — כל אחד בחיבור משלו או במתאם USB. אין צורך בסדר מסוים.')}</li>
          <li>${t('יצרתם קודם תמונות דיסק מהכוננים? פתחו את כולן ברשימת הכוננים.')}</li>
          <li>${t('לחצו <b>חיפוש שוב</b>.')}</li>
        </ol>
      </details>
    </div>
    <div class="panel-foot">
      <button class="btn" id="btn-raid-again">${Icon.refresh}<span>${t('חיפוש שוב')}</span></button>
      <button class="btn" id="btn-cancel-raid">${t('ביטול')}</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-raid').onclick = closePanel;
  el('btn-raid-again').onclick = async () => {
    el('raid-list').innerHTML = `<p class="confirm-hint">${t('מרענן את רשימת הכוננים ומחפש…')}</p>`;
    await loadDisks();
    findRaids(disk, part);
  };
  findRaids(disk, part);
}

async function findRaids(disk, part) {
  const box = el('raid-list');
  if (!box) return;
  let arrays;
  try {
    arrays = await Bridge.call('raid.find', {}, 0);
  } catch (err) {
    box.innerHTML = errorNotice(t('החיפוש נכשל'), err, 'spaced');
    return;
  }
  if (!el('raid-list')) return;

  // המערך של הכונן שנלחץ — ראשון.
  const mine = (a) => a.members.some((m) => m.disk === disk.number && m.offset === part.offset);
  arrays.sort((a, b) => mine(b) - mine(a));
  if (!arrays.length) {
    box.innerHTML = notice('warn', Icon.alert, t('לא נמצא מערך'),
      t('הכותרת של המערך לא נקראה מהכונן. ייתכן שהיא ניזוקה — אז <b>סריקה מתקדמת</b> של כל כונן בנפרד עדיין תמצא קבצים קטנים.'), null, 'spaced');
    return;
  }

  box.innerHTML = arrays.map((a) => {
    const title = a.name ? `${esc(a.name)} · ${esc(a.level)}` : esc(a.level);
    const facts = [t('{0} כוננים', a.disks), formatSize(a.size)];
    if (a.chunk) facts.push(t('רצועה של {0}', formatSize(a.chunk)));
    const members = a.members.map((m) =>
      `<li>${t('כונן {0} במערך:', m.role)} <b>${esc(m.title)}</b>${m.stale ? ` <span class="chip warn">${t('לא עדכני')}</span>` : ''}</li>`).join('');
    const missing = a.missing.length
      ? `<li class="raid-missing">${a.missing.length === 1 ? t('חסר: כונן {0} במערך', a.missing[0]) : t('חסרים: כוננים {0} במערך', a.missing.join(', '))}</li>`
      : '';
    const state = a.problem
      ? notice('danger', Icon.alert, t('אי אפשר להרכיב את המערך'), esc(a.problem), null, 'spaced')
      : a.missing.length
      ? notice('warn', Icon.info, t('אפשר להרכיב גם בלי הכונן החסר'),
          t('התוכן שלו מחושב מהכוננים האחרים. אם אפשר לחבר אותו — עדיף.'), null, 'spaced')
      : '';
    const button = a.problem ? ''
      : `<button class="btn btn-primary" data-raid-assemble="${esc(a.id)}">${Icon.layers}<span>${t(a.open ? 'מעבר למערך' : 'הרכבת המערך')}</span></button>`;
    return `
      <div class="raid-card">
        <div class="raid-title">${title}</div>
        <div class="confirm-hint">${facts.join(' · ')}</div>
        <ul class="raid-members">${members}${missing}</ul>
        ${state}
        ${button}
        <div data-raid-status="${esc(a.id)}"></div>
      </div>`;
  }).join('');

  box.querySelectorAll('[data-raid-assemble]').forEach((b) => {
    b.onclick = async () => {
      const id = b.dataset.raidAssemble;
      b.disabled = true;
      const status = box.querySelector(`[data-raid-status="${id}"]`);
      status.innerHTML = `<p class="confirm-hint">${t('מרכיב את המערך…')}</p>`;
      try {
        const r = await Bridge.call('raid.assemble', { id }, 0);
        await showOpenedVolume(r.number);
      } catch (err) {
        b.disabled = false;
        status.innerHTML = errorNotice(t('המערך לא הורכב'), err, 'spaced');
      }
    };
  });
}

/// מחיצה (או מערך) שמחולקת לאזורים בשכבה של לינוקס — "מאגר לוגי". בשרתי אחסון ביתיים היא
/// יושבת מעל המערך, ובהרבה מחשבי לינוקס — ישירות על הכונן. כל אזור נפתח ככונן נוסף.
function openLvmPanel(disk, part) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · ${t('מאגר לוגי')} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('info', Icon.layers, t('המחיצה מחולקת לאזורים'),
        t('לינוקס ושרתי אחסון ביתיים מחלקים מחיצה (או כמה מחיצות ביחד) לאזורים, וכל אזור הוא כמו כונן נפרד עם הקבצים שלו. ' +
          'בחרו אזור, והוא יופיע ברשימה ככונן נוסף, לקריאה בלבד.'))}
      <div id="lvm-list" aria-live="polite"><p class="confirm-hint">${t('קורא את תיאור האזורים…')}</p></div>
    </div>
    <div class="panel-foot">
      <button class="btn" id="btn-lvm-again">${Icon.refresh}<span>${t('חיפוש שוב')}</span></button>
      <button class="btn" id="btn-cancel-lvm">${t('ביטול')}</button>
    </div>`;

  el('overlay').hidden = false;
  el('panel-close').onclick = closePanel;
  el('btn-cancel-lvm').onclick = closePanel;
  el('btn-lvm-again').onclick = async () => {
    el('lvm-list').innerHTML = `<p class="confirm-hint">${t('מרענן את רשימת הכוננים ומחפש…')}</p>`;
    await loadDisks();
    findLvm();
  };
  findLvm();
}

async function findLvm() {
  const box = el('lvm-list');
  if (!box) return;
  let groups;
  try {
    groups = await Bridge.call('lvm.find', {}, 0);
  } catch (err) {
    box.innerHTML = errorNotice(t('החיפוש נכשל'), err, 'spaced');
    return;
  }
  if (!el('lvm-list')) return;
  if (!groups.length) {
    box.innerHTML = notice('warn', Icon.alert, t('תיאור האזורים לא נקרא'),
      t('ייתכן שהוא ניזוק. <b>סריקה מתקדמת</b> של המחיצה עדיין תמצא קבצים לפי סוג.'), null, 'spaced');
    return;
  }

  box.innerHTML = groups.map((g) => {
    const drives = g.pvs.map((title) => title
      ? `<li><b>${esc(title)}</b></li>`
      : `<li class="raid-missing">${t('כונן שלא נמצא')}</li>`).join('');
    const missing = g.missing
      ? notice('warn', Icon.info, t('חסרים כוננים במאגר'),
          t('אזורים שיושבים גם עליהם לא ייפתחו. חברו את כל הכוננים של השרת (או פתחו את התמונות שלהם) ולחצו <b>חיפוש שוב</b>.'), null, 'spaced')
      : '';
    const volumes = g.volumes.map((v) => `
      <div class="lvm-volume">
        <div class="grow"><b>${esc(v.name)}</b> · ${formatSize(v.size)}
          ${v.problem ? `<div class="confirm-hint">${esc(v.problem)}</div>` : ''}</div>
        ${v.problem ? '' : `<button class="btn btn-sm btn-primary" data-lvm-open="${esc(v.name)}" data-lvm-group="${esc(g.id)}">${t(v.open ? 'מעבר לאזור' : 'פתיחה')}</button>`}
      </div>`).join('');
    return `
      <div class="raid-card">
        <div class="raid-title">${esc(g.name)}</div>
        <div class="confirm-hint">${t('הכוננים של המאגר:')}</div>
        <ul class="raid-members">${drives}</ul>
        ${missing}
        ${volumes || `<p class="confirm-hint">${t('אין במאגר אזורים.')}</p>`}
        <div data-lvm-status="${esc(g.id)}"></div>
      </div>`;
  }).join('');

  box.querySelectorAll('[data-lvm-open]').forEach((b) => {
    b.onclick = async () => {
      const id = b.dataset.lvmGroup;
      b.disabled = true;
      const status = box.querySelector(`[data-lvm-status="${id}"]`);
      try {
        const r = await Bridge.call('lvm.open', { id, volume: b.dataset.lvmOpen }, 0);
        await showOpenedVolume(r.number);
      } catch (err) {
        b.disabled = false;
        status.innerHTML = errorNotice(t('האזור לא נפתח'), err, 'spaced');
      }
    };
  });
}

function findPart(diskNumber, partIndex) {
  const disk = State.disks.find((d) => d.number === diskNumber);
  if (!disk) return null;
  const part = disk.partitions.find((p) => p.index === partIndex);
  return part ? { disk, part } : null;
}

function partTitle(part) {
  return part.label || (part.letter ? t('כונן {0}', part.letter) : t('מחיצה {0}', part.index));
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

  // כונן במערך RAID: לבד הוא מחזיק רק חלקים — קודם מרכיבים את המערך.
  if (part.fs === 'LinuxRaid') {
    openRaidPanel(disk, part);
    return;
  }

  // מאגר לוגי: הקבצים באזורים שבתוכו — קודם פותחים אזור.
  if (part.fs === 'Lvm') {
    openLvmPanel(disk, part);
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
    <button class="scan-opt" data-mode="${m.id}" title="${esc(t(m.tech))}"${blocked ? ' disabled' : ''}>
      <div class="scan-opt-icon">${m.icon}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">${t(m.name)}${blocked ? `<span class="chip">${t('לא זמין')}</span>` : ''}</div>
        <div class="scan-opt-desc">${t(m.desc)}</div>
        <div class="scan-opt-time">${t(m.time)}</div>
      </div>
    </button>`;
  }).join('');

  const fsNotice = !part.scannable
    ? notice('warn', Icon.alert, t('מערכת הקבצים {0} אינה נתמכת', esc(t(part.fsLabel))),
        t('<b>סריקה מתקדמת</b> עדיין תעבוד — היא אינה תלויה במערכת הקבצים.'),
        t('נתמכות: NTFS, exFAT, FAT32, FAT16, FAT12, ext2/3/4, XFS ו-btrfs.'))
    : '';

  const readThroughNotice = part.readThrough
    ? notice('ok-notice', Icon.shield, t('המחיצה נקראת דרך עותק הגיבוי'),
        t('בחרו <b>סריקה מהירה</b> — יוצגו כל הקבצים עם השמות, ולא רק קבצים שנמחקו.'),
        t('תחילת המחיצה (מגזר האתחול) פגומה, והתוכנה קוראת אותה דרך עותק הגיבוי שלה — בזיכרון בלבד. שום דבר לא נכתב לכונן.'))
    : '';

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${esc(partTitle(part))}</div>
        <div class="panel-sub">${esc(disk.name)} · ${esc(t(part.fsLabel))} · ${formatSize(part.size)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${readThroughNotice}
      ${fsNotice}
      <div class="section-label">${t('בחרו סוג סריקה')}</div>
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
    <div class="section-label" style="margin-top:18px">${t('כונן חלש או שמשמיע רעשים?')}</div>
    <button class="scan-opt ${hdd ? '' : 'subtle'}" id="btn-image-part">
      <div class="scan-opt-icon">${Icon.copy}</div>
      <div class="scan-opt-body">
        <div class="scan-opt-title">${t('יצירת תמונה של המחיצה לפני הסריקה')}</div>
        <div class="scan-opt-desc">${t('מעתיקים את המחיצה פעם אחת לקובץ על כונן אחר, וסורקים את ההעתק — בלי לשחוק כונן שעלול להפסיק לעבוד.')}</div>
      </div>
    </button>`;
}



async function showStrategy(disk, part, modeId) {
  const mode = SCAN_MODES.find((m) => m.id === modeId);

  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${t(mode.name)}</div>
        <div class="panel-sub">${esc(partTitle(part))} · ${esc(disk.name)}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body"><div class="loading" style="height:180px">
      <div class="spinner"></div><p>${t('מחשב אסטרטגיית שחזור…')}</p></div></div>`;
  el('panel-close').onclick = closePanel;

  let profile;
  try {
    profile = await Bridge.call('scan.profile', { disk: disk.number, mode: modeId });
  } catch (err) {
    el('panel').querySelector('.panel-body').innerHTML = errorNotice(t('לא ניתן להכין את הסריקה'), err);
    return;
  }

  const cls = profile.outlook >= 70 ? 'good' : profile.outlook >= 40 ? 'mid' : 'low';
  const word = t(profile.outlook >= 70 ? 'גבוהים' : profile.outlook >= 40 ? 'בינוניים' : 'נמוכים');
  const warning = profile.warning
    ? `<div class="notice warn">${Icon.alert}<div>${esc(profile.warning)}</div></div>` : '';

  const health = healthOf(disk);
  const healthWarning = health && health.level !== 'Good'
    ? `<div class="health-warning">${healthNote(disk, false)}
        ${health.level === 'Bad' && disk.rawAccessible
          ? `<button class="btn btn-sm" id="btn-image-first">${Icon.copy}<span>${t('יצירת תמונה במקום סריקה ישירה')}</span></button>` : ''}
      </div>` : '';

  el('panel').querySelector('.panel-body').innerHTML = `
    ${healthWarning}
    ${warning}
    <div class="section-label">${t('אסטרטגיה שנבחרה אוטומטית')}</div>
    <div class="strategy">
      <p>${esc(profile.rationale)}</p>
      <div class="kv">
        <div><dt>${t('גודל בלוק קריאה')}</dt><dd>${profile.blockSizeKb} KB</dd></div>
        <div><dt>${t('ערוצי קריאה מקבילים')}</dt><dd>${profile.parallelism}</dd></div>
        <div><dt>${t('סדר סריקה')}</dt><dd class="words">${t(profile.sequential ? 'רציף' : 'חופשי')}</dd></div>
        <div><dt>${t('סוג אמצעי אחסון')}</dt><dd class="words">${esc(t(profile.mediaLabel))}</dd></div>
      </div>
      <div class="meter">
        <div class="meter-head">
          <span>${t('הערכת סיכויי שחזור:')} <b>${word}</b></span>
          <span style="direction:ltr;color:var(--text-faint)">${profile.outlook}%</span>
        </div>
        <div class="meter-track" role="meter" aria-label="${t('הערכת סיכויי שחזור')}" aria-valuemin="0" aria-valuemax="100"
             aria-valuenow="${profile.outlook}" aria-valuetext="${word}, ${profile.outlook}%"><div class="meter-fill ${cls}" style="width:${profile.outlook}%"></div></div>
      </div>
    </div>

    ${modeId === 3 ? `
    <div class="section-label" style="margin-top:16px">${t('אילו סוגי קבצים לחפש')}</div>
    <div class="type-picks" id="type-picks">
      ${CATEGORIES.filter((c) => c.id !== 'all').map((c) => `
        <button type="button" class="type-pick on" data-type="${c.id}">${Icon.check}<span>${t(c.label)}</span></button>`).join('')}
    </div>
    <p class="switch-note">${t('בחירה של סוגים מסוימים מקצרת את רשימת התוצאות ומתמקדת במה שמחפשים.')}</p>
    <div class="custom-types-line" id="custom-types-line"></div>` : ''}

    ${modeId === 3 && part.scannable ? `
    <label class="switch">
      <input type="checkbox" id="opt-free-only" checked>
      <span>${t('לסרוק רק את המקום הפנוי — מהיר בהרבה')}</span>
    </label>
    <p class="switch-note">${t('קבצים שנמחקו נמצאים במקום שמערכת הקבצים סימנה כפנוי, והמקום התפוס מכיל את הקבצים הקיימים. בכונן מלא ברובו הסריקה מהירה פי כמה. כבו אם מבנה המחיצה פגום.')}</p>` : ''}

    <label class="switch">
      <input type="checkbox" id="opt-include-existing"${part.readThrough ? ' checked' : ''}>
      <span>${t('הצג גם קבצים קיימים, ולא רק קבצים שנמחקו')}</span>
    </label>

    ${notice('info', Icon.shield, t('קריאה בלבד מהדיסק המקור'),
      t('השחזור יתאפשר רק לכונן אחר.'))}`;
  el('btn-image-first')?.addEventListener('click', () => openImagePanel(disk, null));

  el('panel').insertAdjacentHTML('beforeend', `
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-start">${Icon.bolt}<span>${t('התחלת סריקה')}</span></button>
      <button class="btn" id="btn-back">${t('חזרה')}</button>
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
    ${types.length ? `<span>${t('גם סוגים שהוספתם: {0}. הם נכללים ב"אחר".', types.map((ct) => `<b>${esc(ct.name)}</b>`).join(', '))}</span>` : ''}
    <button type="button" class="btn btn-sm" id="btn-custom-types">${Icon.file}<span>${t(types.length ? 'ניהול הסוגים שהוספתם' : 'הוספת סוג קובץ שהתוכנה לא מכירה')}</span></button>`;
  el('btn-custom-types').onclick = () => openCustomTypes(() => showStrategy(disk, part, modeId));
}

/// חלון הסוגים: הרשימה, ולימוד סוג חדש מקבצים לדוגמה.
async function openCustomTypes(back) {
  el('panel').innerHTML = `
    <div class="panel-head">
      <div class="grow">
        <div class="panel-title">${t('סוגי קבצים שהתוכנה לא מכירה')}</div>
        <div class="panel-sub">${t('מלמדים את הסריקה המתקדמת לחפש אותם')}</div>
      </div>
      <button class="panel-close" id="panel-close" aria-label="${t('סגירה')}">${Icon.close}</button>
    </div>
    <div class="panel-body">
      ${notice('info', Icon.info, t('איך זה עובד'),
        t('בוחרים כמה קבצים תקינים מאותו סוג — שלושה ומעלה, למשל מגיבוי או ממחשב אחר. ' +
        'התוכנה מוצאת מה זהה בתחילת כולם, ולפי זה הסריקה המתקדמת תמצא קבצים שנמחקו מהסוג הזה.'),
        t('הקבצים לדוגמה רק נקראים — הם לא משתנים ולא מועתקים לשום מקום.'))}
      <div id="learn-result"></div>
      <div class="section-label">${t('הסוגים שהוספתם')}</div>
      <div id="custom-types-list"></div>
    </div>
    <div class="panel-foot">
      <button class="btn btn-primary" id="btn-learn">${Icon.file}<span>${t('בחירת קבצים לדוגמה')}</span></button>
      <button class="btn" id="btn-types-back">${t('חזרה')}</button>
    </div>`;

  el('panel-close').onclick = closePanel;
  el('btn-types-back').onclick = back;

  const renderList = (types) => {
    el('custom-types-list').innerHTML = types.length
      ? types.map((ct) => `
        <div class="custom-type-row">
          <div class="grow"><b>${esc(ct.name)}</b> <span class="muted">· ‎.${esc(ct.extension)} · ${t('נלמד מ-{0} קבצים', ct.samples)}${ct.exactLength ? ' · ' + t('אורך מדויק') : ''}</span></div>
          <button type="button" class="btn btn-sm" data-remove="${esc(ct.extension)}">${t('הסרה')}</button>
        </div>`).join('')
      : `<p class="muted">${t('עדיין לא הוספתם סוגים.')}</p>`;
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
      el('learn-result').innerHTML = errorNotice(t('לא ניתן ללמוד מהקבצים'), err, 'spaced');
      return;
    }
    if (!r.picked) return;

    if (!r.ok) {
      el('learn-result').innerHTML = `<div class="notice warn spaced">${Icon.alert}<div>${esc(r.message)}</div></div>`;
      return;
    }

    el('learn-result').innerHTML = `
      <div class="notice ok-notice spaced">${Icon.check}<div>${esc(r.message)}</div></div>
      <div class="section-label">${t('שם לסוג — כך תיקרא התיקייה של הקבצים שיימצאו')}</div>
      <div class="target-row">
        <input type="text" class="name-input" id="custom-type-name" maxlength="60" value="${esc(r.name)}" aria-label="${t('שם לסוג')}">
        <button class="btn btn-primary" id="btn-save-type">${Icon.check}<span>${t('שמירה')}</span></button>
      </div>`;
    el('btn-save-type').onclick = async () => {
      renderList(await Bridge.call('types.save', { name: el('custom-type-name').value }));
      el('learn-result').innerHTML =
        `<div class="notice ok-notice tiny-notice">${Icon.check}<div>${t('נשמר. מעכשיו הסריקה המתקדמת תחפש גם את הסוג הזה.')}</div></div>`;
    };
  };
}
