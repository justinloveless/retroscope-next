import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';
import { FakeEvent, installDom } from '../core/_dom.js';
import { createMenu } from '../../../web/src/ui/menu.js';
import { bindKeyboard, shortcutFor } from '../../../web/src/poker/keyboard.js';

let doc;

beforeEach(() => {
  doc = installDom();
  doc.documentElement.clientWidth = 1024;
  doc.documentElement.clientHeight = 768;
  globalThis.requestAnimationFrame = (fn) => setTimeout(fn, 0);
  globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
});

function element(tag, attrs = {}) {
  const el = doc.createElement(tag);
  for (const [name, value] of Object.entries(attrs)) {
    el.setAttribute(name, value);
  }
  doc.body.appendChild(el);
  return el;
}

/** Binds the table shortcuts and returns the actions handled and a key presser. */
function bound() {
  const actions = [];
  const unbind = bindKeyboard((action) => actions.push(action.type), doc);
  const press = (target, init = {}) => {
    const event = new FakeEvent('keydown', { bubbles: true, key: 'l', ...init });
    target.dispatchEvent(event);
    return event;
  };
  return { actions, press, unbind };
}

test('shortcutFor maps letters, digits and arrows', () => {
  assert.deepEqual(shortcutFor({ key: 'l' }), { type: 'lock' });
  assert.deepEqual(shortcutFor({ key: 'N' }), { type: 'next' });
  assert.deepEqual(shortcutFor({ code: 'Digit2', key: '2' }), { type: 'vote', points: 2 });
  assert.equal(shortcutFor({ key: 'l', ctrlKey: true }), null);
});

test('a shortcut on the table is handled and its default prevented', () => {
  const { actions, press, unbind } = bound();
  assert.equal(press(element('button')).defaultPrevented, true);
  assert.deepEqual(actions, ['lock']);
  unbind();
  press(element('button'));
  assert.deepEqual(actions, ['lock']);
});

test('keys typed in fields are text, outside contenteditable="false"', () => {
  const { actions, press } = bound();
  for (const [tag, attrs] of [['input'], ['textarea'], ['select'], ['div', { contenteditable: 'true' }]]) {
    assert.equal(press(element(tag, attrs)).defaultPrevented, false, tag);
  }
  assert.deepEqual(actions, []);
  press(element('div', { contenteditable: 'false' }));
  assert.deepEqual(actions, ['lock']);
});

test('an open dialog blocks the shortcuts', () => {
  const { actions, press } = bound();
  element('dialog', { open: '' });
  press(element('button'));
  assert.deepEqual(actions, []);
});

test('an open popover or menu blocks the shortcuts until it closes', () => {
  const { actions, press } = bound();
  const menu = createMenu({ label: 'Round actions', items: [{ action: 'delete', label: 'Delete' }], onSelect() {} });
  const trigger = element('button');
  menu.attach(trigger);
  trigger.click();
  assert.equal(menu.el.hidden, false);
  press(doc.activeElement, { key: 'n' });
  press(trigger, { code: 'Digit3', key: '3' });
  assert.deepEqual(actions, []);

  menu.close();
  press(trigger, { key: 'n' });
  assert.deepEqual(actions, ['next']);
});
