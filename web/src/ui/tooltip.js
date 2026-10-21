import { h } from '../core/h.js';
import { place } from './popover.js';

const DELAY_MS = 400;
let shared = null;

function tip() {
  if (!shared || !document.body.contains(shared)) {
    shared = h('div', { class: 'tooltip', role: 'tooltip', id: 'rs-tooltip', hidden: true });
    document.body.appendChild(shared);
  }
  return shared;
}

/**
 * Hover/focus tooltip on `target`; hover waits DELAY_MS, focus shows at once, Escape hides.
 * Returns {update(text), destroy()}.
 */
export function tooltip(target, text, { placement = 'top' } = {}) {
  let label = text;
  let timer = 0;

  function show() {
    clearTimeout(timer);
    const el = tip();
    el.textContent = label;
    el.hidden = false;
    target.setAttribute('aria-describedby', el.id);
    place(el, target.getBoundingClientRect(), placement);
  }

  function hide() {
    clearTimeout(timer);
    if (shared && target.getAttribute('aria-describedby') === shared.id) {
      shared.hidden = true;
      target.removeAttribute('aria-describedby');
    }
  }

  const handlers = {
    pointerenter: (event) => {
      if (event.pointerType === 'mouse') {
        clearTimeout(timer);
        timer = setTimeout(show, DELAY_MS);
      }
    },
    pointerleave: hide,
    focus: show,
    blur: hide,
    keydown: (event) => {
      if (event.key === 'Escape') {
        hide();
      }
    },
  };
  for (const [type, handler] of Object.entries(handlers)) {
    target.addEventListener(type, handler);
  }

  return {
    update(next) {
      label = next;
      if (shared && !shared.hidden && target.getAttribute('aria-describedby') === shared.id) {
        shared.textContent = label;
      }
    },
    destroy() {
      hide();
      for (const [type, handler] of Object.entries(handlers)) {
        target.removeEventListener(type, handler);
      }
    },
  };
}
