import { h, patch } from '../core/h.js';
import { icon } from './icons.js';
import { createResizable } from './resizable.js';

/**
 * Drawer handles for a side panel that sits against one screen edge (`edge`: 'left' | 'right'): a resize
 * strip with a grip chip on the panel's inner edge, a small chevron tab beside it that collapses the panel,
 * and, while it is collapsed, a taller tab on the screen edge that opens it. `name` ("Jira panel") makes the
 * labels; `openIcon` is the open tab's icon. The panel needs an id (aria-controls).
 *
 * The tabs only ask: onToggle(open) runs on a click and the owner calls setOpen() when the panel is ready.
 * onCommit(width) runs after a drag or a key press, as in createResizable.
 */
export function createSidePanel({ panel, edge, name, openIcon, width, open = true, onToggle, onCommit }) {
  const resizer = createResizable({
    panel,
    side: edge === 'left' ? 'right' : 'left',
    width,
    label: `Resize ${name}`,
    onCommit,
  });
  resizer.el.append(h('span', { class: 'resize-grip', 'aria-hidden': 'true' }, icon('grip-vertical')));
  const collapse = h('button', {
    type: 'button',
    class: ['drawer-tab', 'drawer-collapse', `drawer-${edge}`],
    'aria-label': `Collapse ${name}`,
    'aria-expanded': 'true',
    'aria-controls': panel.id,
    on: { click: () => onToggle(false) },
  }, icon(edge === 'left' ? 'chevron-left' : 'chevron-right'));
  const opener = h('button', {
    type: 'button',
    class: ['drawer-tab', 'drawer-open', `drawer-${edge}`],
    'aria-label': `Open ${name}`,
    'aria-expanded': 'false',
    'aria-controls': panel.id,
    hidden: true,
    on: { click: () => onToggle(true) },
  }, icon(openIcon));
  panel.classList.add('drawer-panel', `drawer-panel-${edge}`);
  panel.append(resizer.el, collapse);
  panel.after(opener);

  let isOpen = open;

  function setOpen(next, { focus = false } = {}) {
    isOpen = next;
    panel.hidden = !next;
    opener.hidden = next;
    patch(collapse, { 'aria-expanded': String(next) });
    patch(opener, { 'aria-expanded': String(next) });
    if (focus) {
      (next ? collapse : opener).focus();
    }
  }

  setOpen(open);
  return {
    get open() {
      return isOpen;
    },
    get width() {
      return resizer.width;
    },
    setOpen,
    destroy() {
      resizer.destroy();
      collapse.remove();
      opener.remove();
      panel.classList.remove('drawer-panel', `drawer-panel-${edge}`);
      panel.style.removeProperty('width');
      panel.hidden = true;
    },
  };
}
