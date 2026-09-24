/* ==========================================================================
   סריקה
   מסך הסריקה בזמן שהיא רצה, ומפת הסקטורים.
   ========================================================================== */

'use strict';

/* =====================================================================
   מסך 2 — סריקה מתבצעת
   ===================================================================== */

async function startScan(disk, part, modeId, includeExisting, freeSpaceOnly = false, types = null) {
  closePanel();
  State.scan = { disk, part, mode: modeId };
  showScanScreen(modeId, `${partTitle(part)} · ${disk.name}`);
  await runScan('scan.start', {
    disk: disk.number, part: part.index, mode: modeId, includeExisting, freeSpaceOnly, types,
  }, partTitle(part));
}

/// המשך של סריקה מתקדמת שנעצרה — מהסריקה הנוכחית (בלי נתיב) או מ"סריקות אחרונות".
async function resumeScan(path, title) {
  closePanel();
  showScanScreen(3, `${title} · המשך מהנקודה שבה נעצרה`);
  await runScan('scan.resume', path ? { path } : {}, title);
}

/// מסך הסריקה. בסריקה מתקדמת יש גם השהיה: הסריקה נשמרת ואפשר להמשיך אחר כך.
function showScanScreen(modeId, subtitle) {
  Steps.set(2);
  State.selection = { count: 0, bytes: 0 };
  const mode = SCAN_MODES.find((m) => m.id === modeId);
  const pausable = modeId === 3;

  el('content').innerHTML = `
    <div class="scanning">
      <div class="scan-head">
        <div class="scan-icon">${mode.icon}</div>
        <div>
          <div class="page-title">${mode.name}</div>
          <div class="page-desc">${esc(subtitle)}</div>
        </div>
      </div>

      <div class="progress-card">
        <div class="progress-stage" id="scan-stage">מתחיל…</div>
        <div class="progress-track"><div class="progress-fill" id="scan-fill" style="width:0%"></div></div>
        <div class="progress-numbers">
          <span id="scan-percent">0%</span>
          <span id="scan-speed"></span>
        </div>

        ${SectorMapView.html('scan')}

        <div class="kv" style="margin-top:18px">
          <div><dt>קבצים שנמצאו</dt><dd id="scan-files">0</dd></div>
          <div><dt>נקרא מהדיסק</dt><dd id="scan-bytes">0 B</dd></div>
          <div><dt>זמן שחלף</dt><dd id="scan-elapsed">0:00</dd></div>
          <div><dt>זמן משוער שנותר</dt><dd id="scan-eta" class="words">מחשב…</dd></div>
          <div><dt>מצב</dt><dd style="direction:rtl" id="scan-state">פועל</dd></div>
        </div>
      </div>

      <div class="scan-actions">
        ${pausable ? `<button class="btn" id="btn-pause-scan">${Icon.pause}<span>השהיה</span></button>` : ''}
        <button class="btn" id="btn-cancel-scan">${Icon.stop}<span>עצירת הסריקה</span></button>
      </div>

      ${pausable
        ? notice('info', Icon.info, 'אפשר להשהות ולהמשיך אחר כך',
            'בהשהיה הסריקה נשמרת עם הנקודה שבה עצרה — אפשר להמשיך עכשיו, או גם אחרי סגירת התוכנה, ' +
            'מ"סריקות אחרונות". בעצירה מוצג מה שנמצא עד אז.')
        : notice('info', Icon.info, 'אפשר לעצור בכל רגע',
            'מה שנמצא עד אז יוצג, ואפשר יהיה לשחזר אותו.')}
    </div>`;

  el('btn-cancel-scan').onclick = () => {
    el('scan-state').textContent = 'עוצר…';
    Bridge.call('scan.cancel');
  };
  if (pausable) {
    el('btn-pause-scan').onclick = () => {
      el('scan-state').textContent = 'משהה…';
      el('btn-pause-scan').disabled = true;
      Bridge.call('scan.pause');
    };
  }

  setStatus('סורק…');
}

