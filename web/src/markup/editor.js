// The shared markup editor (DESIGN §g.6): a contenteditable region whose DOM is
// render(parse(markup)). Native typing edits the DOM; every input is serialized back to canonical
// markup through an allow-list walk, and the DOM is re-rendered only when it holds something the
// renderer would not produce. Line breaks, atom deletion, paste, formatting and undo are done here
// instead of by the browser, so every browser produces the same markup.

import { parse } from './parse.js';
import { render } from './render.js';
import {
  DIALECT_JIRA,
  DIALECT_JIRA_DESCRIPTION,
  DIALECT_RETRO,
  isAllowedHref,
  isIslandId,
  isUuid,
  toMarkup,
} from './doc.js';
import { isAtom, isBlock, isFillerBreak, itemBeside, readSelection, scan, writeSelection } from './caret.js';
import { createHistory } from './undo.js';

const MARK_TAGS = { strong: 'b', b: 'b', em: 'i', i: 'i', s: 's', strike: 's', del: 's' };
const CANONICAL_MARK_TAGS = new Set(['strong', 'em', 's']);
const HEADINGS = new Set(['h1', 'h2', 'h3', 'h4', 'h5', 'h6']);
const MAX_SUGGESTIONS = 8;
const MENTION_QUERY = /(?:^|[\s(])@([^\s@[\]]{0,32})$/;
const WHITESPACE = /\s/;
let nextId = 0;

/**
 * Serializes an editor DOM to a DocNode with the dialect's allow-list. `dirty` is true when the
 * DOM holds anything render() would not have produced, so the caller re-renders it.
 *
 * @param {Element} root
 * @param {{dialect?: string, atoms?: WeakMap<Element, object>}} [options]
 * @returns {{doc: object, dirty: boolean}}
 */
export function domToDoc(root, { dialect = DIALECT_RETRO, atoms = new WeakMap() } = {}) {
  const retro = dialect === DIALECT_RETRO;
  const jira = dialect === DIALECT_JIRA || dialect === DIALECT_JIRA_DESCRIPTION;
  let dirty = false;

  const blocks = (parent) => {
    const out = [];
    let run = [];
    const flush = () => {
      const c = normalizeInlines(run);
      if (c.length > 0) {
        out.push({ t: 'p', c });
      }
      run = [];
    };
    for (const child of parent.childNodes) {
      if (child.nodeType === 1 && isBlock(child)) {
        flush();
        out.push(...block(child));
      } else if (child.nodeType === 3 || child.nodeType === 1) {
        if (child.nodeType === 1 || child.data.trim() !== '') {
          dirty = true;
        }
        run.push(...inlines(child, new Set()));
      }
    }
    flush();
    return out;
  };

  const block = (el) => {
    if (el.hasAttribute('style')) {
      dirty = true;
    }
    const tag = el.localName;
    if (isAtom(el) || el.hasAttribute('data-island-id')) {
      return atomBlock(el);
    }
    if (tag === 'p' || (HEADINGS.has(tag) && jira)) {
      if ([...el.childNodes].some((c) => c.nodeType === 1 && isBlock(c))) {
        dirty = true;
        return blocks(el);
      }
      const c = normalizeInlines([...el.childNodes].flatMap((child) => inlines(child, new Set())));
      if (c.length === 0) {
        return [];
      }
      return [tag === 'p' ? { t: 'p', c } : { t: 'h', level: Number(tag[1]), c }];
    }
    if (tag === 'ul' || tag === 'ol') {
      const items = [];
      for (const child of el.childNodes) {
        if (child.nodeType === 1 && child.localName === 'li') {
          items.push({ t: 'li', c: blocks(child) });
        } else if (child.nodeType === 1 || child.data.trim() !== '') {
          dirty = true;
          items.push({ t: 'li', c: blocks({ childNodes: [child] }) });
        }
      }
      const list = { t: tag, c: items.filter((item) => item.c.length > 0) };
      if (tag === 'ol') {
        const start = Number(el.getAttribute('start') ?? 1);
        list.start = Number.isInteger(start) && start >= 0 ? start : 1;
      }
      return list.c.length > 0 ? [list] : [];
    }
    if (tag === 'blockquote') {
      const c = blocks(el);
      return c.length > 0 ? [{ t: 'quote', c }] : [];
    }
    if (tag === 'pre') {
      const v = preText(el).replace(/\n$/, '');
      const lang = el.querySelector('code')?.getAttribute('data-lang');
      return v.trim() === '' ? [] : [lang ? { t: 'pre', lang, v } : { t: 'pre', v }];
    }
    dirty = true;
    return blocks(el);
  };

  const atomBlock = (el) => {
    const known = atoms.get(el);
    if (el.localName === 'hr') {
      dirty ||= !jira;
      return jira ? [{ t: 'hr' }] : [];
    }
    const id = known?.id ?? el.getAttribute('data-island-id');
    if (dialect === DIALECT_JIRA_DESCRIPTION && isIslandId(id)) {
      if (!known) {
        dirty = true;
      }
      return [{ t: 'island', id, c: [] }];
    }
    dirty = true;
    return [];
  };

  const inlines = (node, active) => {
    if (node.nodeType === 3) {
      return node.data === '' ? [] : [{ t: 'text', v: node.data.replaceAll('\u00a0', ' ') }];
    }
    if (node.nodeType !== 1) {
      return [];
    }
    if (node.hasAttribute('style')) {
      dirty = true;
    }
    const tag = node.localName;
    if (tag === 'br') {
      return isFillerBreak(node) ? [] : [{ t: 'br' }];
    }
    if (isAtom(node) || tag === 'img' || node.hasAttribute('data-mention-id')) {
      return inlineAtom(node);
    }
    if (isBlock(node)) {
      dirty = true;
      return [{ t: 'br' }, ...[...node.childNodes].flatMap((child) => inlines(child, active))];
    }
    const children = () => [...node.childNodes].flatMap((child) => inlines(child, active));
    if (Object.hasOwn(MARK_TAGS, tag)) {
      if (!CANONICAL_MARK_TAGS.has(tag)) {
        dirty = true;
      }
      const t = MARK_TAGS[tag];
      if (active.has(t)) {
        return children();
      }
      const inner = [...node.childNodes].flatMap((child) => inlines(child, new Set([...active, t])));
      return [{ t, c: inner }];
    }
    if (tag === 'code') {
      if ([...node.childNodes].some((child) => child.nodeType !== 3)) {
        dirty = true;
      }
      return [{ t: 'code', v: node.textContent.replaceAll('\u00a0', ' ') }];
    }
    if (tag === 'a') {
      const href = node.getAttribute('href');
      if (!isAllowedHref(href) || active.has('a')) {
        dirty = true;
        return children();
      }
      const inner = [...node.childNodes].flatMap((child) => inlines(child, new Set([...active, 'a'])));
      return [{ t: 'a', href, c: inner }];
    }
    dirty = true;
    return children();
  };

  const inlineAtom = (el) => {
    const known = atoms.get(el);
    if (known && ((known.t === 'mention' || known.t === 'img') === retro)) {
      return [{ ...known }];
    }
    dirty = true;
    const id = el.getAttribute('data-mention-id');
    if (retro && isUuid(id)) {
      const name = el.textContent.replace(/^@/, '').replace(/[\]\n]/g, '').slice(0, 100);
      return name === '' ? [] : [{ t: 'mention', id, name }];
    }
    const blob = /^\/blob\/([0-9a-f-]{36})$/.exec(el.getAttribute('src') ?? '')?.[1];
    if (retro && isUuid(blob)) {
      return [{ t: 'img', blob, alt: 'image' }];
    }
    return [];
  };

  const doc = { t: 'doc', c: blocks(root) };
  return { doc, dirty };
}

