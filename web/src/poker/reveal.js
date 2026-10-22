// Card faces from the sprite and the revealed results (§g.8). The seats flip their own cards 300 ms after a
// live reveal (seats.js).

import { h } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { revealGroups, voteLabel } from './model.js';

export const FLIP_DELAY_MS = 300;
const NAMES_SHOWN = 4;

export function reducedMotion() {
  return globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
}

/** One sprite frame: a deck value, 'abstain' or 'back'. */
export function cardEl(frame, className) {
  return h('span', { class: ['nt-card', className], 'data-card': String(frame), 'aria-hidden': 'true' });
}

/** A "between" card: the low value in the top-left triangle, the high value in the bottom-right one. */
export function betweenEl(low, high, className) {
  return h('span', { class: ['nt-card', 'nt-between', className], 'aria-hidden': 'true' },
    cardEl(low, 'nt-between-low'),
    cardEl(high, 'nt-between-high'));
}

export function faceEl(points, high, abstained) {
  if (abstained || points == null) {
    return cardEl('abstain');
  }
  return high == null ? cardEl(points) : betweenEl(points, high);
}

function groupLabel(group) {
  const count = group.voters.length;
  return group.abstain ? `${count} x Abstain` : `${count} x ${voteLabel(group.points, group.high).replace(/ pts$/, '')} pts`;
}

export function namesText(voters) {
  const names = voters.map((v) => v.name);
  return names.length > NAMES_SHOWN
    ? `${names.slice(0, NAMES_SHOWN).join(', ')} +${names.length - NAMES_SHOWN}`
    : names.join(', ');
}

/**
 * The revealed votes grouped by value ("2 x 5 pts" and who), shown next to the winner while the seats
 * hold the flipped cards themselves.
 */
export function createResults() {
  const el = h('ul', { class: 'nt-stacks', 'aria-label': 'Revealed votes' });

  function create() {
    return h('li', { class: 'nt-stack' },
      h('span', { class: 'nt-stack-pill' }),
      h('span', { class: 'nt-stack-names' }));
  }

  function update(node, group) {
    node.querySelector('.nt-stack-pill').textContent = groupLabel(group);
    const names = node.querySelector('.nt-stack-names');
    names.textContent = namesText(group.voters);
    node.title = group.voters.map((v) => v.name).join(', ');
    node.setAttribute('aria-label', `${groupLabel(group)}: ${group.voters.map((v) => v.name).join(', ')}`);
  }

  return {
    el,
    update(state, roundId) {
      reconcile(el, revealGroups(state, roundId), (g) => g.key, (group) => {
        const node = create();
        update(node, group);
        return node;
      }, update);
    },
    clear() {
      reconcile(el, [], (g) => g.key, create, null);
    },
  };
}
