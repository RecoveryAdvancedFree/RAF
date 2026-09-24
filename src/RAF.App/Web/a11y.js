/* ==========================================================================
   נגישות
   מקלדת וקוראי מסך, לכל החלונות והמסכים.
   ========================================================================== */

'use strict';

/* =====================================================================
   נגישות: מקלדת וקוראי מסך
   ===================================================================== */

/// מה שקורה בכל חלון, בלי שכל חלון יצטרך לדאוג לזה בעצמו:
/// - בפתיחה המיקוד עובר לכותרת החלון, וקורא המסך מקריא אותה. גם כשתוכן החלון
///   מתחלף (שלב הבא באותו חלון) — אם המיקוד נשאר על משהו שנמחק.
/// - Tab מסתובב בתוך החלון, ולא בורח אל המסך שמאחוריו.
/// - בסגירה המיקוד חוזר למקום שממנו החלון נפתח.
/// ובכל התוכנה: אנטר ורווח מפעילים גם אלמנט שמתנהג ככפתור בלי להיות כפתור.
const A11y = (() => {
  const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), ' +
                    'textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])';
  const overlays = [el('help-overlay'), el('overlay')];
  const returnTo = new Map();

  const openPanel = () => overlays.find((o) => !o.hidden)?.querySelector('.panel');

  function focusTitle(panel) {
    const title = panel.querySelector('.panel-title, .help-title, h2, h3');
    if (title) {
      if (!title.id) title.id = panel.id + '-title';
      if (!panel.hasAttribute('aria-label')) panel.setAttribute('aria-labelledby', title.id);
      title.tabIndex = -1;
      title.focus({ preventScroll: true });
    } else {
      panel.tabIndex = -1;
      panel.focus({ preventScroll: true });
    }
  }

  for (const overlay of overlays) {
    const panel = overlay.querySelector('.panel');

    new MutationObserver(() => {
      if (!overlay.hidden) {
        if (!returnTo.has(overlay)) returnTo.set(overlay, document.activeElement);
        if (!panel.contains(document.activeElement)) focusTitle(panel);
      } else if (returnTo.has(overlay)) {
        const back = returnTo.get(overlay);
        returnTo.delete(overlay);
        if (back && back.isConnected && typeof back.focus === 'function') back.focus({ preventScroll: true });
      }
    }).observe(overlay, { attributes: true, attributeFilter: ['hidden'] });

    // תוכן חדש באותו חלון: המיקוד היה על כפתור שנמחק — חוזר לכותרת החדשה.
    new MutationObserver(() => {
      if (overlay.hidden) return;
      const title = panel.querySelector('.panel-title, .help-title');
      if (title && !title.id) title.id = panel.id + '-title';
      if (title && !panel.hasAttribute('aria-label')) panel.setAttribute('aria-labelledby', title.id);
      if (!panel.contains(document.activeElement)) focusTitle(panel);
    }).observe(panel, { childList: true });
  }

  document.addEventListener('keydown', (e) => {
    // Tab בתוך חלון פתוח
    if (e.key === 'Tab') {
      const panel = openPanel();
      if (!panel) return;
      const items = [...panel.querySelectorAll(FOCUSABLE)].filter((n) => n.getClientRects().length > 0);
      if (items.length === 0) { e.preventDefault(); return; }
      const first = items[0], last = items[items.length - 1];
      if (!panel.contains(document.activeElement)) { e.preventDefault(); first.focus(); }
      else if (e.shiftKey && (document.activeElement === first || !items.includes(document.activeElement) && document.activeElement.tabIndex === -1)) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      return;
    }

    // אנטר ורווח על אלמנט שמתנהג ככפתור
    if ((e.key === 'Enter' || e.key === ' ') && !e.repeat) {
      const t = e.target;
      if (t instanceof HTMLElement && t.getAttribute('role') === 'button' &&
          !['BUTTON', 'A', 'INPUT', 'SUMMARY', 'TEXTAREA', 'SELECT'].includes(t.tagName)) {
        e.preventDefault();
        t.click();
      }
    }
  });

  // פסי התקדמות: כל פס מזוהה כפס התקדמות, עם האחוז העדכני; השלב שמעליו מוקרא כשהוא מתחלף.
  let stages = 0;
  function tagProgress(root) {
    root.querySelectorAll?.('.progress-track:not([role])').forEach((track) => {
      track.setAttribute('role', 'progressbar');
      track.setAttribute('aria-valuemin', '0');
      track.setAttribute('aria-valuemax', '100');
      // השם של הפס הוא השלב שמעליו — ומתעדכן איתו.
      const stage = track.closest('.progress-card')?.querySelector('.progress-stage');
      if (stage) {
        if (!stage.id) stage.id = 'progress-stage-' + ++stages;
        track.setAttribute('aria-labelledby', stage.id);
      } else {
        track.setAttribute('aria-label', 'התקדמות');
      }
    });
    root.querySelectorAll?.('.progress-stage:not([aria-live])').forEach((s) => s.setAttribute('aria-live', 'polite'));
  }
  new MutationObserver((changes) => {
    for (const c of changes) {
      if (c.type === 'childList') { c.addedNodes.forEach((n) => n.nodeType === 1 && tagProgress(n)); continue; }
      const fill = c.target;
      if (!fill.classList?.contains('progress-fill')) continue;
      const track = fill.parentElement;
      if (!track.hasAttribute('role')) tagProgress(track.parentElement);
      const pct = Math.round(parseFloat(fill.style.width) || 0);
      if (fill.classList.contains('indeterminate')) track.removeAttribute('aria-valuenow');
      else if (track.getAttribute('aria-valuenow') !== String(pct)) track.setAttribute('aria-valuenow', String(pct));
    }
  }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'class'] });

  // עץ התיקיות ורשימת הקבצים מתחלפים עם כל תוצאות — החיבור ברמת המסמך.
  document.addEventListener('keydown', (e) => { if (e.target.closest?.('#tree')) treeKey(e); }, true);
  document.addEventListener('focusin', (e) => {
    // מי שמגיע ברשימה במקלדת בלי קובץ פעיל — מתחיל בראשון.
    if (e.target.id === 'filelist' && !e.target.getAttribute('aria-activedescendant') && FileList.total > 0)
      FileList.key(new KeyboardEvent('keydown', { key: 'Home' }));
  });

  return { focusTitle };
})();
