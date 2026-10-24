// A test stand-in for the server's edit projection (Core/Adf/AdfEditProjection, server-path §1.3): whatever the
// editor's strict reader refuses becomes a token. An unknown attribute moves into the rsKeep stash, a refused mark
// becomes rsMark, and any other refused node becomes rsOpaque (rsOpaqueInline in inline content). Refs are the
// server's form, the first 12 bytes of the sha256 of the canonical JSON as base64url, so they are real-looking
// and stable. Driven by the reader's own errors, it yields exactly the documents the editor must accept.

import { createHash } from 'node:crypto';
import { AdfError, adfToPm } from '../../../web/src/rich/adf.js';
import { allowList } from '../../../web/src/rich/rules.js';
import { TOKEN_NODES, childTypes } from '../../../web/src/rich/schema.js';

function canonical(value) {
  if (Array.isArray(value)) {
    return `[${value.map(canonical).join(',')}]`;
  }
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonical(value[k])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

export function ref(value) {
  return createHash('sha256').update(canonical(value)).digest().subarray(0, 12).toString('base64url');
}

const isInlineContext = (parent) => parent.type !== 'doc' && Object.hasOwn(allowList.nodes, parent.type)
  && childTypes(parent.type).includes('text');

function locate(doc, path) {
  const segments = path === '' ? [] : path.split('.');
  const chain = [{ node: doc, container: null, index: -1 }];
  let i = 0;
  while (i + 1 < segments.length && segments[i] === 'content' && /^\d+$/.test(segments[i + 1])) {
    const parent = chain[chain.length - 1].node;
    const index = Number(segments[i + 1]);
    chain.push({ node: parent.content[index], container: parent.content, index, parent });
    i += 2;
  }
  return { chain, rest: segments.slice(i) };
}

function tokenise(chain) {
  for (let depth = chain.length - 1; depth > 0; depth--) {
    const { node, container, index, parent } = chain[depth];
    if (node !== null && typeof node === 'object' && TOKEN_NODES.has(node.type)) {
      continue;
    }
    container[index] = {
      type: isInlineContext(parent) ? 'rsOpaqueInline' : 'rsOpaque',
      attrs: { ref: ref(node) },
    };
    return;
  }
  throw new Error('the document itself cannot be projected');
}

function fix(doc, error) {
  const { chain, rest } = locate(doc, error.path);
  const { node } = chain[chain.length - 1];
  if (rest[0] === 'marks' && rest.length >= 2 && node.marks?.[Number(rest[1])]?.type !== 'rsMark') {
    const mark = node.marks[Number(rest[1])];
    node.marks[Number(rest[1])] = { type: 'rsMark', attrs: { ref: ref(mark) } };
    return;
  }
  if (rest[0] === 'attrs' && error.code === 'unknown_attr' && chain.length > 1) {
    const { [rest[1]]: moved, ...kept } = node.attrs;
    const stash = { ...(node.rsStash ?? {}), [rest[1]]: moved };
    Object.defineProperty(node, 'rsStash', { value: stash, enumerable: false, configurable: true });
    node.attrs = { ...kept, rsKeep: ref(stash) };
    return;
  }
  tokenise(chain);
}

/** The projection of a raw ADF doc: what the server's edit view would send for it. */
export function project(adf) {
  const doc = structuredClone(adf);
  for (let round = 0; round < 1000; round++) {
    try {
      adfToPm(doc);
      return doc;
    } catch (error) {
      if (!(error instanceof AdfError)) {
        throw error;
      }
      fix(doc, error);
    }
  }
  throw new Error('projection did not settle');
}

/** Every token ref in an ADF value, with how often each appears. */
export function tokenRefs(adf) {
  const counts = new Map();
  const walk = (v) => {
    if (Array.isArray(v)) {
      v.forEach(walk);
    } else if (v !== null && typeof v === 'object') {
      if ((TOKEN_NODES.has(v.type) || v.type === 'rsMark') && typeof v.attrs?.ref === 'string') {
        counts.set(v.attrs.ref, (counts.get(v.attrs.ref) ?? 0) + 1);
      }
      Object.values(v).forEach(walk);
    }
  };
  walk(adf);
  return counts;
}
