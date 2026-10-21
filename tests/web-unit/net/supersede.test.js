import { test } from 'node:test';
import assert from 'node:assert/strict';
import { COMMANDS } from '../../../web/src/net/protocol.gen.js';
import { SUPERSEDE, supersedeKey } from '../../../web/src/net/supersede.js';

// Kinds that overwrite a value but deliberately have no key: their precondition names the value they replace
// (`from`), or they patch only some fields, so a newer command can depend on the older one having applied.
const UNKEYED_OVERWRITES = new Set([
  'poker.settings.update',
  'retro.column.update',
  'retro.config.update',
  'retro.stage.set',
  'retro.focus.set',
  'retro.timer.setFlags',
]);

const OVERWRITE = /\.(set|edit|update|rename|reorder|move|assign|done|activate|end|setTicket|setActionItems|setFlags|submitPoints)$/;

test('every catalog command kind has a row, and only those', () => {
  assert.deepEqual(Object.keys(SUPERSEDE).sort(), Object.keys(COMMANDS).sort());
});

test('every overwrite kind has a supersede key unless it is a listed exception', () => {
  for (const kind of Object.keys(COMMANDS).filter((k) => OVERWRITE.test(k))) {
    assert.equal(SUPERSEDE[kind] !== null, !UNKEYED_OVERWRITES.has(kind), kind);
  }
});

test('creates have no supersede key', () => {
  const creates = Object.keys(COMMANDS).filter((k) => /\.(add|create|createMany|next|clearAll|give)$/.test(k));
  assert.ok(creates.includes('retro.card.add') && creates.includes('poker.round.create'));
  for (const kind of creates) {
    assert.equal(SUPERSEDE[kind], null, kind);
  }
});

test('key members are required members of the command', () => {
  for (const [kind, row] of Object.entries(SUPERSEDE)) {
    for (const field of row?.fields ?? []) {
      assert.ok(COMMANDS[kind].required.includes(field), `${kind}.${field}`);
    }
    if (row?.group) {
      assert.ok(row.group in COMMANDS, `${kind} groups under a catalog kind`);
    }
  }
});

test('a key is the kind (or its group) plus the target', () => {
  assert.equal(supersedeKey('poker.vote.set', { roundId: 'r1', points: '3' }), 'poker.vote.set:r1');
  assert.equal(supersedeKey('poker.abstain.set', { roundId: 'r1', abstain: true }), 'poker.vote.set:r1');
  assert.equal(supersedeKey('retro.card.edit', { cardId: 'c1', body: 'x', baseVersion: 1 }), 'retro.card.edit:c1');
  assert.equal(supersedeKey('retro.vote.set', { cardId: 'c1', on: true }), 'retro.vote.set:c1');
  assert.equal(supersedeKey('retro.column.reorder', { order: [] }), 'retro.column.reorder');
  assert.equal(supersedeKey('retro.card.add', { cardId: 'c1' }), null);
  assert.equal(supersedeKey('no.such.kind', {}), null);
});
