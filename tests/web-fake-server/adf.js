// ADF for the fake Jira's rich description editor routes (UX10, UX12). The real server projects Jira's ADF and
// validates saves (Core/Adf); this fake hands the editor a document, restores tokens on save and rebuilds the read view.
// - Fixture issues may carry `adf`. WebFakeAdfTests checks that it renders to the issue's markup and islands through
//   the real pipeline, and writes its real projection (tokens, opaque parts, originals) to edit-views.gen.json, which
//   the fake serves as the edit view.
// - Other issues get ADF built from their markup Doc.
// - After a rich save the read view is rebuilt from the saved ADF by adfToDoc.

import palette from '../../web/src/rich/palette.json' with { type: 'json' };

export const EMPTY_DOC = Object.freeze({ type: 'doc', version: 1, content: [] });

export const EDIT_LIMITS = Object.freeze({ nodes: 5000, depth: 28, text: 32000, bytes: 1048576 });

const STYLE_MARKS = { b: 'strong', i: 'em', s: 'strike', u: 'underline' };
const MARK_STYLES = { strong: 'b', em: 'i', strike: 's', underline: 'u' };

// Markup Doc -> ADF, for the Doc kinds markup can hold. Islands only appear in issues that carry their own `adf`.
export function docToAdf(doc) {
  return { type: 'doc', version: 1, content: (doc?.c ?? []).map(blockToAdf).filter(Boolean) };
}

function blockToAdf(node) {
  switch (node.t) {
    case 'p':
      return { type: 'paragraph', content: inlineToAdf(node.c ?? [], []) };
    case 'h':
      return { type: 'heading', attrs: { level: node.level }, content: inlineToAdf(node.c ?? [], []) };
    case 'ul':
      return { type: 'bulletList', content: node.c.map(blockToAdf) };
    case 'ol':
      return { type: 'orderedList', attrs: { order: node.start ?? 1 }, content: node.c.map(blockToAdf) };
    case 'li':
      return { type: 'listItem', content: node.c.map(blockToAdf) };
    case 'quote':
      return { type: 'blockquote', content: node.c.map(blockToAdf) };
    case 'pre':
      return { type: 'codeBlock', ...(node.lang ? { attrs: { language: node.lang } } : {}), content: node.v ? [{ type: 'text', text: node.v }] : [] };
    case 'hr':
      return { type: 'rule' };
    default:
      return null;
  }
}

function inlineToAdf(nodes, marks) {
  const out = [];
  for (const node of nodes) {
    if (node.t === 'text' && node.v) {
      out.push({ type: 'text', text: node.v, ...(marks.length ? { marks } : {}) });
    } else if (node.t === 'code') {
      out.push({ type: 'text', text: node.v, marks: [...marks.filter((m) => m.type === 'link'), { type: 'code' }] });
    } else if (node.t === 'br') {
      out.push({ type: 'hardBreak' });
    } else if (STYLE_MARKS[node.t]) {
      out.push(...inlineToAdf(node.c ?? [], [...marks, { type: STYLE_MARKS[node.t] }]));
    } else if (node.t === 'a') {
      out.push(...inlineToAdf(node.c ?? [], [...marks, { type: 'link', attrs: { href: node.href } }]));
    }
  }
  return out;
}

// Saved ADF -> the read view's Doc, in the shapes Core/Adf/AdfReader writes for the kinds the fixtures and the
// editor specs use; adf.test.js pins it to the fixtures' read views. A top-level block that markup cannot hold becomes
// an island, as the server's AdfToDoc does; anything this reader does not know becomes an island of its plain text.
export function adfToDoc(adf, attachments = []) {
  const reader = { attachments };
  return { t: 'doc', c: (adf?.content ?? []).map((node, index) => topBlock(reader, node, index)) };
}

const MARKUP_BLOCKS = new Set(['p', 'h', 'ul', 'ol', 'li', 'quote', 'pre', 'hr']);
const MARKUP_INLINE = new Set(['text', 'b', 'i', 's', 'u', 'code', 'a', 'br']);

function topBlock(reader, node, index) {
  const block = blockToDoc(reader, node);
  if (block && isMarkup(block) && isMarkupAdf(node)) {
    return block;
  }
  return { t: 'island', id: `adf${index}`, c: [block ?? { t: 'p', c: [{ t: 'text', v: plainText(node) }] }] };
}

