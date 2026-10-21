// Phones and round changes (§g.8): below 768 px the toolbar controls move into a bottom bar with a
// History sheet, and a horizontal swipe on the table changes round (direction locks after 10 px when
// |dx| > 1.2·|dy|, the table follows with damping sign(dx)·|dx|^0.75, 50 px commits, 200 ms settle).
// Every round change slides the table in from the side it came from (280 ms). Motion honours
// prefers-reduced-motion.

import { h, setStyle } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { createHistoryList } from './history.js';

export const MOBILE_QUERY = '(max-width: 767px)';
export const SWIPE_LOCK_PX = 10;
export const SWIPE_RATIO = 1.2;
export const SWIPE_COMMIT_PX = 50;
export const SWIPE_SETTLE_MS = 200;
export const ROUND_SLIDE_MS = 280;

// A swipe starting here belongs to the control under the finger.
const NO_SWIPE = '.nt-hand, input, textarea, select, [contenteditable]:not([contenteditable="false"]), .nt-drop';

export function damp(dx) {
  return Math.sign(dx) * Math.abs(dx) ** 0.75;
}

/** 'x', 'y' or null (not decided yet) for a movement of (dx, dy) since the touch started. */
export function swipeAxis(dx, dy) {
  if (Math.hypot(dx, dy) < SWIPE_LOCK_PX) {
    return null;
  }
  return Math.abs(dx) > SWIPE_RATIO * Math.abs(dy) ? 'x' : 'y';
}

/** The round step a released horizontal swipe asks for: swipe left (dx < 0) is the next round. */
export function swipeStep(dx) {
  if (Math.abs(dx) < SWIPE_COMMIT_PX) {
    return 0;
  }
  return dx < 0 ? 1 : -1;
}

function reducedMotion() {
  return globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? true;
}

export function createMobile(ctx) {
  const media = globalThis.matchMedia(MOBILE_QUERY);
  const center = ctx.slots.center;
  const controls = h('div', { class: 'nt-bottom-controls' });
  const historyButton = createButton({ label: 'Round history', icon: 'list-ordered', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: openHistory });
  const bar = h('nav', { class: 'nt-bottom-bar', 'aria-label': 'Session controls', hidden: true },
    controls, h('span', { class: 'nt-bottom-sep', 'aria-hidden': 'true' }), historyButton.el);
  ctx.slots.page.append(bar);

  let view = null;
  let viewed = null;
  let moved = [];
  let sheet = null;
  let swipe = null;
  // The centre element outlives this feature (the screen reuses it for the next session).
  const listening = new AbortController();
  const on = (type, handler) => center.addEventListener(type, handler, { signal: listening.signal });

  // The controls other features put in the toolbar move to the bar on a phone and back on a desktop.
  function relocate() {
    if (media.matches) {
      moved = [...ctx.slots.toolbar.querySelectorAll(':scope > .nt-control')];
      controls.append(...moved);
    } else if (moved.length) {
      ctx.slots.toolbar.append(...moved);
      moved = [];
    }
    bar.hidden = !media.matches;
  }
  media.addEventListener('change', relocate);

  function openHistory() {
    if (sheet || !view) {
      return;
    }
    const list = createHistoryList({ navigate: ctx.navigate, onPick: () => sheet?.close() });
    list.update(view, viewed);
    sheet = createDialog({
      title: 'Round history',
      className: 'nt-sheet',
      content: list.el,
      onClose() {
        sheet.destroy();
        sheet = null;
      },
    });
    sheet.open();
  }

  function stepTarget(step) {
    const rounds = view?.rounds ?? [];
    const at = rounds.findIndex((r) => r.id === viewed);
    return at < 0 ? null : rounds[at + step]?.id ?? null;
  }

  function settleBack(from) {
    setStyle(center, { transform: null });
    if (from !== 0 && !reducedMotion()) {
      center.animate([{ transform: `translateX(${from}px)` }, { transform: 'none' }], { duration: SWIPE_SETTLE_MS, easing: 'ease-out' });
    }
  }

  on('pointerdown', (event) => {
    if (!media.matches || event.pointerType === 'mouse' || !event.isPrimary || swipe || event.target.closest(NO_SWIPE)) {
      return;
    }
    swipe = { id: event.pointerId, x: event.clientX, y: event.clientY, axis: null, dx: 0 };
  });

  on('pointermove', (event) => {
    if (swipe?.id !== event.pointerId) {
      return;
    }
    const dx = event.clientX - swipe.x;
    const dy = event.clientY - swipe.y;
    if (swipe.axis === null) {
      swipe.axis = swipeAxis(dx, dy);
      if (swipe.axis === 'y') {
        swipe = null;
        return;
      }
      if (swipe.axis === 'x') {
        try {
          center.setPointerCapture(event.pointerId);
        } catch {
          // A synthetic pointer cannot be captured; its events still reach this element.
        }
      }
    }
    if (swipe.axis === 'x') {
      swipe.dx = dx;
      setStyle(center, { transform: `translateX(${damp(dx)}px)` });
    }
  });

  function release(event, commit) {
    if (swipe?.id !== event.pointerId) {
      return;
    }
    const { axis, dx } = swipe;
    swipe = null;
    if (axis !== 'x') {
      return;
    }
    const target = commit ? stepTarget(swipeStep(dx)) : null;
    if (target) {
      setStyle(center, { transform: null });
      ctx.navigate(target);
    } else {
      settleBack(damp(dx));
    }
  }

  on('pointerup', (event) => release(event, true));
  on('pointercancel', (event) => release(event, false));

  function slide(from, to) {
    if (reducedMotion() || swipe) {
      return;
    }
    const ids = view.rounds.map((r) => r.id);
    const direction = ids.indexOf(to) >= ids.indexOf(from) ? 1 : -1;
    center.animate([
      { transform: `translateX(${direction * 30}%)`, opacity: 0 },
      { transform: 'none', opacity: 1 },
    ], { duration: ROUND_SLIDE_MS, easing: 'ease-in-out' });
  }

  relocate();

  return {
    update(next, roundId) {
      const previous = viewed;
      view = next;
      viewed = roundId;
      if (previous !== null && previous !== roundId && next.rounds.some((r) => r.id === previous)) {
        slide(previous, roundId);
      }
    },
    destroy() {
      media.removeEventListener('change', relocate);
      listening.abort();
      sheet?.destroy();
      if (moved.length) {
        ctx.slots.toolbar.append(...moved);
      }
      setStyle(center, { transform: null });
      bar.remove();
    },
  };
}
