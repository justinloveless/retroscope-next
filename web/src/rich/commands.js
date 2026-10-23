// The editor's commands (UX12): marks, colours, block types, lists and links over ProseMirror state, so the toolbar,
// the keymap and the tests share one implementation. Each is a ProseMirror command, (state, dispatch?) => boolean,
// where a call without dispatch only asks whether the command applies.

import { chainCommands, lift, setBlockType, toggleMark, wrapIn } from 'prosemirror-commands';
import { liftListItem, sinkListItem, splitListItem, wrapInList } from 'prosemirror-schema-list';
import { TextSelection } from 'prosemirror-state';
import { goToNextCell } from 'prosemirror-tables';
import { liftTarget } from 'prosemirror-transform';
import { contextMarks, isAdfColor, isHref } from './rules.js';
import { schema } from './schema.js';

const N = schema.nodes;
const M = schema.marks;

/** A fresh localId for the task and decision nodes the editor creates (ADF requires one). */
export const newLocalId = () => crypto.randomUUID();

const LISTS = new Set([N.bulletList, N.orderedList]);
// Text marks the "clear formatting" command removes; links and the server's tokens stay.
const FORMATTING = ['strong', 'em', 'underline', 'strike', 'code', 'subsup', 'textColor', 'backgroundColor'];

/** Whether `type` covers the selection (or, for a caret, the marks typing would take). */
export function markActive(state, type) {
  const { from, $from, to, empty } = state.selection;
  if (empty) {
    return Boolean(type.isInSet(state.storedMarks ?? $from.marks()));
  }
  return state.doc.rangeHasMark(from, to, type);
}

/** The first mark of `type` in the selection, or null. */
export function markOf(state, type) {
  const { from, $from, to, empty } = state.selection;
  if (empty) {
    return type.isInSet(state.storedMarks ?? $from.marks()) ?? null;
  }
  let found = null;
  state.doc.nodesBetween(from, to, (node) => {
    found ??= type.isInSet(node.marks) ?? null;
    return found === null;
  });
  return found;
}

/** Bold, italic, underline, strike and inline code: toggled like Jira, refused where the schema forbids the mark. */
export const toggle = (name) => toggleMark(M[name]);

/** Text colour or highlight: a strict #rrggbb, or null to remove it. */
export function setColor(name, hex) {
  const type = M[name];
  return (state, dispatch) => {
    if (hex !== null && !isAdfColor(hex)) {
      return false;
    }
    const { from, to, empty, $from } = state.selection;
    if (!$from.parent.inlineContent || $from.parent.type.spec.code) {
      return false;
    }
    if (dispatch) {
      const tr = state.tr;
      if (empty) {
        tr.removeStoredMark(type);
        if (hex) {
          tr.addStoredMark(type.create({ color: hex }));
        }
      } else {
        tr.removeMark(from, to, type);
        if (hex) {
          tr.addMark(from, to, type.create({ color: hex }));
        }
      }
      dispatch(tr.scrollIntoView());
    }
    return true;
  };
}

export function clearFormatting(state, dispatch) {
  const { from, to, empty } = state.selection;
  if (dispatch) {
    const tr = state.tr;
    if (!empty) {
      for (const name of FORMATTING) {
        tr.removeMark(from, to, M[name]);
      }
    }
    tr.setStoredMarks([]);
    dispatch(tr);
  }
  return true;
}

/** "Normal text" (level 0) or a heading level, for the textblock(s) in the selection. */
export const setHeading = (level) => (level === 0 ? setBlockType(N.paragraph) : setBlockType(N.heading, { level }));

/** What the block-type menu shows for the selection: 'p', 'h1'…'h6' or null (code, task text and the like). */
export function blockTypeOf(state) {
  const parent = state.selection.$from.parent;
  if (parent.type === N.heading) {
    return `h${parent.attrs.level}`;
  }
  return parent.type === N.paragraph ? 'p' : null;
}

function ancestor(state, test) {
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth--) {
    if (test($from.node(depth))) {
      return { node: $from.node(depth), depth, pos: $from.before(depth) };
    }
  }
  return null;
}

/** The innermost list the selection is in: 'bulletList', 'orderedList', 'taskList' or null. */
export function listTypeOf(state) {
  return ancestor(state, (node) => LISTS.has(node.type) || node.type === N.taskList)?.node.type.name ?? null;
}