async function runScan(method, params, title) {
  try {
    // ללא מגבלת זמן: סריקה עמוקה על דיסק גדול עשויה להימשך שעות.
    const summary = await longCall(method, params);

    if (summary.paused) { showPaused(summary, title); return; }

    State.summary = summary;
    await renderResults();
  } catch (err) {
    const resuming = method === 'scan.resume';
    el('content').innerHTML = `
      <div class="page-head"><div>
        <div class="page-title">${resuming ? 'אי אפשר להמשיך את הסריקה כרגע' : 'הסריקה נכשלה'}</div>
        <div class="page-desc">${esc(title)}</div>
      </div>
      <div class="head-actions">
        ${resuming ? `<button class="btn btn-primary" id="btn-retry">${Icon.play}<span>ניסיון נוסף</span></button>` : ''}
        <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button>
      </div></div>
      ${errorNotice('', err)}
      ${resuming ? '<p class="doc-hint">הסריקה עצמה שמורה, עם הנקודה שבה נעצרה — אפשר להמשיך גם אחר כך, מ"סריקות אחרונות".</p>' : ''}`;
    el('btn-home').onclick = loadDisks;
    if (resuming) el('btn-retry').onclick = () => resumeScan(params.path || null, title);
    setStatus(resuming ? 'אי אפשר להמשיך כרגע' : 'שגיאה');
  }
}

