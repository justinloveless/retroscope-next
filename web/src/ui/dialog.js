import { h, appendChildren } from '../core/h.js';
import { icon } from './icons.js';

/**
 * Modal dialog on native <dialog> (focus trap, inert background and Escape come from the browser).
 * props: title, description, content (nodes), actions (nodes), dismissible (default true: Escape,
 * backdrop click and the close button), className, onClose(returnValue).
 */
export function createDialog(props) {
  const dismissible = props.dismissible ?? true;
  const titleId = `dlg-${Math.random().toString(36).slice(2, 10)}`;
  const body = h('div', { class: 'dialog-body' });
  const footer = h('div', { class: 'dialog-actions' });
  const el = h('dialog', {
    class: ['dialog', props.className],
    'aria-labelledby': titleId,
    on: {
      cancel(event) {
        if (!dismissible) {
          event.preventDefault();
        }
      },
      close() {
        props.onClose?.(el.returnValue);
      },
      click(event) {
        if (dismissible && event.target === el) {
          el.close('dismiss');
        }
      },
    },
  },
  h('header', { class: 'dialog-header' },
    h('h2', { id: titleId, class: 'dialog-title', text: props.title ?? '' }),
    dismissible
      ? h('button', { type: 'button', class: 'btn btn-ghost btn-icon dialog-close', 'aria-label': 'Close', on: { click: () => el.close('dismiss') } }, icon('x'))
      : null),
  props.description ? h('p', { class: 'dialog-description', text: props.description }) : null,
  body,
  footer);

  function setContent(content, actions) {
    body.replaceChildren();
    appendChildren(body, [content]);
    footer.replaceChildren();
    appendChildren(footer, [actions]);
    footer.hidden = !actions || (Array.isArray(actions) && actions.length === 0);
  }

  setContent(props.content, props.actions);

  return {
    el,
    open() {
      if (!el.isConnected) {
        document.body.appendChild(el);
      }
      if (!el.open) {
        el.showModal();
      }
    },
    close(returnValue = '') {
      if (el.open) {
        el.close(returnValue);
      }
    },
    update(next) {
      if ('title' in next) {
        el.querySelector('.dialog-title').textContent = next.title ?? '';
      }
      if ('content' in next || 'actions' in next) {
        setContent(next.content ?? props.content, next.actions ?? props.actions);
      }
    },
    destroy() {
      if (el.open) {
        el.close();
      }
      el.remove();
    },
  };
}
