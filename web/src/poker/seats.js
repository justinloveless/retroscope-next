// The players around the issue (docs/followups/UX1.md, UX2.md): each seat sits on the left or right arc of
// the stage (geometry.js), turned so its card faces the centre, with the avatar on the card's outer end and an
// upright name label. A locked vote slides in face down along the seat's axis; a reveal flips every seated
// card in place. Votes stay secret until the reveal: before it a seat only knows "waiting", "thinking" or
// "locked in". Narrow stages get one scrolling avatar row instead of the ring. The felt under them carries a
// printed outline per seat (drawn by the stylesheet around the card slot) and one printed betting line per
// side (docs/followups/UX3.md), the two mirror images of each other (UX5.md), SVG paths recomputed with the layout.

import { announce } from '../core/a11y.js';
import { h, replaceContent, setStyle } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { createAvatar } from '../ui/avatar.js';
import { createPopover } from '../ui/popover.js';
import { STAGE_PAD_PX, columnShift, extents, mirroredArcs, planSeats, shiftLayout } from './geometry.js';
import { voteLabel } from './model.js';
import { FLIP_DELAY_MS, cardEl, faceEl, reducedMotion } from './reveal.js';

export const ROW_BELOW_PX = 560;
export const ANNOUNCE_EVERY_MS = 1000;
const LABEL_GAP_PX = 3;
const LABEL_MAX_PX = 128;
// The printed outline stands this far out from the card (px at card scale 1, plus a fixed part); the
// stylesheet draws it with the same numbers (.nt-seat-slot::before).
const PRINT_PAD_PX = 4;
const PRINT_PAD_SCALE = 3;
// The betting line keeps this gap from the seats' outlines and ends this far past the outer seats.
export const ARC_GAP_PX = 28;
const ARC_OVERHANG_PX = 22;
// Room left between the betting line and the issue panel.
const ARC_CLEAR_PX = 10;
// Where the issue pane stops (neotro.css .nt-field --nt-pane-x): max(ring, 50% - 23rem) + 0.75rem.
const PANE_HALF_PX = 368;
const PANE_INSET_PX = 12;
// The share of the stage's width the issue pane keeps however the seats are centred.
export const ISSUE_KEEP = 0.5;
const SVG_NS = 'http://www.w3.org/2000/svg';

const STATUS_TEXT = { empty: 'waiting', selected: 'thinking', locked: 'locked in' };

function statusOf(seat) {
  return seat.vote?.status ?? (seat.vote ? 'locked' : 'empty');
}

function revealedText(vote) {
  if (!vote) {
    return 'did not vote';
  }
  if (vote.abstained || vote.points == null) {
    return 'abstained';
  }
  return vote.high == null ? `voted ${vote.points}` : `voted ${vote.points} to ${vote.high}`;
}

/** The seat's accessible name, e.g. "Marcus Chen, locked in" or "Marcus Chen, voted 5". */
export function seatName(seat, revealed) {
  const who = seat.kind === 'guest' ? `${seat.name} (guest)` : seat.name;
  const what = seat.observer ? 'observer' : revealed ? revealedText(seat.vote) : STATUS_TEXT[statusOf(seat)];
  return `${who}, ${what}${seat.present ? '' : ', away'}`;
}

function same(a, b) {
  return a.name === b.name && a.kind === b.kind && a.present === b.present && a.vote === b.vote
    && a.observer === b.observer && a.avatar === b.avatar && a.revealed === b.revealed;
}

/** A card that turns over on reveal; between cards and reduced motion crossfade instead. */
export function flipCard(vote, { flipped, fade }) {
  const abstained = vote.abstained || vote.points == null;
  const between = !abstained && vote.high != null;
  return h('span', { class: ['nt-flip', (between || fade) && 'nt-flip-fade', flipped && 'is-flipped'] },
    h('span', { class: 'nt-flip-inner' },
      cardEl('back', 'nt-flip-back'),
      h('span', { class: 'nt-flip-front' }, faceEl(vote.points, vote.high, abstained))));
}

