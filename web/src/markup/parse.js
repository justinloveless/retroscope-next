// Retro Markup → DocNode, mirroring Core/Markup/MarkupParser and InlineParser rule for rule.
// Total over every string: malformed syntax is literal text, never an error. Indices are UTF-16
// code units and character classes are ASCII-only, exactly as in C#, so both sides agree.

import {
  DIALECT_JIRA,
  DIALECT_JIRA_DESCRIPTION,
  DIALECT_RETRO,
  IMAGE_PREFIX,
  ISLAND_PREFIX,
  MAX_BLOCK_DEPTH,
  MAX_HREF_LENGTH,
  MAX_IMAGE_ALT_LENGTH,
  MAX_INLINE_DEPTH,
  MAX_MENTION_NAME_LENGTH,
  MENTION_PREFIX,
  countLeading,
  imageBlobIds,
  isAllowedHref,
  isAsciiAlphanumeric,
  isAsciiPunctuation,
  isBlank,
  isHrefChar,
  isIslandId,
  isLanguage,
  isRule,
  isWhitespace,
  parseUuid,
  tryHeading,
  tryListMarker,
} from './doc.js';

// Stored bodies and their dialects, mirroring Core/Markup/MarkupLimits. Lengths count code
// points, as Postgres char_length does for the table checks.
export const MARKUP_FIELDS = {
  card: { maxLength: 8000, dialect: DIALECT_RETRO },
  comment: { maxLength: 4000, dialect: DIALECT_RETRO },
  actionItem: { maxLength: 8000, dialect: DIALECT_RETRO },
  jiraText: { maxLength: 32000, dialect: DIALECT_JIRA },
  jiraDescription: { maxLength: 32000, dialect: DIALECT_JIRA_DESCRIPTION },
};

export const IMAGES_PER_BODY = 8;

// The client twin of MarkupLimits.Check: {doc} when the body is acceptable, else {error}.
export function checkMarkup(markup, field) {
  const { maxLength, dialect } = MARKUP_FIELDS[field];
  const codePoints = countCodePoints(markup);
  if (codePoints < 0) {
    return { error: 'invalid_character' };
  }
  if (/^[ \t\r\n]*$/.test(markup)) {
    return { error: 'empty' };
  }
  if (codePoints > maxLength) {
    return { error: 'too_long' };
  }
  const doc = parse(markup, { dialect });
  if (imageBlobIds(doc).length > IMAGES_PER_BODY) {
    return { error: 'too_many_images' };
  }
  return { doc };
}

// -1 when the text holds a character no stored body may contain: NUL and other C0 controls
// besides tab and line breaks, or an unpaired UTF-16 surrogate.
function countCodePoints(text) {
  let count = 0;
  for (let i = 0; i < text.length; i++) {
    const unit = text.charCodeAt(i);
    const next = text.charCodeAt(i + 1);
    if (unit >= 0xd800 && unit <= 0xdbff && next >= 0xdc00 && next <= 0xdfff) {
      i++;
    } else if ((unit >= 0xd800 && unit <= 0xdfff) || (unit < 0x20 && unit !== 0x09 && unit !== 0x0a && unit !== 0x0d)) {
      return -1;
    }
    count++;
  }
  return count;
}

export function parse(markup, { dialect = DIALECT_RETRO } = {}) {
  const lines = String(markup).replaceAll('\r\n', '\n').replaceAll('\r', '\n').split('\n');
  return { t: 'doc', c: new BlockParser(dialect).parse(lines, 0, true) };
}

class BlockParser {
  constructor(dialect) {
    this.dialect = dialect;
    this.jira = dialect !== DIALECT_RETRO;
  }

