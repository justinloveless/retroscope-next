import { h, setStyle } from '../core/h.js';

const GAP = 6;
const MARGIN = 8;

/**
 * Where a popover of natural size `size` goes next to `rect` in a `vw`×`vh` viewport: {top, left, maxHeight}.
 * It flips to the other side when it would overflow and the other side has room; when neither side fits, it
 * takes the roomier one and `maxHeight` caps it to that side (null when it fits), so the popover scrolls.
 */
export function fit(size, rect, { vw, vh }, placement = 'bottom-start') {
  const [side, align = 'center'] = placement.split('-');
  const { width } = size;
  let { height } = size;
  let top;
  let left;
  if (side === 'top' || side === 'bottom') {
    const below = vh - MARGIN - rect.bottom - GAP;
    const above = rect.top - GAP - MARGIN;
    const [first, second] = side === 'bottom' ? [below, above] : [above, below];
    const onFirst = height <= first || (height > second && first >= second);
    height = Math.min(height, Math.max(0, onFirst ? first : second));
    top = (side === 'bottom') === onFirst ? rect.bottom + GAP : rect.top - GAP - height;
    left = align === 'start' ? rect.left : align === 'end' ? rect.right - width : rect.left + (rect.width - width) / 2;
  } else {
    const after = rect.right + GAP;
    const before = rect.left - GAP - width;
    left = side === 'right'
      ? (after + width > vw - MARGIN && before >= MARGIN ? before : after)
      : (before < MARGIN && after + width <= vw - MARGIN ? after : before);
    height = Math.min(height, Math.max(0, vh - 2 * MARGIN));
    top = align === 'start' ? rect.top : align === 'end' ? rect.bottom - height : rect.top + (rect.height - height) / 2;
  }
  left = Math.min(Math.max(MARGIN, left), Math.max(MARGIN, vw - width - MARGIN));
  top = Math.min(Math.max(MARGIN, top), Math.max(MARGIN, vh - height - MARGIN));
  return { top: Math.round(top), left: Math.round(left), maxHeight: height < size.height ? Math.floor(height) : null };
}

// Places `el` next to `rect` (fixed positioning). The natural height comes from scrollHeight, so a popover
// already capped and scrolled keeps its scroll position when it is placed again.
export function place(el, rect, placement = 'bottom-start') {
  const box = el.getBoundingClientRect();
  const height = Math.max(box.height, el.scrollHeight + el.offsetHeight - el.clientHeight);
  const view = { vw: document.documentElement.clientWidth, vh: document.documentElement.clientHeight };
  const at = fit({ width: box.width, height }, rect, view, placement);
  setStyle(el, { top: `${at.top}px`, left: `${at.left}px`, 'max-height': at.maxHeight === null ? null : `${at.maxHeight}px` });
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

  function reposition(event) {
    if (event?.type === 'scroll' && el.contains(event.target)) {
      return;
    }
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
    // For content that grows or shrinks while open, so the height cap follows it.
    reposition: () => reposition(),
    get isOpen() {
      return !el.hidden;
    },
    destroy() {
      close();
      el.remove();
    },
  };
}

// Whether any popover or menu is open in `doc`. A closed one stays in the page with `hidden`, and destroy()
// removes it, so a visible `.popover` is an open one. Page shortcuts stand down while one is open.
export function popoverOpen(doc = document) {
  return doc.querySelector('.popover:not([hidden])') !== null;
}

// A zero-size anchor at a viewport point, for context menus.
export function pointAnchor(x, y) {
  return { getBoundingClientRect: () => ({ top: y, bottom: y, left: x, right: x, width: 0, height: 0 }) };
}