/** Bullet and numbered lists: leave the list, switch its type, or wrap the selection in one. */
export function toggleList(name) {
  const type = N[name];
  return (state, dispatch) => {
    const inTask = ancestor(state, (node) => node.type === N.taskItem);
    if (inTask) {
      return false;
    }
    const list = ancestor(state, (node) => LISTS.has(node.type));
    if (list) {
      if (list.node.type === type) {
        return liftListItem(N.listItem)(state, dispatch);
      }
      if (dispatch) {
        dispatch(state.tr.setNodeMarkup(list.pos, type, type === N.orderedList ? { order: null } : {}));
      }
      return true;
    }
    return wrapInList(type)(state, dispatch);
  };
}

/** Task list: turn the paragraphs of the selection into tasks, or tasks back into paragraphs. */
export function toggleTaskList(state, dispatch) {
  if (state.selection.$from.parent.type === N.taskItem) {
    return taskToParagraphs(state, dispatch);
  }
  const { $from, $to } = state.selection;
  const range = $from.blockRange($to);
  if (!range) {
    return false;
  }
  const blocks = [];
  for (let i = range.startIndex; i < range.endIndex; i++) {
    blocks.push(range.parent.child(i));
  }
  if (blocks.length === 0 || !blocks.every((b) => b.type === N.paragraph || b.type === N.heading)) {
    return false;
  }
  const items = blocks.map((b) => N.taskItem.create({ localId: newLocalId(), state: 'TODO' }, b.content));
  const list = N.taskList.create({ localId: newLocalId() }, items);
  if (!range.parent.canReplaceWith(range.startIndex, range.endIndex, N.taskList)) {
    return false;
  }
  if (dispatch) {
    const tr = state.tr.replaceWith(range.start, range.end, list);
    // The caret stays at the same offset in the same block, now a task: block i starts 1 + i * 2 later than it did.
    const offset = $from.parentOffset;
    const index = $from.index(range.depth) - range.startIndex;
    let pos = range.start + 1;
    for (let i = 0; i < index; i++) {
      pos += items[i].nodeSize;
    }
    tr.setSelection(TextSelection.create(tr.doc, pos + 1 + offset));
    dispatch(tr.scrollIntoView());
  }
  return true;
}

// The task under the caret becomes a paragraph after (or before, or between the halves of) its list.
function taskToParagraphs(state, dispatch) {
  const { $from } = state.selection;
  const item = $from.parent;
  const listDepth = $from.depth - 1;
  const list = $from.node(listDepth);
  const index = $from.index(listDepth);
  const para = N.paragraph.create(null, item.content);
  const listStart = $from.before(listDepth);
  const listEnd = $from.after(listDepth);
  const $list = state.doc.resolve(listStart);
  if (!$list.parent.canReplaceWith($list.index(), $list.index() + 1, N.paragraph)) {
    return false;
  }
  if (dispatch) {
    const tr = state.tr;
    const offset = $from.parentOffset;
    const itemStart = $from.before();
    const itemEnd = $from.after();
    let at;
    if (list.childCount === 1) {
      tr.replaceWith(listStart, listEnd, para);
      at = listStart;
    } else if (index === list.childCount - 1) {
      tr.delete(itemStart, itemEnd);
      at = tr.mapping.map(listEnd);
      tr.insert(at, para);
    } else if (index === 0) {
      tr.delete(itemStart, itemEnd);
      at = listStart;
      tr.insert(at, para);
    } else {
      // Split the list around the item, then put the paragraph between the halves.
      tr.split(itemStart, 1, [{ type: N.taskList, attrs: { localId: newLocalId() } }]);
      const start = itemStart + 2;
      tr.delete(start, start + item.nodeSize);
      at = itemStart + 1;
      tr.insert(at, para);
    }
    tr.setSelection(TextSelection.create(tr.doc, at + 1 + offset));
    dispatch(tr.scrollIntoView());
  }
  return true;
}

function taskContext(state) {
  const { $from } = state.selection;
  if ($from.parent.type !== N.taskItem) {
    return null;
  }
  return { $from, item: $from.parent, list: $from.node(-1), index: $from.index(-1) };
}

/** Tab in a task: nest it under the task above (ADF nests a taskList inside the taskList, after that task). */
export function sinkTask(state, dispatch) {
  const ctx = taskContext(state);
  if (!ctx || ctx.index === 0) {
    return false;
  }
  const { $from, item, list, index } = ctx;
  if (dispatch) {
    const tr = state.tr;
    const itemStart = $from.before();
    const itemEnd = $from.after();
    const offset = $from.pos - itemStart;
    const before = list.child(index - 1);
    if (before.type === N.taskList) {
      tr.delete(itemStart, itemEnd);
      const at = itemStart - 1;
      tr.insert(at, item);
      tr.setSelection(TextSelection.create(tr.doc, at + offset));
    } else {
      tr.replaceWith(itemStart, itemEnd, N.taskList.create({ localId: newLocalId() }, item));
      tr.setSelection(TextSelection.create(tr.doc, itemStart + 1 + offset));
    }
    dispatch(tr.scrollIntoView());
  }
  return true;
}