  parse(lines, depth, topLevel) {
    const blocks = [];
    const at = { i: 0 };
    while (at.i < lines.length) {
      const line = lines[at.i];
      const fence = tryFence(line);
      const marker = depth < MAX_BLOCK_DEPTH ? tryListMarker(line) : null;
      const heading = this.jira ? tryHeading(line) : null;
      const islandId = topLevel ? this.tryIsland(line) : null;
      if (isBlank(line)) {
        at.i++;
      } else if (fence) {
        blocks.push(parseFence(lines, at, fence));
      } else if (depth < MAX_BLOCK_DEPTH && line[0] === '>') {
        blocks.push(this.parseQuote(lines, at, depth));
      } else if (marker) {
        blocks.push(this.parseList(lines, at, marker, depth));
      } else if (heading) {
        blocks.push({ t: 'h', level: heading.level, c: this.parseInlines(heading.content) });
        at.i++;
      } else if (this.jira && isRule(line)) {
        blocks.push({ t: 'hr' });
        at.i++;
      } else if (islandId !== null) {
        blocks.push({ t: 'island', id: islandId, c: [] });
        at.i++;
      } else {
        blocks.push(this.parseParagraph(lines, at, depth, topLevel));
      }
    }
    return blocks;
  }

  startsBlock(line, depth, topLevel) {
    return tryFence(line) !== null
      || (depth < MAX_BLOCK_DEPTH && (line[0] === '>' || tryListMarker(line) !== null))
      || (this.jira && (tryHeading(line) !== null || isRule(line)))
      || (topLevel && this.tryIsland(line) !== null);
  }

  parseParagraph(lines, at, depth, topLevel) {
    const parts = [stripLine(lines[at.i])];
    at.i++;
    while (at.i < lines.length && !isBlank(lines[at.i]) && !this.startsBlock(lines[at.i], depth, topLevel)) {
      parts.push(stripLine(lines[at.i]));
      at.i++;
    }
    return { t: 'p', c: this.parseInlines(parts.join('\n')) };
  }

  parseQuote(lines, at, depth) {
    const inner = [];
    while (at.i < lines.length && lines[at.i][0] === '>') {
      const line = lines[at.i];
      inner.push(line[1] === ' ' ? line.slice(2) : line.slice(1));
      at.i++;
    }
    return { t: 'quote', c: this.parse(inner, depth + 1, false) };
  }

  parseList(lines, at, first, depth) {
    const items = [];
    let marker;
    while (at.i < lines.length && (marker = tryListMarker(lines[at.i])) && marker.ordered === first.ordered) {
      const itemLines = [marker.content];
      at.i++;
      while (at.i < lines.length) {
        const line = lines[at.i];
        if (isBlank(line)) {
          let next = at.i;
          while (next < lines.length && isBlank(lines[next])) {
            next++;
          }
          if (next === lines.length || indent(lines[next]) < marker.width) {
            break;
          }
          for (; at.i < next; at.i++) {
            itemLines.push(lines[at.i].slice(Math.min(indent(lines[at.i]), marker.width)));
          }
        } else if (indent(line) >= marker.width) {
          itemLines.push(line.slice(marker.width));
          at.i++;
        } else {
          break;
        }
      }
      items.push({ t: 'li', c: this.parse(itemLines, depth + 1, false) });
    }
    return first.ordered ? { t: 'ol', start: first.start, c: items } : { t: 'ul', c: items };
  }

  tryIsland(line) {
    if (this.dialect !== DIALECT_JIRA_DESCRIPTION) {
      return null;
    }
    const trimmed = line.replace(/[ \t]+$/, '');
    if (trimmed.length < ISLAND_PREFIX.length + 2 || !trimmed.startsWith(ISLAND_PREFIX) || !trimmed.endsWith(']]')) {
      return null;
    }
    const candidate = trimmed.slice(ISLAND_PREFIX.length, -2);
    return isIslandId(candidate) ? candidate : null;
  }

  parseInlines(text) {
    const scope = { start: 0, end: text.length, inLink: false, depth: 0, open: OPEN_NONE };
    return new InlineParser(text, this.dialect === DIALECT_RETRO).parse(scope);
  }
}

function tryFence(line) {
  const length = countLeading(line, '`');
  if (length < 3) {
    return null;
  }
  const info = stripLine(line.slice(length));
  if (info === '') {
    return { length, language: null };
  }
  return isLanguage(info) ? { length, language: info } : null;
}

