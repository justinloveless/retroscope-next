import { h } from '../core/h.js';
import { rovingTabindex } from '../core/a11y.js';
import { icon } from './icons.js';
import { createPopover, pointAnchor } from './popover.js';

const ITEMS = '[role=menuitem], [role=menuitemradio]';

/**
 * Action menu. items: [entry | 'separator'], where an entry is an item {action, label, icon, detail,
 * checked, destructive, disabled}, a labelled group {group, items, className} or a decorative {node}. An
 * item with a boolean `checked` is a menuitemradio; `detail` is a second, quieter line; `icon` is a
 * sprite name or an element. onSelect(action) runs after the menu closes. Bind it to a trigger with
 * attach(button), open it from one with open(button), or at a point with openAt(x, y) for context menus.
 * onClose() runs whenever it closes.
 */
export function createMenu({ items, onSelect, label, placement = 'bottom-start', onClose }) {
  let trigger = null;
  let groups = 0;
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
  const roving = rovingTabindex(popover.el, { selector: ITEMS, orientation: 'vertical' });

  function entry(item) {
    if (item === 'separator') {
      return h('div', { class: 'menu-separator', role: 'separator' });
    }
    if (item.node) {
      return item.node;
    }
    if (item.group) {
      const id = `menu-group-${++groups}`;
      return h('div', { class: ['menu-group', item.className], role: 'group', 'aria-labelledby': id },
        h('span', { class: 'menu-heading', id, text: item.group }),
        item.items.map(entry));
    }
    const radio = typeof item.checked === 'boolean';
    return h('button', {
      type: 'button',
      role: radio ? 'menuitemradio' : 'menuitem',
      'aria-checked': radio ? String(item.checked) : null,
      class: ['menu-item', item.destructive && 'menu-item-destructive'],
      disabled: item.disabled ?? false,
      tabindex: '-1',
      dataset: { action: item.action },
    },
    typeof item.icon === 'string' ? icon(item.icon) : item.icon ?? null,
    item.detail
      ? h('span', { class: 'menu-item-text' }, h('span', { text: item.label }), h('span', { class: 'menu-item-detail', text: item.detail }))
      : h('span', { text: item.label }),
    item.checked ? icon('check', { className: 'menu-item-check' }) : null);
  }

  function render(entries) {
    list.replaceChildren(...entries.filter(Boolean).map(entry));
    roving.refresh();
  }

  list.addEventListener('click', (event) => {
    const item = event.target.closest(ITEMS);
    if (item && !item.disabled) {
      popover.close();
      trigger?.focus();
      onSelect(item.dataset.action);
    }
  });

  function focusFirst() {
    const first = list.querySelector('[role=menuitem]:not([disabled]), [role=menuitemradio]:not([disabled])');
    if (first) {
      roving.refresh();
      first.tabIndex = 0;
      first.focus();
    }
  }

  function open(button) {
    trigger = button;
    popover.open(button);
    focusFirst();
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
          open(button);
        }
      });
      button.addEventListener('keydown', (event) => {
        if (event.key === 'ArrowDown' && !popover.isOpen) {
          event.preventDefault();
          open(button);
        }
      });
    },
    open,
    openAt(x, y) {
      trigger = document.activeElement;
      // A point cannot take focus, so Escape hands it back to whatever held it when the menu opened.
      popover.open({ ...pointAnchor(x, y), focus: () => trigger?.focus?.() });
      focusFirst();
    },
    close: popover.close,
    get isOpen() {
      return popover.isOpen;
    },
    update({ items: next }) {
      render(next);
    },
    destroy() {
      roving.destroy();
      popover.destroy();
    },
  };
}