/** Shift-Tab in a nested task: move it out one level. A task at the top level stays a task. */
export function liftTask(state, dispatch) {
  const ctx = taskContext(state);
  if (!ctx) {
    return false;
  }
  const { $from } = ctx;
  if ($from.depth < 3 || $from.node(-2).type !== N.taskList) {
    return true;
  }
  const range = $from.blockRange($from, (node) => node.type === N.taskList);
  const target = range && liftTarget(range);
  if (target == null) {
    return false;
  }
  if (dispatch) {
    dispatch(state.tr.lift(range, target).scrollIntoView());
  }
  return true;
}

/** Enter in a task: a new task after it, or on an empty task, leave the list (outdenting a nested one first). */
export function splitTask(state, dispatch) {
  const ctx = taskContext(state);
  if (!ctx) {
    return false;
  }
  if (ctx.item.content.size === 0) {
    const nested = ctx.$from.depth >= 3 && ctx.$from.node(-2).type === N.taskList;
    return nested ? liftTask(state, dispatch) : taskToParagraphs(state, dispatch);
  }
  if (dispatch) {
    const tr = state.tr.deleteSelection();
    tr.split(tr.selection.from, 1, [{ type: N.taskItem, attrs: { localId: newLocalId(), state: 'TODO' } }]);
    dispatch(tr.scrollIntoView());
  }
  return true;
}

/** Tick or untick the task at `pos`. */
export function toggleTaskState(pos) {
  return (state, dispatch) => {
    const node = state.doc.nodeAt(pos);
    if (node?.type !== N.taskItem) {
      return false;
    }
    if (dispatch) {
      dispatch(state.tr.setNodeAttribute(pos, 'state', node.attrs.state === 'DONE' ? 'TODO' : 'DONE'));
    }
    return true;
  };
}

// A paragraph or heading's ADF indentation (1–6), where its parent allows the mark.
function indentBlock(delta) {
  return (state, dispatch) => {
    const { $from } = state.selection;
    const block = $from.parent;
    if (block.type !== N.paragraph && block.type !== N.heading) {
      return false;
    }
    const parent = $from.depth > 0 ? $from.node(-1).type.name : null;
    if (!contextMarks(parent, block.type.name).includes('indentation')) {
      return false;
    }
    const level = Math.max(0, Math.min(6, (block.attrs.indent ?? 0) + delta));
    if (level === (block.attrs.indent ?? 0)) {
      return false;
    }
    if (dispatch) {
      dispatch(state.tr.setNodeMarkup($from.before(), null, { ...block.attrs, indent: level || null }));
    }
    return true;
  };
}

function codeTab(state, dispatch) {
  if (!state.selection.$from.parent.type.spec.code) {
    return false;
  }
  if (dispatch) {
    dispatch(state.tr.insertText('  ').scrollIntoView());
  }
  return true;
}

/** Indent: the next cell, a nested list item or task, two spaces in code, or a paragraph's indentation. */
export const indent = chainCommands(goToNextCell(1), sinkTask, sinkListItem(N.listItem), codeTab, indentBlock(1));

/** Outdent: the previous cell, a list item or task one level up, or less indentation. */
export const outdent = chainCommands(goToNextCell(-1), liftTask, liftListItem(N.listItem), indentBlock(-1));

/** Whether the outdent button applies: inside a list item, a nested task or an indented paragraph. */
export function canOutdent(state) {
  const task = taskContext(state);
  if (task) {
    return task.$from.depth >= 3 && task.$from.node(-2).type === N.taskList;
  }
  return liftListItem(N.listItem)(state) || indentBlock(-1)(state);
}

export const enter = chainCommands(splitTask, splitListItem(N.listItem));

/** Quote: wrap the selection in a blockquote, or lift it out of one. */
export function toggleQuote(state, dispatch) {
  if (ancestor(state, (node) => node.type === N.blockquote)) {
    return lift(state, dispatch);
  }
  return wrapIn(N.blockquote)(state, dispatch);
}

/** Code block: turn the textblock into one (its text kept, its marks dropped), or back into a paragraph. */
export function toggleCodeBlock(state, dispatch) {
  if (state.selection.$from.parent.type === N.codeBlock) {
    return setBlockType(N.paragraph)(state, dispatch);
  }
  return setBlockType(N.codeBlock)(state, dispatch);
}

