import { h } from '../core/h.js';
import { createMenu } from './menu.js';

/**
 * One value out of a short list, drawn as a menu button in the kit's select styling. Use it instead of a
 * native <select>: WebKit reports a style-src-attr CSP violation for every <select>, because the inline
 * styles of its user-agent shadow tree fall under the page's CSP (docs/decisions/UF14.md).
 * options: [{value, label}]; like a select, an unknown value falls back to the first option.
 */
export function createChoice({ label, name, options, value, className, onChange }) {
  let current = { options, value };
  const el = h('button', { type: 'button', class: ['select', 'choice', className], name: name ?? null });
  const menu = createMenu({
    label,
    items: [],
    onSelect(next) {
      if (next !== current.value) {
        current.value = next;
        render();
        onChange?.(next);
      }
    },
  });
  menu.attach(el);

  function render() {
    const chosen = current.options.find((o) => o.value === current.value) ?? current.options[0] ?? null;
    current.value = chosen?.value ?? '';
    el.textContent = chosen?.label ?? '';
    el.setAttribute('aria-label', `${label}: ${chosen?.label ?? ''}`);
    menu.update({
      items: current.options.map((o) => ({ action: o.value, label: o.label, icon: o.value === current.value ? 'check' : 'circle' })),
    });
  }

  render();
  return {
    el,
    get value() {
      return current.value;
    },
    update(next) {
      current = { ...current, ...next };
      el.disabled = next.disabled ?? el.disabled;
      render();
    },
    destroy() {
      menu.destroy();
      el.remove();
    },
  };
}
