// DocNode → DOM for cards, comments, action items and Jira views. Builds nodes with
// createElement/createTextNode/setAttribute only and re-checks every field, because Jira DocNodes
// arrive from the server rather than from parse().

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

const MAX_DEPTH = MAX_BLOCK_DEPTH + MAX_INLINE_DEPTH + 4;

const SIMPLE_TAGS = {
  p: 'p',
  ul: 'ul',
  li: 'li',
  quote: 'blockquote',
  hr: 'hr',
  tr: 'tr',
  th: 'th',
  td: 'td',
  b: 'strong',
  i: 'em',
  s: 's',
  u: 'u',
  br: 'br',
};

const UNKNOWN_MENTION = 'unknown';
const MISSING_IMAGE = 'image (open in Jira)';
const MISSING_ISLAND = 'content unavailable';
const TRUNCATED = 'content truncated';

/**
 * @param {object} doc DocNode with t 'doc'
 * @param {object} [options]
 * @param {Document} [options.document]
 * @param {(id: string) => string | null | undefined} [options.resolveMention] current display name of a user
 * @param {(node: object) => string | null | undefined} [options.imageSrc] same-origin URL for an img or
 *   attachment node; null renders a placeholder. Defaults to /blob/{id} for board images.
 * @param {Map<string, object>} [options.islands] island nodes by id, for refs parsed from markup
 * @returns {DocumentFragment}
 */
export function render(doc, options = {}) {
  const ctx = {
    document: options.document ?? globalThis.document,
    resolveMention: options.resolveMention ?? (() => null),
    imageSrc: options.imageSrc ?? defaultImageSrc,
    islands: options.islands ?? new Map(),
  };
  const fragment = ctx.document.createDocumentFragment();
  appendChildren(ctx, fragment, doc?.c, 1);
  return fragment;
}

function defaultImageSrc(node) {
  return node.t === 'img' ? `/blob/${node.blob}` : null;
}

function appendChildren(ctx, parent, children, depth) {
  if (!Array.isArray(children)) {
    return;
  }
  if (depth > MAX_DEPTH) {
    parent.append(ctx.document.createTextNode(TRUNCATED));
    return;
  }
  for (const child of children) {
    const node = renderNode(ctx, child, depth);
    if (node) {
      parent.append(node);
    }
  }
}

function renderNode(ctx, node, depth) {
  if (node === null || typeof node !== 'object') {
    return null;
  }
  const doc = ctx.document;
  const el = (tag, ...classes) => {
    const e = doc.createElement(tag);
    if (classes.length > 0) {
      e.setAttribute('class', classes.join(' '));
    }
    return e;
  };
  const withChildren = (e) => {
    appendChildren(ctx, e, node.c, depth + 1);
    return e;
  };
  const text = (value) => doc.createTextNode(typeof value === 'string' ? value : '');
  const withText = (e, value) => {
    e.append(text(value));
    return e;
  };

  if (Object.hasOwn(SIMPLE_TAGS, node.t)) {
    return withChildren(el(SIMPLE_TAGS[node.t]));
  }
  switch (node.t) {
    case 'text':
      return text(node.v);
    case 'h': {
      const valid = Number.isInteger(node.level) && node.level >= 1 && node.level <= 6;
      return withChildren(el(valid ? `h${node.level}` : 'p'));
    }
    case 'ol': {
      const ol = el('ol');
      if (Number.isInteger(node.start) && node.start >= 0 && node.start !== 1) {
        ol.setAttribute('start', String(node.start));
      }
      return withChildren(ol);
    }
    case 'pre': {
      const code = el('code');
      if (isLanguage(node.lang)) {
        code.setAttribute('data-lang', node.lang);
      }
      const pre = el('pre');
      pre.append(withText(code, node.v));
      return pre;
    }
    case 'code':
      return withText(el('code'), node.v);
    case 'panel':
      return withChildren(el('div', 'panel', `panel-${PANEL_KINDS.has(node.kind) ? node.kind : 'info'}`));
    case 'table': {
      const table = el('table');
      table.append(withChildren(el('tbody')));
      return table;
    }
    case 'expand': {
      const details = el('details', 'expand');
      details.append(withText(el('summary'), node.title));
      return withChildren(details);
    }
    case 'tasks':
      return withChildren(el('ul', 'tasks'));
    case 'task': {
      const box = el('input');
      box.setAttribute('type', 'checkbox');
      box.setAttribute('disabled', '');
      if (node.done === true) {
        box.setAttribute('checked', '');
      }
      const li = el('li', 'task');
      li.append(box);
      return withChildren(li);
    }
    case 'island':
      return renderIsland(ctx, node, depth, el);
    case 'a': {
      if (!isAllowedHref(node.href)) {
        return withChildren(doc.createDocumentFragment());
      }
      const a = el('a');
      a.setAttribute('href', node.href);
      a.setAttribute('rel', 'noopener noreferrer nofollow');
      a.setAttribute('target', '_blank');
      return withChildren(a);
    }
    case 'mention': {
      const span = el('span', 'mention');
      const name = isUuid(node.id) ? ctx.resolveMention(node.id) : null;
      if (isUuid(node.id)) {
        span.setAttribute('data-mention-id', node.id);
      }
      return withText(span, `@${typeof name === 'string' && name !== '' ? name : UNKNOWN_MENTION}`);
    }
    case 'img':
    case 'attachment':
      return renderImage(ctx, node, el, withText);
    case 'status':
      return withText(el('span', 'status', `status-${STATUS_COLORS.has(node.color) ? node.color : 'neutral'}`), node.v);
    default:
      // Unknown kinds keep their content, never their markup.
      if (Array.isArray(node.c)) {
        return withChildren(doc.createDocumentFragment());
      }
      return typeof node.v === 'string' ? text(node.v) : null;
  }
}

// An island parsed from markup is a bare ref (empty c); its read-only content comes from the
// issue's DocNode through `islands`.
function renderIsland(ctx, node, depth, el) {
  const div = el('div', 'island');
  if (!isIslandId(node.id)) {
    return div;
  }
  div.setAttribute('data-island-id', node.id);
  const content = Array.isArray(node.c) && node.c.length > 0 ? node.c : ctx.islands.get(node.id)?.c;
  if (Array.isArray(content) && content.length > 0) {
    appendChildren(ctx, div, content, depth + 1);
  } else {
    div.setAttribute('class', 'island island-missing');
    div.append(ctx.document.createTextNode(MISSING_ISLAND));
  }
  return div;
}

function renderImage(ctx, node, el, withText) {
  const valid = node.t === 'img' ? isUuid(node.blob) : isAttachmentId(node.id);
  const src = valid ? ctx.imageSrc(node) : null;
  if (typeof src !== 'string' || !isSameOriginPath(src)) {
    return withText(el('span', 'image-missing'), MISSING_IMAGE);
  }
  const img = el('img');
  img.setAttribute('src', src);
  img.setAttribute('alt', typeof node.alt === 'string' ? node.alt : '');
  img.setAttribute('loading', 'lazy');
  return img;
}

// img-src allows 'self' (plus data: and blob:, which no DocNode may name), so only same-origin
// paths load.
function isSameOriginPath(src) {
  return /^\/(?![/\\])/.test(src);
}
