import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { AdfError, EDIT_LIMITS, adfToPm, canonicalAdf, pmToAdf } from '../../../web/src/rich/adf.js';
import { project, ref, tokenRefs } from './_project.js';

const FIXTURES = fileURLToPath(new URL('../../fixtures/adf/', import.meta.url));

function fixtureDocs() {
  const docs = [];
  for (const file of readdirSync(FIXTURES).filter((f) => f.endsWith('.json')).sort()) {
    for (const c of JSON.parse(readFileSync(`${FIXTURES}${file}`, 'utf8')).cases) {
      if (c.adf?.type === 'doc' && c.adf.version === 1 && Array.isArray(c.adf.content)) {
        docs.push({ name: `${file}: ${c.name}`, adf: c.adf });
      }
    }
  }
  return docs;
}

const roundTrip = (adf) => pmToAdf(adfToPm(adf));
const doc = (...content) => ({ type: 'doc', version: 1, content });
const p = (...content) => ({ type: 'paragraph', content });
const t = (text, marks) => (marks ? { type: 'text', text, marks } : { type: 'text', text });
const R1 = 'AAAAAAAAAAAAAAAA';
const R2 = 'BBBBBBBBBBBBBBBB';

test('every ADF fixture, projected, survives pmToAdf(adfToPm(d)) canonical-equal', () => {
  const docs = fixtureDocs();
  assert.ok(docs.length >= 60, `fixtures found: ${docs.length}`);
  for (const { name, adf } of docs) {
    const projected = project(adf);
    const back = roundTrip(projected);
    assert.equal(canonicalAdf(back), canonicalAdf(projected), name);
    assert.deepEqual(tokenRefs(back), tokenRefs(projected), `${name}: tokens are neither lost nor cloned`);
  }
});

// The fixtures whose projection carries a token or a stash, each for a part the allow-list refuses on purpose
// (an unknown node, a hostile value, blockTaskItem, a heading in a quote, an image without its required layout).
// Any other fixture must open as it is,
// so a reader that starts refusing an allowed node fails here by name.
const TOKENISED = [
  'hostile.json: a hostile link makes the paragraph an island that keeps it',
  'hostile.json: an escaped lone surrogate reads as a replacement character',
  'hostile.json: colours that are not strict #rrggbb are dropped',
  'hostile.json: control characters make the paragraph an island',
  'hostile.json: hostile card urls are text',
  'hostile.json: hostile table, media and layout sizes are clamped or dropped',
  'hostile.json: inline nodes directly in the doc become a paragraph',
  'hostile.json: links outside http, https and mailto are dropped',
  'hostile.json: mistyped members are ignored',
  'hostile.json: unknown nodes flatten to text',
  'inline.json: a link with parentheses is percent-encoded',
  'inline.json: placeholder renders nothing',
  'islands.json: code language markup cannot hold',
  'islands.json: code that is also bold',
  'islands.json: heading inside a quote',
  'islands.json: nested task list keeps its nesting',
  'islands.json: subscript and superscript',
  'islands.json: text colours map palette hexes to theme tokens',
  'media.json: caption and inline media',
  'media.json: colliding names map by upload order',
  'media.json: external media is a link',
  'media.json: media resolves by filename',
  'media.json: svg is never an inline image',
  'media.json: the same media twice is the same attachment',
  'media.json: unresolved media is a placeholder',
];

test('only the fixtures with a refused part need a token; every other one opens as it is', () => {
  const tokenised = [];
  for (const { name, adf } of fixtureDocs()) {
    const projected = project(adf);
    if (canonicalAdf(projected) !== canonicalAdf(adf)) {
      tokenised.push(name);
    } else {
      assert.doesNotThrow(() => adfToPm(adf), name);
    }
  }
  assert.deepEqual(tokenised.sort(), [...TOKENISED].sort());
});

