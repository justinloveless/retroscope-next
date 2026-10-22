// Where the players sit around the issue (docs/followups/UX2.md, UX4.md): two independent shallow circular
// arcs, one down each side, wrapping around the issue like brackets: ( ISSUE ). Each arc's middle is the point
// farthest from the stage centre and its ends curve in toward it by a small sagitta. Each side's group is
// centred vertically between the top and bottom bands (the round header and the hand). Players fill the sides
// alternately and are spread evenly by arc length. Each seat is rotated so its card's top edge points at the
// stage centre.
// Pure: stage size, seat count and seat size in; centres and angles out.

export const STAGE_PAD_PX = 8;
export const MAX_SAGITTA_PX = 56;
export const MAX_SAGITTA_SHARE = 0.05;
export const MAX_DEPTH_SHARE = 0.16;
export const MAX_DEPTH_PX = 220;
const IDEAL_SPREAD = 1.6;
const FIT_STEPS = 24;
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

/** How far a side's arc may bow in from its chord on a stage `width` wide. */
export function maxSagitta(width) {
  return Math.min(MAX_SAGITTA_PX, width * MAX_SAGITTA_SHARE);
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

// A circular arc through the chord's ends (half-length `half`) whose middle lies `sagitta` in from the chord.
function arcOf(half, sagitta) {
  if (sagitta < 1e-6) {
    return { radius: Infinity, length: 2 * half, sagitta: 0 };
  }
  const radius = (half * half + sagitta * sagitta) / (2 * sagitta);
  return { radius, length: 2 * radius * Math.asin(Math.min(1, half / radius)), sagitta };
}

// The point `along` px of arc length from the arc's middle: how far in from the middle (the arc's outermost
// point), and how far down.
function pointOn(arc, along) {
  if (!Number.isFinite(arc.radius)) {
    return { inward: 0, down: along };
  }
  const theta = along / arc.radius;
  return { inward: arc.radius * (1 - Math.cos(theta)), down: arc.radius * Math.sin(theta) };
}

// One side's seats, top to bottom, `step` apart along the arc and centred on its middle, so the group sits
// halfway between the bands. The whole side is moved in just far enough that every rotated seat clears the
// stage padding, which keeps its shape.
function sideSlots(frame, side, count, arc, step) {
  const { stage, middle, w, h } = frame;
  const cx = stage.width / 2;
  const cy = stage.height / 2;
  const points = Array.from({ length: count }, (_, j) => pointOn(arc, (j - (count - 1) / 2) * step));
  let shift = STAGE_PAD_PX + h / 2;
  let slots = [];
  for (let pass = 0; pass < 3; pass++) {
    slots = points.map(({ inward, down }) => {
      const x = shift + inward;
      const y = middle + down;
      return { x: side === 'left' ? x : stage.width - x, y, angle: angleToward(side === 'left' ? x : stage.width - x, y, cx, cy), side };
    });
    shift = Math.max(...slots.map((slot, j) => STAGE_PAD_PX + extents(w, h, slot.angle).ex - points[j].inward));
  }
  return slots;
}

function depthOf(slots, frame) {
  let depth = 0;
  for (const slot of slots) {
    const { ex } = extents(frame.w, frame.h, slot.angle);
    depth = Math.max(depth, slot.side === 'left' ? slot.x + ex : frame.stage.width - slot.x + ex);
  }
  return depth;
}

function overlapping(slots, w, h) {
  for (let i = 1; i < slots.length; i++) {
    if (seatsOverlap(slots[i - 1], slots[i], w, h)) {
      return true;
    }
  }
  return false;
}

// The deepest bow and widest spread at which `count` seats fit on one side: the sagitta gives way first, then
// the spread, down to the seat's minimum spacing. Null when they do not fit within the stage's depth.
function fitSide(frame, count) {
  const { half, spacing, maxDepth, w, h } = frame;
  const fits = (sagitta, step) => {
    const arc = arcOf(half, sagitta);
    if ((count - 1) * step > arc.length + 1e-6) {
      return null;
    }
    const slots = sideSlots(frame, 'left', count, arc, step);
    return depthOf(slots, frame) <= maxDepth && !overlapping(slots, w, h) ? { arc, step } : null;
  };
  const roomy = (sagitta) => (count > 1 ? Math.min(spacing * IDEAL_SPREAD, arcOf(half, sagitta).length / (count - 1)) : 0);
  const search = (lo, hi, at) => {
    for (let i = 0; i < FIT_STEPS; i++) {
      const mid = (lo + hi) / 2;
      [lo, hi] = at(mid) ? [mid, hi] : [lo, mid];
    }
    return lo;
  };
  const most = frame.sagitta;
  const best = fits(most, roomy(most));
  if (best) {
    return best;
  }
  const flat = fits(0, roomy(0));
  if (flat) {
    const sagitta = search(0, most, (s) => fits(s, roomy(s)));
    return fits(sagitta, roomy(sagitta));
  }
  if (count === 1 || !fits(0, spacing)) {
    return null;
  }
  return fits(0, search(spacing, roomy(0), (step) => fits(0, step)));
}

/**
 * Seats `count` players around a stage.
 *
 * stage: {width, height, top?, bottom?, maxDepth?}; `top` and `bottom` are the heights of the bands kept
 * clear (the round header and the hand): no seat centre lies in them, though a corner seat's box may spill
 * into them. `maxDepth` caps how far a side may reach in from its edge, so the issue keeps its width.
 * seat: {width, height, spacing}; the seat's box before rotation (`width` across its axis, `height` along
 * it) and the smallest distance between neighbouring centres along an arc.
 *
 * Returns {seats: [{x, y, angle, side}], chip, hidden, depth, sagitta, curve}: the seats in player order
 * (player i sits on the left when i is even), the "+N" chip's slot when not everyone fits (the last slot of
 * the right arc, which alternation leaves free, so both arcs hold as many items as fit), how many players
 * that chip stands for, how far the seats reach in from either side edge, how far the arcs' ends curve in,
 * and the arcs' shared circle: {radius, middle} (radius Infinity for a straight side, middle the height of
 * the arcs' outermost point). The circle's centre lies in toward the stage centre.
 */
export function seatLayout(stage, count, seat) {
  const empty = { seats: [], chip: null, hidden: Math.max(0, count), depth: 0, sagitta: 0, curve: null };
  const { width: W, height: H } = stage;
  const { width: w, height: h, spacing } = seat;
  if (count <= 0 || !(W > 0) || !(H > 0)) {
    return empty;
  }
  const corner = Math.hypot(w, h) / 2 + STAGE_PAD_PX;
  const first = Math.max(stage.top ?? 0, corner);
  const last = Math.min(H - (stage.bottom ?? 0), H - corner);
  if (last < first) {
    return empty;
  }
  // Centred between the bands, as long as the room allows on both sides of the middle.
  const middle = Math.max(first, Math.min(last, ((stage.top ?? 0) + H - (stage.bottom ?? 0)) / 2));
  const half = Math.min(middle - first, last - middle);
  const frame = {
    stage,
    w,
    h,
    spacing,
    middle,
    half,
    sagitta: Math.min(maxSagitta(W), half),
    maxDepth: Math.min(stage.maxDepth ?? Infinity, W / 2),
  };

  let perSide = Math.floor((2 * frame.half) / spacing + 1e-9) + 1;
  while (perSide > 0 && !fitSide(frame, perSide)) {
    perSide--;
  }
  if (perSide === 0) {
    return empty;
  }
  const capacity = 2 * perSide;
  const overflowing = count > capacity;
  const shown = overflowing ? capacity - 1 : count;
  const slots = overflowing ? capacity : count;
  const left = Math.ceil(slots / 2);
  const right = Math.floor(slots / 2);
  const { arc, step } = fitSide(frame, left);
  const leftSlots = sideSlots(frame, 'left', left, arc, step);
  const rightSlots = right > 0 ? sideSlots(frame, 'right', right, arc, step) : [];

  const seats = [];
  for (let i = 0; i < shown; i++) {
    seats.push(i % 2 === 0 ? leftSlots[i / 2] : rightSlots[(i - 1) / 2]);
  }
  const chip = overflowing ? rightSlots[right - 1] : null;
  const depth = depthOf([...leftSlots, ...rightSlots], frame);
  return { seats, chip, hidden: count - shown, depth, sagitta: arc.sagitta, curve: { radius: arc.radius, middle: frame.middle } };
}

const round1 = (n) => Math.round(n * 10) / 10;

// The side's circle in the left side's coordinates (the right side mirrored): its centre, in from the slots.
function circleOf(slots, curve, width) {
  const local = (slot) => (slots[0].side === 'left' ? slot.x : width - slot.x);
  const { radius, middle } = curve;
  const first = slots[0];
  const cx = local(first) + radius * Math.cos(Math.asin(Math.max(-1, Math.min(1, (first.y - middle) / radius))));
  return { cx, cy: middle, radius, local };
}

function segmentDistance(px, py, [x1, y1], [x2, y2]) {
  const dx = x2 - x1;
  const dy = y2 - y1;
  const t = Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / (dx * dx + dy * dy)));
  return Math.hypot(px - x1 - t * dx, py - y1 - t * dy);
}

