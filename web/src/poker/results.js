// The results overlay (docs/followups/UX2.md, item 5): a revealed round's votes as piles of face-up cards over
// the issue, one pile per point value, counted the way the scoring counts them (a between vote lands on both
// of its ends; abstains get their own pile). It is a translucent, non-modal dialog that opens for everyone on
// a reveal and when a revealed round is opened. Minimize (or Esc inside it) folds it back into the dock's
// compact results bar, and this tab remembers that per round until the round is revealed again.

import { h } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { createButton } from '../ui/button.js';
import { faceEl, namesText } from './reveal.js';

const MINIMIZED_KEY = 'nt-results-minimized';
const PILE_STEP_PX = 22;
const PILE_RISE_PX = 110;
const TEXT_FIELD = 'input:not([type="button"], [type="checkbox"], [type="radio"], [type="submit"]), textarea, select, [contenteditable]:not([contenteditable="false"])';

/**
 * The revealed votes as one pile per point value, lowest first, with abstains last. A between vote sits in the
 * piles of both its ends; each voter carries their whole vote.
 */
export function resultPiles(state, roundId) {
  const people = new Map([...state.roster, ...state.presence].map((p) => [p.pid, p]));
  const piles = new Map();
  const add = (key, points, vote) => {
    let pile = piles.get(key);
    if (!pile) {
      pile = { key, points, abstain: points == null, voters: [] };
      piles.set(key, pile);
    }
    const person = people.get(vote.pid);
    pile.voters.push({
      pid: vote.pid,
      name: person?.name ?? 'Unknown',
      points: vote.points ?? null,
      high: vote.high ?? null,
      abstained: Boolean(vote.abstained) || vote.points == null,
    });
  };
  for (const vote of Object.values(state.votes[roundId] ?? {})) {
    if (vote.abstained || vote.points == null) {
      add('abstain', null, vote);
    } else {
      add(String(vote.points), vote.points, vote);
      if (vote.high != null) {
        add(String(vote.high), vote.high, vote);
      }
    }
  }
  return [...piles.values()].sort((a, b) => (a.abstain - b.abstain) || (a.points - b.points));
}

// The rounds whose results this tab minimized; session storage keeps them across a reload when allowed.
function createMemory() {
  let ids = [];
  try {
    ids = JSON.parse(sessionStorage.getItem(MINIMIZED_KEY) ?? '[]');
  } catch {
    // Without session storage the memory lasts as long as the page.
  }
  const set = new Set(Array.isArray(ids) ? ids : []);
  return {
    has: (id) => set.has(id),
    set(id, minimized) {
      if (minimized === set.has(id)) {
        return;
      }
      if (minimized) {
        set.add(id);
      } else {
        set.delete(id);
      }
      try {
        sessionStorage.setItem(MINIMIZED_KEY, JSON.stringify([...set]));
      } catch {
        // As above.
      }
    },
  };
}

const memory = createMemory();

function pileValue(pile) {
  return pile.abstain ? 'Abstain' : `${pile.points} pts`;
}

