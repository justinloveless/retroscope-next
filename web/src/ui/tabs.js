import { h } from '../core/h.js';
import { rovingTabindex } from '../core/a11y.js';

/**
 * Tab list with automatic activation. tabs: [{id, label}]. Returns {el, panels, select(id),
 * update({tabs, selected}), destroy()}; panels maps tab id → its tabpanel element, for the caller to
 * fill and place.
 */
export function createTabs({ tabs, selected, label, onChange }) {
  const prefix = `tabs-${Math.random().toString(36).slice(2, 8)}`;
  const el = h('div', { class: 'tabs-list', role: 'tablist', 'aria-label': label ?? null });
  const panels = new Map();
  let current = selected ?? tabs[0]?.id;

  function render(list) {
    el.replaceChildren(...list.map((tab) => {
      if (!panels.has(tab.id)) {
        panels.set(tab.id, h('div', { class: 'tabs-panel', role: 'tabpanel', id: `${prefix}-panel-${tab.id}`, 'aria-labelledby': `${prefix}-tab-${tab.id}`, tabindex: '0' }));
      }
      return h('button', {
        type: 'button',
        role: 'tab',
        class: 'tabs-tab',
        id: `${prefix}-tab-${tab.id}`,
        'aria-controls': `${prefix}-panel-${tab.id}`,
        dataset: { tab: tab.id },
      }, tab.label);
    }));
    sync();
  }

  function sync() {
    for (const button of el.querySelectorAll('[role=tab]')) {
      const active = button.dataset.tab === current;
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
    }
    for (const [id, panel] of panels) {
      panel.hidden = id !== current;
    }
  }

  function select(id, notify = false) {
    if (id === current) {
      return;
    }
    current = id;
    sync();
    if (notify) {
      onChange?.(id);
    }
  }

  el.addEventListener('click', (event) => {
    const tab = event.target.closest('[role=tab]');
    if (tab) {
      select(tab.dataset.tab, true);
    }
  });
  const roving = rovingTabindex(el, {
    selector: '[role=tab]',
    onMove: (tab) => select(tab.dataset.tab, true),
  });

  render(tabs);

  return {
    el,
    panels,
    select: (id) => select(id),
    get selected() {
      return current;
    },
    update({ tabs: next, selected: id }) {
      if (next) {
        render(next);
        roving.refresh();
      }
      if (id !== undefined) {
        select(id);
      }
    },
    destroy() {
      roving.destroy();
      el.remove();
    },
  };
}
