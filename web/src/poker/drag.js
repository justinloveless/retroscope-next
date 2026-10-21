// Drag-to-play (§g.8): Pointer Events with pointer capture, an 8 px threshold, a floating card, the dashed
// drop overlay on the table and two side zones that vote "between" after a 2 s hold. The hand cards and
// grips carry touch-action: none, otherwise a touch pan fires pointercancel mid-drag (F19).

import { h, setStyle } from '../core/h.js';
import { DECK } from './model.js';
import { betweenEl, cardEl, faceEl } from './reveal.js';

export const DRAG_THRESHOLD_PX = 8;
export const HOLD_MS = 2000;
const WIPE_CAP = 0.55;
const ZONE_GAP_PX = 10;

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

function sideZone(selection, shown, side) {
  const fill = h('span', { class: 'nt-zone-fill' });
  const pill = h('span', { class: 'nt-zone-pill', role: 'status' });
  const el = h('div', { class: ['nt-zone', `nt-zone-${side}`], 'data-zone': side },
    fill,
    cardEl(shown, 'nt-zone-card'),
    h('span', { class: 'nt-zone-label', text: `Between ${selection.points} & ${selection.high}` }),
    pill);
  return { el, fill, pill, selection };
}

/**
 * dropZone: the table element a drop locks in over. onPlay({points, high}) plays the card (vote and lock
 * in one command). press(event, {points, high, source, onClick}) starts tracking a pointerdown on a hand
 * card; a release before the threshold calls onClick(event) instead.
 */
export function createDrag({ dropZone, onPlay }) {
  let session = null;

  function cleanup() {
    if (!session) {
      return;
    }
    const { source, pointerId, float, zones, overlay, frame, listeners } = session;
    cancelAnimationFrame(frame);
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

  function begin(s, x, y) {
    s.dragging = true;
    const rect = s.source.getBoundingClientRect();
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
      const width = rect.width * 1.15;
      const height = rect.height * 1.1;
      const top = rect.top + (rect.height - height) / 2;
      if (lower) {
        const zone = sideZone(lower, lower.points, 'left');
        setStyle(zone.el, { left: `${rect.left - width - ZONE_GAP_PX}px`, top: `${top}px`, width: `${width}px`, height: `${height}px` });
        s.zones.push(zone);
      }
      if (higher) {
        const zone = sideZone(higher, higher.high, 'right');
        setStyle(zone.el, { left: `${rect.right + ZONE_GAP_PX}px`, top: `${top}px`, width: `${width}px`, height: `${height}px` });
        s.zones.push(zone);
      }
      document.body.append(...s.zones.map((z) => z.el));
    }
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
    const over = inside(dropZone.getBoundingClientRect(), x, y);
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