function parseFence(lines, at, fence) {
  const content = [];
  at.i++;
  while (at.i < lines.length) {
    const line = lines[at.i];
    at.i++;
    if (isClosingFence(line, fence.length)) {
      break;
    }
    content.push(line);
  }
  const block = { t: 'pre' };
  if (fence.language !== null) {
    block.lang = fence.language;
  }
  block.v = content.join('\n');
  return block;
}

function isClosingFence(line, fenceLength) {
  const run = countLeading(line, '`');
  return run >= fenceLength && isBlank(line.slice(run));
}

function indent(line) {
  return countLeading(line, ' ');
}

function stripLine(line) {
  return line.replace(/^[ \t]+|[ \t]+$/g, '');
}

const DOUBLE = 'double';
const STAR = 'star';
const UNDERSCORE = 'underscore';

// Delimiters already open around a scope. Emphasis never nests inside emphasis opened with the
// same delimiter: the outer closer search would stop at the inner delimiter, so no writer could
// reproduce it.
const OPEN_NONE = 0;
const OPEN_BOLD = 1;
const OPEN_STRIKE = 2;
const OPEN_STAR_ITALIC = 4;
const OPEN_UNDERSCORE_ITALIC = 8;

// Parses one paragraph's text (lines joined by '\n'). Scopes are index ranges into the same
// string, so token lookups are cached by absolute position: a token found from an outer scope is
// the same token seen from an inner one, and hostile input stays quadratic at worst.
class InlineParser {
  constructor(text, retro) {
    this.text = text;
    this.retro = retro;
    this.codes = new Map();
    this.links = new Map();
    this.mentions = new Map();
    this.images = new Map();
    this.brackets = null;
  }

  parse(scope) {
    const text = this.text;
    const output = [];
    let pending = '';
    const flush = () => {
      if (pending !== '') {
        output.push({ t: 'text', v: pending });
        pending = '';
      }
    };
    const end = scope.end;
    let i = scope.start;
    while (i < end) {
      const c = text[i];
      if (c === '\\' && i + 1 < end && isAsciiPunctuation(text[i + 1])) {
        pending += text[i + 1];
        i += 2;
        continue;
      }
      if (c === '\n') {
        flush();
        output.push({ t: 'br' });
        i++;
        continue;
      }
      if (c === '`') {
        const code = this.code(i);
        if (code.isSpan && code.end <= end) {
          flush();
          output.push({ t: 'code', v: text.slice(code.contentStart, code.contentEnd) });
          i = code.end;
        } else {
          const runEnd = this.runEnd(i, '`', end);
          pending += text.slice(i, runEnd);
          i = runEnd;
        }
        continue;
      }
      let found = scope.inLink ? null : this.atom(i, scope);
      if (found === null && (c === '*' || c === '_' || c === '~') && scope.depth < MAX_INLINE_DEPTH) {
        found = this.emphasis(i, scope);
      }
      if (found !== null) {
        flush();
        output.push(found.node);
        i = found.next;
        continue;
      }
      pending += c;
      i++;
    }
    flush();
    return output;
  }

  atom(i, scope) {
    const mention = this.mention(i);
    if (mention && mention.end <= scope.end) {
      return { next: mention.end, node: { t: 'mention', id: mention.id, name: mention.text } };
    }
    const image = this.image(i);
    if (image && image.end <= scope.end) {
      return { next: image.end, node: { t: 'img', blob: image.id, alt: image.text } };
    }
    const link = this.link(i);
    if (link && link.end <= scope.end) {
      const label = { start: i + 1, end: link.labelEnd, inLink: true, depth: scope.depth + 1, open: OPEN_NONE };
      return { next: link.end, node: { t: 'a', href: link.href, c: this.parse(label) } };
    }
    return null;
  }