function valueText(vote) {
  if (vote.abstained || vote.points == null) {
    return '–';
  }
  return voteLabel(vote.points, vote.high).replace(/ pts$/, '');
}

function createNode(seat) {
  const slot = h('span', { class: 'nt-seat-slot' });
  const value = h('span', { class: 'nt-seat-value', hidden: true });
  const avatar = createAvatar({ name: seat.name, src: seat.avatar ? `/blob/${seat.avatar}` : null, size: 'sm' });
  avatar.el.removeAttribute('role');
  avatar.el.removeAttribute('aria-label');
  const name = h('span', { class: 'nt-seat-name' });
  const vote = h('span', { class: 'nt-seat-vote', hidden: true });
  const label = h('span', { class: 'nt-seat-label', 'aria-hidden': 'true' }, name, h('span', { class: 'nt-badge-guest', text: 'guest' }), vote);
  const node = h('li', { class: 'nt-seat' },
    h('span', { class: 'nt-seat-body', 'aria-hidden': 'true' },
      slot,
      h('span', { class: 'nt-seat-avatar' }, avatar.el, h('span', { class: 'nt-presence' }), value)),
    label);
  node.parts = { slot, value, avatar, name, vote, label };
  return node;
}

/**
 * The seats. update(seats, {revealed, animate, live}) patches them: `animate` is false when the viewer
 * switched rounds (nothing slides in), `live` is true when a reveal happened in front of the viewer (the
 * cards flip after FLIP_DELAY_MS). layout({width, height, top, bottom}) places them for a stage of that
 * size and returns {depth, row}: how far the ring reaches in from either side, and whether the seats are
 * one scrolling row instead.
 */
/** The printed outline's pad and stroke (px) for a seat size; the stylesheet's formulas, in script. */
export function printMetrics(size) {
  const scale = Math.max(size.card, 0.5);
  return { pad: PRINT_PAD_PX + PRINT_PAD_SCALE * size.card, stroke: 2 + 2.5 * scale };
}

// One side of a placed ring, measured in from its edge: the seats' outermost and innermost reach. Null when the
// side is empty.
function sideOf(plan, side, width) {
  const own = slotsOf(plan).filter((slot) => slot.side === side);
  if (!own.length) {
    return null;
  }
  const { width: w, height: h } = plan.size;
  const local = (slot) => (side === 'left' ? slot.x : width - slot.x);
  return {
    outer: Math.min(...own.map((slot) => local(slot) - extents(w, h, slot.angle).ex)),
    inner: Math.max(...own.map((slot) => local(slot) + extents(w, h, slot.angle).ex)),
  };
}

function slotsOf(plan) {
  return plan.chip ? [...plan.seats, plan.chip] : plan.seats;
}

/**
 * The betting lines for a placed ring (docs/followups/UX5.md): {left, right, reach, needs}: the two paths, mirror
 * images of each other (both null when nobody is seated), how far the lines reach in from the side edges
 * (with the stroke and clearance), so the issue can keep clear of them, and `needs`: that reach were only
 * one side's seats placing the lines.
 */
export function bettingLines(plan, width) {
  const all = slotsOf(plan);
  const { pad, stroke } = printMetrics(plan.size);
  const box = { w: plan.size.width + 2 * pad, h: plan.size.height + 2 * pad };
  const sides = { left: all.filter((slot) => slot.side === 'left'), right: all.filter((slot) => slot.side === 'right') };
  const arcs = mirroredArcs(sides, plan.curve, { gap: ARC_GAP_PX, overhang: ARC_OVERHANG_PX + box.w / 4, box, width });
  if (!arcs) {
    return { left: null, right: null, reach: 0, needs: {} };
  }
  const clear = stroke / 2 + ARC_CLEAR_PX;
  const needs = Object.fromEntries(Object.entries(arcs.needs).map(([side, reach]) => [side, reach + clear]));
  return { left: arcs.left, right: arcs.right, reach: arcs.reach + clear, needs };
}

