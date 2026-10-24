import { test } from 'node:test';
import assert from 'node:assert/strict';
import { splitBlock } from 'prosemirror-commands';
import { EditorState, TextSelection } from 'prosemirror-state';
import { adfToPm, pmToAdf } from '../../../web/src/rich/adf.js';
import { tokenCounts, tokenGuard } from '../../../web/src/rich/tokens.js';
import { tokenRefs } from './_project.js';

const R1 = 'AAAAAAAAAAAAAAAA';
const R2 = 'BBBBBBBBBBBBBBBB';
const R3 = 'CCCCCCCCCCCCCCCC';

function open(content) {
  const doc = adfToPm({ type: 'doc', version: 1, content });
  return EditorState.create({ doc, plugins: [tokenGuard(doc)] });
}

const BASE = [
  { type: 'rsOpaque', attrs: { ref: R1 } },
  { type: 'paragraph', attrs: { rsKeep: R2 }, marks: [{ type: 'rsMark', attrs: { ref: R3 } }], content: [
    { type: 'text', text: 'hello world' },
    { type: 'rsOpaqueInline', attrs: { ref: R3 } },
  ] },
];

test('inserting a copy of an opaque block is refused', () => {
  const state = open(BASE);
  const copy = state.doc.firstChild.copy();
  const after = state.apply(state.tr.insert(state.doc.content.size, copy));
  assert.equal(after.doc, state.doc);
  assert.deepEqual(tokenCounts(after.doc), new Map([[R1, 1], [R3, 1]]));
});

test('inserting a copy of an opaque inline atom is refused', () => {
  const state = open(BASE);
  const inline = state.doc.child(1).lastChild;
  const after = state.apply(state.tr.insert(2, inline.copy()));
  assert.equal(after.doc, state.doc);
});

test('moving an opaque block is allowed: its count does not change', () => {
  const state = open(BASE);
  const block = state.doc.firstChild;
  const tr = state.tr.delete(0, block.nodeSize);
  tr.insert(tr.doc.content.size, block);
  const after = state.apply(tr);
  assert.notEqual(after.doc, state.doc);
  assert.equal(after.doc.lastChild.type.name, 'rsOpaque');
  assert.deepEqual(tokenCounts(after.doc), tokenCounts(state.doc));
});

test('deleting a token and undoing by re-inserting it is allowed', () => {
  const state = open(BASE);
  const block = state.doc.firstChild;
  const deleted = state.apply(state.tr.delete(0, block.nodeSize));
  const restored = deleted.apply(deleted.tr.insert(0, block));
  assert.equal(restored.doc.firstChild.type.name, 'rsOpaque');
});

test('splitting a paragraph keeps its stash and node marks on the first half only', () => {
  const state = open(BASE);
  const start = state.doc.firstChild.nodeSize + 1;
  let next = state.apply(state.tr.setSelection(TextSelection.create(state.doc, start + 5)));
  splitBlock(next, (tr) => {
    next = next.apply(tr);
  });
  const [, first, second] = next.doc.content.content;
  assert.equal(first.textContent, 'hello');
  assert.equal(second.textContent, ' world');
  assert.equal(first.attrs.rsKeep, R2);
  assert.equal(second.attrs.rsKeep, null);
  assert.deepEqual(first.attrs.blockMarks, [{ type: 'rsMark', attrs: { ref: R3 } }]);
  assert.equal(second.attrs.blockMarks, null);
  const refs = tokenRefs(pmToAdf(next.doc));
  assert.deepEqual([...refs.entries()].sort(), [[R1, 1], [R3, 2]], 'the paragraph mark and the inline token, once each');
});

test('typing beside tokens is unaffected by the guard', () => {
  const state = open(BASE);
  const start = state.doc.firstChild.nodeSize + 1;
  const after = state.apply(state.tr.insertText('x', start));
  assert.equal(after.doc.child(1).textContent, 'xhello world');
});

test('a copy of a stashed block inserted before the original leaves the stash on the original', () => {
  const state = open([
    { type: 'paragraph', attrs: { rsKeep: R2 }, marks: [{ type: 'rsMark', attrs: { ref: R3 } }], content: [{ type: 'text', text: 'orig' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'other' }] },
  ]);
  const original = state.doc.firstChild;
  const after = state.apply(state.tr.insert(0, original.copy(original.content)));
  const [copy, kept, other] = after.doc.content.content;
  assert.equal(copy.textContent, 'orig');
  assert.equal(copy.attrs.rsKeep, null);
  assert.equal(copy.attrs.blockMarks, null);
  assert.equal(kept.attrs.rsKeep, R2, 'the original keeps its stash');
  assert.deepEqual(kept.attrs.blockMarks, [{ type: 'rsMark', attrs: { ref: R3 } }]);
  assert.equal(other.attrs.rsKeep, null);
});

test('a copy inserted after the original leaves the stash on the original too', () => {
  const state = open([{ type: 'paragraph', attrs: { rsKeep: R2 }, content: [{ type: 'text', text: 'orig' }] }]);
  const original = state.doc.firstChild;
  const after = state.apply(state.tr.insert(state.doc.content.size, original.copy(original.content)));
  assert.deepEqual(after.doc.content.content.map((n) => n.attrs.rsKeep), [R2, null]);
});

test('a moved stashed block keeps its stash', () => {
  const state = open([
    { type: 'paragraph', content: [{ type: 'text', text: 'first' }] },
    { type: 'paragraph', attrs: { rsKeep: R2 }, content: [{ type: 'text', text: 'moved' }] },
  ]);
  const moved = state.doc.child(1);
  const tr = state.tr.delete(state.doc.child(0).nodeSize, state.doc.content.size);
  tr.insert(0, moved);
  const after = state.apply(tr);
  assert.deepEqual(after.doc.content.content.map((n) => [n.textContent, n.attrs.rsKeep]), [['moved', R2], ['first', null]]);
});
