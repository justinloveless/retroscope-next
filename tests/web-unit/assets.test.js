import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { brotliDecompressSync } from 'node:zlib';
import {
  CARD_FRAMES, CARD_GUTTER, CARD_HEIGHT, CARD_WIDTH, ICONS, decodePng, encodePng, packCards,
} from '../../tools/make-sprite.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const assets = join(root, 'web', 'assets');
const read = (name) => readFileSync(join(assets, name));

// Deck values (Core/Poker/Deck.cs), the between separator, the unknown-seat mark and the pill text.
const DECK_GLYPHS = '1 2 3 5 8 13 21 - ? x pts Abstain Lock In Reveal';

function readUIntBase128(buf, at) {
  let value = 0;
  for (let i = 0; i < 5; i++) {
    const byte = buf[at + i];
    value = value * 128 + (byte & 0x7f);
    if ((byte & 0x80) === 0) {
      return [value, at + i + 1];
    }
  }
  throw new Error('UIntBase128 longer than 5 bytes');
}

// The WOFF2 table directory, with each table's bytes from the decompressed stream (WOFF2 §4, §5).
function woff2Tables(buf) {
  assert.equal(buf.toString('latin1', 0, 4), 'wOF2');
  const numTables = buf.readUInt16BE(12);
  const compressedSize = buf.readUInt32BE(20);
  const known = { 0: 'cmap', 5: 'name', 10: 'glyf', 11: 'loca' };
  const entries = [];
  let at = 48;
  for (let i = 0; i < numTables; i++) {
    const flags = buf[at++];
    let tag = known[flags & 0x3f] ?? `#${flags & 0x3f}`;
    if ((flags & 0x3f) === 63) {
      tag = buf.toString('latin1', at, at + 4);
      at += 4;
    }
    const version = flags >> 6;
    let length;
    [length, at] = readUIntBase128(buf, at);
    const transformed = tag === 'glyf' || tag === 'loca' ? version !== 3 : version !== 0;
    if (transformed) {
      [length, at] = readUIntBase128(buf, at);
    }
    entries.push({ tag, length });
  }
  const stream = brotliDecompressSync(buf.subarray(at, at + compressedSize));
  const tables = {};
  let offset = 0;
  for (const { tag, length } of entries) {
    tables[tag] = stream.subarray(offset, offset + length);
    offset += length;
  }
  assert.equal(offset, stream.length, 'table lengths add up to the decompressed stream');
  return tables;
}

function cmapCovers(cmap) {
  const subtables = [];
  for (let i = 0; i < cmap.readUInt16BE(2); i++) {
    const platform = cmap.readUInt16BE(4 + i * 8);
    const encoding = cmap.readUInt16BE(6 + i * 8);
    subtables.push({ platform, encoding, table: cmap.subarray(cmap.readUInt32BE(8 + i * 8)) });
  }
  const unicode = subtables.find((s) => (s.platform === 3 && s.encoding === 1) || s.platform === 0);
  assert.ok(unicode, 'a Unicode cmap subtable exists');
  const t = unicode.table;
  assert.equal(t.readUInt16BE(0), 4, 'format 4 subtable');
  const segments = t.readUInt16BE(6) / 2;
  const ends = 14;
  const starts = ends + segments * 2 + 2;
  const deltas = starts + segments * 2;
  const rangeOffsets = deltas + segments * 2;
  return (cp) => {
    for (let s = 0; s < segments; s++) {
      if (cp > t.readUInt16BE(ends + s * 2)) {
        continue;
      }
      const start = t.readUInt16BE(starts + s * 2);
      if (cp < start) {
        return false;
      }
      const delta = t.readUInt16BE(deltas + s * 2);
      const rangeOffset = t.readUInt16BE(rangeOffsets + s * 2);
      if (rangeOffset === 0) {
        return ((cp + delta) & 0xffff) !== 0;
      }
      const glyph = t.readUInt16BE(rangeOffsets + s * 2 + rangeOffset + (cp - start) * 2);
      return glyph !== 0;
    }
    return false;
  };
}

function nameStrings(name) {
  const count = name.readUInt16BE(2);
  const storage = name.readUInt16BE(4);
  const strings = [];
  for (let i = 0; i < count; i++) {
    const rec = 6 + i * 12;
    const platform = name.readUInt16BE(rec);
    const length = name.readUInt16BE(rec + 8);
    const offset = storage + name.readUInt16BE(rec + 10);
    const bytes = name.subarray(offset, offset + length);
    strings.push(platform === 1 ? bytes.toString('latin1') : Buffer.from(bytes).swap16().toString('utf16le'));
  }
  return strings;
}

