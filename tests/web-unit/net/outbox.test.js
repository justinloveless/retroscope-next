import { test } from 'node:test';
import assert from 'node:assert/strict';
import { CID_CHECK_MS, OUTBOX_CAP, createOutbox } from '../../../web/src/net/outbox.js';
import {
  OTHER_PID, OTHER_ROOM, PID, ROOM, brokenStorage, channelHub, fakeTimers, memoryStorage, uuidSequence,
} from './_fakes.js';

function outboxWith(options = {}) {
  const timers = fakeTimers();
  return {
    timers,
    outbox: createOutbox({ channel: null, now: timers.now, setTimer: timers.setTimer, uuid: uuidSequence(), ...options }),
  };
}

test('a fresh tab mints a cid and persists it with the principal', () => {
  const storage = memoryStorage();
  const { outbox } = outboxWith({ storage, pid: PID });
  assert.equal(outbox.cid, 'c1');
  assert.equal(outbox.persistent, true);
  assert.deepEqual(JSON.parse(storage.data.get('rs.cid')), { cid: 'c1', pid: PID });
});

test('q strictly increases and entries persist with pid and sentAt', () => {
  const storage = memoryStorage();
  const { outbox, timers } = outboxWith({ storage, pid: PID });
  const a = outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  const b = outbox.add(OTHER_ROOM, 'poker.lock.set', { roundId: 'r', locked: true });
  assert.deepEqual([a.q, b.q], [1, 2]);
  const saved = JSON.parse(storage.data.get('rs.outbox'));
  assert.equal(saved.cseq, 2);
  assert.deepEqual(saved.entries[0], { r: ROOM, q: 1, k: 'retro.vote.set', d: { cardId: 'x', on: true }, pid: PID, sentAt: timers.now(), sent: false });
  assert.deepEqual(outbox.entries(ROOM), [a]);
});

test('a reload restores the cid, cseq and entries', () => {
  const storage = memoryStorage();
  const { outbox } = outboxWith({ storage, pid: PID });
  outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  const { outbox: reloaded } = outboxWith({ storage, pid: PID, uuid: uuidSequence('new') });
  assert.equal(reloaded.cid, 'c1');
  assert.equal(reloaded.cseq, 1);
  assert.equal(reloaded.entries(ROOM)[0].q, 1);
  assert.equal(reloaded.add(ROOM, 'retro.vote.set', { cardId: 'y', on: true }).q, 2);
});

test('the outbox refuses commands beyond its cap', () => {
  const { outbox } = outboxWith({ storage: memoryStorage() });
  for (let i = 0; i < OUTBOX_CAP; i++) {
    assert.ok(outbox.add(ROOM, 'retro.vote.set', { cardId: `${i}`, on: true }));
  }
  assert.equal(outbox.add(ROOM, 'retro.vote.set', { cardId: 'over', on: true }), null);
  assert.equal(outbox.size, OUTBOX_CAP);
});

test('sync raises cseq to lastQ and never lowers it', () => {
  const { outbox } = outboxWith({ storage: memoryStorage() });
  outbox.sync(ROOM, 40);
  assert.equal(outbox.cseq, 40);
  outbox.sync(ROOM, 7);
  assert.equal(outbox.cseq, 40);
  assert.equal(outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true }).q, 41);
});

test('sync renumbers unsent entries whose q the server already saw, and keeps sent ones', () => {
  const { outbox } = outboxWith({ storage: memoryStorage() });
  const sent = outbox.add(ROOM, 'retro.vote.set', { cardId: 'a', on: true });
  outbox.markSent(sent);
  const unsent = outbox.add(ROOM, 'retro.vote.set', { cardId: 'b', on: true });
  outbox.sync(ROOM, 5);
  assert.equal(sent.q, 1, 'a sent entry resends with its q and is acked dup if it landed');
  assert.equal(unsent.q, 6);
});

test('a principal change clears the outbox and mints a new cid', () => {
  const storage = memoryStorage();
  const { outbox } = outboxWith({ storage, pid: PID });
  outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  let changes = 0;
  outbox.onChange(() => changes++);
  assert.equal(outbox.bindPrincipal(PID), false);
  assert.equal(outbox.bindPrincipal(OTHER_PID), true);
  assert.equal(outbox.cid, 'c2');
  assert.equal(outbox.pid, OTHER_PID);
  assert.equal(outbox.size, 0);
  assert.equal(changes, 1);
  assert.deepEqual(JSON.parse(storage.data.get('rs.outbox')).entries, []);
});

test('a reload under another principal starts empty', () => {
  const storage = memoryStorage();
  const { outbox } = outboxWith({ storage, pid: PID });
  outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  const { outbox: other } = outboxWith({ storage, pid: OTHER_PID, uuid: uuidSequence('n') });
  assert.equal(other.cid, 'n1');
  assert.equal(other.size, 0);
});

