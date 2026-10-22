// Seats centred in their felt column, and the betting line kept clear of every outline (docs/followups/UX4.md):
// each side's seats sit midway between the rail and the issue pane, with the betting line between them and the
// pane at about double UX3's gap from every seat outline.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { STAGE_PAD_PX, columnShift, extents, planSeats, seatsOverlap } from '../../../web/src/poker/geometry.js';
import { ARC_GAP_PX, ISSUE_KEEP, bettingLines, centreInColumns, printMetrics } from '../../../web/src/poker/seats.js';

const STAGES = [
  { name: '1440x900', width: 800, height: 780, top: 52, bottom: 172 },
  { name: '1920x1080', width: 1280, height: 960, top: 52, bottom: 172 },
  { name: '1280x800', width: 640, height: 680, top: 52, bottom: 172 },
];
// neotro.css .nt-field --nt-pane-x: max(ring, 50% - 23rem) + 0.75rem.
const paneEdge = (stage, ring) => Math.max(ring, stage.width / 2 - 368) + 12;

const slotsOf = (plan) => (plan.chip ? [...plan.seats, plan.chip] : plan.seats);
const local = (stage, slot) => (slot.side === 'left' ? slot.x : stage.width - slot.x);

function corners(slot, w, h) {
  const a = (slot.angle * Math.PI) / 180;
  const c = Math.cos(a);
  const s = Math.sin(a);
  return [[-w / 2, -h / 2], [w / 2, -h / 2], [w / 2, h / 2], [-w / 2, h / 2]]
    .map(([px, py]) => [slot.x + px * c - py * s, slot.y + px * s + py * c]);
}

function segmentDistance([px, py], [x1, y1], [x2, y2]) {
  const dx = x2 - x1;
  const dy = y2 - y1;
  const t = Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / (dx * dx + dy * dy)));
  return Math.hypot(px - x1 - t * dx, py - y1 - t * dy);
}

// Points along an SVG "M x y A r r 0 0 s x y" or "M x y L x y" path.
function sample(d, n = 200) {
  const v = d.match(/-?\d+(\.\d+)?/g).map(Number);
  if (!d.includes('A')) {
    return Array.from({ length: n + 1 }, (_, i) => [v[0] + ((v[2] - v[0]) * i) / n, v[1] + ((v[3] - v[1]) * i) / n]);
  }
  const [x1, y1, r, , , , sweep, x2, y2] = v;
  const mx = (x1 + x2) / 2;
  const my = (y1 + y2) / 2;
  const half = Math.hypot(x2 - x1, y2 - y1) / 2;
  const back = Math.sqrt(Math.max(0, r * r - half * half));
  const ux = (x2 - x1) / (2 * half);
  const uy = (y2 - y1) / (2 * half);
  // The small arc's centre lies to the right of travel for sweep 1 (clockwise on screen), else to the left.
  const sign = sweep === 1 ? 1 : -1;
  const cx = mx - sign * uy * back;
  const cy = my + sign * ux * back;
  const from = Math.atan2(y1 - cy, x1 - cx);
  let to = Math.atan2(y2 - cy, x2 - cx);
  if (sweep === 1 && to < from) to += 2 * Math.PI;
  if (sweep === 0 && to > from) to -= 2 * Math.PI;
  return Array.from({ length: n + 1 }, (_, i) => {
    const phi = from + ((to - from) * i) / n;
    return [cx + r * Math.cos(phi), cy + r * Math.sin(phi)];
  });
}

function columns(stage, plan) {
  const ring = Math.max(plan.depth, bettingLines(plan, stage.width).reach);
  const pane = paneEdge(stage, ring);
  const { width: w, height: h } = plan.size;
  return ['left', 'right'].map((side) => {
    const own = slotsOf(plan).filter((s) => s.side === side);
    const outer = Math.min(...own.map((s) => local(stage, s) - extents(w, h, s.angle).ex)) - STAGE_PAD_PX;
    const inner = pane - Math.max(...own.map((s) => local(stage, s) + extents(w, h, s.angle).ex));
    return { side, outer, inner, issue: stage.width - 2 * pane };
  });
}

test('on a stage with room, each side\'s seats sit centred between the rail and the issue pane', () => {
  const roomy = [{ name: '1920x1080, panels closed', width: 1900, height: 960, top: 52, bottom: 172 }, STAGES[1]];
  for (const stage of roomy) {
    for (const count of stage === STAGES[1] ? [2, 3] : [2, 5, 6, 9, 12]) {
      for (const { side, outer, inner } of columns(stage, centreInColumns(planSeats(stage, count), stage))) {
        assert.ok(Math.abs(outer - inner) < 2, `${stage.name}, ${count} players, ${side}: ${outer.toFixed(1)} px outside, ${inner.toFixed(1)} px inside`);
      }
    }
  }
});