test('mark order is the only change ProseMirror makes to marks', () => {
  const adf = doc(p(t('x', [{ type: 'link', attrs: { href: 'https://e.example' } }, { type: 'strong' }, { type: 'em' }])));
  const back = roundTrip(adf);
  assert.deepEqual(back.content[0].content[0].marks.map((m) => m.type), ['strong', 'em', 'link']);
  assert.equal(canonicalAdf(back), canonicalAdf(adf));
});

test('attribute keys survive as they arrived, defaults and nulls included', () => {
  const adf = doc(
    { type: 'codeBlock', attrs: { language: null }, content: [t('x')] },
    { type: 'table', attrs: {}, content: [{ type: 'tableRow', content: [{ type: 'tableCell', attrs: { colspan: 1 }, content: [p()] }] }] },
    { type: 'paragraph' },
  );
  assert.deepEqual(roundTrip(adf), adf);
});

test('tokens, stashes and node marks round trip', () => {
  const adf = doc(
    { type: 'rsOpaque', attrs: { ref: R1 } },
    { type: 'paragraph', attrs: { rsKeep: R2 }, marks: [{ type: 'alignment', attrs: { align: 'center' } }, { type: 'rsMark', attrs: { ref: R1 } }], content: [
      t('a', [{ type: 'rsMark', attrs: { ref: R2 } }, { type: 'strong' }]),
      { type: 'rsOpaqueInline', attrs: { ref: R2 } },
      { type: 'status', attrs: { text: 'OK', color: 'green' }, marks: [{ type: 'rsMark', attrs: { ref: R1 } }] },
    ] },
    { type: 'mediaSingle', attrs: { layout: 'center' }, marks: [{ type: 'link', attrs: { href: 'https://e.example' } }], content: [
      { type: 'media', attrs: { type: 'file', id: 'm-1', collection: '' } },
    ] },
  );
  assert.equal(canonicalAdf(roundTrip(adf)), canonicalAdf(adf));
});

