// The players around the table while a round is voting (§g.8): a dashed "?" slot until a player locks
// in, then the card back sliding in; absent players at half opacity; a name pill with a guest badge.
// Each seat is patched only when its own data changed, so a vote touches one seat.

import { h } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { cardEl } from './reveal.js';

const STATUS_TEXT = { empty: 'still pointing', selected: 'choosing', locked: 'locked in' };

function statusOf(seat) {
  return seat.vote?.status ?? (seat.vote ? 'locked' : 'empty');
}

function same(a, b) {
  return a.name === b.name && a.kind === b.kind && a.present === b.present && a.vote === b.vote && a.me === b.me;
}

function create(seat) {
  const slot = h('span', { class: 'nt-seat-slot' });
  const node = h('li', { class: 'nt-seat' },
    slot,
    h('span', { class: 'nt-seat-label' },
      h('span', { class: 'nt-seat-name' }),
      h('span', { class: 'nt-badge-guest', text: 'guest' })));
  node.cardSlot = slot;
  patchSeat(node, seat, false);
  return node;
}

function patchSeat(node, seat, animate) {
  const previous = node.seat;
  if (previous && same(previous, seat)) {
    return;
  }
  node.seat = seat;
  const status = statusOf(seat);
  const wasLocked = previous && statusOf(previous) === 'locked';
  node.dataset.pid = seat.pid;
  node.dataset.status = status;
  node.toggleAttribute('data-absent', !seat.present);
  node.toggleAttribute('data-me', seat.me);
  node.classList.toggle('is-guest', seat.kind === 'guest');
  const name = node.querySelector('.nt-seat-name');
  name.textContent = seat.me ? `${seat.name} (you)` : seat.name;
  name.title = seat.name;
  node.setAttribute('aria-label', `${seat.name}${seat.kind === 'guest' ? ' (guest)' : ''}: ${STATUS_TEXT[status]}${seat.present ? '' : ', away'}`);
  if (status === 'locked') {
    if (!wasLocked) {
      node.cardSlot.replaceChildren(cardEl('back', animate ? 'nt-slide-in' : null));
    }
  } else if (!previous || wasLocked || !node.cardSlot.querySelector('.nt-slot-empty')) {
    node.cardSlot.replaceChildren(h('span', { class: 'nt-slot-empty', text: '?' }));
  }
}

export function createSeats() {
  const el = h('ul', { class: 'nt-seats', 'aria-label': 'Players' });
  return {
    el,
    /** `animate` is false when the viewer switched rounds, so seats that are already locked do not slide in. */
    update(seats, { animate = true } = {}) {
      reconcile(el, seats, (s) => s.pid, create, (node, seat) => patchSeat(node, seat, animate), { flip: animate });
    },
    destroy() {
      el.remove();
    },
  };
}
