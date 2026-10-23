// Node and mark views (UX12): the editor draws the document with the read view's jira.css classes, so editing
// looks like reading. Colours, widths and spans reach the DOM through the CSSOM only (style-src 'self'), and the
// server's tokens draw as read-only chips and blocks named by the edit view's labels and previews.

import { toggleTaskState } from './commands.js';
import palette from './palette.json' with { type: 'json' };

const PANEL_ICONS = { info: 'info', note: 'clipboard-list', tip: 'lightbulb', success: 'circle-check', warning: 'triangle-alert', error: 'circle-x' };
const MEDIA_LAYOUTS = new Set(['center', 'wide', 'full-width', 'align-start', 'align-end', 'wrap-left', 'wrap-right']);
const STATUS_COLORS = new Set(['neutral', 'purple', 'blue', 'red', 'yellow', 'green']);

/** A palette hex as its theme token (light and dark, from jira.css), any other #rrggbb as itself. */
export function themedColor(hex, kind) {
  if (typeof hex !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(hex)) {
    return null;
  }
  const token = palette[kind][hex.toLowerCase()];
  return token ? `var(--jc-${token})` : hex.toLowerCase();
}

function el(tag, className, attrs = {}) {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  for (const [key, value] of Object.entries(attrs)) {
    if (value !== null && value !== undefined) {
      node.setAttribute(key, value);
    }
  }
  return node;
}

function setStyle(node, property, value) {
  if (value === null || value === undefined) {
    node.style.removeProperty(property);
  } else {
    node.style.setProperty(property, value);
  }
}

// A read-only piece inside editable content: never a target for the caret or the IME.
const atom = (tag, className) => el(tag, className, { contenteditable: 'false' });

/** "2026-01-02"-style label of an ADF timestamp (ms of a UTC day), in the reader's locale. */
function dayLabel(timestamp) {
  const ms = Number(timestamp);
  if (!Number.isFinite(ms)) {
    return String(timestamp ?? '');
  }
  return new Date(ms).toLocaleDateString(undefined, { timeZone: 'UTC', dateStyle: 'medium' });
}

function docText(node) {
  if (!node || typeof node !== 'object') {
    return '';
  }
  return (typeof node.v === 'string' ? node.v : '') + (Array.isArray(node.c) ? node.c.map(docText).join(' ') : '');
}

function cardLabel(href) {
  try {
    const url = new URL(href);
    const issue = /^\/browse\/([A-Z][A-Z0-9_]*-\d+)\/?$/.exec(url.pathname);
    return issue ? issue[1] : `${url.host}${url.pathname === '/' ? '' : url.pathname}`;
  } catch {
    return String(href ?? '');
  }
}

/**
 * @param {object} context
 * @param {Map<string, {kind: string, label: string, preview: object}>} context.opaque the edit view's parts by ref
 * @param {Map<string, {attachmentId: string|null, name: string|null, image: boolean}>} context.media by media id
 * @param {(attachmentId: string) => string} context.imageSrc
 * @param {(doc: object) => Node} [context.renderDoc] a read-only rendering of a Doc (an opaque part's preview)
 * @param {(name: string) => Node} [context.icon]
 */
