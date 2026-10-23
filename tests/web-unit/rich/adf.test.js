import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { AdfError, adfToPm, canonicalAdf, pmToAdf } from '../../../web/src/rich/adf.js';
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

test('most fixture documents need no token at all', () => {
  const docs = fixtureDocs();
  const clean = docs.filter(({ adf }) => tokenRefs(project(adf)).size === 0);
  assert.ok(clean.length / docs.length > 0.6, `${clean.length} of ${docs.length} fixtures project without tokens`);
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

test('the test projection hashes refs the way the server does: 16 base64url characters', () => {
  assert.match(ref({ type: 'extension' }), /^[A-Za-z0-9_-]{16}$/);
  assert.equal(ref({ b: 1, a: 2 }), ref({ a: 2, b: 1 }));
});

test('a code block keeps its newlines and tabs as text', () => {
  const adf = doc({ type: 'codeBlock', attrs: { language: 'js' }, content: [t('a\n\tb')] });
  assert.deepEqual(roundTrip(adf), adf);
});
