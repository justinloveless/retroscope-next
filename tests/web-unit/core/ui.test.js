import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { FakeEvent, installDom } from './_dom.js';
import { createAvatar } from '../../../web/src/ui/avatar.js';
import { createButton } from '../../../web/src/ui/button.js';
import { confirmDialog } from '../../../web/src/ui/confirm.js';
import { createMenu } from '../../../web/src/ui/menu.js';
import { createResizable } from '../../../web/src/ui/resizable.js';
import { createSelect } from '../../../web/src/ui/select.js';
import { createSwitch } from '../../../web/src/ui/switch.js';
import { createTabs } from '../../../web/src/ui/tabs.js';
import { toast } from '../../../web/src/ui/toast.js';

let doc;

beforeEach(() => {
  doc = installDom();
  doc.documentElement.clientWidth = 1024;
  doc.documentElement.clientHeight = 768;
  globalThis.requestAnimationFrame = (fn) => setTimeout(fn, 0);
  globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const proto = Object.getPrototypeOf(doc.createElement('dialog'));
  proto.showModal = function showModal() {
    this.open = true;
  };
  proto.close = function close(value = '') {
    this.open = false;
    this.returnValue = value;
    this.dispatchEvent(new FakeEvent('close'));
  };
});

const key = (k, init = {}) => new FakeEvent('keydown', { bubbles: true, key: k, ...init });

test('button renders variant, press style, label and delegation data', () => {
  let clicks = 0;
  const button = createButton({ label: 'Reveal', variant: 'blue', press: true, big: true, action: 'reveal', onClick: () => clicks++ });
  assert.equal(button.el.className, 'btn btn-blue press press-big');
  assert.equal(button.el.dataset.action, 'reveal');
  assert.equal(button.el.textContent, 'Reveal');
  button.el.click();
  assert.equal(clicks, 1);
  button.update({ pressed: true, disabled: true, iconOnly: true, icon: 'lock' });
  assert.equal(button.el.getAttribute('aria-pressed'), 'true');
  assert.equal(button.el.getAttribute('aria-label'), 'Reveal');
  assert.equal(button.el.disabled, true);
  assert.equal(button.el.querySelector('svg').firstChild.getAttribute('href'), '#lock');
});

test('switch toggles aria-checked and reports the new value', () => {
  const seen = [];
  const toggle = createSwitch({ label: 'Observer', checked: false, onChange: (v) => seen.push(v) });
  const button = toggle.el.querySelector('[role=switch]');
  button.click();
  assert.equal(button.getAttribute('aria-checked'), 'true');
  button.click();
  assert.deepEqual(seen, [true, false]);
  toggle.update({ checked: true });
  assert.equal(toggle.checked, true);
});

test('tabs select by click and by arrow keys, showing one panel', () => {
  const changes = [];
  const tabs = createTabs({
    tabs: [{ id: 'boards', label: 'Boards' }, { id: 'poker', label: 'Poker' }, { id: 'members', label: 'Members' }],
    selected: 'poker',
    onChange: (id) => changes.push(id),
  });
  doc.body.appendChild(tabs.el);
  const [boards, poker, members] = tabs.el.querySelectorAll('[role=tab]');
  assert.equal(poker.getAttribute('aria-selected'), 'true');
  assert.equal(poker.tabIndex, 0);
  assert.equal(boards.tabIndex, -1);
  assert.equal(tabs.panels.get('boards').hidden, true);
  assert.equal(tabs.panels.get('poker').hidden, false);

  members.click();
  assert.equal(tabs.selected, 'members');
  members.dispatchEvent(key('ArrowRight'));
  assert.equal(tabs.selected, 'boards', 'wraps around');
  assert.equal(doc.activeElement, boards);
  boards.dispatchEvent(key('End'));
  assert.deepEqual(changes, ['members', 'boards', 'members']);

  tabs.update({ selected: 'poker' });
  assert.equal(tabs.selected, 'poker');
  assert.deepEqual(changes, ['members', 'boards', 'members'], 'programmatic selection does not notify');
});

test('toasts stack up to three, errors are alerts and stay until dismissed', async () => {
  const dismiss = toast('Saved', { variant: 'success', duration: 5 });
  toast('Card could not be added', { variant: 'error' });
  toast('Third');
  toast('Fourth');
  const region = doc.body.querySelector('.toaster');
  assert.equal(region.children.length, 3);
  assert.equal(region.textContent.includes('Saved'), false, 'oldest dropped');
  assert.equal(region.querySelector('[role=alert]').textContent.startsWith('Card could not be added'), true);
  dismiss();
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(region.children.length, 3);
  region.querySelector('[role=alert]').querySelector('.toast-close').click();
  assert.equal(region.children.length, 2);
});

