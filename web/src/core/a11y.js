const FOCUSABLE = [
  'a[href]', 'button:not([disabled])', 'input:not([disabled]):not([type=hidden])',
  'select:not([disabled])', 'textarea:not([disabled])', '[tabindex]:not([tabindex="-1"])',
  '[contenteditable=true]',
].join(',');

export function focusable(container) {
  return [...container.querySelectorAll(FOCUSABLE)].filter((el) => !el.closest('[hidden],[inert]'));
}

// Keeps Tab inside `container` and restores focus to the previously focused element on release.
// Native modal <dialog> already does this; this is for non-modal surfaces such as sheets and menus.
export function trapFocus(container, { initial } = {}) {
  const previous = document.activeElement;

  function onKeyDown(event) {
    if (event.key !== 'Tab') {
      return;
    }
    const items = focusable(container);
    if (items.length === 0) {
      event.preventDefault();
      return;
    }
    const first = items[0];
    const last = items[items.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  container.addEventListener('keydown', onKeyDown);
  (initial ?? focusable(container)[0] ?? container).focus();

  return function release() {
    container.removeEventListener('keydown', onKeyDown);
    if (previous?.isConnected) {
      previous.focus();
    }
  };
}

// Whether a key typed at `target` is text for a field rather than a page shortcut: inside an input, textarea,
// select or editable content (a non-editable chip inside an editor still counts as the editor's).
export function isTypingTarget(target) {
  return typeof target?.closest === 'function'
    && target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])') !== null;
}

const KEYS = {
  horizontal: { next: ['ArrowRight'], prev: ['ArrowLeft'] },
  vertical: { next: ['ArrowDown'], prev: ['ArrowUp'] },
  both: { next: ['ArrowRight', 'ArrowDown'], prev: ['ArrowLeft', 'ArrowUp'] },
};

/**
 * Roving tabindex: one item in `container` matching `selector` is tabbable; arrows, Home and End move
 * focus between items. Call refresh() after the items change. onMove(item) runs after focus moves.
 */
export function rovingTabindex(container, { selector, orientation = 'horizontal', wrap = true, onMove } = {}) {
  const keys = KEYS[orientation];
  const items = () => [...container.querySelectorAll(selector)].filter((el) => !el.disabled && !el.hidden);

  function setActive(target) {
    for (const item of items()) {
      item.tabIndex = item === target ? 0 : -1;
    }
  }

  function refresh() {
    const all = items();
    setActive(all.find((el) => el.tabIndex === 0) ?? all.find((el) => el.getAttribute('aria-selected') === 'true') ?? all[0]);
  }

  function onKeyDown(event) {
    const all = items();
    const index = all.indexOf(event.target.closest?.(selector));
    if (index < 0) {
      return;
    }
    let next;
    if (keys.next.includes(event.key)) {
      next = index + 1 < all.length ? index + 1 : (wrap ? 0 : index);
    } else if (keys.prev.includes(event.key)) {
      next = index > 0 ? index - 1 : (wrap ? all.length - 1 : index);
    } else if (event.key === 'Home') {
      next = 0;
    } else if (event.key === 'End') {
      next = all.length - 1;
    } else {
      return;
    }
    event.preventDefault();
    setActive(all[next]);
    all[next].focus();
    onMove?.(all[next]);
  }

  function onFocusIn(event) {
    const item = event.target.closest?.(selector);
    if (item && container.contains(item)) {
      setActive(item);
    }
  }

  container.addEventListener('keydown', onKeyDown);
  container.addEventListener('focusin', onFocusIn);
  refresh();

  return {
    refresh,
    destroy() {
      container.removeEventListener('keydown', onKeyDown);
      container.removeEventListener('focusin', onFocusIn);
    },
  };
}

const regions = {};

function region(politeness) {
  let el = regions[politeness];
  if (!el || !document.body.contains(el)) {
    el = document.createElement('div');
    el.className = 'sr-only';
    el.setAttribute('aria-live', politeness);
    el.setAttribute('aria-atomic', 'true');
    document.body.appendChild(el);
    regions[politeness] = el;
  }
  return el;
}

// Screen-reader announcement. Clearing first makes a repeated identical message announce again.
export function announce(message, { assertive = false } = {}) {
  const el = region(assertive ? 'assertive' : 'polite');
  el.textContent = '';
  setTimeout(() => {
    el.textContent = message;
  }, 50);
}