/**
 * The placed ring moved in from the edges so each side's seats sit centred in the felt between the rail and
 * the issue pane (docs/followups/UX4.md), with the betting line between them and the pane. Where the stage is
 * too narrow for that, the seats move in only as far as keeps the issue pane ISSUE_KEEP of the stage's width.
 */
export function centreInColumns(plan, stage) {
  if (!plan.seats.length) {
    return plan;
  }
  const { width } = stage;
  const wide = width / 2 - PANE_HALF_PX + PANE_INSET_PX;
  const most = Math.max(wide, (width * (1 - ISSUE_KEEP)) / 2);
  const measure = (layout, side) => {
    const m = sideOf(layout, side, width);
    return m && { ...m, reach: bettingLines(layout, width).needs[side] + PANE_INSET_PX };
  };
  // The pane's edge follows the side whose line reaches furthest in; that side is centred with the pane moving
  // along with it, the other within the pane that leaves. Moving a seat also turns it a little further toward
  // the centre, which widens it, so a few passes settle the result.
  const shift = { left: 0, right: 0 };
  let placed = plan;
  for (let pass = 0; pass < 8; pass++) {
    const sides = { left: measure(placed, 'left'), right: measure(placed, 'right') };
    const [lead, other] = !sides.right || (sides.left && sides.left.reach >= sides.right.reach) ? ['left', 'right'] : ['right', 'left'];
    const m = sides[lead];
    const step = Math.min(most - m.reach, columnShift({ ...m, wide }));
    shift[lead] = Math.max(0, shift[lead] + step);
    const pane = Math.max(m.reach + step, wide);
    if (sides[other]) {
      const o = sides[other];
      const centred = (STAGE_PAD_PX + pane - o.outer - o.inner) / 2;
      shift[other] = Math.max(0, shift[other] + Math.min(centred, pane - o.reach));
    }
    placed = shiftLayout(plan, shift, stage);
  }
  return placed;
}