test('toast action runs once and dismisses', () => {
  let undone = 0;
  toast('Card deleted', { action: { label: 'Undo', onClick: () => undone++ } });
  const region = doc.body.querySelector('.toaster');
  region.querySelector('.btn-secondary').click();
  assert.equal(undone, 1);
  assert.equal(region.children.length, 0);
});

test('menu opens from its trigger, moves with arrows and reports the chosen action', () => {
  const chosen = [];
  const menu = createMenu({
    label: 'Round actions',
    items: [{ action: 'left', label: 'Move left' }, 'separator', { action: 'delete', label: 'Delete', destructive: true }, { action: 'off', label: 'Off', disabled: true }],
    onSelect: (a) => chosen.push(a),
  });
  const trigger = doc.createElement('button');
  doc.body.appendChild(trigger);
  menu.attach(trigger);
  trigger.click();
  assert.equal(menu.el.hidden, false);
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');
  const [first, second] = menu.el.querySelectorAll('[role=menuitem]');
  assert.equal(doc.activeElement, first);
  first.dispatchEvent(key('ArrowDown'));
  assert.equal(doc.activeElement, second);
  second.click();
  assert.deepEqual(chosen, ['delete']);
  assert.equal(menu.el.hidden, true);
  assert.equal(doc.activeElement, trigger);
});

test('menu closes on Escape and returns focus to the anchor', () => {
  const menu = createMenu({ items: [{ action: 'a', label: 'A' }], onSelect() {} });
  const trigger = doc.createElement('button');
  doc.body.appendChild(trigger);
  menu.attach(trigger);
  trigger.click();
  menu.el.querySelector('[role=menuitem]').dispatchEvent(key('Escape'));
  assert.equal(menu.el.hidden, true);
  assert.equal(doc.activeElement, trigger);
});

test('confirmDialog resolves true on confirm and false on dismiss', async () => {
  const yes = confirmDialog({ title: 'Delete round?', message: 'This cannot be undone.', confirmLabel: 'Delete', destructive: true });
  let dialog = doc.body.querySelector('dialog');
  assert.equal(dialog.open, true);
  assert.equal(dialog.querySelector('.dialog-description').textContent, 'This cannot be undone.');
  dialog.querySelector('.btn-destructive').click();
  assert.equal(await yes, true);
  assert.equal(doc.body.querySelector('dialog'), null);

  const no = confirmDialog({ title: 'Leave?' });
  dialog = doc.body.querySelector('dialog');
  dialog.querySelector('.dialog-close').click();
  assert.equal(await no, false);
});

test('avatar shows initials without an image and falls back when the image fails', () => {
  const avatar = createAvatar({ name: 'Ada Lovelace' });
  assert.equal(avatar.el.textContent, 'AL');
  assert.equal(avatar.el.getAttribute('aria-label'), 'Ada Lovelace');
  avatar.update({ src: '/api/blobs/1' });
  const img = avatar.el.querySelector('img');
  img.dispatchEvent(new FakeEvent('error'));
  assert.equal(avatar.el.textContent, 'AL');
  assert.throws(() => createAvatar({ name: 'x', src: 'javascript:alert(1)' }), /refused/);
});

test('select reflects options and value', () => {
  const changes = [];
  const select = createSelect({ label: 'Sort', options: [{ value: 'votes', label: 'Votes' }, { value: 'time', label: 'Time' }], value: 'time', onChange: (v) => changes.push(v) });
  const el = select.el.querySelector('select');
  assert.equal(el.querySelectorAll('option').length, 2);
  assert.equal(el.value, 'time');
  el.value = 'votes';
  el.dispatchEvent(new FakeEvent('change'));
  assert.deepEqual(changes, ['votes']);
});

test('resizable clamps width and resizes with arrow keys', () => {
  const panel = doc.createElement('aside');
  const commits = [];
  const handle = createResizable({ panel, side: 'right', width: 470, onCommit: (w) => commits.push(w) });
  assert.equal(panel.style.getPropertyValue('width'), '470px');
  handle.el.dispatchEvent(key('ArrowRight'));
  assert.equal(handle.width, 480);
  handle.el.dispatchEvent(key('ArrowLeft'));
  assert.equal(handle.width, 464);
  assert.deepEqual(commits, [480, 464]);
  handle.update({ width: 100 });
  assert.equal(handle.width, 240);
  assert.equal(handle.el.getAttribute('aria-valuenow'), '240');
});
