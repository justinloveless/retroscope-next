// ADF for the fake Jira's rich description editor routes (UX10). The real server projects Jira's ADF and validates
// saves (Core/Adf); this fake only needs a document to hand the editor and a read view after a save.
// - Fixture issues may carry `adf`; WebFakeAdfTests checks that it renders to the issue's markup and islands through
//   the real pipeline, and that it projects to itself, so the fake serves it unchanged.
// - Other issues get ADF built from their markup Doc.
// - After a rich save the read view is rebuilt from the saved ADF: markup kinds keep their shape; any other block
//   becomes an island holding its plain text.

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

// Saved ADF -> the read view's Doc.
export function adfToDoc(adf) {
  return { t: 'doc', c: (adf?.content ?? []).map(blockToDoc) };
}

function blockToDoc(node, index) {
  switch (node?.type) {
    case 'paragraph':
      return { t: 'p', c: inlineToDoc(node.content ?? []) };
    case 'heading':
      return { t: 'h', level: node.attrs?.level ?? 1, c: inlineToDoc(node.content ?? []) };
    case 'bulletList':
      return { t: 'ul', c: (node.content ?? []).map(blockToDoc) };
    case 'orderedList':
      return { t: 'ol', start: node.attrs?.order ?? 1, c: (node.content ?? []).map(blockToDoc) };
    case 'listItem':
      return { t: 'li', c: (node.content ?? []).map(blockToDoc) };
    case 'blockquote':
      return { t: 'quote', c: (node.content ?? []).map(blockToDoc) };
    case 'codeBlock':
      return { t: 'pre', ...(node.attrs?.language ? { lang: node.attrs.language } : {}), v: plainText(node) };
    case 'rule':
      return { t: 'hr' };
    default:
      return { t: 'island', id: `adf${index}`, c: [{ t: 'p', c: [{ t: 'text', v: plainText(node) }] }] };
  }
}

function inlineToDoc(nodes) {
  const out = [];
  for (const node of nodes) {
    if (node.type === 'hardBreak') {
      out.push({ t: 'br' });
      continue;
    }
    const text = node.type === 'text' ? node.text : plainText(node);
    if (!text) {
      continue;
    }
    const marks = node.marks ?? [];
    let piece = marks.some((m) => m.type === 'code') ? { t: 'code', v: text } : { t: 'text', v: text };
    for (const mark of marks) {
      if (MARK_STYLES[mark.type]) {
        piece = { t: MARK_STYLES[mark.type], c: [piece] };
      } else if (mark.type === 'link' && typeof mark.attrs?.href === 'string') {
        piece = { t: 'a', href: mark.attrs.href, c: [piece] };
      }
    }
    out.push(piece);
  }
  return out;
}

function plainText(node) {
  if (node?.type === 'text') {
    return node.text ?? '';
  }
  if (node?.type === 'mention' || node?.type === 'status' || node?.type === 'emoji') {
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

// The fake's edit views never hold tokens, so a save naming one started from another version.
export function hasTokens(node) {
  if (Array.isArray(node)) {
    return node.some(hasTokens);
  }
  if (!node || typeof node !== 'object') {
    return false;
  }
  if (typeof node.type === 'string' && /^rs[A-Z]/.test(node.type)) {
    return true;
  }
  if (node.attrs && typeof node.attrs === 'object' && 'rsKeep' in node.attrs) {
    return true;
  }
  return hasTokens(node.content) || hasTokens(node.marks);
}