export function createSeats() {
  const el = h('ul', { class: 'nt-seats', 'aria-label': 'Players' });
  const print = document.createElementNS(SVG_NS, 'svg');
  print.setAttribute('class', 'nt-print');
  print.setAttribute('aria-hidden', 'true');
  const lines = ['left', 'right'].map((side) => {
    const path = document.createElementNS(SVG_NS, 'path');
    path.setAttribute('class', `nt-print-arc nt-print-${side}`);
    print.append(path);
    return path;
  });
  const moreList = h('ul', { class: 'nt-more-list' });
  const more = h('li', { class: 'nt-seat-more' });
  const moreButton = h('button', { type: 'button', class: 'nt-more-button', 'aria-haspopup': 'dialog' });
  more.append(moreButton);
  const popover = createPopover({ anchor: moreButton, label: 'More players', content: moreList, placement: 'left' });
  moreButton.addEventListener('click', () => {
    renderMore();
    popover.open();
  });

  let seats = [];
  let stage = null;
  let plan = null;
  let hidden = [];
  let flipTimer = 0;
  let queue = [];
  let lastAnnounced = 0;
  let announceTimer = 0;

  function say(message) {
    queue.push(message);
    if (announceTimer) {
      return;
    }
    const wait = Math.max(0, lastAnnounced + ANNOUNCE_EVERY_MS - Date.now());
    announceTimer = setTimeout(() => {
      announceTimer = 0;
      lastAnnounced = Date.now();
      announce(queue.join('. '));
      queue = [];
    }, wait);
  }

  function renderMore() {
    moreButton.textContent = `+${hidden.length}`;
    moreButton.setAttribute('aria-label', `${hidden.length} more ${hidden.length === 1 ? 'player' : 'players'}`);
    replaceContent(moreList, hidden.map((seat) => h('li', { class: 'nt-more-item', 'data-pid': seat.pid },
      h('span', { class: 'nt-more-name', text: seat.name }),
      h('span', { class: 'nt-more-status', text: seatName(seat, seat.revealed).slice(seat.name.length + 2) }))));
  }

  function renderSlot(node, seat, previous, animate) {
    const { slot, value, vote } = node.parts;
    value.hidden = true;
    vote.hidden = true;
    if (seat.observer) {
      slot.replaceChildren();
      return;
    }
    if (seat.revealed) {
      if (!previous?.revealed || previous.vote !== seat.vote || !slot.querySelector('.nt-flip, .nt-slot-empty')) {
        slot.replaceChildren(seat.vote ? flipCard(seat.vote, { flipped: !animate.live, fade: reducedMotion() }) : h('span', { class: 'nt-slot-empty' }));
      }
      if (seat.vote) {
        value.hidden = false;
        vote.hidden = false;
        value.textContent = valueText(seat.vote);
        vote.textContent = valueText(seat.vote);
      }
      return;
    }
    const status = statusOf(seat);
    const wasLocked = previous && !previous.revealed && statusOf(previous) === 'locked';
    if (status === 'locked') {
      if (!wasLocked || !slot.querySelector('.nt-card')) {
        slot.replaceChildren(cardEl('back', animate.slide ? 'nt-slide-in' : null));
      }
    } else {
      slot.replaceChildren(h('span', { class: ['nt-slot-empty', status === 'selected' && 'is-thinking'] }));
    }
  }

  function patch(node, seat, animate) {
    const previous = node.seat;
    if (previous && same(previous, seat)) {
      return;
    }
    node.seat = seat;
    const status = statusOf(seat);
    node.dataset.pid = seat.pid;
    node.dataset.status = seat.revealed ? 'revealed' : status;
    node.toggleAttribute('data-absent', !seat.present);
    node.toggleAttribute('data-observer', seat.observer);
    node.classList.toggle('is-guest', seat.kind === 'guest');
    node.parts.name.textContent = seat.name;
    node.parts.avatar.update({ name: seat.name, src: seat.avatar ? `/blob/${seat.avatar}` : null });
    node.setAttribute('aria-label', seatName(seat, seat.revealed));
    node.title = seat.name;
    renderSlot(node, seat, previous, animate);
    if (previous && animate.slide) {
      if (previous.present !== seat.present) {
        say(`${seat.name} ${seat.present ? 'joined' : 'left'}`);
      }
      if (!seat.revealed && !previous.revealed && status !== statusOf(previous) && (status === 'locked' || statusOf(previous) === 'locked')) {
        say(`${seat.name} ${status === 'locked' ? 'locked in' : 'unlocked'}`);
      }
    }
  }

  function position(node, slot, size) {
    if (!slot) {
      node.placed = false;
      node.labelAt = null;
      setStyle(node, { '--x': null, '--y': null, '--a': null, '--lx': null, '--ly': null, '--label-max': null });
      return;
    }
    const rad = (slot.angle * Math.PI) / 180;
    // The avatar sits on the card's outer end and the label hangs just below it, over the seat's own card, so
    // a label never lands on a neighbouring seat.
    const reach = size.height / 2 - size.avatar / 2 - size.inset;
    const ax = -reach * Math.sin(rad);
    const ay = reach * Math.cos(rad);
    const offset = size.avatar / 2 + LABEL_GAP_PX;
    const left = slot.side === 'left';
    const anchor = left
      ? Math.max(STAGE_PAD_PX / 2 - slot.x, ax - size.avatar / 2)
      : Math.min(stage.width - STAGE_PAD_PX / 2 - slot.x, ax + size.avatar / 2);
    setStyle(node, {
      '--x': `${slot.x}px`,
      '--y': `${slot.y}px`,
      '--a': `${slot.angle}deg`,
      '--lx': `${anchor + size.width / 2}px`,
      '--ly': `${ay + offset + size.height / 2}px`,
      '--label-max': `${LABEL_MAX_PX}px`,
    });
    node.dataset.side = slot.side;
    node.labelAt = slot.x + anchor;
    // A seat's first place is taken at once; only later moves glide.
    if (!node.placed) {
      node.placed = true;
      node.classList.add('is-placing');
      requestAnimationFrame(() => requestAnimationFrame(() => node.classList.remove('is-placing')));
    }
  }

  function place() {
    if (!stage) {
      return { depth: 0, row: false };
    }
    plan = planSeats(stage, seats.length);
    const ring = plan.seats.length > 0 && stage.width >= ROW_BELOW_PX;
    if (ring) {
      plan = centreInColumns(plan, stage);
    }
    el.classList.toggle('is-row', !ring);
    el.classList.toggle('is-ring', ring);
    hidden = ring ? seats.slice(plan.seats.length) : [];
    const size = ring ? plan.size : null;
    if (size) {
      el.dataset.size = size.name;
      setStyle(el, {
        '--seat-w': `${size.width}px`,
        '--seat-h': `${size.height}px`,
        '--seat-avatar': `${size.avatar}px`,
        '--seat-inset': `${size.inset}px`,
        '--seat-scale': size.card,
      });
    } else {
      delete el.dataset.size;
      setStyle(el, { '--seat-w': null, '--seat-h': null, '--seat-avatar': null, '--seat-inset': null, '--seat-scale': null });
    }
    const nodes = [...el.querySelectorAll(':scope > .nt-seat')];
    nodes.forEach((node, i) => {
      node.hidden = ring && i >= plan.seats.length;
      position(node, ring ? plan.seats[i] : null, size);
    });
    let reach = ring ? plan.depth : 0;
    print.hidden = !ring;
    if (ring) {
      const drawn = bettingLines(plan, stage.width);
      reach = Math.max(reach, drawn.reach);
      setStyle(print, { '--print-w': `${printMetrics(size).stroke}px` });
      [drawn.left, drawn.right].forEach((d, i) => {
        // Where CSS `d` is supported it is what transitions; the attribute serves everyone else.
        lines[i].toggleAttribute('hidden', d === null);
        if (d !== null) {
          lines[i].setAttribute('d', d);
          lines[i].style.d = `path("${d}")`;
        }
      });
    }
    if (ring && plan.chip) {
      el.append(more);
      setStyle(more, { '--x': `${plan.chip.x}px`, '--y': `${plan.chip.y}px` });
      renderMore();
    } else {
      more.remove();
      popover.close();
    }
    return { depth: reach, row: !ring };
  }

  return {
    el,
    print,
    update(next, { revealed = false, animate = true, live = false } = {}) {
      const previousCount = seats.length;
      seats = next.map((seat) => ({ ...seat, revealed }));
      const motion = { slide: animate, live: live && !reducedMotion() };
      more.remove();
      reconcile(el, seats, (s) => s.pid, (seat) => {
        const node = createNode(seat);
        patch(node, seat, { slide: false, live: motion.live });
        return node;
      }, (node, seat) => patch(node, seat, motion));
      if (motion.live) {
        clearTimeout(flipTimer);
        flipTimer = setTimeout(() => {
          for (const card of el.querySelectorAll('.nt-flip')) {
            card.classList.add('is-flipped');
          }
        }, FLIP_DELAY_MS);
      }
      if (seats.length !== previousCount || hidden.length > 0) {
        return place();
      }
      if (plan?.chip) {
        el.append(more);
      }
      return null;
    },
    layout(next) {
      stage = next;
      return place();
    },
    /** How far the ring's name labels reach in from either side edge, as drawn (a revealed vote widens them). */
    labelReach() {
      if (!stage || !el.classList.contains('is-ring')) {
        return 0;
      }
      let reach = 0;
      for (const node of el.querySelectorAll(':scope > .nt-seat:not([hidden])')) {
        if (node.labelAt != null) {
          const width = node.parts.label.offsetWidth;
          reach = Math.max(reach, node.dataset.side === 'left' ? node.labelAt + width : stage.width - node.labelAt + width);
        }
      }
      return reach;
    },
    get depth() {
      return plan && el.classList.contains('is-ring') ? plan.depth : 0;
    },
    destroy() {
      clearTimeout(flipTimer);
      clearTimeout(announceTimer);
      popover.destroy();
      print.remove();
      el.remove();
    },
  };
}