function isMarkup(node) {
  if (!MARKUP_BLOCKS.has(node.t) && !MARKUP_INLINE.has(node.t)) {
    return false;
  }
  return (node.c ?? []).every(isMarkup);
}

// What markup writes back unchanged (Core/Adf/AdfLossiness, roughly): its node types, attributes and marks only.
const MARKUP_ADF = { paragraph: [], heading: ['level'], bulletList: [], orderedList: ['order'], listItem: [], blockquote: [],
  codeBlock: ['language'], rule: [], text: [], hardBreak: [] };
const MARKUP_MARKS = new Set(['strong', 'em', 'strike', 'underline', 'code', 'link']);

function isMarkupAdf(node) {
  const allowed = MARKUP_ADF[node?.type];
  return allowed !== undefined
    && Object.keys(node.attrs ?? {}).every((key) => allowed.includes(key))
    && (node.marks ?? []).every((mark) => MARKUP_MARKS.has(mark.type))
    && (node.content ?? []).every(isMarkupAdf);
}

const blocks = (reader, list) => (list ?? []).map((n) => blockToDoc(reader, n)).filter(Boolean);

function blockToDoc(reader, node) {
  const attrs = node?.attrs ?? {};
  switch (node?.type) {
    case 'paragraph':
      return { t: 'p', c: inlineToDoc(node.content ?? []) };
    case 'heading':
      return { t: 'h', level: attrs.level ?? 1, c: inlineToDoc(node.content ?? []) };
    case 'bulletList':
      return { t: 'ul', c: blocks(reader, node.content) };
    case 'orderedList':
      return { t: 'ol', start: attrs.order ?? 1, c: blocks(reader, node.content) };
    case 'listItem':
      return { t: 'li', c: blocks(reader, node.content) };
    case 'blockquote':
      return { t: 'quote', c: blocks(reader, node.content) };
    case 'codeBlock':
      return { t: 'pre', ...(attrs.language ? { lang: attrs.language } : {}), v: plainText(node) };
    case 'rule':
      return { t: 'hr' };
    case 'panel':
      return {
        t: 'panel',
        kind: attrs.panelType,
        ...(attrs.panelType === 'custom' && attrs.panelColor ? { color: background(attrs.panelColor) } : {}),
        ...(attrs.panelType === 'custom' && attrs.panelIconText ? { icon: attrs.panelIconText } : {}),
        c: blocks(reader, node.content),
      };
    case 'expand':
    case 'nestedExpand':
      return { t: 'expand', ...(attrs.title ? { title: attrs.title } : {}), c: blocks(reader, node.content) };
    case 'table':
      return { t: 'table', ...(attrs.isNumberColumnEnabled ? { numbered: true } : {}), c: (node.content ?? []).map((row) => tableRow(reader, row)) };
    case 'layoutSection':
      return { t: 'layout', c: (node.content ?? []).map((col) => ({ t: 'col', width: col.attrs?.width ?? 50, c: blocks(reader, col.content) })) };
    case 'taskList':
      return { t: 'tasks', c: (node.content ?? []).map((item) => (item.type === 'taskList'
        ? blockToDoc(reader, item)
        : { t: 'task', done: item.attrs?.state === 'DONE', c: inlineToDoc(item.content ?? []) })) };
    case 'decisionList':
      return { t: 'decisions', c: (node.content ?? []).map((item) => ({ t: 'decision', done: item.attrs?.state === 'DECIDED', c: inlineToDoc(item.content ?? []) })) };
    case 'mediaSingle':
      return mediaSingle(reader, node);
    default:
      return null;
  }
}

function tableRow(reader, row) {
  return {
    t: 'tr',
    c: (row.content ?? []).map((cell) => {
      const attrs = cell.attrs ?? {};
      return {
        t: cell.type === 'tableHeader' ? 'th' : 'td',
        ...(attrs.colspan > 1 ? { colspan: attrs.colspan } : {}),
        ...(attrs.rowspan > 1 ? { rowspan: attrs.rowspan } : {}),
        ...(Array.isArray(attrs.colwidth) ? { colwidth: attrs.colwidth } : {}),
        ...(attrs.background && background(attrs.background) ? { bg: background(attrs.background) } : {}),
        c: blocks(reader, cell.content),
      };
    }),
  };
}

