import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { FakeEvent, installDom } from '../core/_dom.js';
import { createSideSheet } from '../../../web/src/ui/side-sheet.js';

let doc;

beforeEach(() => {
  doc = installDom();
});

const escape = () => new FakeEvent('keydown', { bubbles: true, key: 'Escape' });

test('the sheet starts hidden and detached, and open() attaches, shows and focuses it', () => {
  const sheet = createSideSheet({ label: 'Sprint report', onClose() {} });
  assert.equal(sheet.el.hidden, true);
  assert.equal(sheet.el.isConnected, false);
  assert.equal(sheet.isOpen, false);

  sheet.open();
  assert.equal(sheet.el.parentNode, doc.body);
  assert.equal(sheet.el.hidden, false);
  assert.equal(sheet.isOpen, true);
  assert.equal(doc.activeElement, sheet.el);
  assert.equal(sheet.el.getAttribute('role'), 'dialog');
  assert.equal(sheet.el.getAttribute('aria-label'), 'Sprint report');
});

test('the side and width place the sheet, never wider than the screen', () => {
  const sheet = createSideSheet({ side: 'left', label: 'x', width: '26rem', onClose() {} });
  assert.equal(sheet.el.style.getPropertyValue('left'), '0');
  assert.equal(sheet.el.style.getPropertyValue('width'), 'min(26rem, 100vw)');
  assert.match(sheet.el.className, /jira-sheet-left/);
});

test('Escape and the close button call onClose; an Escape already handled inside does not', () => {
  let closes = 0;
  const sheet = createSideSheet({ label: 'x', onClose: () => closes++ });
  sheet.open();
  sheet.el.dispatchEvent(escape());
  assert.equal(closes, 1);

  const handled = escape();
  handled.preventDefault();
  sheet.el.dispatchEvent(handled);
  assert.equal(closes, 1);

  sheet.el.querySelector('button[aria-label="Close"]').dispatchEvent(new FakeEvent('click', { bubbles: true }));
  assert.equal(closes, 2);
});

test('hide() returns focus to what had it before open(), if it is still on the page', () => {
  const opener = doc.createElement('button');
  doc.body.append(opener);
  opener.focus();
  const sheet = createSideSheet({ label: 'x', onClose() {} });
  sheet.open();
  sheet.open();
  sheet.hide();
  assert.equal(sheet.el.hidden, true);
  assert.equal(doc.activeElement, opener);

  sheet.open();
  opener.remove();
  sheet.hide();
  assert.notEqual(doc.activeElement, opener, 'a removed opener is not focused');
});

test('destroy() takes the sheet off the page', () => {
  const sheet = createSideSheet({ label: 'x', onClose() {} });
  sheet.open();
  sheet.destroy();
  assert.equal(sheet.el.isConnected, false);
});
