// The avatar on the card's corner (docs/followups/UX6.md): each ring seat's avatar is centred on the bottom corner
// of its card nearer the stage's top edge (seats above the middle) or bottom edge (the rest, the middle seat
// included), it never comes closer than the stage padding to the rail, and the name label stays off the card.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { STAGE_PAD_PX, SEAT_SIZES, planSeats } from '../../../web/src/poker/geometry.js';
import { avatarCorner, centreInColumns, clearAvatars, seatMarks } from '../../../web/src/poker/seats.js';

const STAGES = [
  { name: '1440x900', width: 800, height: 780, top: 104, bottom: 148 },
  { name: '1920x1080', width: 1280, height: 960, top: 104, bottom: 148 },
  { name: '1280x800', width: 640, height: 680, top: 104, bottom: 148 },
];
const FULL = SEAT_SIZES.find((size) => size.name === 'full');
const DEG = Math.PI / 180;

// A point given in the seat's own frame (x across the card, y toward its bottom edge), on screen.
function turn(slot, [px, py]) {
  const c = Math.cos(slot.angle * DEG);
  const s = Math.sin(slot.angle * DEG);
  return [px * c - py * s, px * s + py * c];
}

const placed = (stage, count) => clearAvatars(centreInColumns(planSeats(stage, count), stage), stage);

test('an upper seat takes the higher bottom corner, a lower or middle seat the lower one', () => {
  const cases = [
    { slot: { x: 100, y: 200, angle: 110, side: 'left' }, corner: 'upper' },
    { slot: { x: 100, y: 600, angle: 70, side: 'left' }, corner: 'lower' },
    { slot: { x: 100, y: 400, angle: 90, side: 'left' }, corner: 'lower' },
    { slot: { x: 700, y: 200, angle: -110, side: 'right' }, corner: 'upper' },
    { slot: { x: 700, y: 600, angle: -70, side: 'right' }, corner: 'lower' },
    { slot: { x: 700, y: 400, angle: -90, side: 'right' }, corner: 'lower' },
  ];
  for (const { slot, corner } of cases) {
    const got = avatarCorner(slot, FULL, 400);
    assert.equal(got.corner, corner, `${slot.side} seat at y ${slot.y}`);
    const bottoms = [-1, 1].map((sx) => turn(slot, [(sx * FULL.width) / 2, FULL.height / 2]));
    const [dx, dy] = turn(slot, [(got.sx * FULL.width) / 2, FULL.height / 2]);
    assert.ok(Math.abs(got.dx - dx) < 1e-9 && Math.abs(got.dy - dy) < 1e-9, 'the offset is that corner on screen');
    const pick = corner === 'upper' ? Math.min(...bottoms.map(([, y]) => y)) : Math.max(...bottoms.map(([, y]) => y));
    assert.ok(Math.abs(got.dy - pick) < 1e-9, `${slot.side} seat at y ${slot.y}: the ${corner} of the two bottom corners`);
    // The bottom corner is on the side away from the stage centre.
    assert.ok(slot.side === 'left' ? got.dx < 0 : got.dx > 0, 'an outer corner');
  }
});

test('on every placed ring each side splits into upper and lower corners around the middle', () => {
  for (const stage of STAGES) {
    for (let count = 2; count <= 16; count++) {
      const plan = placed(stage, count);
      for (const slot of plan.seats) {
        const { corner } = avatarCorner(slot, plan.size, plan.curve.middle);
        assert.equal(corner, slot.y < plan.curve.middle - 0.5 ? 'upper' : 'lower', `${stage.name}, ${count} players`);
      }
    }
  }
});

test('no avatar comes closer than the stage padding to the rail', () => {
  for (const stage of STAGES) {
    for (let count = 1; count <= 16; count++) {
      const plan = placed(stage, count);
      if (!plan.size.card) {
        continue;
      }
      for (const slot of plan.seats) {
        const { dx } = avatarCorner(slot, plan.size, plan.curve.middle);
        const x = slot.x + dx;
        const outer = (slot.side === 'left' ? x : stage.width - x) - plan.size.avatar / 2;
        assert.ok(outer >= STAGE_PAD_PX - 0.5, `${stage.name}, ${count} players: an avatar is ${outer.toFixed(1)} px from the rail`);
      }
    }
  }
});

test('the name label hangs past the card on the avatar\'s side and starts at the avatar\'s outer edge', () => {
  for (const stage of STAGES) {
    for (let count = 1; count <= 16; count++) {
      const plan = placed(stage, count);
      if (!plan.size.card) {
        continue;
      }
      const { width: w, height: h, avatar } = plan.size;
      for (const slot of plan.seats) {
        const marks = seatMarks(slot, plan.size, plan.curve.middle);
        const ys = [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([px, py]) => turn(slot, [(px * w) / 2, (py * h) / 2])[1]);
        const where = `${stage.name}, ${count} players`;
        if (marks.label.above) {
          assert.ok(marks.label.y < Math.min(...ys), `${where}: a label above its card stays above it`);
        } else {
          assert.ok(marks.label.y > Math.max(...ys), `${where}: a label below its card stays below it`);
        }
        const outerEdge = slot.side === 'left' ? marks.avatar.x - avatar / 2 : marks.avatar.x + avatar / 2;
        assert.equal(marks.label.x, outerEdge);
      }
    }
  }
});
