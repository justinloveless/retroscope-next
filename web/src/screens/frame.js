// The frame every shell screen sits in (a centred main column under the app header) and the small form
// helpers they share. Layout goes through h()'s style objects (CSSOM), because the shell has no
// stylesheet of its own beyond the kit (see docs/decisions/U29.md).

import { h } from '../core/h.js';
import { timeAgo } from '../core/fmt.js';
import { ApiError } from '../core/http.js';

export const stack = (gap = '1rem') => ({ display: 'grid', gap, 'align-content': 'start' });
export const row = (gap = '0.75rem') => ({ display: 'flex', 'flex-wrap': 'wrap', 'align-items': 'center', gap });
export const MUTED = { color: 'hsl(var(--muted-foreground))', 'font-size': '0.875rem' };
const CARD = { ...stack('1rem'), padding: '1.25rem' };

export function link(href, text, props = {}) {
  return h('a', { href, 'data-nav': '', ...props }, text);
}

export function card(title, ...children) {
  return h('section', { class: 'card', style: CARD },
    title ? h('h2', { text: title, style: { 'font-size': '1.125rem', 'font-weight': '600' } }) : null,
    ...children);
}

// HTTP times arrive as ISO strings, wire times as Unix milliseconds.
export const ago = (value) => timeAgo(typeof value === 'number' ? value : Date.parse(value));

export function muted(text) {
  return h('p', { text, style: MUTED });
}

/**
 * Mounts a centred main column into `root` (the app header sits above every route). Returns {main,
 * setTitle(text), destroy()}.
 */
export function createFrame(root, { title, width = '56rem' } = {}) {
  const heading = h('h1', { tabindex: '-1', style: { 'font-size': '1.75rem', 'font-weight': '700' } });
  const body = h('div', { style: stack('1.5rem') });
  const main = h('main', {
    style: { ...stack('1.5rem'), width: '100%', 'max-width': width, margin: '0 auto', padding: '2rem 1rem 3rem' },
  }, heading, body);
  root.replaceChildren(main);

  function setTitle(text) {
    heading.textContent = text ?? '';
    heading.hidden = !text;
    document.title = text ? `${text} · Retroscope` : 'Retroscope';
  }

  setTitle(title);
  return {
    main: body,
    setTitle,
    destroy() {
      root.replaceChildren();
    },
  };
}

// Form errors: problem `code` → message, field error code → message.
const ERRORS = {
  network: 'The server could not be reached. Check your connection and try again.',
  busy: 'The server is busy. Try again in a moment.',
  rate_limited: 'Too many attempts. Wait a few minutes and try again.',
  unauthenticated: 'Your session has ended. Sign in again.',
  forbidden: 'You are not allowed to do that.',
  not_found: 'That no longer exists.',
};

const FIELD_ERRORS = {
  required: 'Required.',
  invalid: 'This value is not valid.',
  too_short: 'Use at least 10 characters.',
  too_long: 'Use at most 72 bytes.',
  too_common: 'This password is too common. Choose another.',
};

export function errorText(error, messages = {}) {
  if (error instanceof ApiError) {
    return messages[error.code] ?? ERRORS[error.code] ?? error.detail ?? 'Something went wrong. Try again.';
  }
  return 'Something went wrong. Try again.';
}

let fieldIds = 0;

/** A labelled input with an error line. props: label, name, type, autocomplete, hint, plus input attributes. */
export function field({ label, hint, ...attrs }) {
  const id = `f${++fieldIds}`;
  const input = h(attrs.type === 'textarea' ? 'textarea' : 'input', {
    class: attrs.type === 'textarea' ? 'textarea' : 'input',
    id,
    ...attrs,
    type: attrs.type === 'textarea' ? undefined : attrs.type ?? 'text',
    'aria-describedby': `${id}-e`,
  });
  const error = h('p', { id: `${id}-e`, hidden: true, style: { color: 'var(--red)', 'font-size': '0.8125rem' } });
  const el = h('div', { class: 'field' },
    h('label', { class: 'field-label', for: id, text: label }),
    input,
    hint ? h('p', { text: hint, style: { ...MUTED, 'font-size': '0.8125rem' } }) : null,
    error);
  return {
    el,
    input,
    get value() {
      return input.value;
    },
    setError(text) {
      error.textContent = text ?? '';
      error.hidden = !text;
      input.setAttribute('aria-invalid', text ? 'true' : 'false');
    },
  };
}

/**
 * A form that submits through `submit(values)`. ApiError `fields` land on the matching fields, anything
 * else in the form's alert line. Returns {el, button, setError(text)}.
 */
export function form({ fields, submitLabel, submit, messages, fieldMessages = {}, extra, submitClass = 'btn btn-primary' }) {
  const alert = h('p', { role: 'alert', hidden: true, style: { color: 'var(--red)', 'font-size': '0.875rem' } });
  const button = h('button', { type: 'submit', class: submitClass }, submitLabel);
  const el = h('form', { style: stack('1rem') },
    ...Object.values(fields).map((f) => f.el), alert, h('div', { style: row() }, button, extra));

  function setError(text) {
    alert.textContent = text ?? '';
    alert.hidden = !text;
  }

  el.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (button.disabled) {
      return;
    }
    setError(null);
    for (const f of Object.values(fields)) {
      f.setError(null);
    }
    const values = Object.fromEntries(Object.entries(fields).map(([name, f]) => [name, f.value]));
    button.disabled = true;
    button.setAttribute('aria-busy', 'true');
    try {
      await submit(values);
    } catch (error) {
      const placed = error instanceof ApiError && error.fields
        ? Object.entries(error.fields).filter(([name, code]) => {
          fields[name]?.setError(fieldMessages[code] ?? FIELD_ERRORS[code] ?? FIELD_ERRORS.invalid);
          return Boolean(fields[name]);
        }).length
        : 0;
      if (placed === 0) {
        setError(errorText(error, messages));
      }
    } finally {
      button.disabled = false;
      button.removeAttribute('aria-busy');
    }
  });

  return { el, button, setError };
}

/** Shows `text` in place of the screen body: loading, empty and error states. */
export function notice(text, ...children) {
  return h('div', { style: { ...stack('0.75rem'), 'justify-items': 'start' } }, muted(text), ...children);
}
