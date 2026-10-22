// The results overlay's piles (docs/followups/UX2.md, item 5): one pile per point value, a between vote counted
// on both of its ends as the scoring counts it, abstains in their own pile last.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resultPiles } from '../../../web/src/poker/results.js';

const R = 'r1';
const people = [
  { pid: 'a', name: 'Ada' },
  { pid: 'b', name: 'Bob' },
  { pid: 'c', name: 'Cy' },
  { pid: 'd', name: 'Dee' },
];
const state = (votes) => ({ roster: people, presence: [], votes: { [R]: Object.fromEntries(votes.map((v) => [v.pid, v])) } });
const summary = (piles) => piles.map((p) => [p.abstain ? 'abstain' : p.points, p.voters.map((v) => v.name)]);

test('a between vote sits in the piles of both its ends, abstains pile last', () => {
  const piles = resultPiles(state([
    { pid: 'a', points: 8 },
    { pid: 'b', points: 3, high: 5 },
    { pid: 'c', abstained: true },
    { pid: 'd', points: 5 },
  ]), R);
  assert.deepEqual(summary(piles), [[3, ['Bob']], [5, ['Bob', 'Dee']], [8, ['Ada']], ['abstain', ['Cy']]]);
  const between = piles[1].voters[0];
  assert.deepEqual([between.points, between.high, between.abstained], [3, 5, false]);
});

test('a vote without points is an abstain, and no votes give no piles', () => {
  assert.deepEqual(summary(resultPiles(state([{ pid: 'a', points: null }]), R)), [['abstain', ['Ada']]]);
  assert.deepEqual(resultPiles(state([]), 'other'), []);
});

test('piles sort by value, and an unknown voter is named "Unknown"', () => {
  const piles = resultPiles(state([{ pid: 'z', points: 13 }, { pid: 'a', points: 1 }, { pid: 'b', points: 2, high: 3 }]), R);
  assert.deepEqual(summary(piles), [[1, ['Ada']], [2, ['Bob']], [3, ['Bob']], [13, ['Unknown']]]);
});
