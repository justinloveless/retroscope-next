// Caret positions as offsets into the editor's linear text: every character counts one, every
// atom (a mention pill, image, Jira island or rule: any contenteditable=false element) counts one,
// a line break counts one, and one break separates consecutive blocks that hold content. A
// trailing <br> that only keeps an empty last line open counts nothing. render(parse(markup))
// yields the same linear text as the DOM it replaces, so offsets survive a re-render.

const BLOCKS = new Set([
  'p', 'div', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'ul', 'ol', 'li', 'blockquote', 'pre', 'hr',
  'table', 'tbody', 'tr', 'th', 'td', 'details', 'summary',
]);

export function isBlock(node) {
  return node.nodeType === 1 && BLOCKS.has(node.localName);
}

export function isAtom(node) {
  return node.nodeType === 1 && node.getAttribute('contenteditable') === 'false';
}

export function isFillerBreak(br) {
  for (let node = br; ; node = node.parentNode) {
    for (let next = node.nextSibling; next; next = next.nextSibling) {
      if (!(next.nodeType === 3 && next.data === '')) {
        return false;
      }
    }
    if (!node.parentNode || isBlock(node.parentNode)) {
      return true;
    }
  }
}

/**
 * Lists the content items of `root` in document order: text nodes, atoms and counted line breaks,
 * each with its linear start and end and whether a block break precedes it. With `stop` set to a
 * DOM point {node, offset}, the walk ends there and `at` is that point's linear offset.
 */
export function scan(root, stop = null) {
  const items = [];
  let pos = 0;
  let pending = false;
  let at = null;

  const content = (node, kind, length) => {
    const breakBefore = pending;
    if (pending) {
      pos++;
      pending = false;
    }
    items.push({ node, kind, start: pos, end: pos + length, breakBefore });
    pos += length;
  };
  const boundary = () => {
    if (items.length > 0) {
      pending = true;
    }
  };
  // Inside a block the caret sits after a pending break; between blocks it sits before it.
  const stopHere = (beforeBlock) => {
    at = pos + (pending && !beforeBlock ? 1 : 0);
  };

  const visit = (node) => {
    if (stop && node === stop.node && node.nodeType === 3) {
      stopHere(false);
      at += Math.min(stop.offset, node.data.length);
      return true;
    }
    if (node.nodeType === 3) {
      if (node.data.length > 0) {
        content(node, 'text', node.data.length);
      }
      return false;
    }
    if (node.nodeType !== 1) {
      return false;
    }
    const block = isBlock(node);
    if (isAtom(node)) {
      if (block) {
        boundary();
      }
      content(node, 'atom', 1);
      if (block) {
        boundary();
      }
      if (stop && (node === stop.node || node.contains(stop.node))) {
        stopHere(false);
        return true;
      }
      return false;
    }
    if (node.localName === 'br') {
      if (!isFillerBreak(node)) {
        content(node, 'br', 1);
      }
      return false;
    }
    if (block) {
      boundary();
    }
    const children = node.childNodes;
    for (let i = 0; i < children.length; i++) {
      if (stop && node === stop.node && i === stop.offset) {
        stopHere(isBlock(children[i]));
        return true;
      }
      if (visit(children[i])) {
        return true;
      }
    }
    if (stop && node === stop.node) {
      stopHere(true);
      return true;
    }
    if (block) {
      boundary();
    }
    return false;
  };

  const children = root.childNodes;
  let stopped = false;
  for (let i = 0; i < children.length && !stopped; i++) {
    if (stop && root === stop.node && i === stop.offset) {
      stopHere(isBlock(children[i]));
      stopped = true;
    } else {
      stopped = visit(children[i]);
    }
  }
  if (stop && !stopped) {
    at = pos;
  }
  return { items, length: pos, at };
}

export function pointToOffset(root, node, offset) {
  return scan(root, { node, offset }).at;
}

/** The DOM point for a linear offset; offsets past the end land at the end. */
export function offsetToPoint(root, offset) {
  const { items } = scan(root);
  let last = null;
  for (const item of items) {
    if (offset < item.start) {
      return last ?? startOf(item);
    }
    if (offset === item.start) {
      return startOf(item);
    }
    if (item.kind === 'text' && offset <= item.end) {
      return { node: item.node, offset: offset - item.start };
    }
    last = item.kind === 'text' ? { node: item.node, offset: item.node.data.length } : { ...before(item.node), offset: indexOf(item.node) + 1 };
  }
  return last ?? firstEditablePoint(root);
}

function startOf(item) {
  return item.kind === 'text' ? { node: item.node, offset: 0 } : before(item.node);
}

function before(node) {
  return { node: node.parentNode, offset: indexOf(node) };
}

function indexOf(node) {
  return Array.prototype.indexOf.call(node.parentNode.childNodes, node);
}

function firstEditablePoint(root) {
  let node = root;
  while (node.firstChild && node.firstChild.nodeType === 1 && isBlock(node.firstChild) && !isAtom(node.firstChild)) {
    node = node.firstChild;
  }
  return { node, offset: 0 };
}

/**
 * The content item that touches `offset` on one side, or null. `acrossBreak` is true when a block
 * break lies between the offset and the item.
 */
export function itemBeside(root, offset, backward) {
  const { items } = scan(root);
  for (const item of items) {
    if (backward && item.end === offset) {
      return { ...item, acrossBreak: false };
    }
    if (!backward && item.start === offset) {
      return { ...item, acrossBreak: false };
    }
  }
  for (let i = 0; i < items.length; i++) {
    const next = items[i + 1];
    if (next?.breakBefore && items[i].end === next.start - 1) {
      if (backward && next.start === offset) {
        return { ...items[i], acrossBreak: true };
      }
      if (!backward && items[i].end === offset) {
        return { ...next, acrossBreak: true };
      }
    }
  }
  return null;
}

/** Reads the selection inside `root` as linear offsets, or null when it is elsewhere. */
export function readSelection(root) {
  const selection = root.ownerDocument.getSelection();
  if (!selection || selection.rangeCount === 0) {
    return null;
  }
  const range = selection.getRangeAt(0);
  if (!root.contains(range.startContainer) || !root.contains(range.endContainer)) {
    return null;
  }
  const start = pointToOffset(root, range.startContainer, range.startOffset);
  const end = range.collapsed ? start : pointToOffset(root, range.endContainer, range.endOffset);
  return { start: Math.min(start, end), end: Math.max(start, end) };
}

export function writeSelection(root, { start, end }) {
  const doc = root.ownerDocument;
  const from = offsetToPoint(root, start);
  const to = end === start ? from : offsetToPoint(root, end);
  const range = doc.createRange();
  range.setStart(from.node, from.offset);
  range.setEnd(to.node, to.offset);
  const selection = doc.getSelection();
  selection.removeAllRanges();
  selection.addRange(range);
  return range;
}
