import { h, patch } from '../core/h.js';

// Native <select> in kit styling. options: [{value, label, disabled}].
export function createSelect(props) {
  let current = { ...props };
  const select = h('select', { class: 'select', on: { change: () => current.onChange?.(select.value) } });
  const el = props.label
    ? h('label', { class: 'field' }, h('span', { class: 'field-label', text: props.label }), select)
    : select;

  function render() {
    select.replaceChildren(...current.options.map((option) => h('option', {
      value: option.value,
      disabled: option.disabled ?? false,
    }, option.label)));
    select.value = current.value ?? '';
    patch(select, { disabled: current.disabled ?? false, name: current.name ?? null, 'aria-label': props.label ? null : current.ariaLabel ?? null });
  }

  render();
  return {
    el,
    get value() {
      return select.value;
    },
    update(next) {
      current = { ...current, ...next };
      render();
    },
    destroy() {
      el.remove();
    },
  };
}
