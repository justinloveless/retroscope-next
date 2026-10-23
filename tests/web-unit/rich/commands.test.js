import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { baseKeymap } from 'prosemirror-commands';
import { history, redo, undo } from 'prosemirror-history';
import { EditorState, TextSelection } from 'prosemirror-state';
import { adfToPm, canonicalAdf, pmToAdf } from '../../../web/src/rich/adf.js';
import {
  blockTypeOf,
  canOutdent,
  canRelabel,
  clearFormatting,
  enter,
  indent,
  insertRule,
  linkAt,
  listTypeOf,
  markActive,
  normalizeHref,
  outdent,
  removeLink,
  setColor,
  setHeading,
  setLink,
  stayInTable,
  toggle,
  toggleCodeBlock,
  toggleList,
  toggleQuote,
  toggleTaskList,
  toggleTaskState,
} from '../../../web/src/rich/commands.js';
import { schema } from '../../../web/src/rich/schema.js';

const doc = (...content) => ({ type: 'doc', version: 1, content });
const p = (text, marks) => ({ type: 'paragraph', content: text ? [{ type: 'text', text, ...(marks ? { marks } : {}) }] : [] });
const item = (...content) => ({ type: 'listItem', content });

function open(adf) {
  return EditorState.create({ doc: adfToPm(adf), plugins: [history()] });
}

// The document position of `text` (its start, or with `end` its end) in the first text node holding it.
function find(state, text, end = false) {
  let found = null;
  state.doc.descendants((node, pos) => {
    if (found === null && node.isText && node.text.includes(text)) {
      found = pos + node.text.indexOf(text) + (end ? text.length : 0);
    }
    return found === null;
  });
  assert.notEqual(found, null, `no text ${text}`);
  return found;
}

const select = (state, text) => state.apply(state.tr.setSelection(TextSelection.create(state.doc, find(state, text), find(state, text, true))));
const caret = (state, text, end = true) => state.apply(state.tr.setSelection(TextSelection.create(state.doc, find(state, text, end))));

function exec(state, command) {
  let next = state;
  const applied = command(state, (tr) => {
    next = state.apply(tr);
  });
  assert.ok(applied, 'the command applies');
  return next;
}

const content = (state) => pmToAdf(state.doc).content;

test('bold, italic, underline, strike and code toggle over a selection and report it', () => {
  let state = select(open(doc(p('make this bold'))), 'this');
  for (const name of ['strong', 'em', 'underline', 'strike']) {
    state = exec(state, toggle(name));
    assert.ok(markActive(state, schema.marks[name]), name);
  }
  assert.deepEqual(content(state)[0].content[1], { type: 'text', text: 'this', marks: [{ type: 'strong' }, { type: 'em' }, { type: 'strike' }, { type: 'underline' }] });
  state = exec(state, toggle('strong'));
  assert.equal(markActive(state, schema.marks.strong), false);
  // code takes only link beside it, so the other marks give way.
  state = exec(state, toggle('code'));
  assert.deepEqual(content(state)[0].content[1].marks, [{ type: 'code' }]);
});

test('text colour and highlight take a strict hex from the palette, and null removes them', () => {
  let state = select(open(doc(p('a red word'))), 'red');
  state = exec(state, setColor('textColor', '#ff5630'));
  state = exec(state, setColor('backgroundColor', '#f8e6a0'));
  assert.deepEqual(content(state)[0].content[1].marks, [{ type: 'textColor', attrs: { color: '#ff5630' } }, { type: 'backgroundColor', attrs: { color: '#f8e6a0' } }]);
  assert.equal(setColor('textColor', 'red')(state), false);
  assert.equal(setColor('textColor', 'var(--x)')(state), false);
  state = exec(state, setColor('textColor', null));
  assert.deepEqual(content(state)[0].content[1].marks, [{ type: 'backgroundColor', attrs: { color: '#f8e6a0' } }]);
  // At a caret the colour is what typing takes next.
  let typing = caret(open(doc(p('x'))), 'x');
  typing = exec(typing, setColor('textColor', '#0747a6'));
  typing = typing.apply(typing.tr.insertText('y'));
  assert.deepEqual(content(typing)[0].content[1], { type: 'text', text: 'y', marks: [{ type: 'textColor', attrs: { color: '#0747a6' } }] });
});

