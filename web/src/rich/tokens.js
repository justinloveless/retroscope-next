// The server's tokens stand for content the editor cannot read, and the save restores each from the issue as Jira
// holds it. A second copy of a token would be a second copy of that content, which the server refuses
// (opaque_duplicated). So no transaction may raise a token's count above what the document opened with, and an
// attribute stash or node mark that a split copied stays on the first node only.

import { Plugin } from 'prosemirror-state';
import { STASH, TOKEN_MARK, TOKEN_NODES } from './schema.js';

/** ref -> how many token nodes in `doc` carry it. */
export function tokenCounts(doc) {
  const counts = new Map();
  doc.descendants((node) => {
    if (TOKEN_NODES.has(node.type.name)) {
      counts.set(node.attrs.ref, (counts.get(node.attrs.ref) ?? 0) + 1);
    }
  });
  return counts;
}

function carriesTokens(node) {
  return TOKEN_NODES.has(node.type.name)
    || (!node.isText && (node.attrs[STASH] != null || node.attrs.blockMarks?.some((m) => m.type === TOKEN_MARK)));
}

// Steps that cannot add a token (typing, marks, deletions) are the common case; only a slice that carries one
// needs the whole document counted.
function mayAddTokens(tr) {
  return tr.steps.some((step) => {
    let found = false;
    step.slice?.content.descendants((node) => {
      found ||= carriesTokens(node);
      return !found;
    });
    return found;
  });
}

function withinBase(doc, base) {
  for (const [ref, count] of tokenCounts(doc)) {
    if (count > (base.get(ref) ?? 0)) {
      return false;
    }
  }
  return true;
}

// Keeps the first node that carries a given stash or node-mark ref and clears it from the copies.
function dropCopies(state) {
  const seen = new Set();
  let tr = null;
  state.doc.descendants((node, pos) => {
    if (node.isText) {
      return true;
    }
    const attrs = {};
    const stash = node.attrs[STASH];
    if (stash != null) {
      if (seen.has(`k:${stash}`)) {
        attrs[STASH] = null;
      }
      seen.add(`k:${stash}`);
    }
    const marks = node.attrs.blockMarks;
    if (marks?.some((m) => m.type === TOKEN_MARK)) {
      const kept = marks.filter((m) => m.type !== TOKEN_MARK || !seen.has(`m:${m.attrs.ref}`));
      for (const m of marks) {
        if (m.type === TOKEN_MARK) {
          seen.add(`m:${m.attrs.ref}`);
        }
      }
      if (kept.length !== marks.length) {
        attrs.blockMarks = kept.length > 0 ? kept : null;
      }
    }
    if (Object.keys(attrs).length > 0) {
      tr ??= state.tr;
      tr.setNodeMarkup(pos, undefined, { ...node.attrs, ...attrs }, node.marks);
    }
    return true;
  });
  return tr;
}

/** The plugin that keeps tokens unique. `base` is the document the editor opened with. */
export function tokenGuard(base) {
  const counts = tokenCounts(base);
  return new Plugin({
    filterTransaction(tr) {
      return !tr.docChanged || !mayAddTokens(tr) || withinBase(tr.doc, counts);
    },
    appendTransaction(trs, _old, state) {
      return trs.some((tr) => tr.docChanged && mayAddTokens(tr)) ? dropCopies(state) : null;
    },
  });
}
