import assert from 'node:assert/strict';
import { test } from 'node:test';
import { bindKudosKey, togglesKudos } from '../../../web/src/retro/keyboard.js';

/** A keydown on `target` (a button unless it says otherwise). */
function key(fields = {}) {
  return { key: 'n', target: field(null), ...fields };
}

/** An element whose closest() matches only when it is a text field. */
function field(kind) {
  return { closest: (selector) => (kind !== null && selector.includes(kind) ? {} : null) };
}

test('n and N toggle the drawer', () => {
  assert.equal(togglesKudos(key()), true);
  assert.equal(togglesKudos(key({ key: 'N' })), true);
  assert.equal(togglesKudos(key({ key: 'm' })), false);
});

test('modifiers, repeats, IME composition and handled keys do not', () => {
  for (const extra of ['ctrlKey', 'metaKey', 'altKey', 'shiftKey', 'repeat', 'isComposing', 'defaultPrevented']) {
    assert.equal(togglesKudos(key({ [extra]: true })), false, extra);
  }
});

test('typing in a field does not', () => {
  for (const kind of ['input', 'textarea', 'select', '[contenteditable]']) {
    assert.equal(togglesKudos(key({ target: field(kind) })), false, kind);
  }
  assert.equal(togglesKudos(key({ target: null })), true);
});

test('another open dialog blocks it, the kudos drawer itself does not', () => {
  const drawer = {};
  const other = {};
  assert.equal(togglesKudos(key(), { openDialogs: [other], drawer: null }), false);
  assert.equal(togglesKudos(key(), { openDialogs: [drawer], drawer }), true);
  assert.equal(togglesKudos(key(), { openDialogs: [drawer, other], drawer }), false);
});

test('bindKudosKey toggles, prevents the default and unbinds', () => {
  const listeners = new Set();
  const open = [];
  const doc = {
    addEventListener: (type, listener) => listeners.add(listener),
    removeEventListener: (type, listener) => listeners.delete(listener),
    querySelectorAll: () => open,
  };
  let toggles = 0;
  const unbind = bindKudosKey(() => toggles++, () => null, doc);
  const event = { ...key(), preventDefault() {
    this.defaultPrevented = true;
  } };
  for (const listener of listeners) {
    listener(event);
  }
  assert.equal(toggles, 1);
  assert.equal(event.defaultPrevented, true);

  open.push({});
  for (const listener of listeners) {
    listener(key());
  }
  assert.equal(toggles, 1);

  unbind();
  assert.equal(listeners.size, 0);
});
