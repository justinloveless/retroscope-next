// The printed betting line of each side (docs/followups/UX3.md): concentric with the side's seat arc, a
// constant gap in toward the centre, ending just past the first and last seat, and absent on an empty side.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SEAT_SIZES, printedArc, seatLayout } from '../../../web/src/poker/geometry.js';

const STAGE = { width: 1280, height: 960, top: 52, bottom: 172 };
const OFFSET = 70;
const OVERHANG = 30;

function parse(d) {
  const n = d.match(/-?\d+(\.\d+)?/g).map(Number);
  return d.includes('A')
    ? { x1: n[0], y1: n[1], r: n[2], sweep: n[6], x2: n[7], y2: n[8] }
    : { x1: n[0], y1: n[1], x2: n[2], y2: n[3] };
}

function sides(layout) {
  const all = layout.chip ? [...layout.seats, layout.chip] : layout.seats;
  return { left: all.filter((s) => s.side === 'left'), right: all.filter((s) => s.side === 'right') };
}

test('each side gets one arc a constant gap inboard of its seats, bowing toward the centre', () => {
  const layout = seatLayout(STAGE, 6, SEAT_SIZES[1]);
  assert.ok(Number.isFinite(layout.curve.radius));
  const { left, right } = sides(layout);
  for (const [side, slots] of [['left', left], ['right', right]]) {
    const arc = parse(printedArc(slots, layout.curve, { offset: OFFSET, overhang: OVERHANG, width: STAGE.width }));
    assert.ok(Math.abs(arc.r - (layout.curve.radius + OFFSET)) < 0.2, `${side}: concentric with the seats`);
    assert.equal(arc.sweep, side === 'left' ? 1 : 0, `${side}: bows toward the centre`);
    // Every seat centre lies the same distance outside the printed circle.
    const local = (x) => (side === 'left' ? x : STAGE.width - x);
    const cx = local(arc.x1) - Math.sqrt(arc.r ** 2 - (arc.y1 - layout.curve.middle) ** 2);
    for (const seat of slots) {
      const gap = arc.r - Math.hypot(local(seat.x) - cx, seat.y - layout.curve.middle);
      assert.ok(Math.abs(gap - OFFSET) < 0.5, `${side}: seat ${gap.toFixed(1)} px from the line`);
    }
    const ys = slots.map((s) => s.y);
    assert.ok(arc.y1 < Math.min(...ys) && arc.y2 > Math.max(...ys), `${side}: runs past the first and last seat`);
    assert.ok(Math.abs(arc.y1 - (Math.min(...ys) - OVERHANG)) < 0.2 && Math.abs(arc.y2 - (Math.max(...ys) + OVERHANG)) < 0.2, `${side}: only just past them`);
    const inward = side === 'left' ? Math.min(arc.x1, arc.x2) > Math.max(...slots.map((s) => s.x)) - 1 : Math.max(arc.x1, arc.x2) < Math.min(...slots.map((s) => s.x)) + 1;
    assert.ok(inward, `${side}: inboard of the seats`);
  }
});

test('a side without seats has no arc', () => {
  const layout = seatLayout(STAGE, 1, SEAT_SIZES[1]);
  const { right } = sides(layout);
  assert.equal(printedArc(right, layout.curve, { offset: OFFSET, overhang: OVERHANG, width: STAGE.width }), null);
});

test('a straight side gets a straight line', () => {
  const slots = [{ x: 60, y: 200, angle: 90, side: 'right' }, { x: 60, y: 400, angle: 90, side: 'right' }];
  const d = printedArc(slots, { radius: Infinity, middle: 300 }, { offset: 50, overhang: 20, width: 600 });
  assert.equal(d, 'M 10 180 L 10 420');
});
