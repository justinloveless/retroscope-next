import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { FakeEvent, installDom } from './_dom.js';
import { announce, focusable, trapFocus } from '../../../web/src/core/a11y.js';

let doc;

beforeEach(() => {
  doc = installDom();
});

function sheet() {
  const box = doc.createElement('div');
  const a = doc.createElement('button');
  const hidden = doc.createElement('div');
  hidden.setAttribute('hidden', '');
  hidden.appendChild(doc.createElement('button'));
  const off = doc.createElement('button');
  off.setAttribute('disabled', '');
  const b = doc.createElement('input');
  box.append(a, hidden, off, b);
  doc.body.appendChild(box);
  return { box, a, b };
}

test('focusable skips disabled and hidden controls', () => {
  const { box, a, b } = sheet();
  assert.deepEqual(focusable(box), [a, b]);
});

test('trapFocus wraps Tab at both ends and restores focus on release', () => {
  const outside = doc.createElement('button');
  doc.body.appendChild(outside);
  outside.focus();
  const { box, a, b } = sheet();
  const release = trapFocus(box);
  assert.equal(doc.activeElement, a);

  b.focus();
  const tab = new FakeEvent('keydown', { bubbles: true, key: 'Tab' });
  b.dispatchEvent(tab);
  assert.equal(tab.defaultPrevented, true);
  assert.equal(doc.activeElement, a);

  const back = new FakeEvent('keydown', { bubbles: true, key: 'Tab', shiftKey: true });
  a.dispatchEvent(back);
  assert.equal(doc.activeElement, b);

  release();
  assert.equal(doc.activeElement, outside);
});

test('announce writes to one polite and one assertive live region', async () => {
  announce('Cards revealed');
  announce('Connection lost', { assertive: true });
  announce('Cards revealed again');
  await new Promise((r) => setTimeout(r, 60));
  const polite = doc.body.querySelector('[aria-live=polite]');
  const assertive = doc.body.querySelector('[aria-live=assertive]');
  assert.equal(doc.body.querySelectorAll('[aria-live=polite]').length, 1);
  assert.equal(polite.textContent, 'Cards revealed again');
  assert.equal(assertive.textContent, 'Connection lost');
  assert.equal(polite.className, 'sr-only');
});
