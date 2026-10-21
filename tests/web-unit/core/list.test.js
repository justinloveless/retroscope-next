import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { installDom } from './_dom.js';
import { reconcile } from '../../../web/src/core/list.js';

let doc;
let created;
let updated;

beforeEach(() => {
  doc = installDom();
  created = [];
  updated = [];
  delete globalThis.matchMedia;
});

const key = (item) => item.id;
function create(item) {
  created.push(item.id);
  const el = doc.createElement('li');
  el.textContent = item.label ?? item.id;
  return el;
}
function update(el, item) {
  updated.push(item.id);
  el.textContent = item.label ?? item.id;
}
const items = (...ids) => ids.map((id) => ({ id }));
const order = (ul) => ul.childNodes.map((n) => n.textContent);

// Counts insertBefore/removeChild on the list element itself (not on item text).
function track(ul) {
  const ops = { inserts: 0, removes: 0 };
  const insertBefore = ul.insertBefore.bind(ul);
  const removeChild = ul.removeChild.bind(ul);
  ul.insertBefore = (node, ref) => {
    ops.inserts++;
    return insertBefore(node, ref);
  };
  ul.removeChild = (node) => {
    ops.removes++;
    return removeChild(node);
  };
  ops.reset = () => {
    ops.inserts = 0;
    ops.removes = 0;
  };
  return ops;
}

function render(ul, list, options) {
  return reconcile(ul, list, key, create, update, options);
}

test('first render creates every item in order', () => {
  const ul = doc.createElement('ul');
  render(ul, items('a', 'b', 'c'));
  assert.deepEqual(order(ul), ['a', 'b', 'c']);
  assert.deepEqual(created, ['a', 'b', 'c']);
});

test('inserts keep existing nodes and only add the new ones', () => {
  const ul = doc.createElement('ul');
  const ops = track(ul);
  render(ul, items('a', 'c'));
  const [a, c] = ul.childNodes;
  created = [];
  ops.reset();
  render(ul, items('x', 'a', 'b', 'c', 'y'));
  assert.deepEqual(order(ul), ['x', 'a', 'b', 'c', 'y']);
  assert.deepEqual(created, ['x', 'b', 'y']);
  assert.equal(ul.childNodes[1], a);
  assert.equal(ul.childNodes[3], c);
  assert.equal(ops.inserts, 3);
});

test('removes drop only the missing keys', () => {
  const ul = doc.createElement('ul');
  const ops = track(ul);
  render(ul, items('a', 'b', 'c', 'd'));
  const [a, , c] = ul.childNodes;
  ops.reset();
  render(ul, items('a', 'c'));
  assert.deepEqual(order(ul), ['a', 'c']);
  assert.equal(ul.childNodes[0], a);
  assert.equal(ul.childNodes[1], c);
  assert.equal(ops.inserts, 0);
  assert.equal(ops.removes, 2);
});

test('moving one item moves one node, not its neighbours', () => {
  const ul = doc.createElement('ul');
  const ops = track(ul);
  render(ul, items('a', 'b', 'c', 'd', 'e'));
  const before = new Map(ul.childNodes.map((n) => [n.textContent, n]));
  ops.reset();
  render(ul, items('b', 'c', 'd', 'e', 'a'));
  assert.deepEqual(order(ul), ['b', 'c', 'd', 'e', 'a']);
  assert.equal(ops.inserts, 1);
  for (const node of ul.childNodes) {
    assert.equal(node, before.get(node.textContent));
  }
});

test('reversal and shuffles end in the requested order with minimal moves', () => {
  const ul = doc.createElement('ul');
  const ops = track(ul);
  render(ul, items('a', 'b', 'c', 'd', 'e', 'f'));
  ops.reset();
  render(ul, items('f', 'e', 'd', 'c', 'b', 'a'));
  assert.deepEqual(order(ul), ['f', 'e', 'd', 'c', 'b', 'a']);
  assert.equal(ops.inserts, 5);

  ops.reset();
  render(ul, items('a', 'c', 'e', 'b', 'd', 'f'));
  assert.deepEqual(order(ul), ['a', 'c', 'e', 'b', 'd', 'f']);
  // Old positions in the new order are 5,3,1,4,2,0; the longest increasing run (1,4) stays.
  assert.equal(ops.inserts, 4);

  ops.reset();
  render(ul, items('c', 'e', 'b', 'd', 'f', 'a'));
  assert.deepEqual(order(ul), ['c', 'e', 'b', 'd', 'f', 'a']);
  assert.equal(ops.inserts, 1);
});

test('mixed insert, remove and move in one pass', () => {
  const ul = doc.createElement('ul');
  render(ul, items('a', 'b', 'c', 'd'));
  const d = ul.childNodes[3];
  render(ul, items('d', 'x', 'b'));
  assert.deepEqual(order(ul), ['d', 'x', 'b']);
  assert.equal(ul.childNodes[0], d);
});

test('update runs for kept items with their new data and index', () => {
  const ul = doc.createElement('ul');
  render(ul, [{ id: 'a', label: 'one' }]);
  render(ul, [{ id: 'b' }, { id: 'a', label: 'two' }]);
  assert.deepEqual(updated, ['a']);
  assert.deepEqual(order(ul), ['b', 'two']);
});

test('duplicate keys are refused', () => {
  const ul = doc.createElement('ul');
  assert.throws(() => render(ul, items('a', 'a')), /duplicate keys/);
});

test('delayed removal keeps the leaving node until done() and after the live ones', () => {
  const ul = doc.createElement('ul');
  render(ul, items('a', 'b', 'c'));
  let finish;
  render(ul, items('a', 'c', 'd'), {
    remove(el, done) {
      assert.equal(el.textContent, 'b');
      finish = done;
    },
  });
  assert.equal(order(ul).filter((t) => t !== 'b').join(), 'a,c,d');
  finish();
  assert.deepEqual(order(ul), ['a', 'c', 'd']);
});

test('flip animates moved nodes from their old position', () => {
  globalThis.matchMedia = () => ({ matches: false });
  const ul = doc.createElement('ul');
  doc.body.appendChild(ul);
  render(ul, items('a', 'b'));
  const [a, b] = ul.childNodes;
  const layout = new Map([
    [a, [{ top: 0, left: 0 }, { top: 20, left: 0 }]],
    [b, [{ top: 20, left: 0 }, { top: 0, left: 0 }]],
  ]);
  for (const [node, rects] of layout) {
    node.getBoundingClientRect = () => rects.shift();
  }
  render(ul, items('b', 'a'), { flip: { duration: 100 } });
  assert.equal(a.animations[0].keyframes[0].transform, 'translate(0px, -20px)');
  assert.equal(b.animations[0].keyframes[0].transform, 'translate(0px, 20px)');
  assert.equal(b.animations[0].options.duration, 100);
});

test('flip is skipped under prefers-reduced-motion', () => {
  globalThis.matchMedia = () => ({ matches: true });
  const ul = doc.createElement('ul');
  doc.body.appendChild(ul);
  render(ul, items('a', 'b'));
  const [, b] = ul.childNodes;
  let calls = 0;
  b.getBoundingClientRect = () => {
    calls++;
    return { top: calls * 10, left: 0 };
  };
  render(ul, items('b', 'a'), { flip: true });
  assert.equal(calls, 0);
  assert.equal(b.animations.length, 0);
});
