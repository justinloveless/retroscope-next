// Jira's markdown autoformat (UX12): what is typed at the start of a paragraph or around a word becomes the block or
// mark it stands for, as the space or closing character is typed. Backspace right after one undoes it.

import { InputRule, inputRules, textblockTypeInputRule, wrappingInputRule } from 'prosemirror-inputrules';
import { TextSelection } from 'prosemirror-state';
import { newLocalId } from './commands.js';
import { schema } from './schema.js';

const N = schema.nodes;
const M = schema.marks;

// "[] " at the start of a paragraph: the paragraph becomes an action item.
const taskRule = new InputRule(/^\[ ?\]\s$/, (state, match, start, end) => {
  const $start = state.doc.resolve(start);
  const block = $start.parent;
  const before = $start.before();
  const $before = state.doc.resolve(before);
  if (block.type !== N.paragraph || !$before.parent.canReplaceWith($before.index(), $before.index() + 1, N.taskList)) {
    return null;
  }
  const item = N.taskItem.create({ localId: newLocalId(), state: 'TODO' }, block.content.cut(end - $start.start()));
  const tr = state.tr.replaceWith(before, $start.after(), N.taskList.create({ localId: newLocalId() }, item));
  return tr.setSelection(TextSelection.create(tr.doc, before + 2));
});

// "---" as the whole of a paragraph: a divider, with the caret in a paragraph after it.
const ruleRule = new InputRule(/^---$/, (state, match, start, end) => {
  const $start = state.doc.resolve(start);
  const before = $start.before();
  const $before = state.doc.resolve(before);
  const fill = [N.rule.create(), N.paragraph.create()];
  if ($start.parent.type !== N.paragraph || $start.parent.content.size !== end - start
    || !$before.parent.canReplace($before.index(), $before.index() + 1, schema.node('doc', null, fill).content)) {
    return null;
  }
  const tr = state.tr.replaceWith(before, $start.after(), fill);
  return tr.setSelection(TextSelection.create(tr.doc, before + 2));
});

// A word wrapped in `delimiter` after a space or at the line's start takes `type` once the closing one is typed.
// U+FFFC stands for an inline node in the matched text, which a mark rule never spans.
function markRule(delimiter, type) {
  const d = delimiter.replace(/[*~`]/g, '\\$&');
  const c = delimiter[0] === '`' ? '`' : `\\${delimiter[0]}`;
  const pattern = new RegExp(`(^|\\s)${d}([^\\s${c}\\ufffc](?:[^${c}\\ufffc]*[^\\s${c}\\ufffc])?)${d}$`);
  return new InputRule(pattern, (state, match, start, end) => {
    const [, lead, text] = match;
    const open = start + lead.length;
    const textStart = open + delimiter.length;
    const textEnd = textStart + text.length;
    if (!state.doc.resolve(open).parent.type.allowsMarkType(type)) {
      return null;
    }
    const tr = state.tr.delete(textEnd, end).delete(open, textStart);
    return tr.addMark(open, open + text.length, type.create()).removeStoredMark(type);
  });
}

export const rules = [
  textblockTypeInputRule(/^(#{1,6})\s$/, N.heading, (m) => ({ level: m[1].length })),
  wrappingInputRule(/^\s*>\s$/, N.blockquote),
  wrappingInputRule(/^\s*([-*])\s$/, N.bulletList),
  wrappingInputRule(/^(\d+)[.)]\s$/, N.orderedList, (m) => ({ order: Number(m[1]) === 1 ? null : Number(m[1]) })),
  textblockTypeInputRule(/^```$/, N.codeBlock),
  taskRule,
  ruleRule,
  markRule('**', M.strong),
  markRule('__', M.strong),
  markRule('*', M.em),
  markRule('_', M.em),
  markRule('~~', M.strike),
  markRule('`', M.code),
];

export const autoformat = () => inputRules({ rules });
