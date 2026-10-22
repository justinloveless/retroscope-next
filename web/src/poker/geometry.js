// Where the players sit around the issue (docs/followups/UX1.md): two arcs of an ellipse inscribed in the
// stage, one down the left side and one down the right, filled alternately and spread evenly by arc length.
// The top and bottom edges stay clear for the round header and the hand. Each seat is rotated so its card's
// top edge points at the stage centre. Pure: stage size, seat count and seat size in; centres and angles out.

export const MAX_ARC_DEG = 120;
export const STAGE_PAD_PX = 8;
const SAMPLES = 96;
const IDEAL_SPREAD = 1.6;
const MAX_DEPTH_SHARE = 0.25;
const MAX_DEPTH_PX = 260;
const DEG = Math.PI / 180;

/** The rotation (degrees, clockwise) that turns a seat's local "up" toward (cx, cy). */
export function angleToward(x, y, cx, cy) {
  return Math.atan2(cx - x, -(cy - y)) / DEG;
}

/** Half the width and height of a w×h box rotated by `angle` degrees. */
export function extents(w, h, angle) {
  const c = Math.abs(Math.cos(angle * DEG));
  const s = Math.abs(Math.sin(angle * DEG));
  return { ex: (w * c + h * s) / 2, ey: (w * s + h * c) / 2 };
}

function corners(seat, w, h) {
  const c = Math.cos(seat.angle * DEG);
  const s = Math.sin(seat.angle * DEG);
  return [[-w / 2, -h / 2], [w / 2, -h / 2], [w / 2, h / 2], [-w / 2, h / 2]]
    .map(([px, py]) => [seat.x + px * c - py * s, seat.y + px * s + py * c]);
}

/** Whether two seats' rotated boxes overlap (separating axis test); boxes that only touch do not. */
export function seatsOverlap(a, b, w, h) {
  const boxes = [corners(a, w, h), corners(b, w, h)];
  for (const box of boxes) {
    for (let i = 0; i < 2; i++) {
      const [x1, y1] = box[i];
      const [x2, y2] = box[i + 1];
      const nx = y2 - y1;
      const ny = x1 - x2;
      const [p, q] = boxes.map((pts) => pts.map(([x, y]) => x * nx + y * ny));
      if (Math.max(...p) <= Math.min(...q) + 1e-6 || Math.max(...q) <= Math.min(...p) + 1e-6) {
        return false;
      }
    }
  }
  return true;
}

// Arc length from the side's midpoint to parameter u, for u in [0, uMax]; the ellipse is symmetric about it.
function arcTable(a, b, uMax) {
  const table = [0];
  const step = uMax / SAMPLES;
  for (let i = 1; i <= SAMPLES; i++) {
    const u = (i - 0.5) * step;
    table.push(table[i - 1] + Math.hypot(a * Math.sin(u), b * Math.cos(u)) * step);
  }
  return { table, step };
}

function paramAt({ table, step }, length) {
  const target = Math.abs(length);
  let i = 1;
  while (i < table.length - 1 && table[i] < target) {
    i++;
  }
  const span = table[i] - table[i - 1];
  const u = (i - 1 + (span > 0 ? Math.min(1, (target - table[i - 1]) / span) : 0)) * step;
  return Math.sign(length) * u;
}

function place(stage, side, u, geometry) {
  const { cx, cy, a, b, w, h } = geometry;
  let x = side === 'left' ? cx - a * Math.cos(u) : cx + a * Math.cos(u);
  let y = cy + b * Math.sin(u);
  let angle = angleToward(x, y, cx, cy);
  for (let pass = 0; pass < 2; pass++) {
    const { ex, ey } = extents(w, h, angle);
    x = Math.min(Math.max(x, ex + STAGE_PAD_PX), stage.width - ex - STAGE_PAD_PX);
    y = Math.min(Math.max(y, ey + STAGE_PAD_PX), stage.height - ey - STAGE_PAD_PX);
    angle = angleToward(x, y, cx, cy);
  }
  return { x, y, angle, side };
}

function sideSlots(stage, side, count, spacing, geometry) {
  if (count === 0) {
    return [];
  }
  return Array.from({ length: count }, (_, j) => {
    const length = (j - (count - 1) / 2) * spacing;
    return place(stage, side, paramAt(geometry.arc, length), geometry);
  });
}

function anyOverlap(slots, w, h) {
  for (let i = 1; i < slots.length; i++) {
    if (seatsOverlap(slots[i - 1], slots[i], w, h)) {
      return true;
    }
  }
  return false;
}

/**
 * Seats `count` players around a stage.
 *
 * stage: {width, height, top?, bottom?, maxDepth?}; `top` and `bottom` are the heights of the bands kept
 * clear (the round header and the hand): no seat centre lies in them, though a corner seat's box may spill
 * into them. `maxDepth` caps how far the ring may reach in from a side edge, so the issue keeps its width.
 * seat: {width, height, spacing}; the seat's box before rotation (`width` across its axis, `height` along
 * it) and the smallest distance between neighbouring centres along an arc.
 *
 * Returns {seats: [{x, y, angle, side}], chip, hidden, depth}: the seats in player order (player i sits on
 * the left when i is even), the "+N" chip's slot when not everyone fits (the last slot of the right arc,
 * which alternation leaves free, so both arcs hold as many items as fit), how many players that chip stands
 * for, and how far the ring reaches in from either side edge.
 */
