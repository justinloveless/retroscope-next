import { h, patch } from '../core/h.js';

// Toggle switch (button role=switch). props: checked, label, disabled, onChange(checked).
export function createSwitch(props) {
  let current = { ...props };
  const thumb = h('span', { class: 'switch-thumb', 'aria-hidden': 'true' });
  const button = h('button', { type: 'button', role: 'switch', class: 'switch' }, thumb);
  const text = h('span', { class: 'switch-text' });
  const el = h('label', { class: 'switch-field' }, button, text);

  button.addEventListener('click', () => {
    current.checked = !current.checked;
    render();
    current.onChange?.(current.checked);
  });

  function render() {
    patch(button, { 'aria-checked': String(Boolean(current.checked)), disabled: current.disabled ?? false });
    text.textContent = current.label ?? '';
  }

  render();
  return {
    el,
    get checked() {
      return Boolean(current.checked);
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
