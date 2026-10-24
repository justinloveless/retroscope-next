// ADF JSON <-> the editor's ProseMirror document.
//
// Two directions, three modes:
//   adfToPm(adf)                    the server's projection (GET …/description). Strict: anything outside the
//                                   allow-list is a server bug, so it throws AdfError instead of guessing.
//   adfFragmentToPm(list, {paste})  a clipboard payload. Hostile until proven otherwise: whatever fails a rule
//                                   is dropped, and tokens, foreign media and unpicked mentions never get in.
//   pmToAdf(doc)                    the save body. Rebuilt from attributes; the server rebuilds it again.
//
// pmToAdf(adfToPm(d)) is canonical-equal to d (canonicalAdf): key order, mark order and the joining of adjacent
// text nodes with the same marks are the only differences ProseMirror introduces, and none has a meaning in ADF.

import { Fragment } from 'prosemirror-model';
import { compileRule, contextMarks, isRequired, isWellFormed, lengthOf, allowList } from './rules.js';
import { BLOCK_MARKS, CELL_DEFAULTS, STASH, TOKEN_MARK, TOKEN_NODES, childTypes, isComplete, schema } from './schema.js';

export class AdfError extends Error {
  constructor(code, path) {
    super(`${code} at ${path || 'doc'}`);
    this.code = code;
    this.path = path;
  }
}

export const EDIT_LIMITS = { depth: 48, nodes: 20_000 };
export const PASTE_LIMITS = { depth: 32, nodes: 5_000 };

const STASH_RULE = compileRule({ type: 'string', pattern: '[A-Za-z0-9_-]{16}' });
const REQUIRED_LOCAL_ID = new Set(['taskList', 'taskItem', 'decisionList', 'decisionItem']);
const PASTE_STATUS = new Set(['neutral', 'purple', 'blue', 'red', 'yellow', 'green']);
const CODE_ONLY_WITH = new Set(allowList.codeCombinesWith);
const MENTION_NAME = compileRule({ type: 'string', max: 100 });
// AdfSchema.ValidText: no control character but tab, and a line break only in a code block.
const BAD_TEXT = /[\u0000-\u0008\u000a-\u001f\u007f-\u009f]/;
const BAD_CODE_TEXT = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/;

// Compiled once: per type, attribute name -> { test, required, lengthOf }.
function compileAttrs(table) {
  return Object.fromEntries(Object.entries(table).map(([name, spec]) => [name, Object.fromEntries(
    Object.entries(spec.attrs).map(([key, rule]) => [key, { test: compileRule(rule), required: isRequired(rule), lengthOf: lengthOf(rule) }]),
  )]));
}
const NODE_ATTRS = compileAttrs(allowList.nodes);
const MARK_ATTRS = compileAttrs(allowList.marks);
const CHILDREN = Object.fromEntries(Object.keys(allowList.nodes).map((name) => [name, new Set(childTypes(name))]));

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
// Input names index these tables, so an inherited name (constructor, __proto__) must not count as listed.
const own = (table, key) => (Object.hasOwn(table, key) ? table[key] : undefined);

function freshId() {
  return globalThis.crypto.randomUUID();
}

class Reader {
  constructor({ paste = false, session = null } = {}) {
    this.paste = paste;
    this.session = session;
    this.limits = paste ? PASTE_LIMITS : EDIT_LIMITS;
    this.count = 0;
  }

  // Strict mode throws; paste mode reports "drop this".
  fail(code, path) {
    if (this.paste) {
      return null;
    }
    throw new AdfError(code, path);
  }

  attrs(type, raw, path) {
    const table = own(NODE_ATTRS, type) ?? own(MARK_ATTRS, type);
    const out = {};
    if (raw !== undefined && !isObject(raw)) {
      return this.fail('invalid_attr', `${path}.attrs`) ?? { out, keys: null, dropped: true };
    }
    // keys stays null for a node without `attrs`, so it is written back without one; required ones are still due.
    const keys = raw === undefined ? null : [];
    for (const [key, value] of Object.entries(raw ?? {})) {
      const at = `${path}.attrs.${key}`;
      if (key === STASH) {
        if (this.paste) {
          continue;
        }
        if (!STASH_RULE(value)) {
          this.fail('invalid_attr', at);
        }
        out[STASH] = value;
        keys.push(key);
        continue;
      }
      const rule = own(table, key);
      if (!rule) {
        if (this.fail('unknown_attr', at) === null) {
          continue;
        }
      }
      if (value === null && !rule.required && CELL_DEFAULTS[key] === undefined) {
        out[key] = null;
        keys.push(key);
        continue;
      }
      if (!rule.test(value) || (this.paste && type === 'status' && key === 'color' && !PASTE_STATUS.has(value))) {
        if (this.fail('invalid_attr', at) === null) {
          continue;
        }
      }
      out[key] = value;
      keys.push(key);
    }
    for (const [key, rule] of Object.entries(table)) {
      if (rule.lengthOf && out[key] != null && out[key].length !== (out[rule.lengthOf] ?? 1)) {
        if (this.fail('invalid_attr', `${path}.attrs.${key}`) === null) {
          delete out[key];
        }
      }
    }
    if (this.paste) {
      // Pasted copies must not repeat an id the document already holds (duplicate_local_id).
      delete out.localId;
      if (REQUIRED_LOCAL_ID.has(type)) {
        out.localId = freshId();
      }
    }
    for (const [key, rule] of Object.entries(table)) {
      if (rule.required && !(key in out)) {
        return this.fail('missing_attr', `${path}.attrs.${key}`) ?? { out, keys, dropped: true };
      }
    }
    return { out, keys };
  }