/// סריקה מושהית: נשמרה עם נקודת ההמשך. ממשיכים עכשיו, מציגים את מה שנמצא, או חוזרים.
function showPaused(p, title) {
  el('content').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">${p.disconnected ? 'הכונן נותק באמצע הסריקה' : 'הסריקה מושהית'}</div>
        <div class="page-desc">${esc(title)} · נסרקו ${p.percent.toFixed(1)}% · ${countFiles(p.files)} נמצאו עד כה</div>
      </div>
    </div>
    ${p.disconnected
      ? notice('warn', Icon.unplug, 'הסריקה נשמרה עם הנקודה שבה עצרה',
          'חברו את הכונן שוב ולחצו "המשך הסריקה" — היא תמשיך מאותה נקודה. ' +
          'אפשר גם לסגור את התוכנה ולהמשיך אחר כך, מ"סריקות אחרונות" במסך הכוננים.')
      : notice('ok-notice', Icon.check, 'הסריקה נשמרה עם הנקודה שבה עצרה',
          'אפשר להמשיך עכשיו, או לסגור את התוכנה ולהמשיך אחר כך — מ"סריקות אחרונות" במסך הכוננים.')}
    <div class="scan-actions" style="margin-top:16px">
      <button class="btn btn-primary" id="btn-resume">${Icon.play}<span>המשך הסריקה</span></button>
      <button class="btn" id="btn-show-found">${Icon.list}<span>הצגת מה שנמצא עד כה</span></button>
      <button class="btn" id="btn-home">${Icon.back}<span>חזרה לכוננים</span></button>
    </div>`;

  el('btn-resume').onclick = () => resumeScan(null, title);
  el('btn-show-found').onclick = async () => {
    State.summary = await Bridge.call('scan.summary');
    await renderResults();
  };
  el('btn-home').onclick = loadDisks;
  setStatus(p.disconnected ? 'הכונן נותק — הסריקה נשמרה' : 'הסריקה מושהית');
}

/* =====================================================================
   מפת הסקטורים — בכל מעבר שעובר סקטור אחרי סקטור: סריקה מתקדמת, סריקת
   עומק, סריקת כונן ויצירת תמונה. כל ריבוע הוא חלק שווה של האזור הנסרק.
   ===================================================================== */

const SectorMapView = (() => {
  // המצבים כפי שהמנוע שולח אותם: תו '0' עד '5' לכל ריבוע.
  const COLORS = ['--border', '--border-strong', '--accent', '--ok', '--warn', '--danger'];
  const LEGENDS = {
    scan: { 2: 'נסרק', 3: 'נמצאו קבצים', 1: 'קבצים קיימים (דולג)', 5: 'לא ניתן לקריאה', 0: 'טרם נסרק' },
    hunt: { 2: 'נבדק', 3: 'נמצאה מחיצה', 5: 'לא ניתן לקריאה', 0: 'טרם נבדק' },
    image: { 2: 'הועתק', 4: 'ממתין לניסיון חוזר', 5: 'לא ניתן לקריאה', 0: 'טרם הועתק' },
  };
  const CELL = 8, GAP = 2, PITCH = CELL + GAP;

  function html(kind) {
    return `
      <div class="sector-map" id="map-${kind}" data-kind="${kind}" hidden>
        <div class="sm-head">
          <span class="section-label">מפת הסקטורים</span>
          <div class="sm-legend"></div>
        </div>
        <div class="sm-canvas-wrap">
          <canvas></canvas>
          <div class="sm-cursor" hidden></div>
        </div>
      </div>`;
  }

  /// מיקום הריבוע: מימין לשמאל ומלמעלה למטה, כמו קריאה בעברית.
  function place(box, i) {
    const col = i % box.cols, row = Math.floor(i / box.cols);
    return { x: box.width - (col + 1) * PITCH + GAP, y: row * PITCH };
  }

  function draw(root) {
    const data = root._map;
    if (!data) return;
    const canvas = root.querySelector('canvas');
    const width = root.querySelector('.sm-canvas-wrap').clientWidth;
    if (width <= 0) return;

    const n = data.cells.length;
    const cols = Math.max(1, Math.floor((width + GAP) / PITCH));
    const rows = Math.ceil(n / cols);
    const height = rows * PITCH - GAP;
    const box = { cols, width, n };
    root._box = box;

    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(box.width * dpr) || canvas.height !== Math.round(height * dpr)) {
      canvas.width = Math.round(box.width * dpr);
      canvas.height = Math.round(height * dpr);
      canvas.style.width = box.width + 'px';
      canvas.style.height = height + 'px';
    }

    const css = getComputedStyle(document.documentElement);
    const colors = COLORS.map((v) => css.getPropertyValue(v).trim());
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, box.width, height);
    for (let i = 0; i < n; i++) {
      const s = data.cells.charCodeAt(i) - 48;
      const { x, y } = place(box, i);
      ctx.fillStyle = colors[s] || colors[0];
      ctx.fillRect(x, y, CELL, CELL);
    }

    const cursor = root.querySelector('.sm-cursor');
    cursor.hidden = !(data.cursor >= 0 && data.cursor < n);
    if (!cursor.hidden) {
      const { x, y } = place(box, data.cursor);
      cursor.style.left = (x - 2) + 'px';
      cursor.style.top = (y - 2) + 'px';
    }

    // מקרא: רק מצבים שיש להם משמעות כאן — "דולג" רק כשבאמת דולג משהו.
    const legend = LEGENDS[root.dataset.kind] || LEGENDS.scan;
    const present = new Set(data.cells);
    const items = Object.entries(legend)
      .filter(([s]) => s !== '1' || present.has('1'))
      .map(([s, label]) => `<span><i style="background:var(${COLORS[s]})"></i>${label}</span>`)
      .join('');
    const legendBox = root.querySelector('.sm-legend');
    if (legendBox._html !== items) legendBox.innerHTML = legendBox._html = items;
  }

  /// ריחוף: איזה אזור בכונן הריבוע מייצג, ומה מצבו.
  function hover(root, e) {
    const box = root._box, data = root._map;
    if (!box || !data) return;
    const rect = e.target.getBoundingClientRect();
    const col = Math.floor((box.width - (e.clientX - rect.left)) / PITCH);
    const row = Math.floor((e.clientY - rect.top) / PITCH);
    const i = row * box.cols + col;
    if (col < 0 || col >= box.cols || i < 0 || i >= box.n) { e.target.title = ''; return; }

    const start = Math.ceil(i * data.length / box.n), end = Math.ceil((i + 1) * data.length / box.n);
    const legend = LEGENDS[root.dataset.kind] || LEGENDS.scan;
    const state = legend[data.cells[i]] || '';
    e.target.title = `${formatSize(start)} – ${formatSize(end)}${state ? ' · ' + state : ''}`;
  }

  /// עדכון מהודעת התקדמות. בלי מפה (שלב שאינו עובר סקטור אחרי סקטור) — המפה מוסתרת.
  function update(kind, map) {
    const root = el('map-' + kind);
    if (!root) return;
    root.hidden = !map;
    if (!map) return;
    root._map = map;

    if (!root._wired) {
      root._wired = true;
      const canvas = root.querySelector('canvas');
      canvas.addEventListener('mousemove', (e) => hover(root, e));
      new ResizeObserver(() => draw(root)).observe(root.querySelector('.sm-canvas-wrap'));
    }
    draw(root);
  }

  return { html, update };
})();

Bridge.on('scan.progress', (p) => {
  const stage = el('scan-stage');
  if (!stage) return;

  if (stage.textContent !== (p.stage || '')) stage.textContent = p.stage || '';

  const pct = p.percent === null || p.percent === undefined ? null : Math.min(100, p.percent);
  el('scan-fill').style.width = (pct === null ? 100 : pct) + '%';
  el('scan-fill').classList.toggle('indeterminate', pct === null);
  el('scan-percent').textContent = pct === null ? '' : pct.toFixed(1) + '%';

  el('scan-speed').textContent = p.speed > 0 ? formatSize(p.speed) + '/שנייה' : '';
  el('scan-files').textContent = (p.files || 0).toLocaleString('he-IL');
  el('scan-bytes').textContent = formatSize(p.bytes);
  el('scan-elapsed').textContent = formatDuration(p.elapsed);
  el('scan-eta').textContent = Eta.text('scan', pct, p.elapsed);
  SectorMapView.update('scan', p.map);
});
