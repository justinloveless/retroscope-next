// DocNode: the one rendered-document tree. Retro Markup (parse.js, Core/Markup/MarkupParser) and
// Jira ADF (Core/Adf/AdfToDoc) both produce it, and render.js turns it into DOM. The JSON shape is
// Core/Markup/DocJson's: every node is {t: kind, ...}; container children are `c`, scalar
// content is `v`. This module holds the shared syntax rules and the writer; allow.js holds the node allow-list.

export const DIALECT_RETRO = 'retro';
export const DIALECT_JIRA = 'jira';
export const DIALECT_JIRA_DESCRIPTION = 'jira-description';

export const MAX_BLOCK_DEPTH = 16;
export const MAX_INLINE_DEPTH = 16;
export const MAX_MENTION_NAME_LENGTH = 100;
export const MAX_IMAGE_ALT_LENGTH = 100;
export const MAX_HREF_LENGTH = 2048;
export const MAX_LANGUAGE_LENGTH = 32;
export const MAX_ISLAND_ID_LENGTH = 64;

export const MENTION_PREFIX = '[[mention:';
export const IMAGE_PREFIX = '](blob:';
export const ISLAND_PREFIX = '[[island:';

export function isWhitespace(c) {
  return c === ' ' || c === '\t' || c === '\n';
}