function preText(el) {
  let text = '';
  for (const child of el.childNodes) {
    if (child.nodeType === 3) {
      text += child.data;
    } else if (child.localName === 'br') {
      text += '\n';
    } else if (child.nodeType === 1) {
      text += preText(child);
    }
  }
  return text;
}

// Inline lists as the parser would produce them: no empty nodes, adjacent text and equal marks
// merged, and whitespace moved outside marks (the writer cannot open "** a**").
function normalizeInlines(list) {
  const out = [];
  const push = (node) => {
    const last = out[out.length - 1];
    if (node.t === 'text') {
      if (node.v === '') {
        return;
      }
      if (last?.t === 'text') {
        last.v += node.v;
        return;
      }
    } else if (node.c) {
      if (last && last.t === node.t && last.href === node.href && last.c) {
        out.pop();
        push({ ...last, c: normalizeInlines([...last.c, ...node.c]) });
        return;
      }
    } else if (node.t === 'code' && node.v === '') {
      return;
    }
    out.push(node);
  };
  for (const node of list) {
    if (!node.c) {
      push({ ...node });
      continue;
    }
    const c = normalizeInlines(node.c);
    const lead = c[0]?.t === 'text' ? /^\s+/.exec(c[0].v)?.[0] : undefined;
    if (lead) {
      c[0] = { t: 'text', v: c[0].v.slice(lead.length) };
      push({ t: 'text', v: lead });
    }
    const lastChild = c[c.length - 1];
    const trail = lastChild?.t === 'text' ? /\s+$/.exec(lastChild.v)?.[0] : undefined;
    if (trail) {
      c[c.length - 1] = { t: 'text', v: lastChild.v.slice(0, -trail.length) };
    }
    const kept = c.filter((child) => child.t !== 'text' || child.v !== '');
    if (kept.length > 0) {
      push({ ...node, c: kept });
    }
    if (trail) {
      push({ t: 'text', v: trail });
    }
  }
  return out;
}

