// Drag-to-play (§g.8, docs/followups/UX1.md, UX2.md): Pointer Events with pointer capture, an 8 px threshold,
// a floating card and the dashed drop overlay on the issue: a drop anywhere above the raised hand plays the
// card. The cards beside the dragged one move out by a slot, and the widened gaps between it and its
// neighbours vote "between" after a 2 s hold. The hand cards carry touch-action: none, otherwise a touch pan
// fires pointercancel mid-drag (F19).

import { h, setStyle } from '../core/h.js';
import { DECK } from './model.js';
import { betweenEl, faceEl } from './reveal.js';

export const DRAG_THRESHOLD_PX = 8;
export const HOLD_MS = 2000;
const WIPE_CAP = 0.55;
const ZONE_MARGIN = 0.15;
const FLOOR_GAP_PX = 4;

function inside(rect, x, y) {
  return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
}

function neighbours(points) {
  const i = DECK.indexOf(points);
  return {
    lower: i > 0 ? { points: DECK[i - 1], high: points } : null,
    higher: i >= 0 && i < DECK.length - 1 ? { points, high: DECK[i + 1] } : null,
  };
}

// `neighbour` is the card on the other side of the gap.
function gapZone(selection, neighbour, side) {
  const fill = h('span', { class: 'nt-zone-fill' });
  const pill = h('span', { class: 'nt-zone-pill', role: 'status' });
  const el = h('div', { class: ['nt-zone', `nt-zone-${side}`], 'data-zone': side },
    fill,
    h('span', { class: 'nt-zone-label', text: `Between ${selection.points} & ${selection.high}` }),
    pill);
  return { el, fill, pill, selection, neighbour };
}

/**
 * The gap between two cards' rects, kept clear of either card's centre, so the zones on the two sides of a
 * card never meet. It spans the cards' height, which lies below the drop floor above the fan.
 */
export function gapRect(a, b) {
  const ax = a.left + a.width / 2;
  const bx = b.left + b.width / 2;
  const d = Math.abs(bx - ax);
  const left = Math.min(ax, bx) + d * ZONE_MARGIN;
  const top = Math.min(a.top, b.top);
  return { left, top, width: d * (1 - 2 * ZONE_MARGIN), height: Math.max(a.bottom, b.bottom) - top };
}

/**
 * dropZone: the element over the issue a drop locks in on. cards(): the hand's single-value cards; the drop
 * only counts above the highest of them, and the gaps beside the dragged card are its "between" zones.
 * onPlay({points, high}) plays the card (vote and lock in one command). press(event, {points, high, source,
 * onClick}) starts tracking a pointerdown on a hand card; a release before the threshold calls
 * onClick(event) instead.
 */
