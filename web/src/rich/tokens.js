// The server's tokens stand for content the editor cannot read, and the save restores each from the issue as Jira
// holds it. A second copy of a token would be a second copy of that content, which the server refuses
// (opaque_duplicated). So no transaction may raise a token's count above what the document opened with, and an
// attribute stash or node mark that a split or a copy repeated stays on the node that held it before.

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

// The stash and node-mark refs a node carries, as keys.
function refKeys(node) {
  const keys = [];
  if (node.isText) {
    return keys;
  }
  if (node.attrs[STASH] != null) {
    keys.push(`k:${node.attrs[STASH]}`);
  }
  for (const m of node.attrs.blockMarks ?? []) {
    if (m.type === TOKEN_MARK) {
      keys.push(`m:${m.attrs.ref}`);
    }
  }
  return keys;
}

// Where each ref sat before `trs`, mapped into the new document; null when that node was deleted (a move).
function owners(old, trs) {
  const at = new Map();
  old.descendants((node, pos) => {
    for (const key of refKeys(node)) {
      let mapped = pos;
      for (const tr of trs) {
        const result = mapped === null ? null : tr.mapping.mapResult(mapped, 1);
        mapped = result && !result.deletedAfter ? result.pos : null;
      }
      at.set(key, mapped);
    }
    return true;
  });
  return at;
}

// A ref carried by several nodes stays on the one that held it before (mapped forward: a copy inserted before the
// original does not take it), or on the first when that node is gone, and is cleared from the others.
function dropCopies(old, trs, state) {
  const carriers = new Map();
  state.doc.descendants((node, pos) => {
    for (const key of refKeys(node)) {
      carriers.set(key, [...(carriers.get(key) ?? []), pos]);
    }
    return true;
  });
  const before = owners(old, trs);
  const clear = new Map();
  for (const [key, positions] of carriers) {
    if (positions.length > 1) {
      const owner = positions.includes(before.get(key)) ? before.get(key) : positions[0];
      for (const pos of positions.filter((p) => p !== owner)) {
        clear.set(pos, [...(clear.get(pos) ?? []), key]);
      }
    }
  }
  if (clear.size === 0) {
    return null;
  }
  const tr = state.tr;
  for (const [pos, keys] of clear) {
    const node = state.doc.nodeAt(pos);
    const attrs = { ...node.attrs };
    if (keys.includes(`k:${attrs[STASH]}`)) {
      attrs[STASH] = null;
    }
    const kept = (attrs.blockMarks ?? []).filter((m) => m.type !== TOKEN_MARK || !keys.includes(`m:${m.attrs.ref}`));
    attrs.blockMarks = kept.length > 0 ? kept : null;
    tr.setNodeMarkup(pos, undefined, attrs, node.marks);
  }
  return tr;
}

/** The plugin that keeps tokens unique. `base` is the document the editor opened with. */
export function tokenGuard(base) {
  const counts = tokenCounts(base);
  return new Plugin({
    filterTransaction(tr) {
      return !tr.docChanged || !mayAddTokens(tr) || withinBase(tr.doc, counts);
    },
    appendTransaction(trs, old, state) {
      return trs.some((tr) => tr.docChanged && mayAddTokens(tr)) ? dropCopies(old.doc, trs, state) : null;
    },
  });
}
