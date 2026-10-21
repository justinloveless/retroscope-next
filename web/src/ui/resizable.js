import { h, patch, setStyle } from '../core/h.js';

const STEP = 16;

/**
 * Drag handle that resizes `panel` between min and max px. side: the panel edge the handle sits on
 * ('right' for a left-hand panel). onResize(width) fires while dragging; onCommit(width) once at the
 * end (persist there). Arrow keys resize by 16 px.
 */
export function createResizable({ panel, side = 'right', min = 240, max = 480, width, label = 'Resize panel', onResize, onCommit }) {
  let current = clamp(width ?? panel.getBoundingClientRect().width);
  const el = h('div', {
    class: ['resize-handle', `resize-handle-${side}`],
    role: 'separator',
    tabindex: '0',
    'aria-orientation': 'vertical',
    'aria-label': label,
    'aria-valuemin': min,
    'aria-valuemax': max,
  });

  function clamp(value) {
    return Math.round(Math.min(max, Math.max(min, value)));
  }

  function apply(value) {
    current = clamp(value);
    setStyle(panel, { width: `${current}px` });
    patch(el, { 'aria-valuenow': current });
    onResize?.(current);
  }

  el.addEventListener('pointerdown', (event) => {
    if (event.button !== 0) {
      return;
    }
    event.preventDefault();
    el.setPointerCapture(event.pointerId);
    const startX = event.clientX;
    const startWidth = current;
    const direction = side === 'right' ? 1 : -1;
    el.classList.add('resizing');

    function onMove(move) {
      apply(startWidth + (move.clientX - startX) * direction);
    }
    function onEnd() {
      el.classList.remove('resizing');
      el.removeEventListener('pointermove', onMove);
      el.removeEventListener('pointerup', onEnd);
      el.removeEventListener('pointercancel', onEnd);
      onCommit?.(current);
    }
    el.addEventListener('pointermove', onMove);
    el.addEventListener('pointerup', onEnd);
    el.addEventListener('pointercancel', onEnd);
  });

  el.addEventListener('keydown', (event) => {
    const grow = side === 'right' ? 'ArrowRight' : 'ArrowLeft';
    const shrink = side === 'right' ? 'ArrowLeft' : 'ArrowRight';
    if (event.key === grow || event.key === shrink) {
      event.preventDefault();
      apply(current + (event.key === grow ? STEP : -STEP));
      onCommit?.(current);
    }
  });

  apply(current);
  return {
    el,
    get width() {
      return current;
    },
    update({ width: next }) {
      apply(next);
    },
    destroy() {
      el.remove();
    },
  };
}
