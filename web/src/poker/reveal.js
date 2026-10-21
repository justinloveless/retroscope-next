// Card faces from the sprite, the revealed stacks and the reveal flip (§g.8): 300 ms after a live reveal
// every card turns over in 0.6 s; between cards crossfade instead, because clip-path and 3D do not mix.

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

function namesText(voters) {
  const names = voters.map((v) => v.name);
  return names.length > NAMES_SHOWN
    ? `${names.slice(0, NAMES_SHOWN).join(', ')} +${names.length - NAMES_SHOWN}`
    : names.join(', ');
}

function stackCard(group, flipped) {
  const face = group.abstain ? cardEl('abstain') : faceEl(group.points, group.high, false);
  const between = !group.abstain && group.high != null;
  return h('span', { class: ['nt-flip', between && 'nt-flip-fade', flipped && 'is-flipped'] },
    h('span', { class: 'nt-flip-inner' },
      cardEl('back', 'nt-flip-back'),
      h('span', { class: 'nt-flip-front' }, face)));
}

/**
 * The revealed table. update(state, roundId, {live}) renders the stacks; `live` is true when the reveal
 * just happened in front of this viewer, which plays the flip.
 */
export function createReveal() {
  const el = h('div', { class: 'nt-stacks', role: 'list', 'aria-label': 'Revealed votes' });
  let flipTimer = 0;

  function create(group) {
    const stack = h('div', { class: 'nt-stack-cards' });
    const node = h('div', { class: 'nt-stack', role: 'listitem' },
      stack,
      h('span', { class: 'nt-stack-pill' }),
      h('span', { class: 'nt-stack-names' }));
    node.stack = stack;
    return node;
  }

  function update(node, group, flipped) {
    node.querySelector('.nt-stack-pill').textContent = groupLabel(group);
    const names = node.querySelector('.nt-stack-names');
    names.textContent = namesText(group.voters);
    names.title = group.voters.map((v) => v.name).join(', ');
    node.setAttribute('aria-label', `${groupLabel(group)}: ${group.voters.map((v) => v.name).join(', ')}`);
    reconcile(node.stack, group.voters, (v) => v.pid, () => stackCard(group, flipped), null);
  }

  return {
    el,
    update(state, roundId, { live = false } = {}) {
      const animate = live && !reducedMotion();
      const groups = revealGroups(state, roundId);
      reconcile(el, groups, (g) => g.key, (group) => {
        const node = create(group);
        update(node, group, !animate);
        return node;
      }, (node, group) => update(node, group, !animate));
      if (animate) {
        clearTimeout(flipTimer);
        flipTimer = setTimeout(() => {
          for (const card of el.querySelectorAll('.nt-flip')) {
            card.classList.add('is-flipped');
          }
        }, FLIP_DELAY_MS);
      }
    },
    clear() {
      clearTimeout(flipTimer);
      reconcile(el, [], (g) => g.key, create, null);
    },
    destroy() {
      clearTimeout(flipTimer);
      el.remove();
    },
  };
}
