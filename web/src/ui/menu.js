import { h } from '../core/h.js';
import { rovingTabindex } from '../core/a11y.js';
import { icon } from './icons.js';
import { createPopover, pointAnchor } from './popover.js';

/**
 * Action menu. items: [{action, label, icon, destructive, disabled} | 'separator'].
 * onSelect(action) runs after the menu closes. Bind it to a trigger with attach(button), or open it
 * at a point with openAt(x, y) for context menus. onClose() runs whenever it closes.
 */
export function createMenu({ items, onSelect, label, placement = 'bottom-start', onClose }) {
  let trigger = null;
  const list = h('div', { class: 'menu-list' });
  const popover = createPopover({
    anchor: pointAnchor(0, 0),
    content: list,
    role: 'menu',
    label,
    className: 'menu',
    placement,
    onClose,
  });
  popover.el.removeAttribute('tabindex');
  const roving = rovingTabindex(popover.el, { selector: '[role=menuitem]', orientation: 'vertical' });

  function render(entries) {
    list.replaceChildren(...entries.map((item) => (item === 'separator'
      ? h('div', { class: 'menu-separator', role: 'separator' })
      : h('button', {
        type: 'button',
        role: 'menuitem',
        class: ['menu-item', item.destructive && 'menu-item-destructive'],
        disabled: item.disabled ?? false,
        tabindex: '-1',
        dataset: { action: item.action },
      }, item.icon ? icon(item.icon) : null, h('span', { text: item.label })))));
    roving.refresh();
  }

  list.addEventListener('click', (event) => {
    const item = event.target.closest('[role=menuitem]');
    if (item && !item.disabled) {
      popover.close();
      trigger?.focus();
      onSelect(item.dataset.action);
    }
  });

  function focusFirst() {
    const first = list.querySelector('[role=menuitem]:not([disabled])');
    if (first) {
      roving.refresh();
      first.tabIndex = 0;
      first.focus();
    }
  }

  render(items);

  return {
    el: popover.el,
    attach(button) {
      trigger = button;
      button.setAttribute('aria-haspopup', 'menu');
      button.setAttribute('aria-expanded', 'false');
      button.addEventListener('click', () => {
        if (popover.isOpen) {
          popover.close();
        } else {
          popover.open(button);
          focusFirst();
        }
      });
      button.addEventListener('keydown', (event) => {
        if (event.key === 'ArrowDown' && !popover.isOpen) {
          event.preventDefault();
          popover.open(button);
          focusFirst();
        }
      });
    },
    openAt(x, y) {
      trigger = document.activeElement;
      popover.open(pointAnchor(x, y));
      focusFirst();
    },
    close: popover.close,
    update({ items: next }) {
      render(next);
    },
    destroy() {
      roving.destroy();
      popover.destroy();
    },
  };
}