test('colour is refused inside a code block', () => {
  const state = select(open(doc({ type: 'codeBlock', content: [{ type: 'text', text: 'let x' }] })), 'let');
  assert.equal(setColor('textColor', '#ff5630')(state), false);
});

test('clear formatting removes text marks and keeps links', () => {
  const link = { type: 'link', attrs: { href: 'https://example.com/' } };
  let state = select(open(doc(p('styled', [{ type: 'strong' }, { type: 'textColor', attrs: { color: '#ff5630' } }, link]))), 'styled');
  state = exec(state, clearFormatting);
  assert.deepEqual(content(state)[0].content[0].marks, [link]);
});

test('headings and normal text, as the text-style menu shows them', () => {
  let state = caret(open(doc(p('Title'))), 'Title');
  assert.equal(blockTypeOf(state), 'p');
  state = exec(state, setHeading(2));
  assert.equal(blockTypeOf(state), 'h2');
  assert.deepEqual(content(state)[0], { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Title' }] });
  state = exec(state, setHeading(0));
  assert.deepEqual(content(state)[0], p('Title'));
});

test('bullet and numbered lists wrap, switch and lift', () => {
  let state = caret(open(doc(p('one'))), 'one');
  state = exec(state, toggleList('bulletList'));
  assert.equal(listTypeOf(state), 'bulletList');
  assert.deepEqual(content(state)[0], { type: 'bulletList', content: [item(p('one'))] });
  state = exec(state, toggleList('orderedList'));
  assert.equal(content(state)[0].type, 'orderedList');
  state = exec(state, toggleList('orderedList'));
  assert.deepEqual(content(state), [p('one')]);
  assert.equal(listTypeOf(state), null);
});

test('Tab nests a list item under the one above and Shift-Tab lifts it back', () => {
  let state = caret(open(doc({ type: 'bulletList', content: [item(p('first')), item(p('second'))] })), 'second');
  assert.equal(canOutdent(state), true);
  state = exec(state, indent);
  assert.deepEqual(content(state)[0], { type: 'bulletList', content: [item(p('first'), { type: 'bulletList', content: [item(p('second'))] })] });
  state = exec(state, outdent);
  assert.deepEqual(content(state)[0], { type: 'bulletList', content: [item(p('first')), item(p('second'))] });
});

test('a task list made from paragraphs gets fresh local ids, ticks, nests and turns back', () => {
  let state = open(doc(p('buy milk'), p('call home')));
  state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, find(state, 'buy'), find(state, 'home', true))));
  state = exec(state, toggleTaskList);
  const list = content(state)[0];
  assert.equal(list.type, 'taskList');
  assert.match(list.attrs.localId, /^[A-Za-z0-9_-]{1,64}$/);
  assert.deepEqual(list.content.map((t) => [t.attrs.state, t.content[0].text]), [['TODO', 'buy milk'], ['TODO', 'call home']]);
  assert.notEqual(list.content[0].attrs.localId, list.content[1].attrs.localId);
  assert.equal(listTypeOf(state), 'taskList');

  state = exec(state, toggleTaskState(1));
  assert.equal(content(state)[0].content[0].attrs.state, 'DONE');

  state = caret(state, 'call home');
  state = exec(state, indent);
  const nested = content(state)[0];
  assert.deepEqual(nested.content.map((n) => n.type), ['taskItem', 'taskList']);
  assert.equal(nested.content[1].content[0].content[0].text, 'call home');
  state = exec(state, outdent);
  assert.deepEqual(content(state)[0].content.map((n) => n.type), ['taskItem', 'taskItem']);

  state = exec(caret(state, 'buy milk'), toggleTaskList);
  assert.deepEqual(content(state)[0], p('buy milk'));
  assert.equal(content(state)[1].type, 'taskList');
});

const task = (id, text) => ({ type: 'taskItem', attrs: { localId: id, state: 'TODO' }, content: text ? [{ type: 'text', text }] : [] });
const tasks = (id, ...items) => ({ type: 'taskList', attrs: { localId: id }, content: items });

