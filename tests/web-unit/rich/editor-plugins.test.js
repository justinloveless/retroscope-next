import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EditorState } from 'prosemirror-state';
import { adfToPm } from '../../../web/src/rich/adf.js';
import { editorPlugins, selectionFlush } from '../../../web/src/rich/rich-editor.js';

const doc = adfToPm({ type: 'doc', version: 1, content: [{ type: 'paragraph', content: [{ type: 'text', text: 'x' }] }] });
const plugins = () => editorPlugins(doc, { openLink() {}, update() {} });

// The stale-selection race (UX12 decision 10): a key that arrives before the browser's selectionchange must act on
// the DOM's selection, so the flush has to see every keydown before any keymap does.
test('a keydown flushes the DOM selection before any keymap handles the key', () => {
  const list = plugins();
  const flushAt = list.indexOf(selectionFlush);
  assert.equal(flushAt, 0);
  assert.ok(list.slice(1).some((p) => p.props.handleKeyDown), 'the keymaps come after it');

  const calls = [];
  const view = { domObserver: { flush: () => calls.push('flush') } };
  assert.equal(selectionFlush.props.handleDOMEvents.keydown(view, new Event('keydown')), false);
  assert.deepEqual(calls, ['flush']);
});

test('the plugins make a valid editor state for the document', () => {
  const state = EditorState.create({ doc, plugins: plugins() });
  assert.equal(state.doc.textContent, 'x');
});