  emphasis(i, scope) {
    const text = this.text;
    const { start, end } = scope;
    const c = text[i];
    let [style, delimiter, length] = ['i', STAR, 0];
    if (c === '*' && this.opens(i, 2, scope, OPEN_BOLD) && text[i + 1] === '*') {
      [style, delimiter, length] = ['b', DOUBLE, 2];
    } else if (c === '~' && this.opens(i, 2, scope, OPEN_STRIKE) && text[i + 1] === '~') {
      [style, delimiter, length] = ['s', DOUBLE, 2];
    } else if (c === '_' && this.opens(i, 1, scope, OPEN_UNDERSCORE_ITALIC)
      && (i === start || !isAsciiAlphanumeric(text[i - 1]))) {
      [style, delimiter, length] = ['i', UNDERSCORE, 1];
    }
    let closer = length > 0 ? this.findCloser(c, delimiter, i + length, end, scope.inLink) : -1;
    if (closer < 0 && c === '*' && this.opens(i, 1, scope, OPEN_STAR_ITALIC)) {
      [style, delimiter, length] = ['i', STAR, 1];
      closer = this.findCloser(c, delimiter, i + 1, end, scope.inLink);
    }
    if (closer < 0) {
      return null;
    }
    let opened = c === '*' ? OPEN_STAR_ITALIC : OPEN_UNDERSCORE_ITALIC;
    if (style === 'b') {
      opened = OPEN_BOLD;
    } else if (style === 's') {
      opened = OPEN_STRIKE;
    }
    const inner = { ...scope, start: i + length, end: closer, depth: scope.depth + 1, open: scope.open | opened };
    return { next: closer + length, node: { t: style, c: this.parse(inner) } };
  }

  opens(i, length, scope, delimiter) {
    return (scope.open & delimiter) === 0 && this.followedByContent(i, length, scope.end);
  }

  followedByContent(i, length, end) {
    return i + length < end && !isWhitespace(this.text[i + length]);
  }

  findCloser(c, delimiter, contentStart, end, inLink) {
    const text = this.text;
    let k = contentStart;
    while (k < end) {
      const skipped = this.skipToken(k, end, inLink);
      if (skipped > k) {
        k = skipped;
        continue;
      }
      if (text[k] !== c) {
        k++;
        continue;
      }
      const runEnd = this.runEnd(k, c, end);
      const run = runEnd - k;
      let candidate = -1;
      if (delimiter === DOUBLE) {
        candidate = run >= 2 ? runEnd - 2 : -1;
      } else if (delimiter === STAR) {
        candidate = run % 2 === 1 ? runEnd - 1 : -1;
      } else {
        candidate = run === 1 ? k : -1;
      }
      if (candidate > contentStart
        && !isWhitespace(text[candidate - 1])
        && (delimiter !== UNDERSCORE || candidate + 1 >= end || !isAsciiAlphanumeric(text[candidate + 1]))) {
        return candidate;
      }
      k = runEnd;
    }
    return -1;
  }

  // Mirrors the token branches of parse(), so a closer is never found inside a code span, link,
  // mention or image.
  skipToken(k, end, inLink) {
    const text = this.text;
    const c = text[k];
    if (c === '\\') {
      return k + 1 < end && isAsciiPunctuation(text[k + 1]) ? k + 2 : k;
    }
    if (c === '`') {
      const code = this.code(k);
      return code.isSpan && code.end <= end ? code.end : this.runEnd(k, '`', end);
    }
    if (inLink) {
      return k;
    }
    for (const token of [this.mention(k), this.image(k), this.link(k)]) {
      if (token && token.end <= end) {
        return token.end;
      }
    }
    return k;
  }

  code(i) {
    if (this.codes.has(i)) {
      return this.codes.get(i);
    }
    const text = this.text;
    const runEnd = this.runEnd(i, '`', text.length);
    const length = runEnd - i;
    let token = { end: runEnd, isSpan: false, contentStart: 0, contentEnd: 0 };
    let p = runEnd;
    while (p < text.length && text[p] !== '\n') {
      if (text[p] !== '`') {
        p++;
        continue;
      }
      const closeEnd = this.runEnd(p, '`', text.length);
      if (closeEnd - p === length) {
        let [contentStart, contentEnd] = [runEnd, p];
        const content = text.slice(contentStart, contentEnd);
        if (content.length >= 2 && content[0] === ' ' && content.at(-1) === ' ' && /[^ ]/.test(content)) {
          [contentStart, contentEnd] = [contentStart + 1, contentEnd - 1];
        }
        token = { end: closeEnd, isSpan: true, contentStart, contentEnd };
        break;
      }
      p = closeEnd;
    }
    this.codes.set(i, token);
    return token;
  }