function createPiles() {
  const el = h('ul', { class: 'nt-piles', 'aria-label': 'Votes by points' });

  function create() {
    const cards = h('span', { class: 'nt-pile-cards', 'aria-hidden': 'true' });
    const value = h('span', { class: 'nt-pile-value', 'aria-hidden': 'true' });
    const names = h('span', { class: 'nt-pile-names', 'aria-hidden': 'true' });
    const node = h('li', { class: 'nt-pile' }, cards, value, names);
    node.parts = { cards, value, names };
    return node;
  }

  function update(node, pile, winner) {
    const { cards, value, names } = node.parts;
    const won = !pile.abstain && pile.points === winner;
    const count = pile.voters.length;
    const who = pile.voters.map((v) => v.name).join(', ');
    node.dataset.points = pile.abstain ? 'abstain' : String(pile.points);
    node.classList.toggle('is-winner', won);
    node.title = who;
    node.setAttribute('aria-label', `${pileValue(pile)}, ${count} ${count === 1 ? 'vote' : 'votes'}${won ? ', winner' : ''}: ${who}`);
    node.style.setProperty('--pile-step', `${count > 1 ? Math.min(PILE_STEP_PX, PILE_RISE_PX / (count - 1)) : 0}px`);
    value.textContent = `${count} x ${pileValue(pile)}`;
    names.textContent = namesText(pile.voters);
    reconcile(cards, pile.voters, (v) => `${v.pid}:${v.points}:${v.high}:${v.abstained}`,
      (v) => h('span', { class: 'nt-pile-card' }, faceEl(v.points, v.high, v.abstained)), null);
    [...cards.children].forEach((card, k) => card.style.setProperty('--k', k));
  }

  return {
    el,
    update(state, roundId, winner) {
      reconcile(el, resultPiles(state, roundId), (p) => p.key, (pile) => {
        const node = create();
        update(node, pile, winner);
        return node;
      }, (node, pile) => update(node, pile, winner));
    },
  };
}

/**
 * The overlay. round(state, id, {revealed, live}) follows the viewed round: it fills the piles, reopens the
 * overlay unless this tab minimized it for that round (a live reveal forgets that), and on a live reveal takes
 * focus unless the viewer is typing. fill(winner, nodes) puts the winner in its head and `nodes` (Replay, the
 * round actions, the submit chips) below the piles; show() and hide() open and close it, and the table moves
 * what the dock needs back there while it is minimized. `showResults` is the dock's button that reopens it.
 * onToggle() runs after Minimize or Show results.
 */
export function createResultsOverlay({ onToggle }) {
  const piles = createPiles();
  const winnerSlot = h('div', { class: 'nt-results-winner' });
  const body = h('div', { class: 'nt-results-actions' });
  const minimize = createButton({ label: 'Minimize', icon: 'chevron-down', variant: 'secondary', size: 'sm', className: 'nt-results-minimize', onClick: () => setMinimized(true) }).el;
  const showResults = createButton({ label: 'Show results', icon: 'maximize-2', variant: 'default', press: true, className: 'nt-show-results', onClick: () => setMinimized(false) }).el;
  const el = h('div', { class: 'nt-results', role: 'dialog', 'aria-modal': 'false', 'aria-label': 'Results', tabindex: '-1', hidden: true },
    h('div', { class: 'nt-results-head' }, winnerSlot, minimize),
    piles.el,
    body);
  let roundId = null;
  let minimized = false;

  // Only the viewer's own Minimize, Esc or Show results get here, so the focus follows the results.
  function setMinimized(next) {
    minimized = next;
    memory.set(roundId, next);
    onToggle();
    (next ? showResults : el).focus({ preventScroll: true });
  }

  el.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !event.defaultPrevented) {
      event.preventDefault();
      setMinimized(true);
    }
  });

  return {
    el,
    showResults,
    get minimized() {
      return minimized;
    },
    round(state, id, { revealed, live }) {
      roundId = id;
      if (live) {
        memory.set(id, false);
      }
      minimized = revealed && memory.has(id);
      if (revealed) {
        piles.update(state, id, state.rounds.find((r) => r.id === id)?.winner ?? null);
      } else {
        reconcile(piles.el, [], (p) => p.key, null, null);
      }
    },
    fill(winner, nodes) {
      winnerSlot.replaceChildren(winner);
      body.replaceChildren(...nodes);
    },
    show() {
      el.hidden = false;
    },
    hide() {
      el.hidden = true;
    },
    /** After a live reveal: take focus unless the viewer is typing somewhere. */
    claimFocus() {
      if (!el.hidden && !document.activeElement?.matches?.(TEXT_FIELD)) {
        el.focus({ preventScroll: true });
      }
    },
  };
}
