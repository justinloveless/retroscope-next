// Golden geometry of the seats around the issue (docs/followups/UX1.md, UX2.md): for 1–16 players on three
// stage sizes, every seat stays inside the stage, out of the top and bottom bands (a corner seat's box may
// spill into them), clear of every other seat and turned toward the stage centre, and each side's seats lie on
// a shallow arc that bows in by no more than its small sagitta.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SEAT_SIZES, depthLimit, extents, maxSagitta, planSeats, seatLayout, seatsOverlap } from '../../../web/src/poker/geometry.js';

// The stage between the panels at 1440×900, 1920×1080 and 1280×800 (both panels open), measured in the demo;
// `top` is the round header, `bottom` the resting hand with its bar plus the room the lowest label needs.
const STAGES = [
  { name: '1440x900', width: 800, height: 780, top: 52, bottom: 172 },
  { name: '1920x1080', width: 1280, height: 960, top: 52, bottom: 172 },
  { name: '1280x800', width: 640, height: 680, top: 52, bottom: 172 },
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

  assert.ok(layout.sagitta <= maxSagitta(stage.width) + 1e-9, `${where}: the arcs bow in ${layout.sagitta} px`);
  for (const side of ['left', 'right']) {
    const xs = all.filter((s) => s.side === side).map((s) => s.x);
    if (xs.length > 0) {
      const spread = Math.max(...xs) - Math.min(...xs);
      assert.ok(spread <= layout.sagitta + 2, `${where}: the ${side} seats spread ${spread.toFixed(1)} px across, the arc bows ${layout.sagitta.toFixed(1)} px`);
    }
  }

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

  test(`${stage.name}: the planner seats 1–16 players, shrinks before it hides anyone, and never grows with the crowd`, () => {
    let previous = 0;
    for (let count = 1; count <= 16; count++) {
      const layout = planSeats(stage, count);
      check(stage, count, layout, layout.size);
      assert.ok(layout.depth <= depthLimit(stage.width) + 0.5, `${stage.name}, ${count} players: the seats reach ${layout.depth} px in`);
      if (layout.hidden > 0) {
        assert.equal(layout.size.name, 'avatar', 'only the smallest seats hide players');
      }
      const rank = SEAT_SIZES.indexOf(layout.size);
      assert.ok(rank >= previous, `${stage.name}: ${count} players get bigger seats than ${count - 1}`);
      previous = rank;
    }
  });
}

test('seat cards use the scales the pixel art renders crisply at', () => {
  assert.deepEqual(SEAT_SIZES.map((s) => s.card), [1.5, 1, 0.75, 0]);
  for (const size of SEAT_SIZES.filter((s) => s.card > 0)) {
    assert.ok(near(size.width, 71 * size.card, 1) && near(size.height, 95 * size.card, 1), `${size.name} is the card itself`);
  }
});

test('at 1440×900 six players sit at full-size cards or larger, and the issue keeps 560 px', () => {
  const stage = STAGES[0];
  for (const players of [3, 6]) {
    const layout = planSeats(stage, players - 1);
    assert.ok(layout.size.card >= 1, `${players} players get ${layout.size.name} seats`);
    assert.ok(stage.width - 2 * Math.ceil(layout.depth) >= 560, `${players} players leave the issue ${stage.width - 2 * Math.ceil(layout.depth)} px`);
  }
  assert.equal(planSeats(STAGES[1], 2).size.name, 'large', 'a small table on a wide stage gets the largest cards');
});

test('players fill the two sides alternately and each side runs top to bottom', () => {
  const { seats } = seatLayout(STAGES[1], 7, SEAT_SIZES[1]);
  assert.deepEqual(seats.map((s) => s.side), ['left', 'right', 'left', 'right', 'left', 'right', 'left']);
  for (const side of ['left', 'right']) {
    const ys = seats.filter((s) => s.side === side).map((s) => s.y);
    assert.deepEqual(ys, [...ys].sort((a, b) => a - b));
  }
});

test('a lone seat on a side sits level with the stage centre and faces straight in', () => {
  const stage = STAGES[0];
  const [left, right] = seatLayout(stage, 2, SEAT_SIZES[1]).seats;
  assert.ok(near(left.y, stage.height / 2, 0.01) && near(left.angle, 90, 0.01));
  assert.ok(near(right.y, stage.height / 2, 0.01) && near(right.angle, -90, 0.01));
});

test('seats on one side are spread evenly along a gentle arc', () => {
  const stage = STAGES[1];
  const size = SEAT_SIZES[1];
  const layout = seatLayout(stage, 8, size);
  const left = layout.seats.filter((s) => s.side === 'left');
  const gaps = left.slice(1).map((s, i) => Math.hypot(s.x - left[i].x, s.y - left[i].y));
  for (const gap of gaps) {
    assert.ok(near(gap, gaps[0], 1), `gaps ${gaps.map((g) => g.toFixed(1))} are not even`);
    assert.ok(gap >= size.spacing - 1);
  }
  assert.ok(layout.sagitta > 0, 'a roomy stage bows its arcs');
  const middle = left[Math.floor(left.length / 2)];
  assert.ok(middle.x > left[0].x && middle.x > left.at(-1).x, 'the arc bows toward the centre');
});

test('a crowded table hides the rest behind one chip, and the depth covers every seat', () => {
  const stage = { width: 640, height: 420, top: 64, bottom: 132 };
  const layout = planSeats(stage, 16);
  assert.equal(layout.size.name, 'avatar');
  assert.ok(layout.hidden > 0 && layout.chip);
  assert.equal(layout.chip.side, 'right');
  for (const seat of layout.seats) {
    const { ex } = extents(layout.size.width, layout.size.height, seat.angle);
    assert.ok(seat.side === 'left' ? seat.x + ex <= layout.depth + 1e-6 : stage.width - seat.x + ex <= layout.depth + 1e-6);
  }
});

test('a stage too small for any seat hides everyone', () => {
  const layout = seatLayout({ width: 60, height: 60 }, 3, SEAT_SIZES[1]);
  assert.deepEqual(layout, { seats: [], chip: null, hidden: 3, depth: 0, sagitta: 0 });
});
