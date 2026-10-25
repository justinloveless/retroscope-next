import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { FakeEvent, installDom } from './_dom.js';
import { createAvatar } from '../../../web/src/ui/avatar.js';
import { createButton } from '../../../web/src/ui/button.js';
import { confirmDialog } from '../../../web/src/ui/confirm.js';
import { createMenu } from '../../../web/src/ui/menu.js';
import { createPopover, fit, popoverOpen } from '../../../web/src/ui/popover.js';
import { createResizable } from '../../../web/src/ui/resizable.js';
import { createSidePanel } from '../../../web/src/ui/side-panel.js';
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

test('menu groups radio items under a visible label, with a detail line and a check on the chosen one', () => {
  const chosen = [];
  const badge = doc.createElement('span');
  const menu = createMenu({
    label: 'Teams',
    items: [
      { group: 'Your teams', items: [{ action: 'a', label: 'Apps', detail: 'Owner', icon: badge, checked: true }, { action: 'b', label: 'Billing', checked: false }] },
      'separator',
      { action: 'create', label: 'Create a team', icon: 'plus' },
      null,
    ],
    onSelect: (a) => chosen.push(a),
  });
  const trigger = doc.createElement('button');
  doc.body.appendChild(trigger);
  menu.open(trigger);
  assert.equal(menu.isOpen, true);
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');

  const group = menu.el.querySelector('[role=group]');
  const heading = group.querySelector('.menu-heading');
  assert.equal(heading.textContent, 'Your teams');
  assert.equal(group.getAttribute('aria-labelledby'), heading.id);
  const [apps, billing] = group.querySelectorAll('[role=menuitemradio]');
  assert.equal(apps.getAttribute('aria-checked'), 'true');
  assert.equal(billing.getAttribute('aria-checked'), 'false');
  assert.equal(apps.firstChild, badge, 'an element icon is used as given');
  assert.equal(apps.querySelector('.menu-item-detail').textContent, 'Owner');
  assert.ok(apps.querySelector('.menu-item-check'));
  assert.equal(billing.querySelector('.menu-item-check'), null);
  assert.equal(doc.activeElement, apps, 'the first item takes focus');

  apps.dispatchEvent(key('ArrowDown'));
  assert.equal(doc.activeElement, billing);
  billing.dispatchEvent(key('ArrowDown'));
  assert.equal(doc.activeElement.dataset.action, 'create', 'arrows cross from the group to the items after it');
  billing.click();
  assert.deepEqual(chosen, ['b']);
  assert.equal(menu.isOpen, false);
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

const VIEW = { vw: 1024, vh: 768 };
const anchorAt = (top, left = 100, height = 32, width = 120) => ({ top, left, bottom: top + height, right: left + width, width, height });

test('a popover that fits opens below its anchor, or above when only above has room, with no height cap', () => {
  assert.deepEqual(fit({ width: 200, height: 300 }, anchorAt(100), VIEW), { top: 138, left: 100, maxHeight: null });
  assert.deepEqual(fit({ width: 200, height: 300 }, anchorAt(600), VIEW), { top: 294, left: 100, maxHeight: null });
  assert.deepEqual(fit({ width: 200, height: 300 }, anchorAt(600), VIEW, 'top-end'), { top: 294, left: 20, maxHeight: null });
});

test('a popover taller than either side takes the roomier side and is capped to it, never past the viewport', () => {
  const below = fit({ width: 200, height: 2000 }, anchorAt(100), VIEW);
  assert.deepEqual(below, { top: 138, left: 100, maxHeight: 622 });
  assert.ok(below.top + below.maxHeight <= VIEW.vh - 8);
  const above = fit({ width: 200, height: 2000 }, anchorAt(500), VIEW);
  assert.deepEqual(above, { top: 8, left: 100, maxHeight: 486 });
  assert.equal(above.top + above.maxHeight, 500 - 6);
  assert.deepEqual(fit({ width: 200, height: 2000 }, anchorAt(100), VIEW, 'top-start').maxHeight, 622, 'top flips below when below is roomier');
  assert.deepEqual(fit({ width: 200, height: 2000 }, anchorAt(100), VIEW, 'left'), { top: 8, left: 226, maxHeight: 752 });
});

test('place() caps an overflowing popover from its scroll height, so placing it again keeps the cap', () => {
  const popover = createPopover({ anchor: { getBoundingClientRect: () => anchorAt(100) }, content: [] });
  Object.assign(popover.el, { rect: { width: 200, height: 622 }, scrollHeight: 2000, offsetHeight: 622, clientHeight: 620 });
  popover.open();
  assert.equal(popover.el.style.getPropertyValue('max-height'), '622px');
  assert.equal(popover.el.style.getPropertyValue('top'), '138px');
  popover.el.scrollHeight = 100;
  popover.el.rect = { width: 200, height: 102 };
  popover.close();
  popover.open();
  assert.equal(popover.el.style.getPropertyValue('max-height'), '');
  popover.destroy();
});

test('an anchor scrolled out of view never leaves a popover taller than the viewport', () => {
  assert.deepEqual(fit({ width: 200, height: 1000 }, anchorAt(2000), VIEW), { top: 8, left: 100, maxHeight: 752 });
  assert.deepEqual(fit({ width: 200, height: 1000 }, anchorAt(-2000), VIEW, 'top-start'), { top: 8, left: 100, maxHeight: 752 });
});

test('a popover whose content grows while open is placed again and capped, until it closes', (t) => {
  const observers = [];
  globalThis.ResizeObserver = class {
    constructor(callback) {
      this.callback = callback;
      this.targets = new Set();
      observers.push(this);
    }

    observe(target) {
      this.targets.add(target);
    }

    disconnect() {
      this.targets.clear();
    }
  };
  t.after(() => delete globalThis.ResizeObserver);
  const results = doc.createElement('div');
  const popover = createPopover({ anchor: { getBoundingClientRect: () => anchorAt(100) }, content: results });
  Object.assign(popover.el, { rect: { width: 200, height: 40 }, scrollHeight: 40, offsetHeight: 40, clientHeight: 40 });
  popover.open();
  assert.equal(popover.el.style.getPropertyValue('max-height'), '');
  const [observer] = observers;
  assert.deepEqual([...observer.targets], [results]);

  Object.assign(popover.el, { rect: { width: 200, height: 2000 }, scrollHeight: 2000, offsetHeight: 2000, clientHeight: 2000 });
  observer.callback([]);
  assert.equal(popover.el.style.getPropertyValue('max-height'), '622px');

  popover.close();
  assert.equal(observer.targets.size, 0);
  popover.destroy();
});

test('popoverOpen reports an open menu until it closes or is destroyed', () => {
  const menu = createMenu({ items: [{ action: 'a', label: 'A' }], onSelect() {} });
  const trigger = doc.createElement('button');
  doc.body.appendChild(trigger);
  menu.attach(trigger);
  assert.equal(popoverOpen(doc), false);
  trigger.click();
  assert.equal(popoverOpen(doc), true);
  menu.close();
  assert.equal(popoverOpen(doc), false);
  trigger.click();
  menu.destroy();
  assert.equal(popoverOpen(doc), false);
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

test('side panel handles: grip strip, collapse tab while open, open tab while closed', () => {
  const body = doc.createElement('div');
  const panel = doc.createElement('aside');
  panel.id = 'side';
  body.append(panel);
  doc.body.append(body);
  const asked = [];
  const commits = [];
  const side = createSidePanel({ panel, edge: 'left', name: 'Jira panel', openIcon: 'search', width: 300, open: true, onToggle: (open) => asked.push(open), onCommit: (w) => commits.push(w) });
  const strip = panel.querySelector('[role="separator"]');
  const collapse = panel.querySelector('.drawer-collapse');
  const opener = body.querySelector('.drawer-open');
  assert.equal(strip.getAttribute('aria-label'), 'Resize Jira panel');
  assert.ok(strip.querySelector('.resize-grip'));
  assert.equal(collapse.getAttribute('aria-label'), 'Collapse Jira panel');
  assert.equal(collapse.getAttribute('aria-controls'), 'side');
  assert.equal(opener.getAttribute('aria-label'), 'Open Jira panel');
  assert.equal(opener.hidden, true);
  assert.equal(panel.hidden, false);
  strip.dispatchEvent(key('ArrowRight'));
  assert.deepEqual(commits, [316]);

  collapse.dispatchEvent(new FakeEvent('click', { bubbles: true }));
  assert.deepEqual(asked, [false]);
  assert.equal(panel.hidden, false, 'the owner decides when the panel closes');
  side.setOpen(false, { focus: true });
  assert.equal(panel.hidden, true);
  assert.equal(opener.hidden, false);
  assert.equal(opener.getAttribute('aria-expanded'), 'false');
  assert.equal(doc.activeElement, opener);
  opener.dispatchEvent(new FakeEvent('click', { bubbles: true }));
  assert.deepEqual(asked, [false, true]);
  side.setOpen(true, { focus: true });
  assert.equal(collapse.getAttribute('aria-expanded'), 'true');
  assert.equal(doc.activeElement, collapse);

  side.destroy();
  assert.equal(body.querySelector('.drawer-open'), null);
  assert.equal(panel.querySelector('.drawer-collapse'), null);
  assert.equal(panel.hidden, true);
});