  link(i) {
    if (this.text[i] !== '[') {
      return null;
    }
    if (!this.links.has(i)) {
      this.links.set(i, this.scanLink(i));
    }
    return this.links.get(i);
  }

  scanLink(i) {
    const text = this.text;
    const k = this.ends().escaped[i + 1];
    if (k + 1 >= text.length || k === i + 1 || text[k] !== ']' || text[k + 1] !== '(') {
      return null;
    }
    const hrefStart = k + 2;
    let hrefEnd = hrefStart;
    while (hrefEnd < text.length && hrefEnd - hrefStart <= MAX_HREF_LENGTH && isHrefChar(text[hrefEnd])) {
      hrefEnd++;
    }
    if (hrefEnd >= text.length || text[hrefEnd] !== ')') {
      return null;
    }
    const href = text.slice(hrefStart, hrefEnd);
    return isAllowedHref(href) ? { end: hrefEnd + 1, labelEnd: k, href } : null;
  }

  mention(i) {
    const text = this.text;
    if (!this.retro || text[i] !== '[') {
      return null;
    }
    if (this.mentions.has(i)) {
      return this.mentions.get(i);
    }
    let token = null;
    const nameStart = i + MENTION_PREFIX.length + 37;
    const id = text.startsWith(MENTION_PREFIX, i) ? parseUuid(text, i + MENTION_PREFIX.length) : null;
    if (id !== null && nameStart < text.length && text[nameStart - 1] === ':') {
      const nameEnd = this.scanLabel(nameStart, MAX_MENTION_NAME_LENGTH);
      if (nameEnd > nameStart && text.startsWith(']]', nameEnd)) {
        token = { end: nameEnd + 2, id, text: text.slice(nameStart, nameEnd) };
      }
    }
    this.mentions.set(i, token);
    return token;
  }

  image(i) {
    const text = this.text;
    if (!this.retro || text[i] !== '!') {
      return null;
    }
    if (this.images.has(i)) {
      return this.images.get(i);
    }
    let token = null;
    if (i + 1 < text.length && text[i + 1] === '[') {
      const altEnd = this.scanLabel(i + 2, MAX_IMAGE_ALT_LENGTH);
      const idStart = altEnd + IMAGE_PREFIX.length;
      const id = altEnd >= 0 && text.startsWith(IMAGE_PREFIX, altEnd) ? parseUuid(text, idStart) : null;
      if (id !== null && idStart + 36 < text.length && text[idStart + 36] === ')') {
        token = { end: idStart + 37, id, text: text.slice(i + 2, altEnd) };
      }
    }
    this.images.set(i, token);
    return token;
  }

  // Raw characters up to the first ']' or newline; -1 when the label is longer than allowed.
  scanLabel(start, maxLength) {
    const k = this.ends().raw[start];
    return k - start > maxLength ? -1 : k;
  }

  // For every index, the next ']' or newline: raw counts every character, escaped steps over
  // backslash escapes the way parse does. Scanning from each '[' instead makes a line of 32,000
  // '[' quadratic.
  ends() {
    if (this.brackets !== null) {
      return this.brackets;
    }
    const text = this.text;
    const raw = new Int32Array(text.length + 1);
    const escaped = new Int32Array(text.length + 1);
    raw[text.length] = escaped[text.length] = text.length;
    for (let k = text.length - 1; k >= 0; k--) {
      if (text[k] === ']' || text[k] === '\n') {
        raw[k] = escaped[k] = k;
        continue;
      }
      raw[k] = raw[k + 1];
      escaped[k] = text[k] === '\\' && k + 1 < text.length && isAsciiPunctuation(text[k + 1])
        ? escaped[k + 2]
        : escaped[k + 1];
    }
    this.brackets = { raw, escaped };
    return this.brackets;
  }

  runEnd(k, c, end) {
    while (k < end && this.text[k] === c) {
      k++;
    }
    return k;
  }
}