export function nodeViews(context) {
  const glyph = (name, className) => {
    const span = el('span', className, { 'aria-hidden': 'true' });
    if (context.icon) {
      span.append(context.icon(name));
    }
    return span;
  };

  return {
    rsOpaque(node) {
      const part = context.opaque.get(node.attrs.ref);
      const dom = atom('div', 'rich-opaque');
      dom.dataset.kind = part?.kind ?? 'unknown';
      const head = el('div', 'rich-opaque-head');
      head.append(glyph('puzzle', 'rich-opaque-icon'), el('span', 'rich-opaque-label'));
      head.lastChild.textContent = part?.label ?? 'Content Jira keeps';
      const note = el('span', 'rich-opaque-note');
      note.textContent = 'Kept as it is';
      head.append(note);
      dom.append(head);
      // An extension's preview is often just its fallback text, which the label already shows.
      if (part?.preview && context.renderDoc && docText(part.preview).trim() !== (part.label ?? '').trim()) {
        const preview = el('div', 'rich-opaque-preview');
        preview.append(context.renderDoc(part.preview));
        dom.append(preview);
      }
      dom.title = `${part?.label ?? 'This part'} is edited in Jira; it is saved back unchanged.`;
      return { dom, ignoreMutation: () => true, stopEvent: () => false };
    },

    rsOpaqueInline(node) {
      const part = context.opaque.get(node.attrs.ref);
      const dom = atom('span', 'rich-opaque-chip');
      dom.dataset.kind = part?.kind ?? 'unknown';
      dom.append(glyph('puzzle', 'rich-opaque-icon'), document.createTextNode(part?.label ?? 'Jira content'));
      dom.title = `${part?.label ?? 'This part'} is edited in Jira; it is saved back unchanged.`;
      return { dom, ignoreMutation: () => true };
    },

    panel(node) {
      const kind = node.attrs.panelType;
      const dom = el('div', `panel panel-${kind}`);
      if (kind === 'custom') {
        setStyle(dom, 'background-color', themedColor(node.attrs.panelColor, 'background'));
        const emoji = atom('span', 'panel-icon panel-emoji');
        emoji.textContent = node.attrs.panelIconText ?? '';
        dom.append(emoji);
      } else {
        const icon = glyph(PANEL_ICONS[kind] ?? 'info', 'panel-icon');
        icon.setAttribute('contenteditable', 'false');
        dom.append(icon);
      }
      const contentDOM = el('div', 'panel-body');
      dom.append(contentDOM);
      return { dom, contentDOM };
    },

    expand: expandView,
    nestedExpand: expandView,

    table(node) {
      const dom = el('div', 'table-wrap');
      const table = el('table');
      const contentDOM = el('tbody');
      table.append(contentDOM);
      dom.append(table);
      const paint = (n) => {
        table.classList.toggle('is-numbered', n.attrs.isNumberColumnEnabled === true);
      };
      paint(node);
      return {
        dom,
        contentDOM,
        update(next) {
          if (next.type !== node.type) {
            return false;
          }
          paint(next);
          return true;
        },
        ignoreMutation: (mutation) => mutation.type === 'attributes' && mutation.target === table,
      };
    },

    tableCell: cellView,
    tableHeader: cellView,

    layoutColumn(node) {
      const dom = el('div', 'layout-col');
      setStyle(dom, 'flex-grow', node.attrs.width > 0 ? String(node.attrs.width) : null);
      return { dom, contentDOM: dom };
    },

    taskItem(node, view, getPos) {
      const dom = el('li', 'task');
      const box = el('input', null, { type: 'checkbox', contenteditable: 'false', 'aria-label': 'Done' });
      box.checked = node.attrs.state === 'DONE';
      box.addEventListener('mousedown', (event) => event.preventDefault());
      box.addEventListener('click', (event) => {
        event.preventDefault();
        const pos = getPos();
        if (pos !== undefined && view.editable) {
          toggleTaskState(pos)(view.state, view.dispatch);
        }
      });
      const contentDOM = el('span', 'task-text');
      dom.append(box, contentDOM);
      return {
        dom,
        contentDOM,
        update(next) {
          if (next.type !== node.type) {
            return false;
          }
          box.checked = next.attrs.state === 'DONE';
          return true;
        },
        stopEvent: (event) => event.target === box,
        ignoreMutation: (mutation) => mutation.target === box,
      };
    },

    decisionItem(node) {
      const dom = el('li', node.attrs.state === 'DECIDED' ? 'decision is-decided' : 'decision');
      const icon = glyph('split', 'decision-icon');
      icon.setAttribute('contenteditable', 'false');
      const contentDOM = el('span', 'decision-text');
      dom.append(icon, contentDOM);
      return { dom, contentDOM, update: (next) => next.type === node.type && next.attrs.state === node.attrs.state };
    },

    mediaSingle(node) {
      const layout = MEDIA_LAYOUTS.has(node.attrs.layout) ? node.attrs.layout : 'center';
      const dom = el('figure', `media layout-${layout}`);
      const width = node.attrs.width;
      if (typeof width === 'number' && width > 0) {
        setStyle(dom, 'width', node.attrs.widthType === 'pixel' ? `${Math.min(width, 10000)}px` : `${Math.min(width, 100)}%`);
      }
      return { dom, contentDOM: dom };
    },

    media(node) {
      const entry = context.media.get(node.attrs.id);
      const dom = atom('span', 'rich-media');
      if (entry?.attachmentId && entry.image) {
        const img = el('img', null, { alt: node.attrs.alt ?? entry.name ?? '', draggable: 'false' });
        img.src = context.imageSrc(entry.attachmentId);
        dom.append(img);
      } else {
        dom.classList.add('file-chip');
        dom.append(glyph('paperclip', 'file-icon'), document.createTextNode(entry?.name ?? node.attrs.alt ?? 'file'));
      }
      return { dom, ignoreMutation: () => true };
    },

    status(node) {
      const color = STATUS_COLORS.has(node.attrs.color) ? node.attrs.color : 'neutral';
      const dom = atom('span', `status status-${color}${node.attrs.style === 'bold' ? ' is-bold' : ''}`);
      dom.textContent = node.attrs.text ?? '';
      return { dom, ignoreMutation: () => true };
    },

    date(node) {
      const dom = atom('time', 'date-lozenge');
      dom.textContent = dayLabel(node.attrs.timestamp);
      return { dom, ignoreMutation: () => true };
    },

    mention(node) {
      const dom = atom('span', 'jira-user');
      dom.textContent = node.attrs.text || '@';
      return { dom, ignoreMutation: () => true };
    },

    emoji(node) {
      const dom = atom('span', 'rich-emoji');
      dom.textContent = node.attrs.text || node.attrs.shortName;
      return { dom, ignoreMutation: () => true };
    },

    inlineCard: cardView(false),
    blockCard: cardView(true),
    embedCard: cardView(true),
  };

  function cardView(block) {
    return (node) => {
      const dom = atom(block ? 'div' : 'span', block ? 'smart-card is-block' : 'smart-card');
      dom.append(glyph('link', 'card-icon'), document.createTextNode(cardLabel(node.attrs.url)));
      dom.title = node.attrs.url;
      return { dom, ignoreMutation: () => true };
    };
  }

  // Collapse is the editor's own view state and is never saved, as in Jira.
  function expandView(node, view, getPos) {
    const dom = el('div', 'rich-expand is-open');
    const head = atom('div', 'rich-expand-head');
    const toggle = el('button', 'rich-expand-toggle', { type: 'button', 'aria-label': 'Collapse', 'aria-expanded': 'true' });
    const title = el('input', 'rich-expand-title', { type: 'text', placeholder: 'Give this expand a title…', 'aria-label': 'Expand title', maxlength: '255' });
    title.value = node.attrs.title ?? '';
    head.append(toggle, title);
    const contentDOM = el('div', 'expand-body');
    dom.append(head, contentDOM);
    toggle.addEventListener('click', () => {
      const open = !dom.classList.contains('is-open');
      dom.classList.toggle('is-open', open);
      toggle.setAttribute('aria-expanded', String(open));
      toggle.setAttribute('aria-label', open ? 'Collapse' : 'Expand');
    });
    title.addEventListener('input', () => {
      const pos = getPos();
      if (pos === undefined) {
        return;
      }
      const text = title.value;
      view.dispatch(view.state.tr.setNodeAttribute(pos, 'title', text === '' ? null : text));
    });
    title.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' || event.key === 'ArrowDown') {
        event.preventDefault();
        view.focus();
      }
    });
    let current = node;
    return {
      dom,
      contentDOM,
      update(next) {
        if (next.type !== current.type) {
          return false;
        }
        current = next;
        if (document.activeElement !== title) {
          title.value = next.attrs.title ?? '';
        }
        return true;
      },
      stopEvent: (event) => head.contains(event.target),
      ignoreMutation: (mutation) => head.contains(mutation.target),
    };
  }

  function cellView(node) {
    const dom = el(node.type.name === 'tableHeader' ? 'th' : 'td');
    const paint = (n) => {
      for (const name of ['colspan', 'rowspan']) {
        if (n.attrs[name] > 1) {
          dom.setAttribute(name, String(n.attrs[name]));
        } else {
          dom.removeAttribute(name);
        }
      }
      setStyle(dom, 'background-color', themedColor(n.attrs.background, 'background'));
      const width = Array.isArray(n.attrs.colwidth) && n.attrs.colwidth.length === 1 ? n.attrs.colwidth[0] : null;
      setStyle(dom, 'width', width ? `${width}px` : null);
    };
    paint(node);
    return {
      dom,
      contentDOM: dom,
      update(next) {
        if (next.type !== node.type) {
          return false;
        }
        paint(next);
        return true;
      },
      ignoreMutation: (mutation) => mutation.type === 'attributes' && mutation.target === dom,
    };
  }
}

/** Mark views for the colour marks: the theme's token for a palette hex, through the CSSOM. */
export function markViews() {
  const colored = (property, kind, className) => (mark) => {
    const dom = el('span', className);
    setStyle(dom, property, themedColor(mark.attrs.color, kind));
    return { dom };
  };
  return {
    textColor: colored('color', 'text', 'text-color'),
    backgroundColor: colored('background-color', 'background', 'highlight'),
    link(mark) {
      const dom = el('a', 'rich-link', { href: mark.attrs.href, title: mark.attrs.href, rel: 'noopener noreferrer nofollow' });
      return { dom };
    },
    rsMark() {
      const dom = el('span', 'rich-opaque-mark', { title: 'Jira keeps a comment or other mark on this text' });
      return { dom };
    },
  };
}

