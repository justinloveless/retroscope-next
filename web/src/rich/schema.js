// The ProseMirror schema of the description editor, built from the ADF allow-list (adf-schema.json) so the two
// cannot drift: node and mark names are the ADF type names, every allow-listed attribute is a ProseMirror
// attribute with its value rule as `validate`, and content expressions come from the allow-list's child lists.
//
// Besides the ADF attributes each type carries a few the editor owns, never sent as such:
//   rsKeep      the server's stash of attributes this allow-list does not know (a ref; restored on save)
//   blockMarks  the ADF marks of a non-text node (rsMark tokens, a link on media), as plain ADF mark objects
//   align, indent  paragraph and heading `alignment` / `indentation` marks, as attributes the editor can set;
//                  which parents allow them is contextMarks (rules.js), kept by the block-mark guard
//   adfShape    which ADF attribute keys the node arrived with, so an untouched node is written back the same
//
// toDOM uses the read view's jira.css classes and never writes a `style` attribute: style-src 'self' refuses it.
// Colours, widths and the editing chrome are the node and mark views' business (views.js), through the CSSOM.

import { Schema } from 'prosemirror-model';
import { allowList, compileRule, contextMarks, isRequired } from './rules.js';

export const STASH = allowList.stash;
export const TOKEN_NODES = new Set(Object.entries(allowList.nodes).filter(([, n]) => n.token).map(([name]) => name));
export const TOKEN_MARK = 'rsMark';
export const BLOCK_MARKS = new Set(Object.entries(allowList.marks).filter(([, m]) => m.block).map(([name]) => name));

// prosemirror-tables reads spans as numbers, so a cell's spans default to 1, not null.
export const CELL_DEFAULTS = { colspan: 1, rowspan: 1 };

function expand(list) {
  return [...new Set(list.flatMap((item) => (item.startsWith('@') ? expand(allowList.groups[item.slice(1)]) : [item])))];
}

/** The ADF child types `name` may hold, groups expanded. */
export function childTypes(name) {
  const spec = allowList.nodes[name];
  return expand([...(spec.first ?? []), ...(spec.children ?? [])]);
}

function contentExpression(name) {
  const spec = allowList.nodes[name];
  if (!spec.children && !spec.first) {
    return undefined;
  }
  const alt = (list) => `(${expand(list).join(' | ')})`;
  const min = spec.min ?? 0;
  if (spec.first) {
    const rest = spec.children ? alt(spec.children) : null;
    if (!rest) {
      return alt(spec.first);
    }
    return `${alt(spec.first)} ${rest}${spec.max ? `{0,${spec.max - 1}}` : '*'}`;
  }
  const quantity = spec.max ? `{${min},${spec.max}}` : min > 0 ? (min === 1 ? '+' : `{${min},}`) : '*';
  return `${alt(spec.children)}${quantity}`;
}

function attrSpecs(owner, rules, extra) {
  const attrs = {};
  for (const [key, rule] of Object.entries(rules)) {
    const test = compileRule(rule);
    const required = isRequired(rule);
    const fallback = CELL_DEFAULTS[key];
    // A required attribute still defaults to null: ProseMirror needs every node in a required content position
    // to be creatable without attributes. Such a node is incomplete (isComplete) until the editor fills it in.
    attrs[key] = {
      default: fallback ?? null,
      validate(v) {
        if (!(v === null && !required && fallback === undefined) && !test(v)) {
          throw new RangeError(`${owner}.${key}: value not allowed`);
        }
      },
    };
  }
  return { ...attrs, ...extra, [STASH]: { default: null }, adfShape: { default: null } };
}

// The read view's classes (jira.css), so an aligned or indented block looks the same while it is edited.
const blockClass = (node) => [
  node.attrs.align ? `align-${node.attrs.align}` : null,
  node.attrs.indent ? `indent-${node.attrs.indent}` : null,
].filter(Boolean).join(' ') || null;

const atomLabel = (cls, text) => ['span', { class: cls, contenteditable: 'false' }, text];

