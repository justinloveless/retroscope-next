// AdfSchema allows alignment and indentation on a paragraph or heading only in some parents (contextMarks in
// rules.js): the document, a layout column and, partly, a table cell. A paste, a drag or a wrap into a list can
// move such a block where the save would refuse its marks, so the guard drops what the new parent does not allow.

import { Plugin } from 'prosemirror-state';
import { contextMarks } from './rules.js';

const ATTRS = [['alignment', 'align'], ['indentation', 'indent']];

/** The attribute changes that keep `node`'s block marks within what a `parent` of that type allows, or null. */
export function blockMarkFix(parent, node) {
  const allowed = contextMarks(parent, node.type.name);
  let fix = null;
  for (const [mark, attr] of ATTRS) {
    if (node.attrs[attr] != null && !allowed.includes(mark)) {
      fix ??= {};
      fix[attr] = null;
    }
  }
  return fix;
}

export function blockMarkGuard() {
  return new Plugin({
    appendTransaction(trs, _old, state) {
      if (!trs.some((tr) => tr.docChanged)) {
        return null;
      }
      let tr = null;
      // Blocks only: the walk never enters a paragraph's text, so it costs the number of blocks.
      state.doc.descendants((node, pos, parent) => {
        const fix = node.isText ? null : blockMarkFix(parent.type.name, node);
        if (fix) {
          tr ??= state.tr;
          tr.setNodeMarkup(pos, undefined, { ...node.attrs, ...fix }, node.marks);
        }
        return !node.isTextblock;
      });
      return tr;
    },
  });
}
