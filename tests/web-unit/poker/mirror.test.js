// The two betting lines are mirror images (docs/followups/UX5.md): the bigger side's span and curve, the same
// distance from the stage centre, at least the gap from every outline on both sides, clear of the issue panel,
// and both drawn whenever anyone is seated.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { planSeats } from '../../../web/src/poker/geometry.js';
import { ARC_GAP_PX, bettingLines, centreInColumns, printMetrics } from '../../../web/src/poker/seats.js';

const STAGES = [
  { name: '1440x900', width: 800, height: 780, top: 52, bottom: 172 },
  { name: '1920x1080', width: 1280, height: 960, top: 52, bottom: 172 },
  { name: '1280x800', width: 640, height: 680, top: 52, bottom: 172 },
];
// neotro.css .nt-field --nt-pane-x: max(ring, 50% - 23rem) + 0.75rem.
const paneEdge = (stage, ring) => Math.max(ring, stage.width / 2 - 368) + 12;
const slotsOf = (plan) => (plan.chip ? [...plan.seats, plan.chip] : plan.seats);
const numbers = (d) => d.match(/-?\d+(\.\d+)?/g).map(Number);

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
  const v = numbers(d);
  if (!d.includes('A')) {
    return Array.from({ length: n + 1 }, (_, i) => [v[0] + ((v[2] - v[0]) * i) / n, v[1] + ((v[3] - v[1]) * i) / n]);
  }
  const [x1, y1, r, , , , sweep, x2, y2] = v;
  const half = Math.hypot(x2 - x1, y2 - y1) / 2;
  const back = Math.sqrt(Math.max(0, r * r - half * half));
  const ux = (x2 - x1) / (2 * half);
  const uy = (y2 - y1) / (2 * half);
  const sign = sweep === 1 ? 1 : -1;
  const cx = (x1 + x2) / 2 - sign * uy * back;
  const cy = (y1 + y2) / 2 + sign * ux * back;
  const from = Math.atan2(y1 - cy, x1 - cx);
  let to = Math.atan2(y2 - cy, x2 - cx);
  if (sweep === 1 && to < from) to += 2 * Math.PI;
  if (sweep === 0 && to > from) to -= 2 * Math.PI;
  return Array.from({ length: n + 1 }, (_, i) => {
    const phi = from + ((to - from) * i) / n;
    return [cx + r * Math.cos(phi), cy + r * Math.sin(phi)];
  });
}

function eachTable(check) {
  for (const stage of STAGES) {
    for (let count = 1; count <= 12; count++) {
      const plan = centreInColumns(planSeats(stage, count), stage);
      check(stage, count, plan, bettingLines(plan, stage.width));
    }
  }
}

test('the two lines mirror each other across the stage centre, uneven sides included', () => {
  let uneven = 0;
  eachTable((stage, count, plan, lines) => {
    const where = `${stage.name}, ${count} players`;
    const left = numbers(lines.left);
    const right = numbers(lines.right);
    assert.equal(left.length, right.length, `${where}: same kind of path`);
    const xs = lines.left.includes('A') ? [0, 7] : [0, 2];
    const rs = lines.left.includes('A') ? [2, 3] : [];
    for (const i of xs) {
      assert.ok(Math.abs(left[i] - (stage.width - right[i])) <= 0.5, `${where}: x ${left[i]} vs ${right[i]}`);
      assert.ok(Math.abs(left[i + 1] - right[i + 1]) <= 0.5, `${where}: y ${left[i + 1]} vs ${right[i + 1]}`);
    }
    for (const i of rs) {
      assert.ok(Math.abs(left[i] - right[i]) <= 0.5, `${where}: radius ${left[i]} vs ${right[i]}`);
    }
    const ys = slotsOf(plan).map((s) => s.y);
    assert.ok(Math.min(left[1], left[xs[1] + 1]) < Math.min(...ys) && Math.max(left[1], left[xs[1] + 1]) > Math.max(...ys), `${where}: both lines run past every seat`);
    const counts = ['left', 'right'].map((side) => slotsOf(plan).filter((s) => s.side === side).length);
    uneven += counts[0] !== counts[1] ? 1 : 0;
  });
  assert.ok(uneven >= 12, 'odd tables are covered');
});

test('both lines keep at least the gap from every seat outline', () => {
  eachTable((stage, count, plan, lines) => {
    const { pad } = printMetrics(plan.size);
    const w = plan.size.width + 2 * pad;
    const h = plan.size.height + 2 * pad;
    for (const side of ['left', 'right']) {
      const points = sample(lines[side]);
      for (const slot of slotsOf(plan)) {
        const box = corners(slot, w, h);
        const nearest = Math.min(...points.map((p) => Math.min(...box.map((q, i) => segmentDistance(p, q, box[(i + 1) % 4])))));
        assert.ok(nearest >= ARC_GAP_PX - 0.5, `${stage.name}, ${count} players: the ${side} line comes ${nearest.toFixed(1)} px from an outline`);
      }
    }
  });
});

test('neither line enters the issue panel', () => {
  eachTable((stage, count, plan, lines) => {
    const pane = paneEdge(stage, Math.max(plan.depth, lines.reach));
    for (const side of ['left', 'right']) {
      const deepest = Math.max(...sample(lines[side]).map(([x]) => (side === 'left' ? x : stage.width - x)));
      assert.ok(deepest + 4 < pane, `${stage.name}, ${count} players, ${side}: the line reaches ${deepest.toFixed(1)} px, the pane starts at ${pane.toFixed(1)}`);
    }
  });
});

test('one seated player still gets both lines', () => {
  for (const stage of STAGES) {
    const plan = centreInColumns(planSeats(stage, 1), stage);
    assert.equal(slotsOf(plan).filter((s) => s.side === 'right').length, 0);
    const lines = bettingLines(plan, stage.width);
    assert.ok(lines.left && lines.right, `${stage.name}: two lines`);
  }
});

test('nobody seated draws no lines', () => {
  for (const stage of STAGES) {
    const lines = bettingLines(planSeats(stage, 0), stage.width);
    assert.equal(lines.left, null);
    assert.equal(lines.right, null);
    assert.equal(lines.reach, 0);
  }
});