  mark(raw, path, { inCode, onNode, parent }) {
    if (!isObject(raw) || typeof raw.type !== 'string') {
      return this.fail('invalid_mark', path);
    }
    const name = raw.type;
    if (name === TOKEN_MARK && this.paste) {
      return null;
    }
    if (!own(MARK_ATTRS, name) || inCode) {
      return this.fail(inCode ? 'invalid_mark' : 'unknown_mark', path);
    }
    for (const key of Object.keys(raw)) {
      if (key !== 'type' && key !== 'attrs') {
        return this.fail('unknown_member', `${path}.${key}`);
      }
    }
    const { out, keys, dropped } = this.attrs(name, raw.attrs, path);
    if (dropped) {
      return null;
    }
    if (onNode && name !== TOKEN_MARK
      && !(BLOCK_MARKS.has(name) ? contextMarks(parent, onNode) : allowList.nodes[onNode].marks ?? []).includes(name)) {
      return this.fail('invalid_mark', path);
    }
    if (!onNode && BLOCK_MARKS.has(name)) {
      return this.fail('invalid_mark', path);
    }
    return { name, attrs: out, keys };
  }

  // A text node's marks as ProseMirror marks; `code` combines only with link and tokens.
  textMarks(list, path, inCode) {
    if (list === undefined) {
      return [];
    }
    if (!Array.isArray(list)) {
      return this.fail('invalid_mark', `${path}.marks`) ?? [];
    }
    const marks = [];
    const seen = new Set();
    for (let i = 0; i < list.length; i++) {
      const mark = this.mark(list[i], `${path}.marks.${i}`, { inCode });
      if (mark && this.paste && mark.name !== TOKEN_MARK && seen.has(mark.name)) {
        continue;
      }
      if (mark) {
        seen.add(mark.name);
        marks.push(mark);
      }
    }
    const names = marks.map((m) => m.name);
    if (names.includes('code') && names.some((n) => n !== 'code' && !CODE_ONLY_WITH.has(n))) {
      if (this.fail('invalid_mark', `${path}.marks`) === null) {
        return marks.filter((m) => m.name === 'code' || CODE_ONLY_WITH.has(m.name)).map(pmMark);
      }
    }
    if (new Set(names.filter((n) => n !== TOKEN_MARK)).size !== names.filter((n) => n !== TOKEN_MARK).length) {
      this.fail('invalid_mark', `${path}.marks`);
    }
    return marks.map(pmMark);
  }

  text(raw, path, inCode) {
    for (const key of Object.keys(raw)) {
      if (key !== 'type' && key !== 'text' && key !== 'marks') {
        if (this.fail('unknown_member', `${path}.${key}`) === null) {
          continue;
        }
      }
    }
    let text = raw.text;
    if (typeof text !== 'string' || !isWellFormed(text)) {
      return this.fail('invalid_text', path);
    }
    const bad = inCode ? BAD_CODE_TEXT : BAD_TEXT;
    if (bad.test(text)) {
      if (this.fail('invalid_text', path) === null) {
        text = text.replace(new RegExp(bad.source, 'g'), ' ');
      }
    }
    if (text === '') {
      return this.fail('invalid_text', path);
    }
    return schema.text(text, this.textMarks(raw.marks, path, inCode));
  }

