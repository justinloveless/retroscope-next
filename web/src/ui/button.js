import { h, patch, replaceContent } from '../core/h.js';
import { icon } from './icons.js';

// variant: default | primary | secondary | ghost | destructive | emerald | gold | blue | purple.
// press: the pixel-art pressable style (hard shadow, sinks when active); big: the 8 px Neotro size.
function classes({ variant = 'default', size, press, big, className }) {
  return ['btn', `btn-${variant}`, size && `btn-${size}`, press && 'press', big && 'press-big', className];
}

function content({ label, icon: name, iconOnly }) {
  return [
    name ? icon(name) : null,
    label && !iconOnly ? h('span', { class: 'btn-label' }, label) : null,
  ];
}

/**
 * props: label, icon, iconOnly (label becomes aria-label), variant, size (sm | lg | icon), press, big,
 * pressed (aria-pressed), disabled, busy, action/id (data-action/data-id for delegated handlers),
 * onClick, type, title, className.
 */
export function createButton(props) {
  let current = { ...props };
  const el = h('button', {
    type: props.type ?? 'button',
    on: props.onClick ? { click: (event) => current.onClick?.(event) } : undefined,
  });

  function render() {
    patch(el, {
      class: classes(current),
      disabled: current.disabled ?? false,
      title: current.title ?? null,
      'aria-label': current.iconOnly ? current.label : null,
      'aria-pressed': current.pressed == null ? null : String(current.pressed),
      'aria-busy': current.busy ? 'true' : null,
      dataset: { action: current.action, id: current.id },
    });
    replaceContent(el, content(current));
  }

  render();
  return {
    el,
    update(next) {
      current = { ...current, ...next };
      render();
    },
    destroy() {
      el.remove();
    },
  };
}