test('a task in the middle of its list becomes a paragraph between two lists', () => {
  let state = caret(open(doc({ type: 'taskList', attrs: { localId: 'l1' }, content: [task('a', 'one'), task('b', 'two'), task('c', 'three')] })), 'two');
  state = exec(state, toggleTaskList);
  const out = content(state);
  assert.deepEqual(out.map((n) => n.type), ['taskList', 'paragraph', 'taskList']);
  assert.equal(out[1].content[0].text, 'two');
  assert.notEqual(out[2].attrs.localId, out[0].attrs.localId);
});

test('Enter in a task starts a fresh one, and Enter in an empty one leaves the list', () => {
  let state = exec(caret(open(doc(tasks('l1', task('a', 'one'), task('c', 'three')))), 'one'), enter);
  let list = content(state)[0];
  assert.deepEqual(list.content.map((t) => t.content?.[0]?.text ?? ''), ['one', '', 'three']);
  assert.equal(list.content[1].attrs.state, 'TODO');
  assert.ok(!['a', 'c'].includes(list.content[1].attrs.localId));
  assert.equal(state.selection.$from.parent.type.name, 'taskItem');

  state = exec(state, enter);
  const out = content(state);
  assert.deepEqual(out.map((n) => n.type), ['taskList', 'paragraph', 'taskList']);
  assert.equal(state.selection.$from.parent.type.name, 'paragraph');
});

test('Enter in an empty nested task moves it out a level, and Tab joins a nested list above', () => {
  let state = exec(caret(open(doc(tasks('l1', task('a', 'one'), tasks('l2', task('b', 'two'))))), 'two'), enter);
  state = exec(state, enter);
  const list = content(state)[0];
  assert.deepEqual(list.content.map((n) => n.type), ['taskItem', 'taskList', 'taskItem']);
  assert.equal(state.selection.$from.depth, 2);

  state = exec(caret(open(doc(tasks('l1', task('a', 'one'), tasks('l2', task('b', 'two')), task('c', 'three')))), 'three'), indent);
  const joined = content(state)[0];
  assert.deepEqual(joined.content.map((n) => n.type), ['taskItem', 'taskList']);
  assert.deepEqual(joined.content[1].content.map((t) => [t.attrs.localId, t.content[0].text]), [['b', 'two'], ['c', 'three']]);
  assert.equal(state.selection.$from.parent.textContent, 'three');
});

test('quote, code block and divider', () => {
  let state = caret(open(doc(p('said'))), 'said');
  state = exec(state, toggleQuote);
  assert.deepEqual(content(state)[0], { type: 'blockquote', content: [p('said')] });
  state = exec(state, toggleQuote);
  assert.deepEqual(content(state), [p('said')]);

  state = exec(state, toggleCodeBlock);
  assert.deepEqual(content(state)[0], { type: 'codeBlock', content: [{ type: 'text', text: 'said' }] });
  state = exec(state, toggleCodeBlock);
  assert.deepEqual(content(state), [p('said')]);

  state = exec(state, insertRule);
  assert.deepEqual(content(state).map((n) => n.type), ['paragraph', 'rule', 'paragraph']);
  assert.equal(state.selection.$from.parent.type.name, 'paragraph');
  assert.equal(state.selection.$from.index(0), 2);
});

test('links: typed addresses are normalised, refused schemes stay out, and edits replace the text', () => {
  assert.equal(normalizeHref('example.com/a'), 'https://example.com/a');
  assert.equal(normalizeHref('someone@example.com'), 'mailto:someone@example.com');
  assert.equal(normalizeHref('https://example.com/'), 'https://example.com/');
  assert.equal(normalizeHref('javascript:alert(1)'), null);
  assert.equal(normalizeHref('data:text/html,x'), null);
  assert.equal(normalizeHref(''), null);

  let state = select(open(doc(p('read the docs today'))), 'the docs');
  assert.equal(setLink({ href: 'javascript:alert(1)' })(state), false);
  state = exec(state, setLink({ href: 'https://example.com/docs' }));
  assert.deepEqual(content(state)[0].content[1], { type: 'text', text: 'the docs', marks: [{ type: 'link', attrs: { href: 'https://example.com/docs' } }] });

  state = caret(state, 'the do');
  const link = linkAt(state);
  assert.equal(link.href, 'https://example.com/docs');
  assert.equal(link.text, 'the docs');
  state = exec(state, setLink({ href: 'https://example.com/guide', text: 'the guide', from: link.from, to: link.to }));
  assert.deepEqual(content(state)[0].content.map((n) => n.text), ['read ', 'the guide', ' today']);
  assert.equal(content(state)[0].content[1].marks[0].attrs.href, 'https://example.com/guide');

  state = exec(caret(state, 'the gu'), removeLink);
  assert.deepEqual(content(state), [p('read the guide today')]);
});