/**
 * How far in from a side's seat curve a `w`×`h` outline around each slot reaches, measured as the betting line
 * is (radially, toward the circle's centre): the smallest `offset` at which printedArc's line touches one.
 */
export function outlineReach(slots, curve, { w, h, width }) {
  if (!slots.length || !curve) {
    return 0;
  }
  const local = (x) => (slots[0].side === 'left' ? x : width - x);
  if (!Number.isFinite(curve.radius)) {
    return Math.max(...slots.map((slot) => Math.max(...corners(slot, w, h).map(([x]) => local(x))) - local(slot.x)));
  }
  const circle = circleOf(slots, curve, width);
  return Math.max(...slots.map((slot) => {
    const box = corners(slot, w, h).map(([x, y]) => [local(x), y]);
    const nearest = Math.min(...box.map((p, i) => segmentDistance(circle.cx, circle.cy, p, box[(i + 1) % 4])));
    return circle.radius - nearest;
  }));
}

/**
 * The printed betting line of one side (docs/followups/UX3.md, UX4.md): {d, reach}, an SVG path concentric
 * with that side's seat arc, `offset` px further in toward the stage centre, running from `overhang` px above
 * the first slot to `overhang` px below the last, and how far in from the side edge the line reaches (at its
 * ends, which curve in). `slots` are the side's slots (seats and the chip), `curve` is seatLayout's, `width`
 * the stage's. Null when the side has no slots.
 */
