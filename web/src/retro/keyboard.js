// Board shortcuts (UX16): N opens and closes the kudos drawer, as in the original. Nothing fires while a text
// field has the keyboard or another dialog is open; inside the drawer itself N still closes it.

import { isTypingTarget } from '../core/a11y.js';

/**
 * Whether a keydown toggles the kudos drawer. `openDialogs` are the page's open dialogs and `drawer` the
 * kudos one (or null while it is closed).
 */
export function togglesKudos(event, { openDialogs = [], drawer = null } = {}) {
  if (event.defaultPrevented || event.repeat || event.isComposing
    || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) {
    return false;
  }
  if (event.key !== 'n' && event.key !== 'N') {
    return false;
  }
  return !isTypingTarget(event.target) && openDialogs.every((dialog) => dialog === drawer);
}

/** Calls toggle() on N typed anywhere outside fields and other dialogs; returns unbind. */
export function bindKudosKey(toggle, drawer, doc = document) {
  function onKey(event) {
    if (togglesKudos(event, { openDialogs: [...doc.querySelectorAll('dialog[open]')], drawer: drawer() })) {
      event.preventDefault();
      toggle();
    }
  }
  doc.addEventListener('keydown', onKey);
  return () => doc.removeEventListener('keydown', onKey);
}