export function seatLayout(stage, count, seat) {
  const empty = { seats: [], chip: null, hidden: Math.max(0, count), depth: 0 };
  const { width: W, height: H } = stage;
  const top = stage.top ?? 0;
  const bottom = stage.bottom ?? 0;
  const maxDepth = stage.maxDepth ?? Infinity;
  const { width: w, height: h, spacing } = seat;
  if (count <= 0 || !(W > 0) || !(H > 0)) {
    return empty;
  }
  const cx = W / 2;
  const cy = H / 2;
  const a = W / 2 - h / 2 - STAGE_PAD_PX;
  const b = H / 2 - Math.hypot(w, h) / 2 - STAGE_PAD_PX;
  const room = Math.min(cy - top, H - bottom - cy);
  if (a <= 0 || b < 0 || room <= 0) {
    return empty;
  }
  const reachAt = (u) => {
    const slot = place(stage, 'left', u, { cx, cy, a, b, w, h });
    return slot.x + extents(w, h, slot.angle).ex;
  };
  if (reachAt(0) > maxDepth) {
    return empty;
  }
  // The arc ends where a seat centre would enter the top or bottom band, at ±60°, or where the ring would
  // reach deeper than allowed, whichever comes first.
  let uMax = b === 0 ? 0 : Math.min((MAX_ARC_DEG / 2) * DEG, Math.asin(Math.min(1, room / b)));
  if (reachAt(uMax) > maxDepth) {
    let lo = 0;
    let hi = uMax;
    for (let i = 0; i < 30; i++) {
      const mid = (lo + hi) / 2;
      [lo, hi] = reachAt(mid) > maxDepth ? [lo, mid] : [mid, hi];
    }
    uMax = lo;
  }
  const geometry = { cx, cy, a, b, w, h, arc: arcTable(a, b, uMax) };
  const length = 2 * geometry.arc.table[SAMPLES];

  let perSide = Math.floor(length / spacing + 1e-9) + 1;
  while (perSide > 1 && anyOverlap(sideSlots(stage, 'left', perSide, length / (perSide - 1), geometry), w, h)) {
    perSide--;
  }
  const capacity = 2 * perSide;
  const overflowing = count > capacity;
  const shown = overflowing ? capacity - 1 : count;
  const slots = overflowing ? capacity : count;
  const left = Math.ceil(slots / 2);
  const right = Math.floor(slots / 2);
  const fuller = Math.max(left, right);
  const step = fuller > 1 ? Math.min(spacing * IDEAL_SPREAD, length / (fuller - 1)) : 0;
  const leftSlots = sideSlots(stage, 'left', left, step, geometry);
  const rightSlots = sideSlots(stage, 'right', right, step, geometry);

  const seats = [];
  for (let i = 0; i < shown; i++) {
    seats.push(i % 2 === 0 ? leftSlots[i / 2] : rightSlots[(i - 1) / 2]);
  }
  const chip = overflowing ? rightSlots[right - 1] : null;
  let depth = 0;
  for (const slot of chip ? [...seats, chip] : seats) {
    const { ex } = extents(w, h, slot.angle);
    depth = Math.max(depth, slot.side === 'left' ? slot.x + ex : W - slot.x + ex);
  }
  return { seats, chip, hidden: count - shown, depth };
}

/**
 * Seat sizes from roomy to the avatar-only minimum: the box (`width` across, `height` along the axis),
 * the smallest centre distance along an arc (the box plus an upright name label), and the avatar diameter
 * and card scale the stylesheet draws. The avatar overlaps the card spot's outer end by 4 px.
 */
export const SEAT_SIZES = Object.freeze([
  Object.freeze({ name: 'full', width: 46, height: 82, spacing: 68, avatar: 34, card: 0.55 }),
  Object.freeze({ name: 'compact', width: 36, height: 64, spacing: 56, avatar: 28, card: 0.42 }),
  Object.freeze({ name: 'avatar', width: 34, height: 34, spacing: 40, avatar: 30, card: 0 }),
]);

/**
 * The roomiest size that seats everyone within `maxDepth` (a quarter of the stage width, at most 260 px,
 * unless the stage says otherwise), else the avatar-only minimum with a "+N" chip.
 */
export function planSeats(stage, count) {
  const limited = { maxDepth: Math.min(MAX_DEPTH_PX, stage.width * MAX_DEPTH_SHARE), ...stage };
  let layout = null;
  for (const size of SEAT_SIZES) {
    layout = { size, ...seatLayout(limited, count, size) };
    if (layout.hidden === 0) {
      return layout;
    }
  }
  return layout;
}
