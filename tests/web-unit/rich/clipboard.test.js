import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EditorState, TextSelection } from 'prosemirror-state';
import { adfToPm, nodeToAdf, pmToAdf } from '../../../web/src/rich/adf.js';
import { parseClipboard, plainTextSlice, serializeSlice } from '../../../web/src/rich/clipboard.js';
import { tokenRefs } from './_project.js';

const R1 = 'AAAAAAAAAAAAAAAA';
const PICKED = '557058:aaaa-bbbb';
const STRANGER = '557058:cccc-dddd';
const OURS = 'f1a1b2c3-0000-4000-8000-000000000001';
const FOREIGN = 'f1a1b2c3-0000-4000-8000-0000000000ff';
const session = () => ({ media: new Set([OURS]), mentions: new Set([PICKED]) });
const payload = (content, openStart = 0, openEnd = 0) => JSON.stringify({ rsClip: 1, openStart, openEnd, content });
const adfOf = (slice) => {
  const out = [];
  slice.content.forEach((n) => out.push(nodeToAdf(n)));
  return out;
};
const walk = (value, visit) => {
  if (Array.isArray(value)) {
    value.forEach((v) => walk(v, visit));
  } else if (value !== null && typeof value === 'object') {
    visit(value);
    Object.values(value).forEach((v) => walk(v, visit));
  }
};

test('a planted payload comes out clean', () => {
  const planted = payload([
    { type: 'extension', attrs: { extensionKey: 'evil', extensionType: 'com.example', parameters: { x: 1 } } },
    { type: 'bodiedExtension', attrs: { extensionKey: 'e', extensionType: 't' }, content: [{ type: 'paragraph' }] },
    { type: 'paragraph', attrs: { localId: 'dup', rsKeep: R1 }, marks: [{ type: 'rsMark', attrs: { ref: R1 } }], content: [
      { type: 'text', text: 'click', marks: [{ type: 'link', attrs: { href: 'javascript:alert(1)' } }] },
      { type: 'text', text: ' red', marks: [{ type: 'textColor', attrs: { color: 'red;background:url(x)' } }] },
      { type: 'text', text: ' ok', marks: [{ type: 'link', attrs: { href: 'https://ok.example/' } }, { type: 'rsMark', attrs: { ref: R1 } }] },
      { type: 'mention', attrs: { id: STRANGER, text: '@Spam Target' } },
      { type: 'mention', attrs: { id: PICKED, text: '@Picked' } },
      { type: 'inlineExtension', attrs: { extensionKey: 'x', extensionType: 'y' } },
      { type: 'rsOpaqueInline', attrs: { ref: R1 } },
      { type: 'inlineCard', attrs: { url: 'data:text/html,<script>' } },
      { type: 'status', attrs: { text: 'HEX', color: '#ff0000' } },
    ] },
    { type: 'rsOpaque', attrs: { ref: R1 } },
    { type: 'mediaSingle', attrs: { layout: 'center' }, content: [{ type: 'media', attrs: { type: 'external', url: 'https://tracker.example/p.gif' } }] },
    { type: 'mediaSingle', attrs: { layout: 'center' }, content: [{ type: 'media', attrs: { type: 'file', id: FOREIGN, collection: '' } }] },
    { type: 'mediaSingle', attrs: { layout: 'center' }, content: [{ type: 'media', attrs: { type: 'file', id: OURS, collection: '' } }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'bad', marks: [{ type: 'backgroundColor', attrs: { color: 'expression(1)' } }] }] },
  ]);
  const slice = parseClipboard(planted, session());
  assert.ok(slice);
  const out = adfOf(slice);

  const types = new Set();
  const hrefs = [];
  const colors = [];
  const mentions = [];
  const media = [];
  walk(out, (node) => {
    if (typeof node.type === 'string') {
      types.add(node.type);
    }
    if (node.type === 'link') {
      hrefs.push(node.attrs.href);
    }
    if (node.type === 'textColor' || node.type === 'backgroundColor') {
      colors.push(node.attrs.color);
    }
    if (node.type === 'mention') {
      mentions.push(node.attrs.id);
    }
    if (node.type === 'media') {
      media.push(node.attrs.id);
    }
  });
  for (const banned of ['extension', 'bodiedExtension', 'inlineExtension', 'rsOpaque', 'rsOpaqueInline', 'rsMark', 'inlineCard', 'status']) {
    assert.ok(!types.has(banned), `${banned} is dropped`);
  }
  assert.deepEqual(hrefs, ['https://ok.example/']);
  assert.deepEqual(colors, []);
  assert.deepEqual(mentions, [PICKED]);
  assert.deepEqual(media, [OURS]);
  assert.equal(tokenRefs(out).size, 0);
  assert.ok(!JSON.stringify(out).includes('rsKeep'));
  assert.ok(!JSON.stringify(out).includes('"dup"'), 'pasted local ids are not repeated');
  assert.equal(slice.content.textBetween(0, slice.content.size, '|', (n) => `<${n.type.name}>`), 'click red ok@Spam Target<mention>|<media>|bad', 'the unpicked mention stays as plain text');
});

