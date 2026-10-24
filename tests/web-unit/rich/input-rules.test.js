import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EditorState, TextSelection } from 'prosemirror-state';
import { adfToPm, pmToAdf } from '../../../web/src/rich/adf.js';
import { rules } from '../../../web/src/rich/input-rules.js';

const doc = (...content) => ({ type: 'doc', version: 1, content });
const p = (text) => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : [] });

function open(adf) {
  const state = EditorState.create({ doc: adfToPm(adf) });
  return state.apply(state.tr.setSelection(TextSelection.atEnd(state.doc)));
}

// Types one character at a time as prosemirror-inputrules' handleTextInput does: the text before the caret plus the
// typed character is matched against each rule, and the first rule that answers replaces the insertion.
function type(state, text) {
  let next = state;
  for (const ch of text) {
    const { $from, from, to } = next.selection;
    const before = $from.parent.textBetween(Math.max(0, $from.parentOffset - 500), $from.parentOffset, null, '\ufffc') + ch;
    let tr = null;
    for (const rule of $from.parent.type.spec.code ? [] : rules) {
      const match = rule.match.exec(before);
      tr = match ? rule.handler(next, match, from - (match[0].length - ch.length), to) : null;
      if (tr) {
        break;
      }
    }
    next = next.apply(tr ?? next.tr.insertText(ch));
  }
  return next;
}

const content = (state) => pmToAdf(state.doc).content;

test('"[] " starts an action item and keeps typing in it', () => {
  const state = type(open(doc(p(''))), '[] buy milk');
  const [list] = content(state);
  assert.equal(list.type, 'taskList');
  assert.equal(list.content[0].type, 'taskItem');
  assert.equal(list.content[0].attrs.state, 'TODO');
  assert.match(list.content[0].attrs.localId, /^[0-9a-f-]{36}$/);
  assert.deepEqual(list.content[0].content, [{ type: 'text', text: 'buy milk' }]);
});

test('"---" on its own line becomes a divider with the caret after it', () => {
  const state = type(open(doc(p(''))), '---next');
  assert.deepEqual(content(state).map((n) => n.type), ['rule', 'paragraph']);
  assert.deepEqual(content(state)[1], p('next'));
  assert.deepEqual(content(type(open(doc(p('a'))), '---')), [p('a---')]);
});

test('**bold**, _italic_, ~~strike~~ and `code` take their marks as the closing character is typed', () => {
  const state = type(open(doc(p(''))), 'a **b** _i_ ~~s~~ `c` d');
  assert.deepEqual(content(state)[0].content, [
    { type: 'text', text: 'a ' },
    { type: 'text', text: 'b', marks: [{ type: 'strong' }] },
    { type: 'text', text: ' ' },
    { type: 'text', text: 'i', marks: [{ type: 'em' }] },
    { type: 'text', text: ' ' },
    { type: 'text', text: 's', marks: [{ type: 'strike' }] },
    { type: 'text', text: ' ' },
    { type: 'text', text: 'c', marks: [{ type: 'code' }] },
    { type: 'text', text: ' d' },
  ]);
});

test('delimiters inside words, around spaces or in a code block stay as typed', () => {
  assert.deepEqual(content(type(open(doc(p(''))), 'snake_case_name')), [p('snake_case_name')]);
  assert.deepEqual(content(type(open(doc(p(''))), 'a ** b **')), [p('a ** b **')]);
  const code = type(open(doc({ type: 'codeBlock', content: [] })), '**x**');
  assert.deepEqual(content(code), [{ type: 'codeBlock', content: [{ type: 'text', text: '**x**' }] }]);
});
