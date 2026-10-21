import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom } from './core/_dom.js';
import { itemBeside, offsetToPoint, pointToOffset, scan } from '../../web/src/markup/caret.js';
import { canonicalMarkup, domToDoc, normalizeHref } from '../../web/src/markup/editor.js';
import { createHistory } from '../../web/src/markup/undo.js';

const ALICE = '0f8fad5b-d9cb-469f-a165-70867728950e';

// Builds a tree from ['tag', {attrs}, ...children] arrays and strings.
function build(doc, spec) {
  if (typeof spec === 'string') {
    return doc.createTextNode(spec);
  }
  const [tag, attributes, ...children] = spec;
  const el = doc.createElement(tag);
  for (const [name, value] of Object.entries(attributes ?? {})) {
    el.setAttribute(name, value);
  }
  for (const child of children) {
    el.appendChild(build(doc, child));
  }
  return el;
}

function root(...children) {
  const doc = installDom();
  return build(doc, ['div', { contenteditable: 'true' }, ...children]);
}

const pill = (name = 'Alice') => ['span', { class: 'mention', 'data-mention-id': ALICE, contenteditable: 'false' }, `@${name}`];

function serialize(el, options) {
  const { doc, dirty } = domToDoc(el, options);
  return { markup: canonicalMarkup(doc, options?.dialect), dirty };
}

// ---- undo.js ----

function history(options) {
  let clock = 0;
  const h = createHistory({ markup: '', caret: { start: 0, end: 0 } }, { now: () => clock, ...options });
  return { h, tick: (ms) => { clock += ms; } };
}

const state = (markup) => ({ markup, caret: { start: markup.length, end: markup.length } });

test('undo coalesces a typing burst and walks back and forth', () => {
  const { h, tick } = history();
  h.record(state('o'), 'insert');
  tick(100);
  h.record(state('on'), 'insert');
  tick(100);
  h.record(state('one'), 'insert');
  tick(100);
  h.record(state('**one**'));
  assert.equal(h.undo().markup, 'one');
  assert.equal(h.undo().markup, '');
  assert.equal(h.undo(), null);
  assert.equal(h.redo().markup, 'one');
  assert.equal(h.redo().markup, '**one**');
  assert.equal(h.redo(), null);
});

test('a pause or a different key starts a new undo entry', () => {
  const { h, tick } = history();
  h.record(state('a'), 'insert');
  tick(2000);
  h.record(state('ab'), 'insert');
  tick(10);
  h.record(state('a'), 'delete');
  assert.equal(h.undo().markup, 'ab');
  assert.equal(h.undo().markup, 'a');
  assert.equal(h.undo().markup, '');
});

test('recording after an undo drops the redo branch and never amends the restored entry', () => {
  const { h } = history();
  h.record(state('one'), 'insert');
  h.record(state('one two'), null);
  h.undo();
  h.record(state('one!'), 'insert');
  assert.equal(h.canRedo(), false);
  assert.equal(h.undo().markup, 'one');
  assert.equal(h.current().markup, 'one');
});

test('an unchanged markup only moves the caret and the stack is bounded', () => {
  const { h } = history({ limit: 3 });
  h.record({ markup: '', caret: { start: 0, end: 0 } });
  assert.equal(h.canUndo(), false);
  for (const markup of ['a', 'b', 'c', 'd']) {
    h.record(state(markup));
  }
  assert.equal(h.undo().markup, 'c');
  assert.equal(h.undo().markup, 'b');
  assert.equal(h.undo(), null);
  h.reset(state('x'));
  assert.equal(h.canUndo(), false);
  assert.equal(h.current().markup, 'x');
});

// ---- caret.js ----

test('linear offsets count characters, atoms, line breaks and block breaks', () => {
  const el = root(['p', {}, 'ab', pill(), 'c'], ['p', {}, 'd', ['br'], 'e', ['br']]);
  const { items, length } = scan(el);
  assert.deepEqual(items.map((i) => [i.kind, i.start, i.end, i.breakBefore]), [
    ['text', 0, 2, false],
    ['atom', 2, 3, false],
    ['text', 3, 4, false],
    ['text', 5, 6, true],
    ['br', 6, 7, false],
    ['text', 7, 8, false],
  ]);
  assert.equal(length, 8);
});