test('the first known principal is adopted without clearing', () => {
  const { outbox } = outboxWith({ storage: memoryStorage() });
  const entry = outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  assert.equal(outbox.bindPrincipal(PID), false);
  assert.equal(outbox.cid, 'c1');
  assert.equal(entry.pid, PID);
});

test('remint after 4409 keeps entries and their q', () => {
  const { outbox } = outboxWith({ storage: memoryStorage() });
  const entry = outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  outbox.remint();
  assert.equal(outbox.cid, 'c2');
  assert.equal(entry.q, 1);
  assert.equal(outbox.size, 1);
});

test('remint after cid_reuse gives every entry a new q', () => {
  const { outbox } = outboxWith({ storage: memoryStorage() });
  const a = outbox.add(ROOM, 'retro.vote.set', { cardId: 'a', on: true });
  const b = outbox.add(ROOM, 'retro.vote.set', { cardId: 'b', on: true });
  outbox.markSent(a);
  outbox.remint({ renumber: true });
  assert.deepEqual([a.q, b.q, a.sent], [3, 4, false]);
});

test('ephemeral entries older than 10 s are dropped, durable ones kept', () => {
  const { outbox, timers } = outboxWith({ storage: memoryStorage() });
  const focus = outbox.add(ROOM, 'retro.focus.set', { cardId: 'a', from: null });
  const vote = outbox.add(ROOM, 'retro.vote.set', { cardId: 'a', on: true });
  timers.advance(10_000);
  assert.deepEqual(outbox.dropStaleEphemeral(ROOM), [], 'exactly 10 s is not older than 10 s');
  const recent = outbox.add(ROOM, 'retro.timer.pause', { from: 'running' });
  timers.advance(1);
  assert.deepEqual(outbox.dropStaleEphemeral(ROOM), [focus]);
  assert.deepEqual(outbox.entries(ROOM), [vote, recent]);
});

test('storage that cannot be read gives an in-memory outbox with a fresh cid per load', () => {
  const { outbox } = outboxWith({ storage: brokenStorage() });
  assert.equal(outbox.persistent, false);
  assert.equal(outbox.cid, 'c1');
  assert.equal(outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true }).q, 1);
  const { outbox: next } = outboxWith({ storage: brokenStorage(), uuid: uuidSequence('n') });
  assert.equal(next.cid, 'n1');
});

test('a failed write switches to memory mode and removes the stored outbox', () => {
  const storage = memoryStorage();
  const { outbox } = outboxWith({ storage });
  outbox.add(ROOM, 'retro.vote.set', { cardId: 'a', on: true });
  storage.failWrites = true;
  const b = outbox.add(ROOM, 'retro.vote.set', { cardId: 'b', on: true });
  assert.equal(outbox.persistent, false);
  assert.equal(b.q, 2);
  assert.equal(outbox.size, 2, 'the tab keeps its entries in memory');
  assert.equal(storage.data.has('rs.outbox'), false, 'a reload must not resurrect a half-written outbox');
  assert.equal(storage.data.has('rs.cid'), false);
});

test('a duplicated tab mints a new cid and drops the copied outbox', async () => {
  const open = channelHub();
  const storage = memoryStorage();
  const first = outboxWith({ storage, channel: open() });
  first.outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  const copy = memoryStorage(Object.fromEntries(storage.data));
  const dup = outboxWith({ storage: copy, channel: open(), uuid: uuidSequence('dup') });
  dup.timers.advance(CID_CHECK_MS);
  await dup.outbox.ready;
  assert.equal(dup.outbox.cid, 'dup1');
  assert.equal(dup.outbox.size, 0);
  assert.equal(first.outbox.cid, 'c1', 'the original keeps its cid');
  assert.equal(first.outbox.size, 1);
});

test('a reloaded tab with no twin keeps its cid after the check', async () => {
  const open = channelHub();
  const storage = memoryStorage();
  outboxWith({ storage, channel: null }).outbox.add(ROOM, 'retro.vote.set', { cardId: 'x', on: true });
  const other = outboxWith({ storage: memoryStorage(), channel: open(), uuid: uuidSequence('o') });
  const reloaded = outboxWith({ storage, channel: open(), uuid: uuidSequence('r') });
  let resolved = false;
  reloaded.outbox.ready.then(() => {
    resolved = true;
  });
  await Promise.resolve();
  assert.equal(resolved, false, 'hello waits for the duplicate check');
  reloaded.timers.advance(CID_CHECK_MS);
  await reloaded.outbox.ready;
  assert.equal(reloaded.outbox.cid, 'c1');
  assert.equal(reloaded.outbox.size, 1);
  assert.equal(other.outbox.cid, 'o1');
});
