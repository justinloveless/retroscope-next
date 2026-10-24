// Board shortcuts (UX16): N opens and closes the kudos drawer, as in the original. Nothing fires while a text
// field has the keyboard or another dialog, popover or menu is open; inside the drawer itself N still closes it.

import { isTypingTarget } from '../core/a11y.js';
import { popoverOpen } from '../ui/popover.js';

/**
 * Whether a keydown toggles the kudos drawer. `openDialogs` are the page's open dialogs, `drawer` the
 * kudos one (or null while it is closed) and `popover` whether a popover or menu is open.
 */
export function togglesKudos(event, { openDialogs = [], drawer = null, popover = false } = {}) {
  if (event.defaultPrevented || event.repeat || event.isComposing
    || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) {
    return false;
  }
  if (event.key !== 'n' && event.key !== 'N') {
    return false;
  }
  return !popover && !isTypingTarget(event.target) && openDialogs.every((dialog) => dialog === drawer);
}

/** Calls toggle() on N typed anywhere outside fields, other dialogs and popovers; returns unbind. */
export function bindKudosKey(toggle, drawer, doc = document) {
  function onKey(event) {
    const openDialogs = [...doc.querySelectorAll('dialog[open]')];
    if (togglesKudos(event, { openDialogs, drawer: drawer(), popover: popoverOpen(doc) })) {
      event.preventDefault();
      toggle();
    }
  }
  doc.addEventListener('keydown', onKey);
  return () => doc.removeEventListener('keydown', onKey);
}
