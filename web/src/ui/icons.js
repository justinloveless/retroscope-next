import { s } from '../core/h.js';

// RS_ASSETS is defined by build.mjs: logical asset name → content-hashed URL.
const sprite = typeof RS_ASSETS === 'object' ? RS_ASSETS['icons.svg'] ?? '' : '';

// A Lucide icon from the sprite (symbol id = Lucide name). Decorative unless given a label.
export function icon(name, { label, className, size } = {}) {
  return s('svg', {
    class: ['icon', className],
    width: size,
    height: size,
    'aria-hidden': label ? null : 'true',
    role: label ? 'img' : null,
    'aria-label': label ?? null,
    focusable: 'false',
  }, s('use', { href: `${sprite}#${name}` }));
}
