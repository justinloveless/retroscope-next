import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  DEFAULT_LEVEL, GLIDE_S, LEGACY_OFF_GAIN, MAX_LEVEL, MIN_LEVEL, clampLevel, gain, glide, legacyGain, levelFromLegacyGain, levelText,
  storedLevel,
} from '../../../web/src/retro/volume.js';

const close = (actual, expected, epsilon = 1e-3) => assert.ok(Math.abs(actual - expected) < epsilon, `${actual} ≉ ${expected}`);

test('the gain falls 40 dB across the range and is silent at 0', () => {
  assert.equal(gain(100), 1);
  close(gain(75), 0.316);
  close(gain(50), 0.1);
  close(gain(25), 0.0316);
  close(gain(1), 0.0105);
  assert.equal(gain(0), 0);
});

test('equal steps are equal ratios of gain', () => {
  for (let level = 10; level < 100; level += 10) {
    close(gain(level + 10) / gain(level), 10 ** 0.2, 1e-9);
  }
});

test('the gain rises with the level', () => {
  for (let level = MIN_LEVEL; level < MAX_LEVEL; level++) {
    assert.ok(gain(level + 1) > gain(level), `gain(${level + 1}) > gain(${level})`);
  }
});

test('the default sounds like the old default gain of 0.5', () => {
  assert.equal(DEFAULT_LEVEL, 85);
  close(gain(DEFAULT_LEVEL), 0.5, 0.01);
  assert.equal(levelFromLegacyGain(0.5), DEFAULT_LEVEL);
});

test('each old menu volume becomes the level that sounds the same', () => {
  assert.equal(levelFromLegacyGain(1), 100);
  assert.equal(levelFromLegacyGain(0.75), 94);
  assert.equal(levelFromLegacyGain(0.25), 70);
  for (const legacy of [0.25, 0.5, 0.75, 1]) {
    close(gain(levelFromLegacyGain(legacy)), legacy, legacy * 0.03);
  }
});

test('a legacy gain out of range is clamped, and an invalid one is the default', () => {
  assert.equal(levelFromLegacyGain(4), 100);
  assert.equal(levelFromLegacyGain(0.001), 0);
  for (const bad of [0, -1, NaN, Infinity, undefined, null]) {
    assert.equal(levelFromLegacyGain(bad), DEFAULT_LEVEL, String(bad));
  }
});

test('the legacy gain is the gain, except a tiny positive one at 0 that migrates back to 0', () => {
  assert.equal(legacyGain(60), gain(60));
  assert.equal(legacyGain(100), 1);
  assert.equal(legacyGain(0), LEGACY_OFF_GAIN);
  assert.ok(LEGACY_OFF_GAIN > 0 && LEGACY_OFF_GAIN < gain(1) / 10, 'far quieter than level 1');
  assert.equal(String(legacyGain(0)), '0.0001');
  assert.equal(storedLevel(null, String(legacyGain(0))), 0);
});

test('levels are clamped to whole numbers in range', () => {
  assert.equal(clampLevel(42.4), 42);
  assert.equal(clampLevel(-5), 0);
  assert.equal(clampLevel(250), 100);
  assert.equal(clampLevel(NaN), DEFAULT_LEVEL);
  assert.equal(clampLevel('60'), DEFAULT_LEVEL);
});

test('a stored level wins, and without one the legacy gain is migrated', () => {
  assert.equal(storedLevel('60', '1'), 60);
  assert.equal(storedLevel('0', '1'), 0);
  assert.equal(storedLevel('140', null), 100);
  assert.equal(storedLevel(null, '0.25'), 70);
  assert.equal(storedLevel('', '0.25'), 70);
  assert.equal(storedLevel('loud', '0.75'), 94);
  assert.equal(storedLevel(null, null), DEFAULT_LEVEL);
  assert.equal(storedLevel(null, ''), DEFAULT_LEVEL);
  assert.equal(storedLevel(null, 'x'), DEFAULT_LEVEL);
});

test('the slider text is a percentage, or Off at 0', () => {
  assert.equal(levelText(60), '60%');
  assert.equal(levelText(100), '100%');
  assert.equal(levelText(0), 'Off');
});

test('a glide sets a target from the current time instead of stepping the value', () => {
  const calls = [];
  const param = { value: 1, setTargetAtTime: (...args) => calls.push(args) };
  glide(param, 0.25, { currentTime: 12.5 });
  assert.deepEqual(calls, [[0.25, 12.5, GLIDE_S]]);
  assert.equal(param.value, 1);
});