export function isAsciiAlphanumeric(c) {
  return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

export function isAsciiPunctuation(c) {
  return (c >= '!' && c <= '/') || (c >= ':' && c <= '@') || (c >= '[' && c <= '`') || (c >= '{' && c <= '~');
}

export function isBlank(line) {
  return /^[ \t]*$/.test(line);
}

export function countLeading(line, c) {
  let count = 0;
  while (count < line.length && line[count] === c) {
    count++;
  }
  return count;
}

const UUID_RE = /^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/;

// Canonical 8-4-4-4-12 hex only, so both parsers accept exactly the same tokens; lower-cased as
// Guid.ToString("D") writes it.
export function parseUuid(text, start) {
  const candidate = text.slice(start, start + 36);
  return UUID_RE.test(candidate) ? candidate.toLowerCase() : null;
}

export function isHrefChar(c) {
  return c > ' ' && c !== '\u007f' && c !== '(' && c !== ')' && c !== '<' && c !== '>';
}

export function isAllowedHref(href) {
  if (typeof href !== 'string' || href.length === 0 || href.length > MAX_HREF_LENGTH) {
    return false;
  }
  for (const c of href.split('')) {
    if (!isHrefChar(c)) {
      return false;
    }
  }
  return ['http://', 'https://', 'mailto:'].some(
    (scheme) => href.length > scheme.length && href.slice(0, scheme.length).toLowerCase() === scheme,
  );
}

export function isIslandId(id) {
  return typeof id === 'string' && id.length <= MAX_ISLAND_ID_LENGTH && /^[A-Za-z0-9_-]+$/.test(id);
}

export function isLanguage(language) {
  return typeof language === 'string' && language.length <= MAX_LANGUAGE_LENGTH && /^[A-Za-z0-9_+#.-]+$/.test(language);
}

export function tryListMarker(line) {
  if (line.length === 0) {
    return null;
  }
  if (line[0] === '-') {
    if (line.length > 1 && line[1] !== ' ') {
      return null;
    }
    return { ordered: false, start: 0, width: 2, content: line.slice(2) };
  }
  const digits = countDigits(line);
  if (digits === 0 || digits > 9 || digits === line.length || line[digits] !== '.') {
    return null;
  }
  const width = digits + 2;
  if (line.length > digits + 1 && line[digits + 1] !== ' ') {
    return null;
  }
  return { ordered: true, start: Number(line.slice(0, digits)), width, content: line.slice(width) };
}

function countDigits(line) {
  let digits = 0;
  while (digits < line.length && line[digits] >= '0' && line[digits] <= '9') {
    digits++;
  }
  return digits;
}

export function tryHeading(line) {
  const level = countLeading(line, '#');
  if (level === 0 || level > 6 || line.length <= level || line[level] !== ' ') {
    return null;
  }
  const content = line.slice(level + 1).replace(/^[ \t]+|[ \t]+$/g, '');
  return content.length > 0 ? { level, content } : null;
}

export function isRule(line) {
  const trimmed = line.replace(/[ \t]+$/, '');
  return trimmed.length >= 3 && /^-+$/.test(trimmed);
}

export function isUuid(v) {
  return typeof v === 'string' && UUID_RE.test(v) && v === v.toLowerCase();
}

export function isAttachmentId(v) {
  return typeof v === 'string' && /^[0-9]{1,20}$/.test(v);
}

export function* walk(node) {
  yield node;
  for (const child of node.c ?? []) {
    yield* walk(child);
  }
}

// Distinct mentioned user ids in document order. Callers keep only current team members.
export function mentionIds(doc) {
  const ids = new Set();
  for (const node of walk(doc)) {
    if (node.t === 'mention') {
      ids.add(node.id);
    }
  }
  return [...ids];
}

export function imageBlobIds(doc) {
  return [...walk(doc)].filter((node) => node.t === 'img').map((node) => node.blob);
}

export function islandIds(doc) {
  return doc.c.filter((node) => node.t === 'island').map((node) => node.id);
}

// Doc → canonical markup, mirroring Core/Markup/MarkupWriter: for any doc parse() produced,
// parse(toMarkup(doc)) equals doc. ADF-only nodes have no markup form; they reach the editor as
// islands, so writing one is a caller bug.
export function toMarkup(doc) {
  return writeBlocks(doc.c);
}

// Blocks are separated by a blank line, except that a list follows a paragraph directly: list
// markers interrupt a paragraph, so "- a\n  - b" keeps the shape people type.
function writeBlocks(blocks) {
  return blocks
    .map((block, i) => {
      if (i === 0) {
        return writeBlock(block);
      }
      const tight = blocks[i - 1].t === 'p' && (block.t === 'ul' || block.t === 'ol');
      return (tight ? '\n' : '\n\n') + writeBlock(block);
    })
    .join('');
}

function writeBlock(block) {
  switch (block.t) {
    case 'p':
      return escapeLineStarts(writeInlines(block.c));
    case 'h':
      return `${'#'.repeat(block.level)} ${writeInlines(block.c).replaceAll('\n', ' ')}`;
    case 'ul':
      return block.c.map((item) => writeItem('-', item)).join('\n');
    case 'ol':
      return block.c.map((item, index) => writeItem(`${itemNumber(block.start, index)}.`, item)).join('\n');
    case 'quote':
      return writeBlocks(block.c)
        .split('\n')
        .map((line) => (line.length === 0 ? '>' : `> ${line}`))
        .join('\n');
    case 'pre':
      return writeCodeBlock(block);
    case 'hr':
      return '---';
    case 'island':
      return `${ISLAND_PREFIX}${block.id}]]`;
    default:
      throw unrepresentable(block);
  }
}

// Item numbers after the first are not read back, but must stay a valid marker (at most nine digits).
function itemNumber(start, index) {
  return start + index <= 999_999_999 ? start + index : start;
}

function writeItem(marker, item) {
  const body = writeBlocks(item.c);
  if (body.length === 0) {
    return marker;
  }
  const indent = ' '.repeat(marker.length + 1);
  const [first, ...rest] = body.split('\n');
  return [`${marker} ${first}`, ...rest.map((line) => (line.length > 0 ? indent + line : ''))].join('\n');
}

function writeCodeBlock(block) {
  const longestRun = Math.max(...block.v.split('\n').map((line) => countLeading(line, '`')));
  const fence = '`'.repeat(Math.max(3, longestRun + 1));
  const language = block.lang !== undefined && isLanguage(block.lang) ? block.lang : '';
  return block.v.length === 0 ? `${fence}${language}\n${fence}` : `${fence}${language}\n${block.v}\n${fence}`;
}

// A rendered line can only begin with a block marker character if that character came from text.
function escapeLineStarts(paragraph) {
  return paragraph
    .split('\n')
    .map((line) => {
      if (line.length === 0) {
        return line;
      }
      const marker = tryListMarker(line);
      if (marker?.ordered) {
        const dot = line.indexOf('.');
        return `${line.slice(0, dot)}\\${line.slice(dot)}`;
      }
      if (line[0] === '>' || marker || tryHeading(line) || isRule(line)) {
        return `\\${line}`;
      }
      return line;
    })
    .join('\n');
}

function writeInlines(nodes) {
  return new InlineWriter().inlines(nodes, '');
}

// Inline nodes → markup, mirroring Core/Markup/InlineWriter. Text escapes every delimiter
// character, so ambiguity can only come from neighbouring delimiters: the italic delimiter choice
// is the one real decision.
class InlineWriter {
  // Set when some italic had no delimiter that reads back as written; an enclosing italic then
  // tries its other delimiter, which changes what its children may use.
  ambiguous = false;

  // `enclosingItalic`: an outer italic's closer search sees every same-character run, including a
  // nested italic's.
  inlines(nodes, enclosingItalic) {
    const rendered = nodes.map((node) => (isItalic(node) ? null : this.inline(node, enclosingItalic)));
    for (let first = 0; first < nodes.length; first++) {
      if (!isItalic(nodes[first])) {
        continue;
      }
      let last = first;
      while (last + 1 < nodes.length && isItalic(nodes[last + 1])) {
        last++;
      }
      this.renderItalics(nodes, rendered, first, last, enclosingItalic);
      first = last;
    }
    // A literal '!' right before a link would read back as an image opener.
    for (let i = 0; i + 1 < nodes.length; i++) {
      if (nodes[i].t === 'text' && rendered[i].endsWith('!') && rendered[i + 1].startsWith('[')) {
        rendered[i] = `${rendered[i].slice(0, -1)}\\!`;
      }
    }
    return rendered.join('');
  }

  // Picks '_' or '*' for a run of adjacent italics. Neighbours must differ ("_a__b_" and "*a**b*"
  // both read as one italic). '_' is no delimiter next to a letter or digit or inside an italic '_'
  // opened; '*' next to a bold run, or before a bold first child, makes a '***' that the parser
  // reads as bold first. '_' is tried first because it reads best.
  renderItalics(nodes, rendered, first, last, enclosingItalic) {
    const count = last - first + 1;
    const allowed = allowedDelimiters(nodes, rendered, first, last, enclosingItalic);
    const ambiguousBefore = this.ambiguous;
    let fallback = null;
    for (const start of ['_', '*']) {
      const delimiters = Array.from({ length: count }, (_, i) => ((i % 2 === 0) === (start === '_') ? '_' : '*'));
      if (!delimiters.every((d, i) => (d === '_' ? allowed[i].underscore : allowed[i].star))) {
        continue;
      }
      this.ambiguous = false;
      const texts = this.renderItalicRun(nodes, first, delimiters);
      if (!this.ambiguous) {
        rendered.splice(first, count, ...texts);
        this.ambiguous = ambiguousBefore;
        return;
      }
      fallback ??= texts;
    }
    fallback ??= this.renderItalicRun(nodes, first, allowed.map((a) => (a.underscore ? '_' : '*')));
    rendered.splice(first, count, ...fallback);
    this.ambiguous = true;
  }

  renderItalicRun(nodes, first, delimiters) {
    return delimiters.map((d, i) => d + this.inlines(nodes[first + i].c, d) + d);
  }

  inline(node, enclosingItalic) {
    switch (node.t) {
      case 'text':
        return escapeText(node.v);
      case 'br':
        return '\n';
      case 'b':
        return `**${this.inlines(node.c, enclosingItalic)}**`;
      case 's':
        return `~~${this.inlines(node.c, enclosingItalic)}~~`;
      case 'code':
        return writeCode(node.v);
      case 'a':
        return `[${node.c.length > 0 ? this.inlines(node.c, '') : escapeText(node.href)}](${encodeHref(node.href)})`;
      case 'mention':
        return `${MENTION_PREFIX}${node.id}:${label(node.name, MAX_MENTION_NAME_LENGTH, 'user')}]]`;
      case 'img':
        return `![${label(node.alt, MAX_IMAGE_ALT_LENGTH, '')}${IMAGE_PREFIX}${node.blob})`;
      default:
        throw unrepresentable(node);
    }
  }
}

function isItalic(node) {
  return node.t === 'i';
}

function isBold(node) {
  return node?.t === 'b';
}

function allowedDelimiters(nodes, rendered, first, last, enclosingItalic) {
  const count = last - first + 1;
  const before = first > 0 ? rendered[first - 1] : '';
  const after = last + 1 < nodes.length ? rendered[last + 1] : '';
  return Array.from({ length: count }, (_, i) => {
    const content = nodes[first + i].c;
    return {
      underscore: enclosingItalic !== '_'
        && (i > 0 || before.length === 0 || !isAsciiAlphanumeric(before.at(-1)))
        && (i < count - 1 || after.length === 0 || !isAsciiAlphanumeric(after[0])),
      star: enclosingItalic !== '*'
        && (i > 0 || first === 0 || !isBold(nodes[first - 1]))
        && (i < count - 1 || last + 1 === nodes.length || !isBold(nodes[last + 1]))
        && (content.length === 0 || !isBold(content[0])),
    };
  });
}

function writeCode(value) {
  const flat = value.replaceAll('\n', ' ');
  if (flat.length === 0) {
    return '';
  }
  const longestRun = Math.max(0, ...(flat.match(/`+/g) ?? []).map((run) => run.length));
  const fence = '`'.repeat(longestRun + 1);
  const pad = flat[0] === '`' || flat.at(-1) === '`' || (flat[0] === ' ' && flat.at(-1) === ' ' && /[^ ]/.test(flat));
  return pad ? `${fence} ${flat} ${fence}` : `${fence}${flat}${fence}`;
}

function escapeText(value) {
  return value.replace(/[\\*_~`[\]]/g, '\\$&');
}

function encodeHref(href) {
  return href
    .replaceAll(' ', '%20')
    .replaceAll('(', '%28')
    .replaceAll(')', '%29')
    .replaceAll('<', '%3C')
    .replaceAll('>', '%3E');
}

function label(value, maxLength, fallback) {
  const cleaned = value.replace(/[\]\n\r]/g, '').slice(0, maxLength);
  return cleaned.length > 0 ? cleaned : fallback;
}

function unrepresentable(node) {
  return new TypeError(`'${node.t}' has no markup form; ADF-only content must be an island`);
}
