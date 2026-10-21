import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createClock } from '../../../web/src/core/clock.js';

function fakeTime() {
  const t = { wall: 1_000_000, mono: 500 };
  return { t, now: () => t.wall, monotonic: () => t.mono };
}

test('welcome seeds the offset', () => {
  const { now, monotonic } = fakeTime();
  const clock = createClock({ now, monotonic });
  clock.seed(1_005_000);
  assert.equal(clock.offset, 5000);
  assert.equal(clock.serverNow(), 1_005_000);
});

test('the offset comes from the minimum-RTT sample of the last eight', () => {
  const { t, now, monotonic } = fakeTime();
  const clock = createClock({ now, monotonic });
  // server = client + 2000; one sample with an asymmetric slow leg.
  clock.sample(1000, 1000 + 2000 + 300, 1400);
  assert.equal(clock.offset, 2000 + 300 - 200);
  clock.sample(2000, 2000 + 2000 + 20, 2040);
  assert.equal(clock.offset, 2000);
  clock.sample(3000, 3000 + 2000 + 500, 3600);
  assert.equal(clock.offset, 2000, 'a slower later sample does not win');
  t.wall = 10_000;
  assert.equal(clock.serverNow(), 12_000);
});

test('samples older than the last eight are forgotten', () => {
  const clock = createClock({ now: () => 0, monotonic: () => 0 });
  clock.sample(0, 5000, 10);
  for (let i = 1; i <= 8; i++) {
    clock.sample(i * 1000, i * 1000 + 100 + 50, i * 1000 + 100);
  }
  assert.equal(clock.offset, 100);
});

test('negative RTT samples are ignored', () => {
  const clock = createClock({ now: () => 0, monotonic: () => 0 });
  clock.seed(500);
  clock.sample(100, 9999, 50);
  assert.equal(clock.offset, 500);
});

test('a wall-clock step against monotonic time drops the samples', () => {
  const { t, now, monotonic } = fakeTime();
  const clock = createClock({ now, monotonic });
  assert.equal(clock.checkStep(), false);
  t.wall += 1000;
  t.mono += 1000;
  assert.equal(clock.checkStep(), false);
  clock.sample(t.wall - 10, t.wall + 3000, t.wall);
  t.wall += 60_000;
  t.mono += 1000;
  assert.equal(clock.checkStep(), true);
  clock.sample(t.wall - 100, t.wall + 7000, t.wall);
  assert.equal(clock.offset, 7050, 'the pre-step sample no longer competes');
});
