// Table shortcuts (§g.8): 1–7 pick a card, Shift+digit votes between that card and the next one, L lock,
// A abstain, R reveal, N next round, ←/→ change round. Nothing fires while a text field or a dialog has
// the keyboard.

import { isTypingTarget } from '../core/a11y.js';
import { DECK } from './model.js';

const DIGIT = /^(?:Digit|Numpad)([1-9])$/;

/** The action a keydown maps to, or null: {type, points?, high?, step?}. */
export function shortcutFor(event) {
  if (event.ctrlKey || event.metaKey || event.altKey || event.isComposing) {
    return null;
  }
  const digit = DIGIT.exec(event.code ?? '');
  if (digit) {
    const i = Number(digit[1]) - 1;
    if (i >= DECK.length) {
      return null;
    }
    if (event.shiftKey) {
      return i + 1 < DECK.length ? { type: 'vote', points: DECK[i], high: DECK[i + 1] } : null;
    }
    return { type: 'vote', points: DECK[i] };
  }
  if (event.shiftKey) {
    return null;
  }
  switch (event.key) {
    case 'l':
    case 'L':
      return { type: 'lock' };
    case 'a':
    case 'A':
      return { type: 'abstain' };
    case 'r':
    case 'R':
      return { type: 'reveal' };
    case 'n':
    case 'N':
      return { type: 'next' };
    case 'ArrowLeft':
      return { type: 'round', step: -1 };
    case 'ArrowRight':
      return { type: 'round', step: 1 };
    default:
      return null;
  }
}

/** Calls handle(action, event) for shortcuts typed anywhere outside fields and dialogs; returns unbind. */
export function bindKeyboard(handle, doc = document) {
  function onKey(event) {
    if (event.defaultPrevented || event.repeat || isTypingTarget(event.target) || doc.querySelector('dialog[open]')) {
      return;
    }
    const action = shortcutFor(event);
    if (action && handle(action, event) !== false) {
      event.preventDefault();
    }
  }
  doc.addEventListener('keydown', onKey);
  return () => doc.removeEventListener('keydown', onKey);
}
