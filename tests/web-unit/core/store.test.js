import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHub, createStore, shallowEq } from '../../../web/src/core/store.js';

function manualFrames() {
  const queue = [];
  return {
    schedule: (run) => queue.push(run),
    frame() {
      const runs = queue.splice(0);
      for (const run of runs) {
        run();
      }
      return runs.length;
    },
    get pending() {
      return queue.length;
    },
  };
}

test('several writes in one frame notify a subscriber once with the latest slice', () => {
  const frames = manualFrames();
  const store = createStore({ count: 0, other: 'x' }, frames);
  const seen = [];
  store.select((s) => s.count, Object.is, (value, previous) => seen.push([value, previous]));

  store.set({ count: 1, other: 'x' });
  store.update((s) => ({ ...s, count: 2 }));
  store.update((s) => ({ ...s, count: 3 }));
  assert.deepEqual(seen, [], 'nothing runs before the frame');
  assert.equal(frames.pending, 1, 'one frame scheduled for many writes');

  frames.frame();
  assert.deepEqual(seen, [[3, 0]]);
});

test('a subscriber whose slice is unchanged is not called', () => {
  const frames = manualFrames();
  const store = createStore({ a: 1, b: 1 }, frames);
  let calls = 0;
  store.select((s) => s.a, Object.is, () => calls++);
  store.update((s) => ({ ...s, b: 2 }));
  frames.frame();
  assert.equal(calls, 0);
});

test('shallowEq lets derived objects with equal fields skip notification', () => {
  const frames = manualFrames();
  const store = createStore({ seat: { name: 'Ann', locked: false }, n: 0 }, frames);
  let calls = 0;
  store.select((s) => ({ name: s.seat.name, locked: s.seat.locked }), shallowEq, () => calls++);
  store.update((s) => ({ ...s, n: 1 }));
  frames.frame();
  assert.equal(calls, 0);
  store.update((s) => ({ ...s, seat: { ...s.seat, locked: true } }));
  frames.frame();
  assert.equal(calls, 1);
});

test('writing the same state schedules nothing', () => {
  const frames = manualFrames();
  const state = { a: 1 };
  const store = createStore(state, frames);
  store.set(state);
  assert.equal(frames.pending, 0);
});

test('unsubscribe stops notifications, also from inside a listener', () => {
  const frames = manualFrames();
  const store = createStore(0, frames);
  const calls = [];
  let offB;
  store.select((s) => s, Object.is, () => {
    calls.push('a');
    offB();
  });
  offB = store.select((s) => s, Object.is, () => calls.push('b'));
  store.set(1);
  frames.frame();
  assert.deepEqual(calls, ['a']);
});

test('a throwing listener does not stop the others', () => {
  const frames = manualFrames();
  const store = createStore(0, frames);
  const reported = [];
  const original = globalThis.reportError;
  globalThis.reportError = (e) => reported.push(e.message);
  try {
    store.select((s) => s, Object.is, () => {
      throw new Error('boom');
    });
    let ok = false;
    store.select((s) => s, Object.is, () => {
      ok = true;
    });
    store.set(1);
    frames.frame();
    assert.equal(ok, true);
    assert.deepEqual(reported, ['boom']);
  } finally {
    globalThis.reportError = original;
  }
});

test('flush() delivers synchronously and the pending frame becomes a no-op', () => {
  const frames = manualFrames();
  const store = createStore(0, frames);
  const seen = [];
  store.select((s) => s, Object.is, (v) => seen.push(v));
  store.set(5);
  store.flush();
  assert.deepEqual(seen, [5]);
  frames.frame();
  assert.deepEqual(seen, [5]);
});

test('createHub reads a lazily computed state only when a frame runs', () => {
  const frames = manualFrames();
  let reads = 0;
  let base = 1;
  const hub = createHub(() => {
    reads++;
    return { view: base * 10 };
  }, frames);
  const seen = [];
  hub.select((s) => s.view, Object.is, (v) => seen.push(v));
  const afterSubscribe = reads;
  base = 2;
  hub.notify();
  hub.notify();
  assert.equal(reads, afterSubscribe);
  frames.frame();
  assert.equal(reads, afterSubscribe + 1);
  assert.deepEqual(seen, [20]);
});

test('shallowEq compares one level of objects, arrays and maps', () => {
  assert.ok(shallowEq({ a: 1, b: 'x' }, { a: 1, b: 'x' }));
  assert.ok(!shallowEq({ a: 1 }, { a: 1, b: undefined }));
  assert.ok(!shallowEq({ a: {} }, { a: {} }));
  assert.ok(shallowEq([1, 2], [1, 2]));
  assert.ok(!shallowEq([1, 2], { 0: 1, 1: 2 }));
  assert.ok(shallowEq(new Map([['k', 1]]), new Map([['k', 1]])));
  assert.ok(!shallowEq(new Map([['k', 1]]), new Map([['k', 2]])));
  assert.ok(shallowEq(NaN, NaN));
  assert.ok(!shallowEq(null, {}));
});