test('the strict reader refuses what the projection would never send', () => {
  const cases = [
    [doc({ type: 'extension', attrs: { extensionKey: 'x', extensionType: 'y' } }), 'unknown_node', 'content.0'],
    [doc(p(t('x', [{ type: 'link', attrs: { href: 'javascript:alert(1)' } }]))), 'invalid_attr', 'content.0.content.0.marks.0.attrs.href'],
    [doc(p(t('x', [{ type: 'textColor', attrs: { color: 'url(x)' } }]))), 'invalid_attr', 'content.0.content.0.marks.0.attrs.color'],
    [doc(p(t('x', [{ type: 'annotation', attrs: { id: 'a' } }]))), 'unknown_mark', 'content.0.content.0.marks.0'],
    [doc(p(t('x', [{ type: 'code' }, { type: 'strong' }]))), 'invalid_mark', 'content.0.content.0.marks'],
    [doc({ type: 'rsOpaque', attrs: {} }), 'missing_attr', 'content.0.attrs.ref'],
    [doc({ type: 'rsOpaque', attrs: { ref: 'short' } }), 'invalid_attr', 'content.0.attrs.ref'],
    [doc(p({ type: 'rsOpaque', attrs: { ref: R1 } })), 'invalid_nesting', 'content.0.content.0'],
    [doc({ type: 'paragraph', attrs: { uniqueness: 1 } }), 'unknown_attr', 'content.0.attrs.uniqueness'],
    [doc({ type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableCell', attrs: { colspan: 2, colwidth: [10] }, content: [p()] }] }] }), 'invalid_attr', 'content.0.content.0.content.0.attrs.colwidth'],
    [doc(p(t('a\u0000b'))), 'invalid_text', 'content.0.content.0'],
    [doc(p(t('\ud800'))), 'invalid_text', 'content.0.content.0'],
    [doc(p(t('a\nb'))), 'invalid_text', 'content.0.content.0'],
    [doc({ type: 'layoutSection', content: [{ type: 'layoutColumn', attrs: { width: 100 }, content: [p()] }] }), 'invalid_nesting', 'content.0.content'],
    [doc({ type: 'bulletList', content: [t('loose')] }), 'invalid_nesting', 'content.0.content.0'],
    [doc({ type: 'blockquote', marks: [{ type: 'alignment', attrs: { align: 'center' } }], content: [p()] }), 'invalid_mark', 'content.0.marks.0'],
    [doc(p({ type: 'status', attrs: { text: 'S', color: 'red' }, marks: [{ type: 'strong' }] })), 'invalid_mark', 'content.0.content.0.marks.0'],
    [{ type: 'doc', version: 2, content: [] }, 'not_doc', ''],
  ];
  for (const [adf, code, path] of cases) {
    assert.throws(() => adfToPm(adf), (error) => error instanceof AdfError && error.code === code && error.path === path, `${code} ${path}`);
  }
});

test('names inherited from Object.prototype are unknown, not crashes', () => {
  for (const name of ['constructor', 'toString', '__proto__', 'hasOwnProperty']) {
    const mark = JSON.parse(`{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":${JSON.stringify(name)}}]}]}]}`);
    assert.throws(() => adfToPm(mark), (e) => e instanceof AdfError && e.code === 'unknown_mark', `mark ${name}`);
    const attr = JSON.parse(`{"type":"doc","version":1,"content":[{"type":"paragraph","attrs":{${JSON.stringify(name)}:"x"}}]}`);
    assert.throws(() => adfToPm(attr), (e) => e instanceof AdfError && e.code === 'unknown_attr', `attribute ${name}`);
  }
});

test('the reader accepts what AdfSchema.cs accepts at its limits', () => {
  const ok = [
    doc(p(t('a'), { type: 'hardBreak', attrs: { localId: 'b1' } }, t('b'))),
    doc({ type: 'mediaSingle', attrs: { layout: 'center' }, content: [{ type: 'media', attrs: { type: 'external', url: 'data:image/png;base64,AA', width: 0, height: 0 } }] }),
    doc({ type: 'embedCard', attrs: { url: 'https://e.example', layout: 'center', originalWidth: 100000, originalHeight: 20000 } }),
    doc({ type: 'mediaGroup', content: [{ type: 'media', attrs: { type: 'file', id: 'x'.repeat(255), collection: 'c'.repeat(255) } }] }),
    doc(p({ type: 'mention', attrs: { id: 'abc', accessLevel: 'a'.repeat(32) } })),
    doc({ type: 'expand', attrs: { title: '\u{1F600}'.repeat(255) }, content: [p()] }),
    doc({ type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'bulletList', content: [{ type: 'listItem', content: [p()] }] }] }] }),
    doc({ type: 'codeBlock', content: [t('a\r\nb')] }),
  ];
  for (const adf of ok) {
    assert.equal(canonicalAdf(roundTrip(adf)), canonicalAdf(adf), JSON.stringify(adf).slice(0, 80));
  }
});

test('the reader refuses what AdfSchema.cs refuses at its limits', () => {
  const media = (attrs) => doc({ type: 'mediaGroup', content: [{ type: 'media', attrs: { type: 'file', ...attrs } }] });
  const cases = [
    [media({ id: 'x'.repeat(256) }), 'content.0.content.0.attrs.id'],
    [media({ id: '' }), 'content.0.content.0.attrs.id'],
    [doc(p({ type: 'mention', attrs: { id: 'abc', accessLevel: 'a'.repeat(33) } })), 'content.0.content.0.attrs.accessLevel'],
    [doc({ type: 'expand', attrs: { title: 'a\u0085b' }, content: [p()] }), 'content.0.attrs.title'],
    [doc({ type: 'panel', attrs: { panelType: 'info', panelIcon: 'a\nb' }, content: [p()] }), 'content.0.attrs.panelIcon'],
    [doc(p(t('x', [{ type: 'link', attrs: { href: 'https://e.example/\u0085' } }]))), 'content.0.content.0.marks.0.attrs.href'],
  ];
  for (const [adf, path] of cases) {
    assert.throws(() => adfToPm(adf), (e) => e instanceof AdfError && e.code === 'invalid_attr' && e.path === path, path);
  }
  assert.throws(() => adfToPm(doc(p(t('a\u009fb')))), (e) => e.code === 'invalid_text');
  assert.throws(() => adfToPm(doc({ type: 'mediaSingle', content: [{ type: 'media', attrs: { type: 'file', id: 'm' } }] })),
    (e) => e.code === 'missing_attr' && e.path === 'content.0.attrs.layout');
  assert.throws(() => adfToPm(doc({ type: 'mediaGroup', content: [{ type: 'rsOpaque', attrs: { ref: R1 } }] })),
    (e) => e.code === 'invalid_nesting');
});