test('Backspace at the start of a panel leaves it and the panel before it apart', () => {
  const panel = (panelType, text) => ({ type: 'panel', attrs: { panelType }, content: [p(text)] });
  const loaded = doc(panel('info', 'first'), panel('note', 'second'));
  const state = caret(open(loaded), 'second', false);
  baseKeymap.Backspace(state, () => assert.fail('nothing is joined'));
  assert.deepEqual(content(state), loaded.content);
});

test('a link over text holding a token or a break keeps them when the text to display changes', () => {
  const token = { type: 'rsOpaqueInline', attrs: { ref: 'AAAAAAAAAAAAAAAA' } };
  const loaded = doc({ type: 'paragraph', content: [{ type: 'text', text: 'see ' }, token, { type: 'hardBreak' }, { type: 'text', text: ' here' }] });
  let state = open(loaded);
  const from = find(state, 'see');
  const to = find(state, 'here', true);
  assert.equal(canRelabel(state, from, to), false);
  state = exec(state, setLink({ href: 'https://example.com/', text: 'renamed', from, to }));
  const kept = content(state)[0].content;
  assert.deepEqual(kept.map((n) => n.type), ['text', 'rsOpaqueInline', 'hardBreak', 'text']);
  assert.deepEqual(kept.filter((n) => n.type === 'text').map((n) => n.marks), [[{ type: 'link', attrs: { href: 'https://example.com/' } }], [{ type: 'link', attrs: { href: 'https://example.com/' } }]]);
});

test('Tab and Shift-Tab past the edge cells of a table stay in the table', () => {
  const cell = (text) => ({ type: 'tableCell', attrs: {}, content: [p(text)] });
  const loaded = doc({ type: 'table', attrs: {}, content: [{ type: 'tableRow', content: [cell('first'), cell('last')] }] }, p('after'));
  const last = caret(open(loaded), 'last');
  assert.equal(indent(last), false);
  assert.equal(stayInTable(last), true);
  assert.equal(outdent(caret(open(loaded), 'first')), false);
  assert.equal(stayInTable(caret(open(loaded), 'first')), true);
  assert.equal(stayInTable(caret(open(loaded), 'after')), false);
});

test('undo and redo walk back to the loaded document and forward again', () => {
  const loaded = doc(p('start'));
  let state = caret(open(loaded), 'start');
  state = state.apply(state.tr.insertText(' and more'));
  state = exec(select(state, 'more'), toggle('strong'));
  state = exec(state, undo);
  state = exec(state, undo);
  assert.equal(canonicalAdf(pmToAdf(state.doc)), canonicalAdf(loaded));
  state = exec(state, redo);
  state = exec(state, redo);
  assert.deepEqual(content(state)[0].content.at(-1), { type: 'text', text: 'more', marks: [{ type: 'strong' }] });
});

// The drawer skips the PATCH when the document is canonical-equal to the edit view it loaded.
test('every edit view the web fake serves opens canonical-equal to itself, and an edit makes it differ', () => {
  const golden = JSON.parse(readFileSync(new URL('../../web-fake-server/edit-views.gen.json', import.meta.url), 'utf8'));
  for (const [key, view] of Object.entries(golden.issues)) {
    const state = open(view.adf);
    assert.equal(canonicalAdf(pmToAdf(state.doc)), canonicalAdf(view.adf), key);
    const edited = state.apply(state.tr.insertText('x', 1));
    assert.notEqual(canonicalAdf(pmToAdf(edited.doc)), canonicalAdf(view.adf), key);
  }
});