  node(raw, path, depth, parent) {
    if (++this.count > this.limits.nodes) {
      throw new AdfError('too_many_nodes', path);
    }
    if (depth > this.limits.depth) {
      throw new AdfError('too_deep', path);
    }
    if (!isObject(raw) || typeof raw.type !== 'string') {
      return this.fail('not_node', path);
    }
    const type = raw.type;
    if (type === 'text') {
      return this.text(raw, path, parent === 'codeBlock');
    }
    if (!Object.hasOwn(allowList.nodes, type) || type === 'doc') {
      return this.fail('unknown_node', path);
    }
    if (this.paste && TOKEN_NODES.has(type)) {
      return null;
    }
    if (type === 'mention' && this.paste && !this.session?.mentions?.has(raw.attrs?.id)) {
      // Every new mention notifies someone; only ids picked in this editing session may be pasted.
      const name = raw.attrs?.text;
      return typeof name === 'string' && name !== '' && MENTION_NAME(name)
        ? schema.text(name)
        : null;
    }
    if (type === 'media' && this.paste && !(raw.attrs?.type === 'file' && this.session?.media?.has(raw.attrs?.id))) {
      return null;
    }
    for (const key of Object.keys(raw)) {
      if (!['type', 'attrs', 'content', 'marks'].includes(key)) {
        if (this.fail('unknown_member', `${path}.${key}`) === null) {
          continue;
        }
      }
    }
    const nodeType = schema.nodes[type];
    const { out, keys, dropped } = this.attrs(type, raw.attrs, path);
    if (dropped) {
      return null;
    }

    let marks = [];
    if (raw.marks !== undefined) {
      if (!Array.isArray(raw.marks)) {
        return this.fail('invalid_mark', `${path}.marks`);
      }
      const read = raw.marks.map((m, i) => this.mark(m, `${path}.marks.${i}`, { onNode: type, parent })).filter(Boolean);
      const seen = new Set();
      const blockMarks = [];
      for (const mark of read) {
        if (mark.name !== TOKEN_MARK && seen.has(mark.name)) {
          if (this.fail('invalid_mark', `${path}.marks`) === null) {
            continue;
          }
        }
        seen.add(mark.name);
        if (mark.name === 'alignment') {
          out.align = mark.attrs.align;
        } else if (mark.name === 'indentation') {
          out.indent = mark.attrs.level;
        } else if (nodeType.isInline) {
          marks.push(pmMark(mark));
        } else {
          blockMarks.push(adfMark(mark.name, mark.attrs, mark.keys));
        }
      }
      if (out.align && out.indent) {
        this.fail('invalid_mark', `${path}.marks`);
      }
      if (blockMarks.length > 0) {
        out.blockMarks = blockMarks;
      }
    }

    let content = Fragment.empty;
    const leaf = !allowList.nodes[type].children && !allowList.nodes[type].first;
    if (raw.content !== undefined) {
      if (leaf || !Array.isArray(raw.content)) {
        if (this.fail('invalid_nesting', `${path}.content`) === null) {
          return null;
        }
      }
      const children = [];
      const allowed = CHILDREN[type];
      for (let i = 0; i < raw.content.length; i++) {
        const at = `${path}.content.${i}`;
        const child = raw.content[i];
        if (isObject(child) && typeof child.type === 'string' && !allowed.has(child.type)
          && Object.hasOwn(allowList.nodes, child.type)) {
          if (this.fail('invalid_nesting', at) === null) {
            continue;
          }
        }
        const node = this.node(child, at, depth + 1, type);
        if (node && (node.isText ? allowed.has('text') : allowed.has(node.type.name))) {
          children.push(node);
        }
      }
      content = Fragment.from(children);
    }
    if (!this.paste) {
      out.adfShape = { keys, content: !leaf && raw.content !== undefined };
    }

    try {
      if (nodeType.validContent(content)) {
        return nodeType.create(out, content, marks);
      }
      if (this.paste) {
        // Filling may only add structure (an empty paragraph in a cell), never a node that needs an id or a
        // source, such as the media of an image whose media was dropped.
        const filled = nodeType.createAndFill(out, content, marks);
        return filled && isComplete(filled) ? filled : null;
      }
    } catch {
      if (this.paste) {
        return null;
      }
      throw new AdfError('invalid_attr', path);
    }
    throw new AdfError('invalid_nesting', `${path}.content`);
  }
}

function pmMark({ name, attrs, keys }) {
  return schema.marks[name].create({ ...attrs, adfShape: keys === null ? null : { keys } });
}

function adfMark(name, attrs, keys) {
  const mark = { type: name };
  if (keys !== null || Object.keys(attrs).length > 0) {
    mark.attrs = attrs;
  }
  return mark;
}

/** The server's projection -> a ProseMirror doc. Throws AdfError on anything the allow-list refuses. */
export function adfToPm(adf) {
  if (!isObject(adf) || adf.type !== 'doc' || adf.version !== 1 || !Array.isArray(adf.content)) {
    throw new AdfError('not_doc', '');
  }
  for (const key of Object.keys(adf)) {
    if (!['type', 'version', 'content'].includes(key)) {
      throw new AdfError('unknown_member', key);
    }
  }
  const reader = new Reader();
  const allowed = CHILDREN.doc;
  const children = adf.content.map((child, i) => {
    if (isObject(child) && !allowed.has(child.type) && Object.hasOwn(allowList.nodes, child.type ?? '')) {
      throw new AdfError('invalid_nesting', `content.${i}`);
    }
    return reader.node(child, `content.${i}`, 1, 'doc');
  });
  return schema.nodes.doc.create(null, children);
}

