import { h, setStyle } from '../core/h.js';

const GAP = 6;
const MARGIN = 8;

// Places `el` next to `rect` (fixed positioning), flipping to the other side when it would overflow.
export function place(el, rect, placement = 'bottom-start') {
  const [side, align = 'center'] = placement.split('-');
  const box = el.getBoundingClientRect();
  const vw = document.documentElement.clientWidth;
  const vh = document.documentElement.clientHeight;
  let top;
  let left;
  if (side === 'top' || side === 'bottom') {
    const below = rect.bottom + GAP;
    const above = rect.top - GAP - box.height;
    top = side === 'bottom'
      ? (below + box.height > vh - MARGIN && above >= MARGIN ? above : below)
      : (above < MARGIN && below + box.height <= vh - MARGIN ? below : above);
    left = align === 'start' ? rect.left : align === 'end' ? rect.right - box.width : rect.left + (rect.width - box.width) / 2;
  } else {
    const after = rect.right + GAP;
    const before = rect.left - GAP - box.width;
    left = side === 'right'
      ? (after + box.width > vw - MARGIN && before >= MARGIN ? before : after)
      : (before < MARGIN && after + box.width <= vw - MARGIN ? after : before);
    top = align === 'start' ? rect.top : align === 'end' ? rect.bottom - box.height : rect.top + (rect.height - box.height) / 2;
  }
  left = Math.min(Math.max(MARGIN, left), Math.max(MARGIN, vw - box.width - MARGIN));
  top = Math.min(Math.max(MARGIN, top), Math.max(MARGIN, vh - box.height - MARGIN));
  setStyle(el, { top: `${Math.round(top)}px`, left: `${Math.round(left)}px` });
}

/**
 * A floating surface anchored to an element (or any {getBoundingClientRect}). Closes on Escape and on
 * a pointer press outside both the surface and the anchor; focus returns to the anchor on Escape.
 * props: anchor, content (nodes), placement, role, label, className, onClose().
 */
export function createPopover(props) {
  let anchor = props.anchor;
  const el = h('div', {
    class: ['popover', props.className],
    role: props.role ?? 'dialog',
    'aria-label': props.label ?? null,
    hidden: true,
    tabindex: '-1',
  }, props.content);
  let frame = 0;

  function reposition() {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => place(el, anchor.getBoundingClientRect(), props.placement));
  }

  function onPointerDown(event) {
    if (!el.contains(event.target) && !(anchor.contains?.(event.target))) {
      close();
    }
  }

  function onKeyDown(event) {
    if (event.key === 'Escape') {
      event.stopPropagation();
      close();
      anchor.focus?.();
    }
  }

  function open(at) {
    if (at) {
      anchor = at;
    }
    // Inside a modal dialog the rest of the document is inert, so the surface joins the dialog.
    const host = anchor.closest?.('dialog[open]') ?? document.body;
    if (el.parentNode !== host) {
      host.appendChild(el);
    }
    el.hidden = false;
    place(el, anchor.getBoundingClientRect(), props.placement);
    anchor.setAttribute?.('aria-expanded', 'true');
    document.addEventListener('pointerdown', onPointerDown, true);
    el.addEventListener('keydown', onKeyDown);
    window.addEventListener('resize', reposition, { passive: true });
    window.addEventListener('scroll', reposition, { passive: true, capture: true });
  }

  function close() {
    if (el.hidden) {
      return;
    }
    el.hidden = true;
    anchor.setAttribute?.('aria-expanded', 'false');
    document.removeEventListener('pointerdown', onPointerDown, true);
    el.removeEventListener('keydown', onKeyDown);
    window.removeEventListener('resize', reposition);
    window.removeEventListener('scroll', reposition, { capture: true });
    cancelAnimationFrame(frame);
    props.onClose?.();
  }

  return {
    el,
    open,
    close,
    toggle: () => (el.hidden ? open() : close()),
    get isOpen() {
      return !el.hidden;
    },
    destroy() {
      close();
      el.remove();
    },
  };
}

// A zero-size anchor at a viewport point, for context menus.
export function pointAnchor(x, y) {
  return { getBoundingClientRect: () => ({ top: y, bottom: y, left: x, right: x, width: 0, height: 0 }) };
}