export function bettingArc(slots, curve, { offset, overhang, width }) {
  if (!slots.length || !curve) {
    return null;
  }
  const { side } = slots[0];
  const sorted = [...slots].sort((a, b) => a.y - b.y);
  const first = sorted[0];
  const last = sorted[sorted.length - 1];
  const out = (x) => round1(side === 'left' ? x : width - x);
  const circle = circleOf(sorted, curve, width);
  const r = circle.radius - offset;
  if (!Number.isFinite(circle.radius) || r <= 0) {
    const x = circle.local(first) + offset;
    return { d: `M ${out(x)} ${round1(first.y - overhang)} L ${out(x)} ${round1(last.y + overhang)}`, reach: x };
  }
  const angle = (y) => Math.asin(Math.max(-1, Math.min(1, (y - circle.cy) / r)));
  const from = angle(first.y - overhang);
  const to = angle(last.y + overhang);
  const at = (phi) => [out(circle.cx - r * Math.cos(phi)), round1(circle.cy + r * Math.sin(phi))];
  const [x1, y1] = at(from);
  const [x2, y2] = at(to);
  const reach = circle.cx - r * Math.cos(Math.max(Math.abs(from), Math.abs(to)));
  return { d: `M ${x1} ${y1} A ${round1(r)} ${round1(r)} 0 0 ${side === 'left' ? 0 : 1} ${x2} ${y2}`, reach };
}

/** bettingArc's path alone. */
export function printedArc(slots, curve, options) {
  return bettingArc(slots, curve, options)?.d ?? null;
}

/**
 * How far to move both sides' seats in from the edge so each side's group sits centred in its felt column,
 * between the rail (`rail` px in) and the issue pane. The seats span `outer` to `inner` px in from the edge;
 * the pane's edge lies at max(reach + shift, wide): `reach` moves in with the seats (the betting line plus
 * its clearance), `wide` is where the pane stops on a stage wider than it needs. Negative when the seats
 * already sit too far in.
 */
export function columnShift({ outer, inner, reach, wide, rail = STAGE_PAD_PX }) {
  const bound = reach + rail - outer - inner;
  return reach + bound >= wide ? bound : (wide + rail - outer - inner) / 2;
}

/**
 * The layout with each side's slots moved `shift.left` or `shift.right` px in from their edge and turned to face
 * the centre again; `depth` becomes the deeper side's reach. `layout` is planSeats'.
 */
export function shiftLayout(layout, shift, stage) {
  const move = (slot) => {
    const x = slot.side === 'left' ? slot.x + shift.left : slot.x - shift.right;
    return { ...slot, x, angle: angleToward(x, slot.y, stage.width / 2, stage.height / 2) };
  };
  const seats = layout.seats.map(move);
  const chip = layout.chip && move(layout.chip);
  const depth = depthOf(chip ? [...seats, chip] : seats, { w: layout.size.width, h: layout.size.height, stage });
  return { ...layout, seats, chip, depth };
}

/**
 * Seat sizes from roomy to the avatar-only minimum: the box (`width` across, `height` along the axis: the
 * card itself), the smallest centre distance along an arc (the card plus an upright name label), the avatar
 * diameter, how far the avatar sits in from the card's outer end, and the card scale the stylesheet draws.
 * The card scales are the ones the pixel art renders crisply at.
 */
export const SEAT_SIZES = Object.freeze([
  Object.freeze({ name: 'large', width: 107, height: 143, spacing: 131, avatar: 48, inset: 6, card: 1.5 }),
  Object.freeze({ name: 'full', width: 71, height: 95, spacing: 95, avatar: 34, inset: 4, card: 1 }),
  Object.freeze({ name: 'compact', width: 54, height: 72, spacing: 78, avatar: 26, inset: 3, card: 0.75 }),
  Object.freeze({ name: 'avatar', width: 34, height: 34, spacing: 40, avatar: 30, inset: 2, card: 0 }),
]);

/** How far the seats may reach in from a side of a stage `width` wide. */
export function depthLimit(width) {
  return Math.min(MAX_DEPTH_PX, width * MAX_DEPTH_SHARE);
}

/**
 * The roomiest size that seats everyone within the stage's depth limit (`depthLimit`, unless the stage says
 * otherwise), else the avatar-only minimum with a "+N" chip.
 */
export function planSeats(stage, count) {
  const limited = { maxDepth: depthLimit(stage.width), ...stage };
  let layout = null;
  for (const size of SEAT_SIZES) {
    layout = { size, ...seatLayout(limited, count, size) };
    if (layout.hidden === 0) {
      return layout;
    }
  }
  return layout;
}