function mediaSingle(reader, node) {
  const media = (node.content ?? []).find((n) => n.type === 'media');
  const caption = (node.content ?? []).find((n) => n.type === 'caption');
  const attachment = reader.attachments.find((a) => a.filename === media?.attrs?.alt);
  if (!attachment) {
    return { t: 'p', c: [{ t: 'text', v: 'image (open in Jira)' }] };
  }
  const attrs = node.attrs ?? {};
  return {
    t: 'media',
    layout: attrs.layout ?? 'center',
    ...(attrs.width ? { width: attrs.width, unit: attrs.widthType === 'pixel' ? 'px' : '%' } : {}),
    ...(caption ? { caption: inlineToDoc(caption.content ?? []) } : {}),
    c: [{ t: 'attachment', id: attachment.id, alt: attachment.filename, ...(media.attrs.width ? { w: media.attrs.width } : {}), ...(media.attrs.height ? { h: media.attrs.height } : {}) }],
  };
}

const textToken = (hex) => (isHex(hex) ? palette.text[hex.toLowerCase()] ?? hex.toLowerCase() : null);
const background = (hex) => (isHex(hex) ? palette.background[hex.toLowerCase()] ?? hex.toLowerCase() : null);
const isHex = (v) => typeof v === 'string' && /^#[0-9a-fA-F]{6}$/.test(v);

function day(timestamp) {
  const ms = Number(timestamp);
  return Number.isFinite(ms) ? new Date(ms).toISOString().slice(0, 10) : null;
}

// Marks wrap outermost-first in the order AdfReader nests them: style marks outside colours, links outermost.
const MARK_ORDER = ['link', 'strong', 'em', 'underline', 'strike', 'subsup', 'textColor', 'backgroundColor'];

function inlineToDoc(nodes) {
  const out = [];
  for (const node of nodes) {
    const attrs = node.attrs ?? {};
    let piece;
    switch (node.type) {
      case 'hardBreak':
        out.push({ t: 'br' });
        continue;
      case 'status':
        piece = { t: 'status', v: attrs.text, color: attrs.color };
        break;
      case 'date':
        piece = { t: 'date', v: day(attrs.timestamp) };
        break;
      case 'mention':
        piece = { t: 'user', v: attrs.text || '@' };
        break;
      case 'text': {
        if (!node.text) {
          continue;
        }
        const marks = node.marks ?? [];
        piece = marks.some((m) => m.type === 'code') ? { t: 'code', v: node.text } : { t: 'text', v: node.text };
        break;
      }
      default: {
        const text = plainText(node);
        if (!text) {
          continue;
        }
        piece = { t: 'text', v: text };
      }
    }
    const marks = [...(node.marks ?? [])].filter((m) => MARK_ORDER.includes(m.type))
      .sort((a, b) => MARK_ORDER.indexOf(b.type) - MARK_ORDER.indexOf(a.type));
    for (const mark of marks) {
      piece = wrap(mark, piece);
    }
    const last = out.at(-1);
    if (piece.t === 'text' && last?.t === 'text') {
      last.v += piece.v;
    } else {
      out.push(piece);
    }
  }
  return out;
}

function wrap(mark, piece) {
  switch (mark.type) {
    case 'link':
      return typeof mark.attrs?.href === 'string' ? { t: 'a', href: mark.attrs.href, c: [piece] } : piece;
    case 'textColor':
      return textToken(mark.attrs?.color) ? { t: 'fg', color: textToken(mark.attrs.color), c: [piece] } : piece;
    case 'backgroundColor':
      return background(mark.attrs?.color) ? { t: 'bg', color: background(mark.attrs.color), c: [piece] } : piece;
    case 'subsup':
      return { t: mark.attrs?.type === 'sup' ? 'sup' : 'sub', c: [piece] };
    default:
      return { t: MARK_STYLES[mark.type], c: [piece] };
  }
}

function plainText(node) {
  if (node?.type === 'text') {
    return node.text ?? '';
  }
  if (node?.type === 'mention' || node?.type === 'status' || node?.type === 'emoji' || /xtension$/.test(node?.type ?? '')) {
    return node.attrs?.text ?? node.attrs?.shortName ?? '';
  }
  return (node?.content ?? []).map(plainText).join('');
}