// What each node needs beyond its content and attributes: ProseMirror roles, and a plain DOM rendering.
const VIEW = {
  doc: {},
  paragraph: { toDOM: (n) => ['p', { class: blockClass(n) }, 0] },
  heading: { defining: true, toDOM: (n) => [`h${n.attrs.level}`, { class: blockClass(n) }, 0] },
  text: {},
  hardBreak: { selectable: false, toDOM: () => ['br'] },
  bulletList: { toDOM: () => ['ul', 0] },
  orderedList: { toDOM: (n) => ['ol', { start: n.attrs.order === null ? null : String(n.attrs.order) }, 0] },
  listItem: { defining: true, toDOM: () => ['li', 0] },
  blockquote: { defining: true, toDOM: () => ['blockquote', 0] },
  rule: { toDOM: () => ['hr'] },
  codeBlock: { code: true, defining: true, marks: '', toDOM: () => ['pre', ['code', 0]] },
  panel: { defining: true, toDOM: (n) => ['div', { class: `panel panel-${n.attrs.panelType}` }, ['div', { class: 'panel-body' }, 0]] },
  expand: { defining: true, isolating: true, toDOM: () => ['div', { class: 'jira-expand' }, 0] },
  nestedExpand: { defining: true, isolating: true, toDOM: () => ['div', { class: 'jira-expand' }, 0] },
  table: { tableRole: 'table', isolating: true, toDOM: () => ['table', ['tbody', 0]] },
  tableRow: { tableRole: 'row', toDOM: () => ['tr', 0] },
  tableHeader: { tableRole: 'header_cell', isolating: true, toDOM: (n) => ['th', spans(n), 0] },
  tableCell: { tableRole: 'cell', isolating: true, toDOM: (n) => ['td', spans(n), 0] },
  layoutSection: { isolating: true, toDOM: () => ['div', { class: 'layout' }, 0] },
  layoutColumn: { isolating: true, toDOM: () => ['div', { class: 'layout-col' }, 0] },
  taskList: { toDOM: () => ['ul', { class: 'tasks' }, 0] },
  taskItem: { defining: true, toDOM: (n) => ['li', { class: 'task', 'data-state': n.attrs.state }, 0] },
  decisionList: { toDOM: () => ['ul', { class: 'decisions' }, 0] },
  decisionItem: { defining: true, toDOM: () => ['li', { class: 'decision' }, 0] },
  mediaSingle: { isolating: true, toDOM: () => ['figure', { class: 'media' }, 0] },
  mediaGroup: { toDOM: () => ['div', { class: 'media-group' }, 0] },
  media: { atom: true, toDOM: (n) => atomLabel('rich-media', n.attrs.alt ?? '') },
  caption: { toDOM: () => ['figcaption', 0] },
  mention: { atom: true, toDOM: (n) => atomLabel('jira-user', n.attrs.text ?? '@') },
  emoji: { atom: true, toDOM: (n) => atomLabel('rich-emoji', n.attrs.text ?? n.attrs.shortName) },
  date: { atom: true, toDOM: (n) => ['time', { class: 'jira-date', contenteditable: 'false' }, n.attrs.timestamp] },
  status: { atom: true, toDOM: (n) => atomLabel('jira-status', n.attrs.text) },
  inlineCard: { atom: true, toDOM: (n) => atomLabel('jira-card', n.attrs.url) },
  blockCard: { atom: true, toDOM: (n) => ['div', { class: 'jira-card is-block', contenteditable: 'false' }, n.attrs.url] },
  embedCard: { atom: true, toDOM: (n) => ['div', { class: 'jira-card is-block', contenteditable: 'false' }, n.attrs.url] },
  rsOpaque: { atom: true, draggable: true, toDOM: () => ['div', { class: 'rich-opaque', contenteditable: 'false' }] },
  rsOpaqueInline: { atom: true, toDOM: () => atomLabel('rich-opaque', '') },
};

function spans(node) {
  return {
    colspan: node.attrs.colspan === 1 ? null : String(node.attrs.colspan),
    rowspan: node.attrs.rowspan === 1 ? null : String(node.attrs.rowspan),
  };
}

const MARK_VIEW = {
  strong: { toDOM: () => ['strong', 0] },
  em: { toDOM: () => ['em', 0] },
  strike: { toDOM: () => ['s', 0] },
  underline: { toDOM: () => ['u', 0] },
  code: { excludes: 'strong em strike underline textColor backgroundColor subsup code', toDOM: () => ['code', 0] },
  link: { inclusive: false, toDOM: () => ['a', { class: 'rich-link' }, 0] },
  textColor: { toDOM: () => ['span', { class: 'rich-fg' }, 0] },
  backgroundColor: { toDOM: () => ['span', { class: 'rich-bg' }, 0] },
  subsup: { toDOM: (m) => [m.attrs.type, 0] },
  // Several unknown marks can cover the same text, each its own ref.
  rsMark: { excludes: '', toDOM: () => ['span', { class: 'rich-opaque-mark' }, 0] },
};

function nodeSpecs() {
  const specs = {};
  for (const [name, spec] of Object.entries(allowList.nodes)) {
    const view = VIEW[name];
    if (!view) {
      throw new Error(`schema.js: no view for ADF node ${name}`);
    }
    if (name === 'text') {
      specs.text = { group: 'inline' };
      continue;
    }
    const extra = { blockMarks: { default: null } };
    if (contextMarks(null, name).length > 0) {
      extra.align = { default: null };
      extra.indent = { default: null };
    }
    const content = contentExpression(name);
    specs[name] = {
      ...view,
      ...(content ? { content } : {}),
      ...(spec.inline ? { inline: true, group: 'inline' } : {}),
      ...(name === 'doc' ? {} : { attrs: attrSpecs(name, spec.attrs, extra) }),
      ...(spec.token ? { atom: true, selectable: true } : {}),
    };
  }
  specs.doc.attrs = { adfShape: { default: null } };
  return specs;
}

function markSpecs() {
  const specs = {};
  for (const [name, spec] of Object.entries(allowList.marks)) {
    if (spec.block) {
      continue;
    }
    specs[name] = { ...MARK_VIEW[name], attrs: attrSpecs(name, spec.attrs, {}) };
  }
  return specs;
}

export const schema = new Schema({ nodes: nodeSpecs(), marks: markSpecs(), topNode: 'doc' });

const REQUIRED = Object.fromEntries(Object.entries(allowList.nodes)
  .map(([name, spec]) => [name, Object.entries(spec.attrs).filter(([, rule]) => isRequired(rule)).map(([key]) => key)]));

/** False when `node` or a descendant lacks a required attribute (one ProseMirror created with its null default). */
export function isComplete(node) {
  let complete = true;
  const check = (n) => {
    if (!n.isText && n.type.name !== 'doc' && REQUIRED[n.type.name].some((key) => n.attrs[key] === null)) {
      complete = false;
    }
    return complete;
  };
  if (check(node)) {
    node.descendants(check);
  }
  return complete;
}