/** Canonical markup for a doc: written, re-parsed in the dialect and written again. */
export function canonicalMarkup(doc, dialect = DIALECT_RETRO) {
  return toMarkup(parse(toMarkup(doc), { dialect }));
}

function visibleText(doc) {
  let text = '';
  const walk = (node) => {
    if (node.t === 'text' || node.t === 'code' || node.t === 'pre') {
      text += node.v;
    } else if (node.t !== 'doc' && node.t !== 'br' && !node.c) {
      text += '\ufffc';
    }
    for (const child of node.c ?? []) {
      walk(child);
    }
  };
  walk(doc);
  return text.replace(/\s+/g, '');
}

/** Percent-encodes what the markup grammar refuses inside an href and adds https:// to a bare host. */
export function normalizeHref(raw) {
  let href = String(raw ?? '').trim();
  if (href === '') {
    return null;
  }
  if (!/^[a-z][a-z0-9+.-]*:/i.test(href)) {
    href = `https://${href}`;
  }
  href = href.replace(/[\s()<>]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase().padStart(2, '0')}`);
  return isAllowedHref(href) ? href : null;
}

function linearText(items) {
  let text = '';
  for (const item of items) {
    if (item.breakBefore) {
      text += '\n';
    }
    text += item.kind === 'text' ? item.node.data : item.kind === 'br' ? '\n' : '\ufffc';
  }
  return text;
}

/**
 * @param {object} options
 * @param {string} [options.value] initial markup
 * @param {string} [options.dialect] 'retro' | 'jira' | 'jira-description'
 * @param {Array<{id: string, name: string}> | (() => Array<{id: string, name: string}>)} [options.mentions]
 *   people the @ listbox offers; also the source of current mention names
 * @param {Map<string, object>} [options.islands] island DocNodes by id (Jira descriptions)
 * @param {(markup: string) => void} [options.onSubmit] Enter submits when set; Shift+Enter adds a line
 * @param {(markup: string) => void} [options.onChange]
 * @param {(file: File) => Promise<string>} [options.uploadImage] uploads a pasted or dropped image,
 *   resolving to its blob id
 * @param {(node: object) => string | null} [options.imageSrc] passed to render()
 * @param {string} [options.placeholder]
 * @param {string} [options.label] accessible name
 * @param {Document} [options.document]
 */
export function createEditor(options = {}) {
  const doc = options.document ?? globalThis.document;
  const dialect = options.dialect ?? DIALECT_RETRO;
  let props = { ...options };
  let value = '';
  let composing = false;
  let pendingTags = [];
  let atoms = new WeakMap();
  let suggestion = null;
  let linkRange = null;
  const id = `rs-editor-${++nextId}`;

  const el = doc.createElement('div');
  el.setAttribute('class', 'rs-editor');
  const input = doc.createElement('div');
  input.setAttribute('class', 'rs-editor-input');
  input.setAttribute('contenteditable', 'true');
  input.setAttribute('role', 'textbox');
  input.setAttribute('aria-multiline', 'true');
  input.setAttribute('spellcheck', 'true');
  input.style.whiteSpace = 'pre-wrap';
  const listbox = doc.createElement('ul');
  listbox.setAttribute('class', 'rs-editor-mentions');
  listbox.setAttribute('role', 'listbox');
  listbox.setAttribute('id', `${id}-mentions`);
  listbox.hidden = true;
  const linkForm = doc.createElement('form');
  linkForm.setAttribute('class', 'rs-editor-link');
  linkForm.hidden = true;
  const linkInput = doc.createElement('input');
  linkInput.setAttribute('type', 'text');
  linkInput.setAttribute('inputmode', 'url');
  linkInput.setAttribute('aria-label', 'Link address');
  linkInput.setAttribute('placeholder', 'https://');
  linkForm.append(linkInput);
  el.append(input, listbox, linkForm);
  if (dialect === DIALECT_RETRO) {
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-controls', listbox.getAttribute('id'));
    input.setAttribute('aria-expanded', 'false');
  }

  const members = () => (typeof props.mentions === 'function' ? props.mentions() : props.mentions) ?? [];
  const renderOptions = () => ({
    document: doc,
    islands: props.islands,
    imageSrc: props.imageSrc,
    resolveMention: (mentionId) => members().find((m) => m.id === mentionId)?.name,
  });

  // render() builds atoms in document order, so its elements pair up with the doc's atom nodes.
  const decorate = (root, docNode) => {
    const nodes = [];
    const collect = (node) => {
      if (['mention', 'img', 'island', 'hr'].includes(node.t)) {
        nodes.push(node);
        return;
      }
      for (const child of node.c ?? []) {
        collect(child);
      }
    };
    collect(docNode);
    const elements = [];
    const find = (node) => {
      for (const child of node.childNodes) {
        if (child.nodeType !== 1) {
          continue;
        }
        const cls = child.getAttribute('class') ?? '';
        if (child.localName === 'img' || child.localName === 'hr' || /\b(mention|image-missing|island)\b/.test(cls)) {
          elements.push(child);
        } else {
          find(child);
        }
      }
    };
    find(root);
    elements.forEach((element, i) => {
      element.setAttribute('contenteditable', 'false');
      if (nodes[i]) {
        atoms.set(element, nodes[i]);
      }
    });
  };

  const paint = (markup, caret) => {
    const parsed = parse(markup, { dialect });
    const fragment = render(parsed, renderOptions());
    atoms = new WeakMap();
    decorate(fragment, parsed);
    if (parsed.c.length === 0) {
      const p = doc.createElement('p');
      p.append(doc.createElement('br'));
      fragment.append(p);
    }
    input.replaceChildren(fragment);
    if (caret && el.isConnected && doc.activeElement === input) {
      writeSelection(input, caret);
    }
  };

  const caretOrEnd = () => readSelection(input) ?? { start: scan(input).length, end: scan(input).length };

  const setCurrent = (markup) => {
    value = markup;
    input.toggleAttribute('data-empty', markup === '');
  };

  const emit = (markup) => {
    if (markup !== value) {
      setCurrent(markup);
      props.onChange?.(markup);
    }
  };

  const sync = (key = null) => {
    if (composing) {
      return;
    }
    pendingTags = [];
    const result = domToDoc(input, { dialect, atoms });
    const markup = canonicalMarkup(result.doc, dialect);
    const changedText = visibleText(result.doc) !== visibleText(parse(markup, { dialect }));
    if (result.dirty || changedText || !input.firstChild) {
      paint(markup, readSelection(input));
    }
    emit(markup);
    history.record({ markup, caret: caretOrEnd() }, key);
  };

  const restore = (state) => {
    if (!state) {
      return;
    }
    paint(state.markup, state.caret);
    emit(state.markup);
  };

  const selectionRange = () => {
    const selection = doc.getSelection();
    if (!selection || selection.rangeCount === 0) {
      return null;
    }
    const range = selection.getRangeAt(0);
    return input.contains(range.startContainer) && input.contains(range.endContainer) ? range : null;
  };

  const placeCaret = (node, offset) => {
    const range = doc.createRange();
    range.setStart(node, offset);
    range.collapse(true);
    const selection = doc.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  };

  // Inserts inline nodes at the selection, replacing it, and leaves the caret after them.
  const insertInline = (nodes) => {
    let range = selectionRange();
    if (!range) {
      writeSelection(input, caretOrEnd());
      range = selectionRange();
    }
    range.deleteContents();
    if (range.startContainer.nodeType === 3) {
      const tail = range.startContainer.splitText(range.startOffset);
      range.setStartBefore(tail);
      range.collapse(true);
    }
    let host = range.startContainer;
    if (host === input || (host.nodeType === 1 && !isInlineHost(host))) {
      const p = doc.createElement('p');
      range.insertNode(p);
      range.setStart(p, 0);
      range.collapse(true);
      host = p;
    }
    const fragment = doc.createDocumentFragment();
    fragment.append(...nodes);
    const last = nodes[nodes.length - 1];
    range.insertNode(fragment);
    if (last.localName === 'br' && isFillerBreak(last)) {
      last.after(doc.createElement('br'));
    }
    if (last.nodeType === 3) {
      placeCaret(last, last.data.length);
    } else {
      const after = doc.createRange();
      after.setStartAfter(last);
      after.collapse(true);
      const selection = doc.getSelection();
      selection.removeAllRanges();
      selection.addRange(after);
    }
  };

  const isInlineHost = (node) => !['ul', 'ol', 'blockquote', 'div', 'table', 'tbody', 'tr'].includes(node.localName);

  const inPre = () => {
    const range = selectionRange();
    return !!range && !!closest(range.startContainer, 'pre');
  };

  const closest = (node, tag) => {
    for (let n = node; n && n !== input; n = n.parentNode) {
      if (n.nodeType === 1 && n.localName === tag) {
        return n;
      }
    }
    return null;
  };

  const insertText = (text) => {
    const normalized = text.replace(/\r\n?/g, '\n');
    if (normalized === '') {
      return;
    }
    if (inPre()) {
      insertInline([doc.createTextNode(normalized)]);
    } else {
      const nodes = [];
      normalized.split('\n').forEach((line, i) => {
        if (i > 0) {
          nodes.push(doc.createElement('br'));
        }
        if (line !== '') {
          nodes.push(doc.createTextNode(line));
        }
      });
      insertInline(nodes);
    }
    sync();
  };

  const insertLineBreak = () => {
    insertInline([inPre() ? doc.createTextNode('\n') : doc.createElement('br')]);
    sync();
  };

  const renderAtom = (node) => {
    const parsedDoc = { t: 'doc', c: [{ t: 'p', c: [node] }] };
    const fragment = render(parsedDoc, renderOptions());
    const atom = fragment.firstChild.firstChild;
    atom.setAttribute('contenteditable', 'false');
    atoms.set(atom, node);
    return atom;
  };

  const removeAtom = (item) => {
    const start = item.start;
    item.node.remove();
    sync();
    writeSelection(input, { start, end: start });
    history.record({ markup: value, caret: { start, end: start } });
  };

  // Splits text nodes so [start, end) is covered by whole nodes, and returns those nodes.
  const textPieces = (start, end) => {
    const pieces = [];
    for (const item of scan(input).items) {
      if (item.kind !== 'text' || item.end <= start || item.start >= end) {
        continue;
      }
      let node = item.node;
      const from = Math.max(start, item.start) - item.start;
      const to = Math.min(end, item.end) - item.start;
      if (to < node.data.length) {
        node.splitText(to);
      }
      if (from > 0) {
        node = node.splitText(from);
      }
      pieces.push(node);
    }
    return pieces;
  };

  const trimmedSelection = () => {
    const selection = readSelection(input);
    if (!selection) {
      return null;
    }
    const text = linearText(scan(input).items);
    let { start, end } = selection;
    while (start < end && WHITESPACE.test(text[start])) {
      start++;
    }
    while (end > start && WHITESPACE.test(text[end - 1])) {
      end--;
    }
    return { start, end };
  };

  // Moves `node` out of `ancestor`, splitting the ancestor (and any wrappers between) around it.
  const lift = (node, ancestor) => {
    const tail = doc.createRange();
    tail.setStartAfter(node);
    tail.setEnd(ancestor, ancestor.childNodes.length);
    const rest = ancestor.cloneNode(false);
    rest.append(tail.extractContents());
    const middle = doc.createRange();
    middle.setStartBefore(node);
    middle.setEnd(ancestor, ancestor.childNodes.length);
    const moved = middle.extractContents();
    ancestor.after(moved, rest);
    for (const part of [ancestor, rest]) {
      if (part.textContent === '' && !part.querySelector('[contenteditable="false"]')) {
        part.remove();
      }
    }
  };

  const toggleTag = (tag, attributes = {}) => {
    const range = trimmedSelection();
    if (!range) {
      return;
    }
    if (range.start === range.end) {
      if (tag !== 'a') {
        pendingTags = pendingTags.includes(tag) ? pendingTags.filter((t) => t !== tag) : [...pendingTags, tag];
      }
      return;
    }
    const pieces = textPieces(range.start, range.end).filter((piece) => tag === 'code' || !closest(piece, 'code'));
    const covered = tag !== 'a' && pieces.length > 0 && pieces.every((piece) => closest(piece, tag));
    for (const piece of pieces) {
      if (covered || tag === 'a') {
        for (let ancestor = closest(piece, tag); ancestor; ancestor = closest(piece, tag)) {
          lift(piece, ancestor);
        }
      }
      if (!covered && !closest(piece, tag)) {
        const wrapper = doc.createElement(tag);
        for (const [name, attr] of Object.entries(attributes)) {
          wrapper.setAttribute(name, attr);
        }
        piece.before(wrapper);
        wrapper.append(piece);
      }
    }
    sync();
    writeSelection(input, range);
  };

  const openLinkForm = () => {
    const range = trimmedSelection();
    if (!range) {
      return;
    }
    const probe = range.start === range.end ? { start: Math.max(0, range.start - 1), end: range.end + 1 } : range;
    const links = new Set(textPieces(probe.start, probe.end).map((piece) => closest(piece, 'a')).filter(Boolean));
    if (links.size > 0) {
      for (const link of links) {
        link.replaceWith(...link.childNodes);
      }
      sync();
      writeSelection(input, range);
      return;
    }
    if (range.start === range.end) {
      return;
    }
    linkRange = range;
    linkForm.hidden = false;
    linkInput.value = '';
    linkInput.removeAttribute('aria-invalid');
    linkInput.focus();
  };

  const closeLinkForm = (apply) => {
    const range = linkRange;
    if (!range) {
      return;
    }
    const href = apply ? normalizeHref(linkInput.value) : null;
    if (apply && !href) {
      linkInput.setAttribute('aria-invalid', 'true');
      return;
    }
    linkRange = null;
    linkForm.hidden = true;
    input.focus();
    writeSelection(input, range);
    if (href) {
      toggleTag('a', { href });
    }
  };

  // ---- @mentions ----

  const closeSuggestions = () => {
    suggestion = null;
    listbox.hidden = true;
    listbox.replaceChildren();
    if (dialect === DIALECT_RETRO) {
      input.setAttribute('aria-expanded', 'false');
      input.removeAttribute('aria-activedescendant');
    }
  };

  const showActive = () => {
    [...listbox.childNodes].forEach((option, i) => {
      option.setAttribute('aria-selected', String(i === suggestion.active));
      if (i === suggestion.active) {
        input.setAttribute('aria-activedescendant', option.getAttribute('id'));
      }
    });
  };

  const updateSuggestions = () => {
    const range = selectionRange();
    if (dialect !== DIALECT_RETRO || composing || !range || !range.collapsed || range.startContainer.nodeType !== 3) {
      closeSuggestions();
      return;
    }
    const node = range.startContainer;
    const match = MENTION_QUERY.exec(node.data.slice(0, range.startOffset));
    const query = match?.[1].toLowerCase();
    const found = match
      ? members()
        .filter((m) => m.name.toLowerCase().includes(query))
        .sort((a, b) => Number(!a.name.toLowerCase().startsWith(query)) - Number(!b.name.toLowerCase().startsWith(query)))
        .slice(0, MAX_SUGGESTIONS)
      : [];
    if (found.length === 0) {
      closeSuggestions();
      return;
    }
    suggestion = { node, from: range.startOffset - match[1].length - 1, to: range.startOffset, found, active: 0 };
    listbox.replaceChildren(...found.map((member, i) => {
      const option = doc.createElement('li');
      option.setAttribute('role', 'option');
      option.setAttribute('id', `${id}-option-${i}`);
      option.setAttribute('data-index', String(i));
      option.textContent = member.name;
      return option;
    }));
    listbox.hidden = false;
    input.setAttribute('aria-expanded', 'true');
    showActive();
    const at = doc.createRange();
    at.setStart(node, suggestion.from);
    at.collapse(true);
    const rect = at.getBoundingClientRect?.();
    const base = el.getBoundingClientRect();
    const anchor = rect && (rect.top || rect.left) ? rect : input.getBoundingClientRect();
    listbox.style.left = `${Math.max(0, anchor.left - base.left)}px`;
    listbox.style.top = `${anchor.bottom - base.top}px`;
  };

  const chooseSuggestion = (index) => {
    const { node, from, to, found } = suggestion;
    const member = found[index];
    closeSuggestions();
    const range = doc.createRange();
    range.setStart(node, from);
    range.setEnd(node, to);
    const selection = doc.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    insertInline([renderAtom({ t: 'mention', id: member.id, name: member.name }), doc.createTextNode(' ')]);
    sync();
  };

  // ---- images ----

  const insertImages = async (files) => {
    if (dialect !== DIALECT_RETRO || !props.uploadImage) {
      return;
    }
    for (const file of files) {
      const blob = await props.uploadImage(file);
      if (isUuid(blob) && el.isConnected) {
        if (doc.activeElement !== input) {
          input.focus();
          writeSelection(input, caretOrEnd());
        }
        insertInline([renderAtom({ t: 'img', blob, alt: 'image' })]);
        sync();
      }
    }
  };

  const imageFiles = (transfer) => [...(transfer?.files ?? [])].filter((file) => /^image\/(png|jpeg|gif|webp)$/.test(file.type));

  // ---- events ----

  const onKeyDown = (e) => {
    if (suggestion) {
      const count = suggestion.found.length;
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        suggestion.active = (suggestion.active + (e.key === 'ArrowDown' ? 1 : count - 1)) % count;
        showActive();
        return;
      }
      if ((e.key === 'Enter' || e.key === 'Tab') && !e.isComposing) {
        e.preventDefault();
        chooseSuggestion(suggestion.active);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        closeSuggestions();
        return;
      }
    }
    if (e.isComposing || composing || e.keyCode === 229) {
      return;
    }
    if (/^(Arrow|Home|End|Page)/.test(e.key)) {
      pendingTags = [];
    }
    const mod = (e.ctrlKey || e.metaKey) && !e.altKey;
    const key = e.key.toLowerCase();
    if (mod && (key === 'z' || key === 'y')) {
      e.preventDefault();
      restore(key === 'y' || e.shiftKey ? history.redo() : history.undo());
      return;
    }
    const tag = mod && !e.shiftKey ? { b: 'strong', i: 'em', e: 'code' }[key] : undefined;
    if (tag) {
      e.preventDefault();
      toggleTag(tag);
      return;
    }
    if (mod && !e.shiftKey && key === 'k') {
      e.preventDefault();
      openLinkForm();
      return;
    }
    if (e.key === 'Enter') {
      e.preventDefault();
      if (e.shiftKey || !props.onSubmit || inPre()) {
        insertLineBreak();
      } else {
        props.onSubmit(value);
      }
    }
  };

  const onBeforeInput = (e) => {
    if (e.isComposing || composing) {
      return;
    }
    const type = e.inputType;
    if (type === 'historyUndo' || type === 'historyRedo') {
      e.preventDefault();
      restore(type === 'historyUndo' ? history.undo() : history.redo());
    } else if (type === 'insertParagraph' || type === 'insertLineBreak') {
      e.preventDefault();
      if (type === 'insertParagraph' && props.onSubmit && !inPre()) {
        props.onSubmit(value);
      } else {
        insertLineBreak();
      }
    } else if (type === 'deleteContentBackward' || type === 'deleteContentForward') {
      const selection = readSelection(input);
      if (selection && selection.start === selection.end) {
        const item = itemBeside(input, selection.start, type === 'deleteContentBackward');
        if (item?.kind === 'atom' && (!item.acrossBreak || isBlock(item.node))) {
          e.preventDefault();
          removeAtom(item);
        }
      }
    } else if (type === 'insertText' && pendingTags.length > 0 && typeof e.data === 'string') {
      e.preventDefault();
      const text = doc.createTextNode(e.data);
      let wrapped = text;
      for (const tag of pendingTags) {
        const wrapper = doc.createElement(tag);
        wrapper.append(wrapped);
        wrapped = wrapper;
      }
      insertInline([wrapped]);
      placeCaret(text, text.data.length);
      sync('insert');
    } else if (type === 'insertFromPaste' || type === 'insertFromDrop' || type === 'insertReplacementText') {
      if (type !== 'insertReplacementText' && e.dataTransfer) {
        e.preventDefault();
        insertText(e.dataTransfer.getData('text/plain'));
      }
    } else if (type.startsWith('format')) {
      e.preventDefault();
      const tag = { formatBold: 'strong', formatItalic: 'em', formatStrikeThrough: 's' }[type];
      if (tag) {
        toggleTag(tag);
      }
    }
  };

  const onInput = (e) => {
    if (e.isComposing || composing) {
      return;
    }
    const type = e.inputType ?? '';
    sync(type.startsWith('insert') ? 'insert' : type.startsWith('delete') ? 'delete' : null);
    updateSuggestions();
  };

  const onCompositionStart = () => {
    composing = true;
  };

  const onCompositionEnd = () => {
    composing = false;
    sync('insert');
    updateSuggestions();
  };

  const onPaste = (e) => {
    e.preventDefault();
    const files = imageFiles(e.clipboardData);
    if (files.length > 0) {
      insertImages(files);
      return;
    }
    insertText(e.clipboardData?.getData('text/plain') ?? '');
  };

  const onDrop = (e) => {
    e.preventDefault();
    const point = doc.caretPositionFromPoint?.(e.clientX, e.clientY);
    const range = point ? doc.createRange() : doc.caretRangeFromPoint?.(e.clientX, e.clientY);
    if (point) {
      range.setStart(point.offsetNode, point.offset);
    }
    if (range && input.contains(range.startContainer)) {
      placeCaret(range.startContainer, range.startOffset);
    }
    const files = imageFiles(e.dataTransfer);
    if (files.length > 0) {
      insertImages(files);
    } else {
      insertText(e.dataTransfer?.getData('text/plain') ?? '');
    }
  };

  const onBlur = () => {
    closeSuggestions();
  };

  const onListPointerDown = (e) => {
    e.preventDefault();
    const option = e.target.closest?.('[data-index]');
    if (option && suggestion) {
      chooseSuggestion(Number(option.getAttribute('data-index')));
    }
  };

  const onLinkSubmit = (e) => {
    e.preventDefault();
    closeLinkForm(true);
  };

  const onLinkKeyDown = (e) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      closeLinkForm(false);
    }
  };

  const listeners = [
    [input, 'keydown', onKeyDown],
    [input, 'beforeinput', onBeforeInput],
    [input, 'input', onInput],
    [input, 'compositionstart', onCompositionStart],
    [input, 'compositionend', onCompositionEnd],
    [input, 'paste', onPaste],
    [input, 'drop', onDrop],
    [input, 'blur', onBlur],
    [input, 'mousedown', () => {
      pendingTags = [];
    }],
    [listbox, 'pointerdown', onListPointerDown],
    [linkForm, 'submit', onLinkSubmit],
    [linkInput, 'keydown', onLinkKeyDown],
  ];
  for (const [target, type, fn] of listeners) {
    target.addEventListener(type, fn);
  }

  const applyProps = () => {
    if (props.placeholder) {
      input.setAttribute('aria-placeholder', props.placeholder);
      input.setAttribute('data-placeholder', props.placeholder);
    }
    if (props.label) {
      input.setAttribute('aria-label', props.label);
    }
  };

  const initial = canonicalMarkup(parse(options.value ?? '', { dialect }), dialect);
  applyProps();
  paint(initial, null);
  setCurrent(initial);
  const history = createHistory({ markup: initial, caret: { start: scan(input).length, end: scan(input).length } });

  return {
    el,
    input,
    getValue: () => value,
    setValue(markup) {
      const next = canonicalMarkup(parse(markup ?? '', { dialect }), dialect);
      closeSuggestions();
      paint(next, null);
      setCurrent(next);
      const end = scan(input).length;
      history.reset({ markup: next, caret: { start: end, end } });
    },
    focus() {
      const caret = caretOrEnd();
      input.focus();
      writeSelection(input, caret);
    },
    selection: () => readSelection(input),
    select(start, end = start) {
      writeSelection(input, { start, end });
    },
    update(next) {
      const repaint = next.islands !== undefined && next.islands !== props.islands;
      props = { ...props, ...next };
      applyProps();
      if (repaint && !composing) {
        paint(value, readSelection(input));
      }
    },
    destroy() {
      for (const [target, type, fn] of listeners) {
        target.removeEventListener(type, fn);
      }
      el.remove();
    },
  };
}
