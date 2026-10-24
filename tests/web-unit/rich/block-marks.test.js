import { test } from 'node:test';
import assert from 'node:assert/strict';
import { wrapIn } from 'prosemirror-commands';
import { wrapInList } from 'prosemirror-schema-list';
import { EditorState, TextSelection } from 'prosemirror-state';
import { adfToPm, pmToAdf } from '../../../web/src/rich/adf.js';
import { blockMarkFix, blockMarkGuard } from '../../../web/src/rich/block-marks.js';
import { schema } from '../../../web/src/rich/schema.js';

const center = { type: 'alignment', attrs: { align: 'center' } };
const indent = { type: 'indentation', attrs: { level: 2 } };

function open(content) {
  const doc = adfToPm({ type: 'doc', version: 1, content });
  return EditorState.create({ doc, plugins: [blockMarkGuard()] });
}

function run(state, command) {
  let next = state;
  assert.ok(command(state, (tr) => {
    next = state.apply(tr);
  }));
  return next;
}

test('wrapping an aligned paragraph in a list drops the alignment the list does not allow', () => {
  let state = open([{ type: 'paragraph', marks: [center], content: [{ type: 'text', text: 'x' }] }]);
  state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, 1)));
  const after = run(state, wrapInList(schema.nodes.bulletList));
  const [list] = pmToAdf(after.doc).content;
  assert.equal(list.type, 'bulletList');
  assert.equal(list.content[0].content[0].marks, undefined);
});

test('a paragraph wrapped in a panel loses both marks', () => {
  let state = open([{ type: 'paragraph', marks: [indent], content: [{ type: 'text', text: 'x' }] }]);
  state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, 1)));
  const after = run(state, wrapIn(schema.nodes.panel, { panelType: 'info' }));
  assert.equal(after.doc.firstChild.firstChild.attrs.indent, null);
});

test('a paragraph moved into a table cell keeps alignment and loses indentation', () => {
  const cell = { type: 'tableCell', content: [{ type: 'paragraph' }] };
  const state = open([
    { type: 'paragraph', marks: [center], content: [{ type: 'text', text: 'a' }] },
    { type: 'paragraph', marks: [indent], content: [{ type: 'text', text: 'b' }] },
    { type: 'table', content: [{ type: 'tableRow', content: [cell] }] },
  ]);
  const [a, b] = [state.doc.child(0), state.doc.child(1)];
  const inCell = state.doc.child(0).nodeSize + state.doc.child(1).nodeSize + 3;
  const after = state.apply(state.tr.insert(inCell, [a, b]));
  const paragraphs = pmToAdf(after.doc).content[2].content[0].content[0].content;
  assert.deepEqual(paragraphs.map((p) => p.marks ?? []), [[center], [], []]);
  assert.equal(after.doc.child(1).attrs.indent, 2, 'the paragraph in the document keeps its mark');
});

test('typing leaves allowed marks alone', () => {
  const state = open([{ type: 'paragraph', marks: [center], content: [{ type: 'text', text: 'x' }] }]);
  const after = state.apply(state.tr.insertText('y', 1));
  assert.equal(after.doc.firstChild.attrs.align, 'center');
});

test('blockMarkFix names only what the parent refuses', () => {
  const p = schema.nodes.paragraph.create({ align: 'end', indent: null });
  assert.equal(blockMarkFix('doc', p), null);
  assert.deepEqual(blockMarkFix('listItem', p), { align: null });
  assert.equal(blockMarkFix('listItem', schema.nodes.rule.create()), null);
});
