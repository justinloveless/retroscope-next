// The "between" zones beside a dragged card (docs/followups/UX2.md): the cards on either side of it move out
// by a slot, so each gap, and the zone in it, is twice as wide as in the resting fan. The two zones never meet
// each other, and both lie below the drop floor above the fan, so no point is two targets at once.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { gapRect } from '../../../web/src/poker/drag.js';

const CARD = { width: 64, height: 86 };
const STEP = (CARD.width * 2) / 3;
const FLOOR_GAP_PX = 4;

function card(centre, top = 700) {
  return { left: centre - CARD.width / 2, right: centre + CARD.width / 2, top, bottom: top + CARD.height, width: CARD.width, height: CARD.height };
}

const overlaps = (a, b) => a.left < b.left + b.width && b.left < a.left + a.width && a.top < b.top + b.height && b.top < a.top + a.height;

test('opening the fan by a slot on each side doubles each zone', () => {
  const own = card(500);
  const resting = gapRect(own, card(500 + STEP));
  const opened = gapRect(own, card(500 + 2 * STEP));
  assert.ok(Math.abs(opened.width - 2 * resting.width) < 1e-9, `${opened.width} is not twice ${resting.width}`);
  assert.ok(opened.left > 500, 'the zone stays clear of the dragged card’s centre');
  assert.ok(opened.left + opened.width < 500 + 2 * STEP, 'the zone stays clear of the neighbour’s centre');
});

test('the zones on the two sides never overlap, and lie below the drop floor', () => {
  // A raised fan: rotated cards sit at slightly different heights.
  const own = card(500, 706);
  const lower = card(500 - 2 * STEP, 702);
  const higher = card(500 + 2 * STEP, 703);
  const left = gapRect(lower, own);
  const right = gapRect(own, higher);
  assert.ok(!overlaps(left, right));
  const floor = Math.min(own.top, lower.top, higher.top) - FLOOR_GAP_PX;
  for (const zone of [left, right]) {
    assert.ok(zone.top > floor, 'a drop above the floor never lands in a zone');
    assert.ok(zone.width > 0 && zone.height >= CARD.height);
  }
});

test('the zone is the same whichever card comes first', () => {
  assert.deepEqual(gapRect(card(400), card(480)), gapRect(card(480), card(400)));
});
