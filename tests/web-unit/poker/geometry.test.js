// Golden geometry of the seats around the issue (docs/followups/UX1.md): for 1–16 players on three stage
// sizes, every seat stays inside the stage, out of the top and bottom bands (a corner seat's box may spill
// into them), clear of every other seat, and turned toward the stage centre.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { MAX_ARC_DEG, SEAT_SIZES, STAGE_PAD_PX, extents, planSeats, seatLayout, seatsOverlap } from '../../../web/src/poker/geometry.js';

// The stage between the panels at 1440×900 and 1920×1080 (both panels open) and at 1280×800 with the Round
// panel collapsed; `top` is the round header, `bottom` the resting hand with its bar.
const STAGES = [
  { name: '1440x900', width: 800, height: 760, top: 64, bottom: 132 },
  { name: '1920x1080', width: 1280, height: 940, top: 64, bottom: 132 },
  { name: '1280x800', width: 920, height: 660, top: 64, bottom: 132 },
];

const near = (a, b, epsilon) => Math.abs(a - b) <= epsilon;

function check(stage, count, layout, size) {
  const where = `${stage.name}, ${count} players, ${size.name}`;
  const all = layout.chip ? [...layout.seats, layout.chip] : layout.seats;
  assert.equal(layout.seats.length + layout.hidden, count, `${where}: every player is seated or behind the chip`);
  assert.equal(layout.chip !== null, layout.hidden > 0, `${where}: a chip exactly when someone is hidden`);
  const { width: w, height: h } = size;
  const cx = stage.width / 2;
  const cy = stage.height / 2;

  for (const seat of all) {
    const { ex, ey } = extents(w, h, seat.angle);
    assert.ok(seat.x - ex >= -0.5 && seat.x + ex <= stage.width + 0.5, `${where}: seat at x ${seat.x} leaves the stage`);
    assert.ok(seat.y - ey >= -0.5 && seat.y + ey <= stage.height + 0.5, `${where}: seat at y ${seat.y} leaves the stage`);

    assert.ok(seat.y >= stage.top - 0.5 && seat.y <= stage.height - stage.bottom + 0.5, `${where}: a seat centre lies in a band`);
    const spills = seat.y - ey < stage.top || seat.y + ey > stage.height - stage.bottom;
    if (spills) {
      assert.ok(seat.x < stage.width / 3 || seat.x > (stage.width * 2) / 3, `${where}: only corner seats spill into a band`);
    }
    assert.equal(seat.side, seat.x < cx ? 'left' : 'right', `${where}: seat on the wrong side`);

    const toward = Math.atan2(cy - seat.y, cx - seat.x);
    const facing = Math.atan2(-Math.cos((seat.angle * Math.PI) / 180), Math.sin((seat.angle * Math.PI) / 180));
    const off = Math.abs(((toward - facing + 3 * Math.PI) % (2 * Math.PI)) - Math.PI) * (180 / Math.PI);
    assert.ok(off <= 1, `${where}: a seat faces ${off.toFixed(2)}° away from the centre`);
  }

  for (let i = 0; i < all.length; i++) {
    for (let j = i + 1; j < all.length; j++) {
      assert.ok(!seatsOverlap(all[i], all[j], w, h), `${where}: seats ${i} and ${j} overlap`);
    }
  }
}

for (const stage of STAGES) {
  test(`${stage.name}: 1–16 players at every seat size`, () => {
    for (const size of SEAT_SIZES) {
      for (let count = 1; count <= 16; count++) {
        check(stage, count, seatLayout(stage, count, size), size);
      }
    }
  });

  test(`${stage.name}: the planner seats 1–16 players and shrinks before it hides anyone`, () => {
    for (let count = 1; count <= 16; count++) {
      const layout = planSeats(stage, count);
      check(stage, count, layout, layout.size);
      assert.ok(layout.depth <= Math.min(260, stage.width / 4) + 0.5, `${stage.name}, ${count} players: the ring is ${layout.depth} px deep`);
      if (layout.hidden > 0) {
        assert.equal(layout.size.name, 'avatar', 'only the smallest seats hide players');
      }
    }
  });
}

test('players fill the two sides alternately and each side runs top to bottom', () => {
  const { seats } = seatLayout(STAGES[0], 7, SEAT_SIZES[0]);
  assert.deepEqual(seats.map((s) => s.side), ['left', 'right', 'left', 'right', 'left', 'right', 'left']);
  for (const side of ['left', 'right']) {
    const ys = seats.filter((s) => s.side === side).map((s) => s.y);
    assert.deepEqual(ys, [...ys].sort((a, b) => a - b));
  }
});

test('a lone seat on a side sits at its middle and faces straight in', () => {
  const stage = STAGES[0];
  const [left, right] = seatLayout(stage, 2, SEAT_SIZES[0]).seats;
  assert.ok(near(left.y, stage.height / 2, 0.01) && near(left.angle, 90, 0.01));
  assert.ok(near(right.y, stage.height / 2, 0.01) && near(right.angle, -90, 0.01));
});

test('seats on one side are spread evenly along the arc, within 120° of the ellipse', () => {
  const stage = STAGES[1];
  const size = SEAT_SIZES[0];
  const { seats } = seatLayout(stage, 12, size);
  const left = seats.filter((s) => s.side === 'left');
  const gaps = left.slice(1).map((s, i) => Math.hypot(s.x - left[i].x, s.y - left[i].y));
  for (const gap of gaps) {
    assert.ok(near(gap, gaps[0], 1.5), `gaps ${gaps.map((g) => g.toFixed(1))} are not even`);
    assert.ok(gap >= size.spacing - 1);
  }
  const b = stage.height / 2 - Math.hypot(size.width, size.height) / 2 - STAGE_PAD_PX;
  const span = (Math.asin((stage.height / 2 - left[0].y) / b) * 2 * 180) / Math.PI;
  assert.ok(span <= MAX_ARC_DEG + 0.5, `the arc spans ${span.toFixed(1)}°`);
});

test('the ring depth covers every seat, and a crowded table hides the rest behind one chip', () => {
  const stage = { width: 640, height: 420, top: 64, bottom: 132 };
  const layout = planSeats(stage, 16);
  assert.equal(layout.size.name, 'avatar');
  assert.ok(layout.hidden > 0 && layout.chip);
  assert.equal(layout.chip.side, 'right');
  for (const seat of layout.seats) {
    const { ex } = extents(layout.size.width, layout.size.height, seat.angle);
    assert.ok(seat.side === 'left' ? seat.x + ex <= layout.depth : stage.width - seat.x + ex <= layout.depth);
  }
});

test('a stage too small for any seat hides everyone', () => {
  const layout = seatLayout({ width: 60, height: 60 }, 3, SEAT_SIZES[0]);
  assert.deepEqual(layout, { seats: [], chip: null, hidden: 3, depth: 0 });
});