export function createDrag({ dropZone, cards = () => [], onPlay }) {
  let session = null;

  function cleanup() {
    if (!session) {
      return;
    }
    const { source, pointerId, float, zones, overlay, frame, follow, listeners } = session;
    cancelAnimationFrame(frame);
    cancelAnimationFrame(follow);
    for (const [type, fn] of listeners) {
      source.removeEventListener(type, fn);
    }
    document.removeEventListener('keydown', listeners.get('keydown'), true);
    if (source.hasPointerCapture?.(pointerId)) {
      source.releasePointerCapture(pointerId);
    }
    source.classList.remove('is-drag-source');
    float?.remove();
    zones.forEach((z) => z.el.remove());
    overlay?.remove();
    dropZone.classList.remove('is-drop-armed');
    document.documentElement.classList.remove('nt-dragging');
    session = null;
  }

  function cardAt(points) {
    return cards().find((card) => Number(card.dataset.points) === points) ?? null;
  }

  function floor() {
    const tops = cards().map((card) => card.getBoundingClientRect().top);
    return tops.length ? Math.min(...tops) - FLOOR_GAP_PX : dropZone.getBoundingClientRect().bottom;
  }

  // The fan rises and opens while the drag starts, so the zones and the overlay follow the cards every frame.
  function track(s) {
    const zone = dropZone.getBoundingClientRect();
    setStyle(s.overlay, { bottom: `${Math.max(0, zone.bottom - floor())}px` });
    const own = s.source.getBoundingClientRect();
    for (const z of s.zones) {
      const other = cardAt(z.neighbour);
      if (other) {
        const r = gapRect(own, other.getBoundingClientRect());
        setStyle(z.el, { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` });
      }
    }
  }

  function begin(s, x, y) {
    s.dragging = true;
    s.source.classList.add('is-drag-source');
    document.documentElement.classList.add('nt-dragging');
    s.wipe = h('span', { class: 'nt-float-wipe' });
    s.floatCard = h('span', { class: 'nt-float-card' }, faceEl(s.points, s.high, false), s.wipe);
    s.float = h('div', { class: 'nt-float', 'aria-hidden': 'true' }, s.floatCard);
    s.overlay = h('div', { class: 'nt-drop', 'aria-hidden': 'true' },
      h('span', { class: 'nt-drop-text', text: 'Drop card here' }));
    dropZone.append(s.overlay);
    dropZone.classList.add('is-drop-armed');
    document.body.append(s.float);
    s.size = { w: s.float.offsetWidth, h: s.float.offsetHeight };
    if (s.high == null) {
      const { lower, higher } = neighbours(s.points);
      if (lower && cardAt(lower.points)) {
        s.zones.push(gapZone(lower, lower.points, 'left'));
      }
      if (higher && cardAt(higher.high)) {
        s.zones.push(gapZone(higher, higher.high, 'right'));
      }
      document.body.append(...s.zones.map((z) => z.el));
    }
    const follow = () => {
      track(s);
      s.follow = requestAnimationFrame(follow);
    };
    s.follow = requestAnimationFrame(follow);
    move(s, x, y);
  }

  function setHold(s, zone) {
    if (s.hold?.zone === zone) {
      return;
    }
    if (s.hold) {
      s.hold.zone.el.classList.remove('is-holding');
      setStyle(s.hold.zone.fill, { 'clip-path': null });
      s.hold.zone.pill.textContent = '';
    }
    cancelAnimationFrame(s.frame);
    s.float.classList.toggle('is-charging', Boolean(zone));
    setStyle(s.wipe, { 'clip-path': null });
    s.hold = zone ? { zone, start: performance.now() } : null;
    if (!zone) {
      return;
    }
    zone.el.classList.add('is-holding');
    s.wipe.replaceChildren(betweenEl(zone.selection.points, zone.selection.high));
    const tick = () => {
      if (session !== s || s.hold?.zone !== zone) {
        return;
      }
      const elapsed = performance.now() - s.hold.start;
      const p = Math.min(1, elapsed / HOLD_MS);
      setStyle(zone.fill, { 'clip-path': `inset(${(1 - p) * 100}% 0 0 0)` });
      zone.pill.textContent = `${Math.max(1, Math.ceil((HOLD_MS - elapsed) / 1000))}s — hold`;
      const wipe = Math.min(p, WIPE_CAP) * 200;
      setStyle(s.wipe, { 'clip-path': `polygon(0 0, ${wipe}% 0, 0 ${wipe}%)` });
      if (p >= 1) {
        const selection = zone.selection;
        cleanup();
        onPlay(selection);
        return;
      }
      s.frame = requestAnimationFrame(tick);
    };
    s.frame = requestAnimationFrame(tick);
  }

  function move(s, x, y) {
    setStyle(s.float, { transform: `translate(${x - s.size.w / 2}px, ${y - s.size.h / 2}px)` });
    track(s);
    const over = inside(dropZone.getBoundingClientRect(), x, y) && y < floor();
    if (over !== s.over) {
      s.over = over;
      s.overlay.classList.toggle('is-hover', over);
      s.overlay.firstChild.textContent = over ? 'Release to lock in!' : 'Drop card here';
    }
    setHold(s, s.zones.find((z) => inside(z.el.getBoundingClientRect(), x, y)) ?? null);
  }

  function press(event, { points, high = null, source, onClick }) {
    if (event.button !== 0 || session) {
      return;
    }
    const s = {
      source,
      points,
      high,
      onClick,
      pointerId: event.pointerId,
      x: event.clientX,
      y: event.clientY,
      dragging: false,
      zones: [],
      listeners: new Map(),
      frame: 0,
      follow: 0,
    };
    session = s;
    source.setPointerCapture?.(event.pointerId);
    const on = (type, fn) => {
      s.listeners.set(type, fn);
      if (type !== 'keydown') {
        source.addEventListener(type, fn);
      }
    };
    on('pointermove', (e) => {
      if (e.pointerId !== s.pointerId) {
        return;
      }
      if (!s.dragging && Math.hypot(e.clientX - s.x, e.clientY - s.y) >= DRAG_THRESHOLD_PX) {
        begin(s, e.clientX, e.clientY);
      } else if (s.dragging) {
        move(s, e.clientX, e.clientY);
      }
    });
    on('pointerup', (e) => {
      if (e.pointerId !== s.pointerId) {
        return;
      }
      const { dragging, over } = s;
      cleanup();
      if (!dragging) {
        onClick?.(e);
      } else if (over) {
        onPlay({ points, high });
      }
    });
    on('pointercancel', () => cleanup());
    on('keydown', (e) => {
      if (e.key === 'Escape' && s.dragging) {
        e.preventDefault();
        cleanup();
      }
    });
    document.addEventListener('keydown', s.listeners.get('keydown'), true);
    event.preventDefault();
  }

  return {
    press,
    get active() {
      return session?.dragging ?? false;
    },
    cancel: cleanup,
    destroy: cleanup,
  };
}
