import { test } from 'node:test';
import assert from 'node:assert/strict';
import { duration, initials, plural, timeAgo } from '../../../web/src/core/fmt.js';

test('initials use the first and last word, grapheme-safe for astral characters', () => {
  assert.equal(initials('Ada Lovelace'), 'AL');
  assert.equal(initials('  grace  brewster murray hopper '), 'GH');
  assert.equal(initials('Ze'), 'Z');
  assert.equal(initials(''), '?');
  assert.equal(initials(null), '?');
  assert.equal(initials('𝒜da 𝔅yron'), '𝒜𝔅');
});

test('plural', () => {
  assert.equal(plural(1, 'vote'), '1 vote');
  assert.equal(plural(0, 'vote'), '0 votes');
  assert.equal(plural(2, 'reply', 'replies'), '2 replies');
});

test('duration renders m:ss and never goes negative', () => {
  assert.equal(duration(0), '0:00');
  assert.equal(duration(59_001), '1:00');
  assert.equal(duration(305_000), '5:05');
  assert.equal(duration(-5), '0:00');
});

test('timeAgo picks a unit by distance', () => {
  const now = Date.UTC(2026, 9, 20, 12);
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  assert.equal(timeAgo(now - 5_000, now), rtf.format(0, 'second'));
  assert.equal(timeAgo(now - 5 * 60_000, now), rtf.format(-5, 'minute'));
  assert.equal(timeAgo(now - 3 * 3_600_000, now), rtf.format(-3, 'hour'));
  assert.equal(timeAgo(now - 2 * 86_400_000, now), rtf.format(-2, 'day'));
});
