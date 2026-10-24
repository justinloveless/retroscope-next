import { h } from '../core/h.js';
import { createButton } from './button.js';

/**
 * A fixed side sheet (non-modal, so menus and toasts stay above it). Escape and the close button call onClose.
 * It spans the viewport's height; a screen whose header stays visible beside it sets --side-sheet-top.
 */
export function createSideSheet({ side = 'right', label, width = '36rem', onClose }) {
  const close = createButton({ label: 'Close', icon: 'x', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => onClose() });
  const body = h('div', { class: 'jira-sheet-body', style: { flex: '1', overflow: 'auto', padding: '0 1rem 1rem' } });
  const title = h('h2', { class: 'jira-sheet-title', style: { 'font-size': '1rem', 'font-weight': '600', flex: '1', 'min-width': '0' } });
  const tools = h('div', { style: { display: 'flex', gap: '0.25rem', 'align-items': 'center' } });
  const el = h('aside', {
    class: ['jira-sheet', `jira-sheet-${side}`],
    role: 'dialog',
    'aria-label': label,
    tabindex: '-1',
    hidden: true,
    style: {
      position: 'fixed',
      top: 'var(--side-sheet-top, 0px)',
      bottom: '0',
      [side]: '0',
      width: `min(${width}, 100vw)`,
      'z-index': '900',
      display: 'flex',
      'flex-direction': 'column',
      background: 'hsl(var(--background))',
      color: 'hsl(var(--foreground))',
      'border-inline': '1px solid hsl(var(--border))',
      'box-shadow': 'var(--shadow-lg)',
    },
    on: {
      keydown(event) {
        if (event.key === 'Escape' && !event.defaultPrevented) {
          event.preventDefault();
          onClose();
        }
      },
    },
  },
  h('header', { style: { display: 'flex', 'align-items': 'center', gap: '0.5rem', padding: '0.75rem 1rem' } }, title, tools, close.el),
  body);
  let returnFocus = null;
  return {
    el,
    body,
    title,
    tools,
    get isOpen() {
      return !el.hidden;
    },
    open() {
      if (!el.isConnected) {
        document.body.append(el);
      }
      if (el.hidden) {
        returnFocus = document.activeElement;
        el.hidden = false;
        el.focus();
      }
    },
    hide() {
      if (!el.hidden) {
        el.hidden = true;
        if (returnFocus?.isConnected) {
          returnFocus.focus();
        }
        returnFocus = null;
      }
    },
    destroy() {
      el.remove();
    },
  };
}