test('offsets and DOM points map both ways', () => {
  const el = root(['p', {}, 'ab', pill(), 'c'], ['p', {}, 'd', ['br'], 'e']);
  const [p1, p2] = el.childNodes;
  for (let offset = 0; offset <= 8; offset++) {
    const point = offsetToPoint(el, offset);
    assert.equal(pointToOffset(el, point.node, point.offset), offset, `offset ${offset}`);
  }
  assert.deepEqual(offsetToPoint(el, 3), { node: p1.childNodes[2], offset: 0 });
  assert.deepEqual(offsetToPoint(el, 5), { node: p2.childNodes[0], offset: 0 });
  assert.equal(pointToOffset(el, p1, 1), 2);
  assert.equal(pointToOffset(el, p1, 2), 3);
  assert.equal(pointToOffset(el, p1.childNodes[1].firstChild, 1), 3);
  assert.equal(pointToOffset(el, p2, 0), 5);
  assert.equal(pointToOffset(el, el, 1), 4);
  assert.equal(pointToOffset(el, el, 2), 8);
  assert.deepEqual(offsetToPoint(el, 99), { node: p2.childNodes[2], offset: 1 });
});

test('a trailing br is a filler; the line before it is counted', () => {
  const el = root(['p', {}, 'one', ['br'], ['br']]);
  const p = el.firstChild;
  assert.equal(scan(el).length, 4);
  assert.deepEqual(offsetToPoint(el, 4), { node: p, offset: 2 });
  assert.equal(pointToOffset(el, p, 2), 4);
  const empty = root(['p', {}, ['br']]);
  assert.equal(scan(empty).length, 0);
  assert.deepEqual(offsetToPoint(empty, 0), { node: empty.firstChild, offset: 0 });
});

test('itemBeside finds atoms next to the caret, across a block break for islands', () => {
  const island = ['div', { class: 'island', 'data-island-id': 't1', contenteditable: 'false' }, ['p', {}, 'cell']];
  const el = root(['p', {}, 'hi ', pill(), ' x'], island, ['p', {}, 'after']);
  assert.equal(itemBeside(el, 4, true).kind, 'atom');
  assert.equal(itemBeside(el, 3, false).kind, 'atom');
  assert.equal(itemBeside(el, 4, false).kind, 'text');
  assert.equal(itemBeside(el, 8, true).acrossBreak, false);
  const before = itemBeside(el, 9, true);
  assert.equal(before.kind, 'atom');
  assert.equal(before.acrossBreak, true);
  assert.equal(before.node.getAttribute('data-island-id'), 't1');
  const next = itemBeside(el, 6, false);
  assert.equal(next.kind, 'atom');
  assert.equal(next.acrossBreak, true);
});

// ---- domToDoc ----

test('a DOM the renderer produced is clean and round-trips', () => {
  const el = root(
    ['p', {}, 'a ', ['strong', {}, 'b ', ['em', {}, 'c']], ' ', ['code', {}, 'x'], ' ', ['a', { href: 'https://e.com/', rel: 'noopener noreferrer nofollow', target: '_blank' }, 'link']],
    ['ul', {}, ['li', {}, ['p', {}, 'one']], ['li', {}, ['p', {}, ['s', {}, 'two']]]],
    ['blockquote', {}, ['p', {}, 'q', ['br'], 'r']],
    ['pre', {}, ['code', {}, 'line 1\nline 2']],
  );
  assert.deepEqual(serialize(el), {
    markup: 'a **b _c_** `x` [link](https://e.com/)\n- one\n- ~~two~~\n\n> q\n> r\n\n```\nline 1\nline 2\n```',
    dirty: false,
  });
});