test('alignment and indentation are allowed by the parent, as AdfSchema contexts say', () => {
  const aligned = (type, mark, extra = {}) => ({ type, ...extra, marks: [mark], content: [t('x')] });
  const center = { type: 'alignment', attrs: { align: 'center' } };
  const indent = { type: 'indentation', attrs: { level: 1 } };
  const cell = (child) => doc({ type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableCell', content: [child] }] }] });
  const ok = [
    doc(aligned('paragraph', center)),
    doc(aligned('heading', indent, { attrs: { level: 1 } })),
    doc({ type: 'layoutSection', content: [0, 1].map(() => ({ type: 'layoutColumn', attrs: { width: 50 }, content: [aligned('paragraph', indent)] })) }),
    cell(aligned('paragraph', center)),
    cell(aligned('heading', indent, { attrs: { level: 2 } })),
  ];
  for (const adf of ok) {
    assert.equal(canonicalAdf(roundTrip(adf)), canonicalAdf(adf));
  }
  const refused = [
    [doc({ type: 'bulletList', content: [{ type: 'listItem', content: [aligned('paragraph', center)] }] }), 'content.0.content.0.content.0.marks.0'],
    [doc({ type: 'panel', attrs: { panelType: 'info' }, content: [aligned('paragraph', center)] }), 'content.0.content.0.marks.0'],
    [doc({ type: 'blockquote', content: [aligned('paragraph', indent)] }), 'content.0.content.0.marks.0'],
    [doc({ type: 'expand', content: [aligned('paragraph', center)] }), 'content.0.content.0.marks.0'],
    [cell(aligned('paragraph', indent)), 'content.0.content.0.content.0.content.0.marks.0'],
  ];
  for (const [adf, path] of refused) {
    assert.throws(() => adfToPm(adf), (e) => e instanceof AdfError && e.code === 'invalid_mark' && e.path === path, path);
  }
});

test('a document over the editing caps is refused, not opened', () => {
  const many = doc(...Array.from({ length: EDIT_LIMITS.nodes + 1 }, () => ({ type: 'paragraph' })));
  assert.throws(() => adfToPm(many), (e) => e instanceof AdfError && e.code === 'too_many_nodes');
  assert.doesNotThrow(() => adfToPm(doc(...Array.from({ length: EDIT_LIMITS.nodes }, () => ({ type: 'paragraph' })))));
  let node = { type: 'paragraph' };
  // Each wrap adds two levels: 23 put the paragraph at depth 47, one more at 49.
  for (let wraps = 0; wraps < Math.floor((EDIT_LIMITS.depth - 1) / 2); wraps++) {
    node = { type: 'bulletList', content: [{ type: 'listItem', content: [node] }] };
  }
  assert.doesNotThrow(() => adfToPm(doc(node)));
  node = { type: 'bulletList', content: [{ type: 'listItem', content: [node] }] };
  assert.throws(() => adfToPm(doc(node)), (e) => e instanceof AdfError && e.code === 'too_deep');
});

test('the test projection hashes refs the way the server does: 16 base64url characters', () => {
  assert.match(ref({ type: 'extension' }), /^[A-Za-z0-9_-]{16}$/);
  assert.equal(ref({ b: 1, a: 2 }), ref({ a: 2, b: 1 }));
});

test('a code block keeps its newlines and tabs as text', () => {
  const adf = doc({ type: 'codeBlock', attrs: { language: 'js' }, content: [t('a\n\tb')] });
  assert.deepEqual(roundTrip(adf), adf);
});