test('cards.json lays out the nine frames left to right with a transparent gutter', () => {
  const map = JSON.parse(read('cards.json'));
  assert.deepEqual(Object.keys(map.frames), CARD_FRAMES);
  CARD_FRAMES.forEach((name, i) => {
    assert.deepEqual(map.frames[name], { x: i * (CARD_WIDTH + CARD_GUTTER), y: 0, w: CARD_WIDTH, h: CARD_HEIGHT });
  });
  assert.equal(map.width, CARD_FRAMES.length * CARD_WIDTH + (CARD_FRAMES.length - 1) * CARD_GUTTER);
  assert.equal(map.height, CARD_HEIGHT);
});

test('each sprite frame holds exactly its source card and the gutters are transparent', () => {
  const map = JSON.parse(read('cards.json'));
  const sprite = decodePng(read('cards.png'));
  assert.equal(sprite.width, map.width);
  assert.equal(sprite.height, map.height);
  const stride = sprite.width * 4;
  const covered = new Set();
  for (const [name, { x, y, w, h }] of Object.entries(map.frames)) {
    const source = decodePng(read(join('cards', `${name}.png`)));
    for (let row = 0; row < h; row++) {
      const start = (y + row) * stride + x * 4;
      const actual = sprite.pixels.subarray(start, start + w * 4);
      assert.ok(actual.equals(source.pixels.subarray(row * w * 4, (row + 1) * w * 4)), `${name} row ${row}`);
    }
    for (let col = x; col < x + w; col++) {
      covered.add(col);
    }
  }
  for (let col = 0; col < sprite.width; col++) {
    if (covered.has(col)) {
      continue;
    }
    for (let row = 0; row < sprite.height; row++) {
      assert.equal(sprite.pixels[row * stride + col * 4 + 3], 0, `gutter pixel ${col},${row} is transparent`);
    }
  }
});

test('the committed sprite is what make-sprite produces from the source frames', () => {
  const frames = CARD_FRAMES.map((name) => ({ name, image: decodePng(read(join('cards', `${name}.png`))) }));
  assert.ok(encodePng(packCards(frames).image).equals(read('cards.png')));
});

test('cards.png stays near its budget', () => {
  assert.ok(read('cards.png').length <= 12 * 1024, `${read('cards.png').length} bytes`);
});

test('the m6x11plus WOFF2 subset covers the deck glyphs and printable Latin-1', () => {
  const font = read('m6x11plus.woff2');
  const covers = cmapCovers(woff2Tables(font).cmap);
  for (const ch of DECK_GLYPHS) {
    assert.ok(covers(ch.codePointAt(0)), `deck glyph ${JSON.stringify(ch)}`);
  }
  // The font itself has no grave accent; every other printable ASCII character is present.
  for (let cp = 0x21; cp <= 0x7e; cp++) {
    if (cp !== 0x60) {
      assert.ok(covers(cp), `U+${cp.toString(16).padStart(4, '0')}`);
    }
  }
  for (const ch of 'àéñöüçÀÉ×') {
    assert.ok(covers(ch.codePointAt(0)), `Latin-1 ${ch}`);
  }
  assert.ok(!covers(0x0141) && !covers(0x03bc), 'Ł and Greek μ, outside the subset, are dropped');
  assert.ok(font.length <= 10 * 1024, `${font.length} bytes`);
});

test('the font keeps its author and licence strings', () => {
  const names = nameStrings(woff2Tables(read('m6x11plus.woff2')).name);
  assert.ok(names.includes('m6x11plus'));
  assert.ok(names.includes('Daniel Linssen'));
  assert.ok(names.includes('Free to use with attribution'));
});

test('icons.svg holds exactly the listed Lucide symbols and nothing executable', () => {
  const svg = read('icons.svg').toString('utf8');
  const ids = [...svg.matchAll(/<symbol id="([^"]+)"/g)].map((m) => m[1]);
  assert.deepEqual(ids, ICONS);
  assert.equal(new Set(ids).size, ids.length);
  assert.doesNotMatch(svg, /<script|<foreignObject|\son[a-z]+=|javascript:|href=/i);
});

test('THIRD_PARTY.md credits the font, the icons and the music track or its absence', () => {
  const notice = readFileSync(join(root, 'THIRD_PARTY.md'), 'utf8');
  assert.match(notice, /m6x11plus/);
  assert.match(notice, /Daniel Linssen/);
  assert.match(notice, /https:\/\/managore\.itch\.io\/m6x11/);
  assert.match(notice, /free to use with attribution/i);
  assert.match(notice, /licence page checked on \d{4}-\d{2}-\d{2}/i);
  assert.match(notice, /Lucide/);
  assert.match(notice, /ISC License/);
  if (existsSync(join(assets, 'music.mp3'))) {
    assert.match(notice, /## music\.mp3[\s\S]*Licence:/);
  } else {
    assert.match(notice, /music\.mp3[^\n]*not shipped/i);
  }
});
