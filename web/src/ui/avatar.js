import { h, replaceContent } from '../core/h.js';
import { initials } from '../core/fmt.js';

// Avatar image, falling back to initials when there is no image or it fails to load.
// size: sm | md | lg. The name is the accessible label.
export function createAvatar(props) {
  let current = { ...props };
  const el = h('span', { class: 'avatar', role: 'img' });

  function render() {
    el.className = ['avatar', `avatar-${current.size ?? 'md'}`, current.className].filter(Boolean).join(' ');
    el.setAttribute('aria-label', current.name ?? '');
    el.title = current.name ?? '';
    const fallback = h('span', { class: 'avatar-initials', 'aria-hidden': 'true', text: initials(current.name) });
    if (current.src) {
      const img = h('img', { src: current.src, alt: '', loading: 'lazy', draggable: 'false' });
      img.addEventListener('error', () => img.replaceWith(fallback), { once: true });
      replaceContent(el, img);
    } else {
      replaceContent(el, fallback);
    }
  }

  render();
  return {
    el,
    update(next) {
      const changed = next.src !== current.src || next.name !== current.name || next.size !== current.size;
      current = { ...current, ...next };
      if (changed) {
        render();
      }
    },
    destroy() {
      el.remove();
    },
  };
}
