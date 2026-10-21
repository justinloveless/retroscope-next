import { h } from '../core/h.js';
import { icon } from './icons.js';

const MAX_VISIBLE = 3;
let region = null;

function container() {
  if (!region || !document.body.contains(region)) {
    region = h('section', { class: 'toaster', 'aria-label': 'Notifications' });
    document.body.appendChild(region);
  }
  return region;
}

/**
 * Shows a toast; returns dismiss(). variant: default | success | error. Errors use role=alert and
 * stay until dismissed unless a duration is given. action: {label, onClick}.
 */
export function toast(message, { variant = 'default', duration, action } = {}) {
  const host = container();
  let timer = 0;
  const el = h('div', {
    class: ['toast', `toast-${variant}`],
    role: variant === 'error' ? 'alert' : 'status',
  },
  h('p', { class: 'toast-message', text: message }),
  action
    ? h('button', {
      type: 'button',
      class: 'btn btn-secondary btn-sm',
      on: {
        click() {
          dismiss();
          action.onClick();
        },
      },
    }, action.label)
    : null,
  h('button', { type: 'button', class: 'btn btn-ghost btn-icon toast-close', 'aria-label': 'Dismiss', on: { click: () => dismiss() } }, icon('x')));

  function dismiss() {
    clearTimeout(timer);
    el.remove();
  }

  host.appendChild(el);
  while (host.children.length > MAX_VISIBLE) {
    host.firstElementChild.remove();
  }
  const ms = duration ?? (variant === 'error' ? 0 : 4000);
  if (ms > 0) {
    timer = setTimeout(dismiss, ms);
  }
  return dismiss;
}