test('pasted tasks get fresh local ids', () => {
  const task = { type: 'taskList', attrs: { localId: 'L' }, content: [{ type: 'taskItem', attrs: { localId: 'I', state: 'TODO' }, content: [{ type: 'text', text: 'do' }] }] };
  const [list] = adfOf(parseClipboard(payload([task]), session()));
  assert.notEqual(list.attrs.localId, 'L');
  assert.notEqual(list.content[0].attrs.localId, 'I');
  assert.match(list.content[0].attrs.localId, /^[A-Za-z0-9_-]{1,64}$/);
});

test('a copy from the editor pastes back the same content, without its tokens', () => {
  const doc = adfToPm({ type: 'doc', version: 1, content: [
    { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Title', marks: [{ type: 'em' }] }] },
    { type: 'rsOpaque', attrs: { ref: R1 } },
    { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'one' }] }] }] },
  ] });
  const state = EditorState.create({ doc });
  const slice = TextSelection.create(doc, 1, doc.content.size - 2).content();
  const back = parseClipboard(serializeSlice(slice), session());
  assert.equal(back.openStart, slice.openStart);
  assert.equal(back.openEnd, slice.openEnd);
  assert.equal(back.content.childCount, slice.content.childCount - 1, 'the opaque block is not pasted');
  const pasted = state.apply(state.tr.replaceWith(0, doc.content.size, back.content));
  assert.deepEqual(pmToAdf(pasted.doc).content.map((n) => n.type), ['heading', 'bulletList']);
});

test('garbage and oversized payloads paste nothing', () => {
  for (const text of ['', 'not json', '[]', '{"rsClip":2,"content":[]}', '{"rsClip":1,"content":{}}', payload([]), 'x'.repeat(1024 * 1024 + 1)]) {
    assert.equal(parseClipboard(text, session()), null, text.slice(0, 30));
  }
});

test('payloads nested past the paste depth paste nothing', () => {
  let node = { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph' }] }] };
  for (let i = 0; i < 20; i++) {
    node = { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph' }, node] }] };
  }
  assert.equal(parseClipboard(payload([node]), session()), null);
});

test('open depths are clamped to what the content can open', () => {
  const slice = parseClipboard(payload([{ type: 'paragraph', content: [{ type: 'text', text: 'x' }] }], 9, 9), session());
  assert.equal(slice.openStart, 1);
  assert.equal(slice.openEnd, 1);
});

test('plain text becomes paragraphs, or raw text in code', () => {
  const slice = plainTextSlice('a\r\nb\u0007', false);
  assert.deepEqual(adfOf(slice).map((n) => n.content?.[0]?.text), ['a', 'b ']);
  const code = plainTextSlice('x\ny', true);
  assert.equal(code.content.firstChild.text, 'x\ny');
  assert.equal(plainTextSlice('', false), null);
});
