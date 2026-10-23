import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { allowList, contextMarks } from '../../../web/src/rich/rules.js';
import { BLOCK_MARKS, STASH, childTypes, isComplete, schema } from '../../../web/src/rich/schema.js';

const OWN_ATTRS = [STASH, 'adfShape'];

// Every node type ProseMirror's content expression for `type` accepts at some position.
function acceptedChildren(type) {
  const seen = new Set();
  const found = new Set();
  const queue = [type.contentMatch];
  while (queue.length > 0) {
    const match = queue.pop();
    if (seen.has(match)) {
      continue;
    }
    seen.add(match);
    for (let i = 0; i < match.edgeCount; i++) {
      const { type: child, next } = match.edge(i);
      found.add(child.name);
      queue.push(next);
    }
  }
  return found;
}

test('the ProseMirror schema has exactly the allow-list nodes and marks', () => {
  assert.deepEqual(Object.keys(schema.nodes).sort(), Object.keys(allowList.nodes).sort());
  const textMarks = Object.keys(allowList.marks).filter((m) => !BLOCK_MARKS.has(m));
  assert.deepEqual(Object.keys(schema.marks).sort(), textMarks.sort());
});

test('each node and mark carries exactly its allow-listed attributes plus the editor-owned ones', () => {
  for (const [name, spec] of Object.entries(allowList.nodes)) {
    if (name === 'text' || name === 'doc') {
      continue;
    }
    const expected = [...Object.keys(spec.attrs), ...OWN_ATTRS, 'blockMarks'];
    if (['paragraph', 'heading'].includes(name)) {
      expected.push('align', 'indent');
    }
    assert.deepEqual(Object.keys(schema.nodes[name].spec.attrs).sort(), expected.sort(), name);
  }
  for (const [name, spec] of Object.entries(allowList.marks)) {
    if (!BLOCK_MARKS.has(name)) {
      assert.deepEqual(Object.keys(schema.marks[name].spec.attrs).sort(), [...Object.keys(spec.attrs), ...OWN_ATTRS].sort(), name);
    }
  }
});

test('each node accepts exactly the children the allow-list names', () => {
  for (const name of Object.keys(allowList.nodes)) {
    const type = schema.nodes[name];
    assert.deepEqual([...acceptedChildren(type)].sort(), childTypes(name).sort(), name);
  }
});

test('inline nodes are inline and code blocks hold unmarked text', () => {
  for (const [name, spec] of Object.entries(allowList.nodes)) {
    assert.equal(schema.nodes[name].isInline, spec.inline === true || name === 'text', name);
  }
  assert.equal(schema.nodes.codeBlock.spec.code, true);
  assert.equal(schema.nodes.codeBlock.markSet.length, 0);
});

test('tokens are atoms, and several rsMarks can cover one text', () => {
  for (const name of ['rsOpaque', 'rsOpaqueInline']) {
    assert.equal(schema.nodes[name].isAtom, true, name);
    assert.equal(schema.nodes[name].isLeaf, true, name);
  }
  const a = schema.marks.rsMark.create({ ref: 'AAAAAAAAAAAAAAAA' });
  const b = schema.marks.rsMark.create({ ref: 'BBBBBBBBBBBBBBBB' });
  assert.equal(b.addToSet(a.addToSet([])).length, 2);
});

test('code combines only with link and tokens', () => {
  const code = schema.marks.code.create();
  for (const name of ['strong', 'em', 'strike', 'underline', 'textColor', 'backgroundColor', 'subsup']) {
    assert.ok(code.type.excludes(schema.marks[name]), name);
  }
  assert.ok(!code.type.excludes(schema.marks.link));
  assert.ok(!code.type.excludes(schema.marks.rsMark));
});

test('attribute rules refuse what the server refuses', () => {
  const bad = [
    () => schema.marks.link.create({ href: 'javascript:alert(1)' }),
    () => schema.marks.textColor.create({ color: 'red' }),
    () => schema.marks.textColor.create({ color: '#fff' }),
    () => schema.nodes.heading.create({ level: 7 }),
    () => schema.nodes.tableCell.create({ colspan: 65 }),
    () => schema.nodes.tableCell.create({ colspan: null }),
    () => schema.nodes.date.create({ timestamp: '1e3' }),
    () => schema.nodes.mention.create({ id: 'a b' }),
    () => schema.nodes.rsOpaque.create({ ref: 'x' }),
  ];
  for (const make of bad) {
    assert.throws(make, RangeError);
  }
  assert.ok(schema.marks.link.create({ href: 'mailto:a@example.com' }));
  assert.ok(schema.nodes.date.create({ timestamp: '1767225600000' }));
});

test('a node made from defaults is incomplete until its required attributes are set', () => {
  assert.equal(isComplete(schema.nodes.taskItem.create()), false);
  assert.equal(isComplete(schema.nodes.taskItem.create({ localId: 'a', state: 'TODO' })), true);
  assert.equal(isComplete(schema.nodes.mediaSingle.createAndFill()), false);
});

test('toDOM never writes a style attribute', () => {
  const source = readFileSync(fileURLToPath(new URL('../../../web/src/rich/schema.js', import.meta.url)), 'utf8');
  assert.doesNotMatch(source, /\bstyle\s*:/);
});

test('the allow-list file is plain data: rules resolve and groups expand', () => {
  for (const [name, spec] of Object.entries(allowList.nodes)) {
    for (const rule of Object.values(spec.attrs)) {
      if (typeof rule === 'string') {
        assert.ok(allowList.rules[rule], `${name}: rule ${rule}`);
      }
    }
    for (const child of childTypes(name)) {
      assert.ok(Object.hasOwn(allowList.nodes, child), `${name}: child ${child}`);
    }
  }
});