/** A divider after the current block (the nearest one whose parent takes a rule), with the caret after it. */
export function insertRule(state, dispatch) {
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth--) {
    const after = $from.after(depth);
    const $after = state.doc.resolve(after);
    if (!$after.parent.canReplaceWith($after.index(), $after.index(), N.rule)) {
      continue;
    }
    if (dispatch) {
      const tr = state.tr.insert(after, N.rule.create());
      const $next = tr.doc.resolve(after + 1);
      if (!$next.nodeAfter?.isTextblock && $next.parent.canReplaceWith($next.index(), $next.index(), N.paragraph)) {
        tr.insert(after + 1, N.paragraph.create());
      }
      tr.setSelection(TextSelection.near(tr.doc.resolve(after + 2)));
      dispatch(tr.scrollIntoView());
    }
    return true;
  }
  return false;
}

/** The link under the caret or selection: {href, from, to, text}, or null. */
export function linkAt(state) {
  const { $from, from, to, empty } = state.selection;
  const mark = markOf(state, M.link);
  if (!mark) {
    return null;
  }
  if (!empty) {
    return { href: mark.attrs.href, from, to, text: state.doc.textBetween(from, to, ' ') };
  }
  // A caret: the run of text around it that carries this same link.
  const children = [];
  $from.parent.forEach((child, offset) => children.push({ child, offset }));
  const at = $from.parentOffset;
  const has = (i) => Boolean(mark.isInSet(children[i].child.marks));
  const index = children.findIndex(({ child, offset }, i) => has(i) && offset <= at && at <= offset + child.nodeSize);
  if (index < 0) {
    return null;
  }
  let low = index;
  let high = index;
  while (low > 0 && has(low - 1)) {
    low--;
  }
  while (high < children.length - 1 && has(high + 1)) {
    high++;
  }
  const start = $from.start() + children[low].offset;
  const end = $from.start() + children[high].offset + children[high].child.nodeSize;
  return { href: mark.attrs.href, from: start, to: end, text: state.doc.textBetween(start, end, ' ') };
}

/** Normalises what someone typed into a link: a bare host gets https://. Null when it cannot be a link. */
export function normalizeHref(input) {
  const text = String(input ?? '').trim();
  if (text === '') {
    return null;
  }
  const withScheme = /^[a-z][a-z0-9+.-]*:/i.test(text) ? text : /^[^\s@/]+@[^\s@/]+\.[^\s@/]+$/.test(text) ? `mailto:${text}` : `https://${text}`;
  return isHref(withScheme) ? withScheme : null;
}

/** Whether [from, to) holds only text, so its text can be replaced without losing a chip, token or line break. */
export function canRelabel(state, from, to) {
  let plain = true;
  state.doc.nodesBetween(from, to, (node) => {
    plain &&= !(node.isInline && !node.isText);
    return plain;
  });
  return plain;
}

/**
 * Sets the link over [from, to) (the selection when omitted), replacing its text when `text` differs, or inserts
 * `text` linked at a caret. A range holding more than text keeps its content and only takes the link. Refused when
 * the href is not one the save would accept.
 */
export function setLink({ href, text, from, to }) {
  return (state, dispatch) => {
    if (!isHref(href)) {
      return false;
    }
    const start = from ?? state.selection.from;
    const end = to ?? state.selection.to;
    const $start = state.doc.resolve(start);
    if (!$start.parent.inlineContent || $start.parent.type.spec.code) {
      return false;
    }
    const mark = M.link.create({ href });
    const current = state.doc.textBetween(start, end, ' ');
    const label = text && text.trim() !== '' ? text : current || href;
    if (dispatch) {
      const tr = state.tr;
      let after = end;
      if (label !== current && canRelabel(state, start, end)) {
        const marks = $start.marks().filter((m) => m.type !== M.link);
        tr.replaceWith(start, end, schema.text(label, mark.addToSet(marks)));
        after = start + label.length;
      } else {
        tr.removeMark(start, end, M.link).addMark(start, end, mark);
      }
      tr.setSelection(TextSelection.create(tr.doc, after)).removeStoredMark(M.link);
      dispatch(tr.scrollIntoView());
    }
    return true;
  };
}

/** Removes the link under the caret or selection. */
export function removeLink(state, dispatch) {
  const link = linkAt(state);
  if (!link) {
    return false;
  }
  if (dispatch) {
    dispatch(state.tr.removeMark(link.from, link.to, M.link).scrollIntoView());
  }
  return true;
}