test('browser soup is flattened to the allow-list and flagged dirty', () => {
  const el = root(
    'loose ',
    ['b', {}, 'bold'],
    ['div', {}, ['span', { style: 'color: red' }, 'red'], ' ', ['font', {}, 'font']],
    ['div', {}, ['i', {}, 'it'], ['br']],
  );
  assert.deepEqual(serialize(el), { markup: 'loose **bold**\n\nred font\n\n_it_', dirty: true });
});

test('marks nest once, keep their spaces outside and merge with equal neighbours', () => {
  const el = root(['p', {}, 'one', ['strong', {}, ' two '], 'three ', ['strong', {}, ['strong', {}, 'a']], ['strong', {}, 'b']]);
  assert.deepEqual(serialize(el), { markup: 'one **two** three **ab**', dirty: false });
});

test('text typed as markup syntax stays literal', () => {
  const el = root(['p', {}, '**not bold** - [x](https://e.com) # h']);
  assert.equal(serialize(el).markup, '\\*\\*not bold\\*\\* - \\[x\\](https://e.com) # h');
});

test('non-breaking spaces become spaces and empty marks disappear', () => {
  const el = root(['p', {}, 'a b', ['strong', {}, ''], ['em', {}, ' ']]);
  assert.deepEqual(serialize(el), { markup: 'a b', dirty: false });
});

test('mention pills keep the token name of an unknown person', () => {
  const doc = installDom();
  const el = build(doc, ['div', {}, ['p', {}, 'hi ', pill('Shown'), ' there']]);
  const atoms = new WeakMap([[el.firstChild.childNodes[1], { t: 'mention', id: ALICE, name: 'Token' }]]);
  assert.deepEqual(serialize(el, { atoms }), { markup: `hi [[mention:${ALICE}:Token]] there`, dirty: false });
  assert.deepEqual(serialize(el), { markup: `hi [[mention:${ALICE}:Shown]] there`, dirty: true });
});

test('dialects decide which nodes survive', () => {
  const mention = () => root(['h2', {}, 'Title'], ['p', {}, pill(), ' x'], ['hr', { contenteditable: 'false' }]);
  assert.equal(serialize(mention(), { dialect: 'retro' }).markup, `Title\n\n[[mention:${ALICE}:Alice]] x`);
  assert.equal(serialize(mention(), { dialect: 'jira' }).markup, '## Title\n\nx\n\n---');
  const island = root(['div', { class: 'island', 'data-island-id': 't1', contenteditable: 'false' }, ['p', {}, 'cell']], ['p', {}, 'after']);
  assert.equal(serialize(island, { dialect: 'jira-description' }).markup, '[[island:t1]]\n\nafter');
  assert.equal(serialize(island, { dialect: 'jira' }).markup, 'after');
});

test('unsafe links lose their href and keep their text', () => {
  const el = root(['p', {}, ['a', { href: 'javascript:alert(1)' }, 'click'], ' ', ['img', { src: 'https://evil.example/x.png' }]]);
  assert.deepEqual(serialize(el), { markup: 'click', dirty: true });
});

test('a code block keeps its lines and language', () => {
  const el = root(['pre', {}, ['code', { 'data-lang': 'js' }, 'a', ['br'], 'b\n']]);
  assert.equal(serialize(el).markup, '```js\na\nb\n```');
});

// ---- links ----

test('normalizeHref encodes what the grammar refuses and requires an allowed scheme', () => {
  assert.equal(normalizeHref('en.wikipedia.org/wiki/Foo_(bar)'), 'https://en.wikipedia.org/wiki/Foo_%28bar%29');
  assert.equal(normalizeHref(' https://e.com/a b<c> '), 'https://e.com/a%20b%3Cc%3E');
  assert.equal(normalizeHref('mailto:a@b.c'), 'mailto:a@b.c');
  assert.equal(normalizeHref('javascript:alert(1)'), null);
  assert.equal(normalizeHref('data:text/html,x'), null);
  assert.equal(normalizeHref('   '), null);
  const markup = canonicalMarkup({ t: 'doc', c: [{ t: 'p', c: [{ t: 'a', href: normalizeHref('e.com/(x)'), c: [{ t: 'text', v: 'x' }] }] }] });
  assert.equal(markup, '[x](https://e.com/%28x%29)');
});