/**
 * ADF nodes from a clipboard payload -> a Fragment of what survives. Tokens (rsOpaque, rsMark, rsKeep) never
 * survive: a copy of something the editor cannot read would be a second copy of it on save (opaque_duplicated).
 * session.media and session.mentions hold the media ids and account ids this editing session may place.
 */
export function adfFragmentToPm(list, { session = null } = {}) {
  if (!Array.isArray(list)) {
    return Fragment.empty;
  }
  const reader = new Reader({ paste: true, session });
  const nodes = [];
  for (let i = 0; i < list.length; i++) {
    let node;
    try {
      node = reader.node(list[i], `content.${i}`, 1, null);
    } catch {
      return Fragment.empty;
    }
    if (node) {
      nodes.push(node);
    }
  }
  const inline = nodes.length > 0 && nodes[0].isInline;
  return Fragment.from(nodes.filter((n) => n.isInline === inline));
}

function attrsOut(node) {
  const table = own(NODE_ATTRS, node.type.name) ?? own(MARK_ATTRS, node.type.name);
  const shape = node.attrs.adfShape;
  const out = {};
  for (const key of Object.keys(table)) {
    const value = node.attrs[key];
    const fallback = CELL_DEFAULTS[key] ?? null;
    if (value !== fallback || shape?.keys?.includes(key)) {
      out[key] = value;
    }
  }
  if (node.attrs[STASH] !== null) {
    out[STASH] = node.attrs[STASH];
  }
  return Object.keys(out).length > 0 || (shape?.keys ?? null) !== null ? out : null;
}

function marksOut(marks) {
  return marks.map((mark) => {
    const attrs = attrsOut(mark);
    return attrs === null ? { type: mark.type.name } : { type: mark.type.name, attrs };
  });
}

/** One ProseMirror node -> ADF. */
export function nodeToAdf(node) {
  if (node.isText) {
    const out = { type: 'text', text: node.text };
    if (node.marks.length > 0) {
      out.marks = marksOut(node.marks);
    }
    return out;
  }
  const name = node.type.name;
  const out = { type: name };
  const attrs = attrsOut(node);
  if (attrs !== null) {
    out.attrs = attrs;
  }
  const leaf = !allowList.nodes[name].children && !allowList.nodes[name].first;
  if (!leaf && (node.childCount > 0 || node.attrs.adfShape?.content !== false)) {
    out.content = [];
    node.forEach((child) => out.content.push(nodeToAdf(child)));
  }
  const marks = [];
  if (node.attrs.align) {
    marks.push({ type: 'alignment', attrs: { align: node.attrs.align } });
  }
  if (node.attrs.indent) {
    marks.push({ type: 'indentation', attrs: { level: node.attrs.indent } });
  }
  marks.push(...(node.attrs.blockMarks ?? []));
  // On an inline atom only the server's token marks mean anything; a bold range over a status leaves it alone.
  marks.push(...marksOut(node.marks.filter((m) => m.type.name === TOKEN_MARK)));
  if (marks.length > 0) {
    out.marks = marks;
  }
  return out;
}

/** The save body (descriptionAdf). */
export function pmToAdf(doc) {
  const content = [];
  doc.forEach((child) => content.push(nodeToAdf(child)));
  return { type: 'doc', version: 1, content };
}

/**
 * Canonical JSON text: object keys sorted, `marks` arrays sorted, adjacent text nodes with the same marks
 * joined. Two documents with the same canonical text are the same ADF.
 */
export function canonicalAdf(value) {
  if (Array.isArray(value)) {
    return `[${joinText(value).map(canonicalAdf).join(',')}]`;
  }
  if (isObject(value)) {
    return `{${Object.keys(value).sort().map((key) => {
      const v = key === 'marks' && Array.isArray(value[key]) ? value[key].map(canonicalAdf).sort() : value[key];
      return `${JSON.stringify(key)}:${Array.isArray(v) && key === 'marks' ? `[${v.join(',')}]` : canonicalAdf(v)}`;
    }).join(',')}}`;
  }
  return JSON.stringify(value);
}

function joinText(list) {
  const out = [];
  for (const item of list) {
    const last = out[out.length - 1];
    if (isObject(item) && item.type === 'text' && isObject(last) && last.type === 'text'
      && Object.keys(item).every((k) => ['type', 'text', 'marks'].includes(k))
      && Object.keys(last).every((k) => ['type', 'text', 'marks'].includes(k))
      && canonicalAdf({ m: item.marks ?? [] }) === canonicalAdf({ m: last.marks ?? [] })) {
      out[out.length - 1] = { ...last, text: last.text + item.text };
    } else {
      out.push(item);
    }
  }
  return out;
}