// The edit view's media table: each file media node and the attachment its filename (alt) names.
export function mediaTable(adf, attachments) {
  const out = [];
  const seen = new Set();
  const walk = (node) => {
    if (node?.type === 'media' && (node.attrs?.type === 'file' || node.attrs?.type === 'link') && !seen.has(node.attrs.id)) {
      seen.add(node.attrs.id);
      const attachment = attachments.find((a) => a.filename === node.attrs.alt);
      out.push({
        id: node.attrs.id,
        attachmentId: attachment?.id ?? null,
        name: attachment?.filename ?? null,
        image: /^image\/(png|jpeg|gif|webp)$/.test(attachment?.mimeType ?? ''),
        w: node.attrs.width ?? null,
        h: node.attrs.height ?? null,
      });
    }
    (node?.content ?? []).forEach(walk);
  };
  walk(adf);
  return out;
}

const isToken = (name) => typeof name === 'string' && /^rs[A-Z]/.test(name);

// Whether a save names any token: refused as stale for an issue the fake has no originals for.
export function hasTokens(node) {
  if (Array.isArray(node)) {
    return node.some(hasTokens);
  }
  if (!node || typeof node !== 'object') {
    return false;
  }
  if (isToken(node.type)) {
    return true;
  }
  if (node.attrs && typeof node.attrs === 'object' && 'rsKeep' in node.attrs) {
    return true;
  }
  return hasTokens(node.content) || hasTokens(node.marks);
}

/**
 * Writes a save as the description's JSON text with every token replaced by its original bytes, the way
 * Core/Adf/AdfWrite does: the i-th use of an opaque ref takes the i-th original (one use too many is
 * opaque_duplicated), a mark ref takes the i-th original or the last, and an rsKeep stash fills in the members the
 * client did not set. A ref the originals do not hold answers stale. Nothing else is validated.
 * @returns {{text: string} | {stale: true} | {code: string, at: string}}
 */
export function restoreTokens(adf, originals) {
  const used = new Map();
  const take = (kind, ref) => {
    const raws = originals[kind]?.[ref];
    if (!raws) {
      throw { stale: true };
    }
    const key = `${kind}:${ref}`;
    const n = used.get(key) ?? 0;
    used.set(key, n + 1);
    return { raws, n };
  };
  const write = (value, path) => {
    if (Array.isArray(value)) {
      return `[${value.map((item, i) => write(item, [...path, i])).join(',')}]`;
    }
    if (value === null || typeof value !== 'object') {
      return JSON.stringify(value);
    }
    if (value.type === 'rsOpaque' || value.type === 'rsOpaqueInline') {
      const { raws, n } = take('opaque', value.attrs?.ref);
      if (n >= raws.length) {
        throw { code: 'opaque_duplicated', at: path.join('.') };
      }
      return raws[n];
    }
    if (value.type === 'rsMark') {
      const { raws, n } = take('marks', value.attrs?.ref);
      return raws[Math.min(n, raws.length - 1)];
    }
    const members = [];
    for (const [name, member] of Object.entries(value)) {
      if (name === 'attrs' && member && typeof member === 'object' && 'rsKeep' in member) {
        const stash = originals.keeps?.[member.rsKeep];
        if (!stash) {
          throw { stale: true };
        }
        const attrs = Object.entries(member).filter(([key]) => key !== 'rsKeep')
          .map(([key, v]) => `${JSON.stringify(key)}:${write(v, [...path, 'attrs', key])}`);
        for (const [key, raw] of Object.entries(stash)) {
          if (!(key in member)) {
            attrs.push(`${JSON.stringify(key)}:${raw}`);
          }
        }
        members.push(`"attrs":{${attrs.join(',')}}`);
      } else {
        members.push(`${JSON.stringify(name)}:${write(member, [...path, name])}`);
      }
    }
    return `{${members.join(',')}}`;
  };
  try {
    return { text: write(adf, []) };
  } catch (refusal) {
    if (refusal instanceof Error) {
      throw refusal;
    }
    return refusal;
  }
}

/** The token refs a projected document uses, so an edit view lists only the parts it still holds. */
export function tokenRefs(node, refs = new Set()) {
  if (Array.isArray(node)) {
    node.forEach((item) => tokenRefs(item, refs));
  } else if (node && typeof node === 'object') {
    if (isToken(node.type) && typeof node.attrs?.ref === 'string') {
      refs.add(node.attrs.ref);
    }
    tokenRefs(node.content, refs);
    tokenRefs(node.marks, refs);
  }
  return refs;
}
