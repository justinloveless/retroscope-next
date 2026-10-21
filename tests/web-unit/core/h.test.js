import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, SVG_NS } from './_dom.js';
import { h, patch, replaceContent, s, safeUrl } from '../../../web/src/core/h.js';

beforeEach(() => {
  installDom();
});

test('strings become text nodes, never markup', () => {
  const el = h('p', null, '<img src=x onerror=alert(1)>', 42, null, false, ['a', ['b']]);
  assert.equal(el.childNodes.length, 4);
  assert.ok(el.childNodes.every((n) => n.nodeType === 3));
  assert.equal(el.textContent, '<img src=x onerror=alert(1)>42ab');
});

test('class, dataset, style and listeners', () => {
  let clicked = 0;
  const el = h('button', {
    class: ['btn', false, 'btn-primary'],
    dataset: { action: 'vote', cardId: 'c1' },
    style: { '--i': 3, width: '10px' },
    on: { click: () => clicked++ },
    'aria-label': 'Vote',
    disabled: true,
  });
  assert.equal(el.className, 'btn btn-primary');
  assert.equal(el.getAttribute('data-card-id'), 'c1');
  assert.equal(el.style.getPropertyValue('--i'), '3');
  assert.equal(el.getAttribute('aria-label'), 'Vote');
  assert.equal(el.getAttribute('disabled'), '');
  el.click();
  assert.equal(clicked, 1);
});

test('attributes outside the allow-list and inline handlers are refused', () => {
  assert.throws(() => h('div', { onclick: 'alert(1)' }), /"on" prop/);
  assert.throws(() => h('div', { srcdoc: 'x' }), /not allowed/);
  assert.throws(() => h('div', { style: 'color:red' }), TypeError);
  assert.throws(() => h('iframe', { formaction: '/x' }), /not allowed/);
});

test('URL attributes accept only safe schemes', () => {
  assert.equal(h('a', { href: '/teams/1' }).getAttribute('href'), '/teams/1');
  assert.equal(h('a', { href: 'https://x.test/a' }).getAttribute('href'), 'https://x.test/a');
  assert.equal(h('a', { href: 'mailto:a@b.c' }).getAttribute('href'), 'mailto:a@b.c');
  for (const bad of ['javascript:alert(1)', ' JavaScript:alert(1)', 'java\tscript:alert(1)', 'data:text/html,x', 'vbscript:x']) {
    assert.throws(() => h('a', { href: bad }), /refused/, bad);
  }
  assert.equal(h('img', { src: 'blob:https://x/1' }).getAttribute('src'), 'blob:https://x/1');
  assert.equal(h('img', { src: 'data:image/png;base64,AA' }).getAttribute('src'), 'data:image/png;base64,AA');
  assert.throws(() => h('img', { src: 'data:image/svg+xml;base64,AA' }), /refused/);
});

test('safeUrl rejects protocol-relative and backslash tricks', () => {
  assert.equal(safeUrl('//evil.test'), null);
  assert.equal(safeUrl('/\\evil.test'), null);
  assert.equal(safeUrl('\\\\evil.test'), null);
  assert.equal(safeUrl('/%09/evil.test'), '/%09/evil.test');
  assert.equal(safeUrl('/\t/evil.test'), null);
  assert.equal(safeUrl('#section'), '#section');
  assert.equal(safeUrl('relative/path'), 'relative/path');
});

test('value and checked are set as properties', () => {
  const input = h('input', { type: 'checkbox', checked: true, value: 'x' });
  assert.equal(input.checked, true);
  assert.equal(input.value, 'x');
  assert.equal(input.getAttribute('checked'), null);
});

test('s() builds SVG in the SVG namespace with its own allow-list', () => {
  const svg = s('svg', { viewBox: '0 0 24 24', class: 'icon', 'aria-hidden': 'true' }, s('use', { href: '/assets/icons.svg#x' }));
  assert.equal(svg.namespaceURI, SVG_NS);
  assert.equal(svg.getAttribute('class'), 'icon');
  assert.equal(svg.firstChild.getAttribute('href'), '/assets/icons.svg#x');
  assert.throws(() => s('svg', { onload: 'x' }), /"on" prop/);
});

test('patch and replaceContent update in place', () => {
  const el = h('div', { title: 'a', hidden: true }, 'old');
  patch(el, { title: null, hidden: false, dataset: { x: 1 } });
  assert.equal(el.hasAttribute('title'), false);
  assert.equal(el.hasAttribute('hidden'), false);
  assert.equal(el.dataset.x, '1');
  replaceContent(el, h('span', null, 'new'));
  assert.equal(el.textContent, 'new');
});