test('on a narrow stage the seats move in only as far as keeps the issue half the stage', () => {
  for (const stage of STAGES) {
    for (let count = 1; count <= 16; count++) {
      const before = planSeats(stage, count);
      const plan = centreInColumns(before, stage);
      for (const { side, outer, inner, issue } of columns(stage, plan)) {
        const where = `${stage.name}, ${count} players, ${side}`;
        if (Math.abs(outer - inner) >= 2) {
          assert.ok(outer < inner, `${where}: never past the middle of the column`);
          const moved = plan.depth > before.depth + 0.5;
          assert.ok(!moved || issue >= ISSUE_KEEP * stage.width - 2, `${where}: the issue keeps ${issue.toFixed(0)} px`);
        }
      }
    }
  }
});

test('moving the seats in keeps them clear of each other and facing the centre', () => {
  for (const stage of STAGES) {
    for (let count = 1; count <= 16; count++) {
      const plan = centreInColumns(planSeats(stage, count), stage);
      const all = slotsOf(plan);
      for (const seat of all) {
        const toward = Math.atan2(stage.height / 2 - seat.y, stage.width / 2 - seat.x);
        const facing = Math.atan2(-Math.cos((seat.angle * Math.PI) / 180), Math.sin((seat.angle * Math.PI) / 180));
        const off = Math.abs(((toward - facing + 3 * Math.PI) % (2 * Math.PI)) - Math.PI) * (180 / Math.PI);
        assert.ok(off <= 1, `${stage.name}, ${count} players: a seat faces ${off.toFixed(2)}° away`);
        assert.equal(seat.side, seat.x < stage.width / 2 ? 'left' : 'right');
      }
      for (let i = 0; i < all.length; i++) {
        for (let j = i + 1; j < all.length; j++) {
          assert.ok(!seatsOverlap(all[i], all[j], plan.size.width, plan.size.height), `${stage.name}, ${count} players: seats ${i} and ${j} overlap`);
        }
      }
    }
  }
});

test('the betting line keeps at least the gap from every seat outline and stops short of the pane', () => {
  assert.ok(ARC_GAP_PX >= 28, 'about double UX3\'s 14 px');
  for (const stage of STAGES) {
    for (let count = 1; count <= 16; count++) {
      const plan = centreInColumns(planSeats(stage, count), stage);
      const lines = bettingLines(plan, stage.width);
      const { pad } = printMetrics(plan.size);
      const w = plan.size.width + 2 * pad;
      const h = plan.size.height + 2 * pad;
      const pane = paneEdge(stage, Math.max(plan.depth, lines.reach));
      for (const side of ['left', 'right']) {
        if (!lines[side]) {
          continue;
        }
        const points = sample(lines[side]);
        for (const slot of slotsOf(plan).filter((s) => s.side === side)) {
          const box = corners(slot, w, h);
          const nearest = Math.min(...points.map((p) => Math.min(...box.map((q, i) => segmentDistance(p, q, box[(i + 1) % 4])))));
          assert.ok(nearest >= ARC_GAP_PX - 0.5, `${stage.name}, ${count} players, ${side}: the line comes ${nearest.toFixed(1)} px from an outline`);
        }
        const deepest = Math.max(...points.map(([x]) => (side === 'left' ? x : stage.width - x)));
        assert.ok(deepest + 4 < pane, `${stage.name}, ${count} players, ${side}: the line reaches ${deepest.toFixed(1)} px, the pane starts at ${pane.toFixed(1)}`);
      }
    }
  }
});

test('the column shift centres the group whichever edge bounds the pane', () => {
  // Pane bounded by the ring, which moves in with the seats.
  const ring = columnShift({ outer: 8, inner: 110, reach: 160, wide: 40 });
  assert.ok(Math.abs((8 + ring - 8) - (160 + ring - (110 + ring))) < 1e-9);
  // Pane bounded by its own width on a wide stage.
  const wide = columnShift({ outer: 8, inner: 110, reach: 160, wide: 290 });
  assert.ok(Math.abs((8 + wide - 8) - (290 - (110 + wide))) < 1e-9);
  assert.ok(columnShift({ outer: 40, inner: 150, reach: 160, wide: 0 }) < 0, 'seats already too far in move back out');
});
