import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';
import { FakeEvent, installDom } from '../core/_dom.js';
import { createMenu } from '../../../web/src/ui/menu.js';
import { bindKudosKey, togglesKudos } from '../../../web/src/retro/keyboard.js';

let doc;

beforeEach(() => {
  doc = installDom();
  doc.documentElement.clientWidth = 1024;
  doc.documentElement.clientHeight = 768;
  globalThis.requestAnimationFrame = (fn) => setTimeout(fn, 0);
  globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
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

test('an open popover or menu blocks it, even over the drawer', () => {
  const drawer = {};
  assert.equal(togglesKudos(key(), { popover: true }), false);
  assert.equal(togglesKudos(key(), { openDialogs: [drawer], drawer, popover: true }), false);
});

test('bindKudosKey stands down while an action menu is open and resumes when it closes', () => {
  let toggles = 0;
  const unbind = bindKudosKey(() => toggles++, () => null, doc);
  const menu = createMenu({ label: 'Card actions', items: [{ action: 'edit', label: 'Edit' }], onSelect() {} });
  const trigger = element('button');
  menu.attach(trigger);
  const press = () => doc.activeElement.dispatchEvent(new FakeEvent('keydown', { bubbles: true, key: 'n' }));

  trigger.click();
  assert.equal(menu.el.hidden, false);
  press();
  assert.equal(toggles, 0);

  menu.close();
  trigger.focus();
  press();
  assert.equal(toggles, 1);
  unbind();
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
