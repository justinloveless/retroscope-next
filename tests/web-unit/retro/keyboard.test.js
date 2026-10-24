import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';
import { FakeEvent, installDom } from '../core/_dom.js';
import { bindKudosKey, togglesKudos } from '../../../web/src/retro/keyboard.js';

let doc;

beforeEach(() => {
  doc = installDom();
});

/** An element in the page, with its attributes. */
function element(tag, attrs = {}) {
  const el = doc.createElement(tag);
  for (const [name, value] of Object.entries(attrs)) {
    el.setAttribute(name, value);
  }
  doc.body.appendChild(el);
  return el;
}

/** A keydown on `target` (a button unless it says otherwise). */
function key(fields = {}) {
  return { key: 'n', target: element('button'), ...fields };
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
  for (const [tag, attrs] of [['input'], ['textarea'], ['select'], ['div', { contenteditable: 'true' }]]) {
    assert.equal(togglesKudos(key({ target: element(tag, attrs) })), false, tag);
  }
  assert.equal(togglesKudos(key({ target: element('div', { contenteditable: 'false' }) })), true);
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
  let toggles = 0;
  const unbind = bindKudosKey(() => toggles++, () => null, doc);
  const button = element('button');
  const press = () => {
    const event = new FakeEvent('keydown', { bubbles: true, key: 'n' });
    button.dispatchEvent(event);
    return event;
  };
  assert.equal(press().defaultPrevented, true);
  assert.equal(toggles, 1);

  element('dialog', { open: '' });
  assert.equal(press().defaultPrevented, false);
  assert.equal(toggles, 1);

  unbind();
  doc.querySelector('dialog').remove();
  press();
  assert.equal(toggles, 1);
});
