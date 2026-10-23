// The DocNode wire allow-list: every kind, where it may sit and its fields (Core/Markup/DocJson). The
// renderer re-checks each field on its own, so the app never needs this; tests use it to pin the wire
// contract, and keeping it out of doc.js keeps it out of every page's bundle.

import {
  MAX_BLOCK_DEPTH,
  MAX_INLINE_DEPTH,
  isAllowedHref,
  isAttachmentId,
  isIslandId,
  isLanguage,
  isUuid,
} from './doc.js';
import { MEDIA_LAYOUTS, PANEL_KINDS, STATUS_COLORS, isDay, isDocColor } from './jira.js';

const BLOCK = 'block';
const INLINE = 'inline';

const int = (min, max) => (v) => Number.isInteger(v) && v >= min && v <= max;
const BLOCK_FORMAT = { align: (v) => v === 'center' || v === 'end', indent: int(1, 6) };
const CELL = {
  children: BLOCK,
  fields: { colspan: int(2, 64), rowspan: int(2, 64), colwidth: (v) => Array.isArray(v) && v.every(int(1, 10_000)), bg: isDocColor },
  optional: ['colspan', 'rowspan', 'colwidth', 'bg'],
};

// kind → its category, what its `c` may hold (a category or a set of kinds) and its scalar fields.
// Panel, table, expand, tasks, underline, status and attachment come only from ADF, and so do the
// display-only kinds and fields of UX8 (colours, alignment, layouts, media, cards, dates…).
const KINDS = {
  doc: { children: BLOCK },
  p: { category: BLOCK, children: INLINE, fields: BLOCK_FORMAT, optional: ['align', 'indent'] },
  h: {
    category: BLOCK,
    children: INLINE,
    fields: { level: int(1, 6), ...BLOCK_FORMAT },
    optional: ['align', 'indent'],
  },
  ul: { category: BLOCK, children: new Set(['li']) },
  ol: { category: BLOCK, children: new Set(['li']), fields: { start: (v) => Number.isInteger(v) && v >= 0 } },
  li: { children: BLOCK },
  quote: { category: BLOCK, children: BLOCK },
  pre: { category: BLOCK, fields: { v: isString, lang: isLanguage }, optional: ['lang'] },
  hr: { category: BLOCK },
  island: { category: BLOCK, children: BLOCK, fields: { id: isIslandId } },
  panel: {
    category: BLOCK,
    children: BLOCK,
    fields: { kind: (v) => PANEL_KINDS.has(v), color: isDocColor, icon: isString },
    optional: ['color', 'icon'],
  },
  table: {
    category: BLOCK,
    children: new Set(['tr']),
    fields: { layout: (v) => MEDIA_LAYOUTS.has(v), numbered: (v) => v === true, width: int(1, 10_000) },
    optional: ['layout', 'numbered', 'width'],
  },
  tr: { children: new Set(['th', 'td']) },
  th: CELL,
  td: CELL,
  expand: { category: BLOCK, children: BLOCK, fields: { title: isString } },
  tasks: { category: BLOCK, children: new Set(['task', 'tasks']) },
  task: { children: INLINE, fields: { done: (v) => typeof v === 'boolean' } },
  decisions: { category: BLOCK, children: new Set(['decision']) },
  decision: { children: INLINE, fields: { done: (v) => typeof v === 'boolean' } },
  layout: { category: BLOCK, children: new Set(['col']) },
  col: { children: BLOCK, fields: { width: (v) => typeof v === 'number' && v >= 0 && v <= 100 } },
  media: {
    category: BLOCK,
    children: INLINE,
    fields: {
      layout: (v) => MEDIA_LAYOUTS.has(v),
      width: (v) => typeof v === 'number' && v > 0 && v <= 10_000,
      unit: (v) => v === '%' || v === 'px',
      caption: (v) => Array.isArray(v) && v.every((n) => fits(INLINE, n) && isAllowedNode(n, 1)),
    },
    optional: ['layout', 'width', 'unit', 'caption'],
  },
  files: { category: BLOCK, children: INLINE },
  text: { category: INLINE, fields: { v: isString } },
  b: { category: INLINE, children: INLINE },
  i: { category: INLINE, children: INLINE },
  s: { category: INLINE, children: INLINE },
  u: { category: INLINE, children: INLINE },
  code: { category: INLINE, fields: { v: isString } },
  a: { category: INLINE, children: INLINE, fields: { href: isAllowedHref, title: isString }, optional: ['title'] },
  fg: { category: INLINE, children: INLINE, fields: { color: isDocColor } },
  bg: { category: INLINE, children: INLINE, fields: { color: isDocColor } },
  sub: { category: INLINE, children: INLINE },
  sup: { category: INLINE, children: INLINE },
  mention: { category: INLINE, fields: { id: isUuid, name: isString } },
  img: { category: INLINE, fields: { blob: isUuid, alt: isString } },
  attachment: {
    category: INLINE,
    fields: { id: isAttachmentId, alt: isString, w: int(1, 100_000), h: int(1, 100_000) },
    optional: ['w', 'h'],
  },
  file: { category: INLINE, fields: { id: isAttachmentId, name: isString } },
  status: {
    category: INLINE,
    fields: { v: isString, color: (v) => STATUS_COLORS.has(v), bold: (v) => v === true },
    optional: ['bold'],
  },
  user: { category: INLINE, fields: { v: isString } },
  date: { category: INLINE, fields: { v: isDay } },
  card: { category: INLINE, fields: { href: isAllowedHref, block: (v) => v === true }, optional: ['block'] },
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
