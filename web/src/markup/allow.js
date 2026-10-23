// The DocNode wire allow-list: every kind, where it may sit and its fields (Core/Markup/DocJson). The
// renderer re-checks each field on its own, so the app never needs this; tests use it to pin the wire
// contract, and keeping it out of doc.js keeps it out of every page's bundle.

import {
  MAX_BLOCK_DEPTH,
  MAX_INLINE_DEPTH,
  PANEL_KINDS,
  STATUS_COLORS,
  isAllowedHref,
  isAttachmentId,
  isIslandId,
  isLanguage,
  isUuid,
} from './doc.js';

const BLOCK = 'block';
const INLINE = 'inline';

// kind → its category, what its `c` may hold (a category or a set of kinds) and its scalar fields.
// Panel, table, expand, tasks, underline, status and attachment come only from ADF.
const KINDS = {
  doc: { children: BLOCK },
  p: { category: BLOCK, children: INLINE },
  h: { category: BLOCK, children: INLINE, fields: { level: (v) => Number.isInteger(v) && v >= 1 && v <= 6 } },
  ul: { category: BLOCK, children: new Set(['li']) },
  ol: { category: BLOCK, children: new Set(['li']), fields: { start: (v) => Number.isInteger(v) && v >= 0 } },
  li: { children: BLOCK },
  quote: { category: BLOCK, children: BLOCK },
  pre: { category: BLOCK, fields: { v: isString, lang: isLanguage }, optional: ['lang'] },
  hr: { category: BLOCK },
  island: { category: BLOCK, children: BLOCK, fields: { id: isIslandId } },
  panel: { category: BLOCK, children: BLOCK, fields: { kind: (v) => PANEL_KINDS.has(v) } },
  table: { category: BLOCK, children: new Set(['tr']) },
  tr: { children: new Set(['th', 'td']) },
  th: { children: BLOCK },
  td: { children: BLOCK },
  expand: { category: BLOCK, children: BLOCK, fields: { title: isString } },
  tasks: { category: BLOCK, children: new Set(['task']) },
  task: { children: INLINE, fields: { done: (v) => typeof v === 'boolean' } },
  text: { category: INLINE, fields: { v: isString } },
  b: { category: INLINE, children: INLINE },
  i: { category: INLINE, children: INLINE },
  s: { category: INLINE, children: INLINE },
  u: { category: INLINE, children: INLINE },
  code: { category: INLINE, fields: { v: isString } },
  a: { category: INLINE, children: INLINE, fields: { href: isAllowedHref } },
  mention: { category: INLINE, fields: { id: isUuid, name: isString } },
  img: { category: INLINE, fields: { blob: isUuid, alt: isString } },
  attachment: { category: INLINE, fields: { id: isAttachmentId, alt: isString } },
  status: { category: INLINE, fields: { v: isString, color: (v) => STATUS_COLORS.has(v) } },
  br: { category: INLINE },
};

function isString(v) {
  return typeof v === 'string';
}


export function kindOf(node) {
  return node !== null && typeof node === 'object' && Object.hasOwn(KINDS, node.t) ? KINDS[node.t] : null;
}

// True when every node, field and parent/child pairing of `doc` is allow-listed.
export function isAllowedDoc(doc) {
  return doc?.t === 'doc' && isAllowedNode(doc, 0);
}

function isAllowedNode(node, depth) {
  const spec = kindOf(node);
  if (spec === null || depth > MAX_BLOCK_DEPTH + MAX_INLINE_DEPTH + 4) {
    return false;
  }
  const fields = spec.fields ?? {};
  for (const key of Object.keys(node)) {
    if (key === 't' || (key === 'c' && spec.children)) {
      continue;
    }
    if (!Object.hasOwn(fields, key) || !fields[key](node[key])) {
      return false;
    }
  }
  for (const key of Object.keys(fields)) {
    if (!Object.hasOwn(node, key) && !spec.optional?.includes(key)) {
      return false;
    }
  }
  if (!spec.children) {
    return true;
  }
  return Array.isArray(node.c)
    && node.c.every((child) => fits(spec.children, child) && isAllowedNode(child, depth + 1));
}

function fits(allowed, child) {
  const spec = kindOf(child);
  if (spec === null) {
    return false;
  }
  return allowed instanceof Set ? allowed.has(child.t) : spec.category === allowed;
}
