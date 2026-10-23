// The stage (docs/followups/UX1.md): the round header with the debounced ticket-key editor on top, the
// viewed round's issue at the centre (the stage's scroll container), the other players seated around it,
// and a dock floating at the bottom with the hand and its bar (Lock In, Abstain, Reveal) or a revealed
// round's actions. The issue keeps clear of both: its side padding is the seat ring's depth and its bottom
// padding the dock's height plus 24 px, both measured live. A revealed round's results overlay
// (results.js) holds the winner, the piles and Replay; while it is open, End/Continue/Next Round and the
// Jira submit chips move into it from the dock. Minimized, the dock keeps only those and Show results,
// which sits beside the Submit button when there is one (docs/decisions/UX7.md).

import { h, replaceContent, setStyle } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { isPresent, roundById, roundPhase, seatsOf } from './model.js';
import { createResults } from './reveal.js';
import { createResultsOverlay } from './results.js';
import { createSeats } from './seats.js';

export const TICKET_DEBOUNCE_MS = 500;
// A seat's avatar and name label stand out past its card's top or bottom edge (seats.js, UX6.md), so the seats
// keep this much more room from the round header (whose band is short, so the top seats' labels also need the
// room the band leaves) and from the dock.
const LABEL_ROOM_TOP_PX = 52;
const LABEL_ROOM_BOTTOM_PX = 28;
// Below this much room between the ring's labels, the results overlay deals smaller piles.
const RESULTS_NARROW_PX = 480;
// A feature that adds to the results marks the row Show results joins while they are minimized.
const INLINE_ROW = '[data-results-inline]';

function roundLabel(round) {
  return round ? `Round ${round.number}` : '';
}

/** The "Start New Round" dialog: an optional ticket key, Enter confirms. Resolves the key, '' or null. */
export function askNewRound({ title = 'Start New Round', confirmLabel = 'Start round' } = {}) {
  return new Promise((resolve) => {
    let result = null;
    const input = h('input', {
      class: 'input',
      name: 'ticketKey',
      placeholder: 'e.g. PROJ-123',
      autocomplete: 'off',
      'aria-label': 'Ticket key (optional)',
    });
    const form = h('form', {
      class: 'nt-new-round',
      on: {
        submit(event) {
          event.preventDefault();
          result = input.value.trim();
          dialog.close('ok');
        },
      },
    },
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Ticket key (optional)' }), input));
    const dialog = createDialog({
      title,
      content: form,
      actions: [
        createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() }).el,
        createButton({ label: confirmLabel, variant: 'purple', press: true, onClick: () => form.requestSubmit() }).el,
      ],
      onClose() {
        dialog.destroy();
        resolve(result);
      },
    });
    dialog.open();
    input.focus();
  });
}

/** Everyone at the table but the viewer: the players, then observers (dimmed, never dealt a card). */
export function tableSeats(state, roundId) {
  const seats = seatsOf(state, roundId).filter((s) => !s.me).map((s) => ({ ...s, observer: false }));
  const seen = new Set(seats.map((s) => s.pid));
  const observers = new Set(state.observers);
  for (const p of [...(state.teamId ? state.roster : []), ...state.presence]) {
    if (observers.has(p.pid) && p.pid !== state.me && !seen.has(p.pid)) {
      seen.add(p.pid);
      seats.push({ pid: p.pid, name: p.name ?? '', kind: p.kind ?? 'user', avatar: p.avatar ?? null, present: isPresent(state, p.pid), vote: null, me: false, observer: true });
    }
  }
  return seats;
}

/**
 * actions: reveal(round), replay(round), end(round), nextActive(round) (end and go to the next active
 * round), continueRound(round) (dialog, then the atomic poker.round.next), setTicket(round, key).
 * `issue` is where a feature puts the issue's details (the fallback shows while it is empty), `results`
 * where it adds to a revealed round's results, and `field` the drop target over the issue.
 * attachHand(hand, note) docks the hand (hand.js): its fan floats in the dock, its buttons join the bar.
 */
export function createTable(actions) {
  const kicker = h('span', { class: 'nt-ticket-kicker' });
  const input = h('input', {
    class: 'nt-ticket-input',
    'aria-label': 'Ticket key',
    autocomplete: 'off',
    spellcheck: 'false',
    maxlength: '64',
  });
  const keyText = h('span', { class: 'nt-ticket-key' });
  const header = h('header', { class: 'nt-ticket' }, kicker, input, keyText);
  const issue = h('div', { class: 'nt-issue-slot' });
  const fallback = h('div', { class: 'nt-issue-fallback' });
  const scroller = h('div', { class: 'nt-issue', tabindex: '-1' }, h('div', { class: 'nt-issue-inner' }, issue, fallback));
  const overlay = createResultsOverlay({
    onToggle() {
      placeResults();
      frame ||= requestAnimationFrame(measure);
    },
  });
  const field = h('div', { class: 'nt-field' }, scroller, overlay.el);
  const seats = createSeats();
  const results = createResults();
  const bar = h('div', { class: 'nt-actions' });
  const extra = h('div', { class: 'nt-dock-results' });
  const handSlot = h('div', { class: 'nt-dock-hand' });
  const actionSlot = h('div', { class: 'nt-dock-tools' });
  const dockBar = h('div', { class: 'nt-dock-bar' }, actionSlot, bar);
  const dock = h('div', { class: 'nt-dock' }, extra, handSlot, dockBar);
  const el = h('section', { class: 'nt-table', 'aria-label': 'Table' }, seats.print, header, seats.el, field, dock);

  let round = null;
  let roundId = null;
  let phase = 'missing';
  let barKey = '';
  let fallbackKey = '';
  let debounce = 0;
  let sentKey = null;
  let frame = 0;
  let revealedParts = null;
  let restingDock = 0;

  function applyLayout(result) {
    if (result) {
      setStyle(el, { '--nt-ring': `${Math.ceil(result.depth)}px` });
      el.classList.toggle('is-row', result.row);
    }
  }

  function measure() {
    frame = 0;
    if (!el.isConnected) {
      return;
    }
    setStyle(el, { '--nt-dock-h': `${Math.ceil(dock.offsetHeight)}px` });
    // The seats keep the room the dock took before the reveal, so they do not grow when the hand goes away
    // and shrink back for the next round.
    if (phase !== 'revealed') {
      restingDock = dock.offsetHeight;
    }
    applyLayout(seats.layout({
      width: el.clientWidth,
      height: el.clientHeight,
      top: header.offsetTop + header.offsetHeight + LABEL_ROOM_TOP_PX,
      bottom: Math.max(dock.offsetHeight, restingDock) + LABEL_ROOM_BOTTOM_PX,
    }));
    const labels = Math.ceil(seats.labelReach());
    setStyle(el, { '--nt-labels': `${labels}px` });
    el.classList.toggle('is-results-narrow', labels > 0 && el.clientWidth - 2 * Math.max(seats.depth, labels) < RESULTS_NARROW_PX);
  }

  const observer = typeof ResizeObserver === 'function'
    ? new ResizeObserver(() => {
      frame ||= requestAnimationFrame(measure);
    })
    : null;
  observer?.observe(el);
  observer?.observe(header);
  observer?.observe(dock);

  // Features fill and show their part of the results after the table has placed them (and again when a
  // team's Jira settings arrive), so Show results follows the inline row as it appears and goes.
  const inlineWatch = typeof MutationObserver === 'function'
    ? new MutationObserver(() => placeShowResults())
    : null;
  inlineWatch?.observe(extra, { subtree: true, childList: true, attributes: true, attributeFilter: ['hidden'] });

  function flushTicket() {
    clearTimeout(debounce);
    debounce = 0;
    if (!round || roundPhase(round) !== 'voting') {
      return;
    }
    const value = input.value.trim();
    const next = value === '' ? null : value;
    if (next === (round.ticketKey ?? null) || next === sentKey) {
      return;
    }
    sentKey = next;
    actions.setTicket(round, next);
  }

  input.addEventListener('input', () => {
    clearTimeout(debounce);
    debounce = setTimeout(flushTicket, TICKET_DEBOUNCE_MS);
  });
  input.addEventListener('blur', () => {
    flushTicket();
    sentKey = null;
    input.value = round?.ticketKey ?? '';
  });
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      input.blur();
    } else if (event.key === 'Escape') {
      clearTimeout(debounce);
      input.value = round?.ticketKey ?? '';
      input.blur();
    }
  });

  function button(label, variant, onClick, extra = {}) {
    return createButton({ label, variant, press: true, onClick, ...extra }).el;
  }

  function renderBar(state) {
    const others = state.rounds.some((r) => r.id !== round?.id && r.active && !r.pending && r.state === 'voting');
    const key = JSON.stringify([round?.id, phase, round?.active, round?.winner, others]);
    if (key === barKey) {
      return;
    }
    barKey = key;
    bar.dataset.phase = phase;
    revealedParts = null;
    if (phase === 'voting') {
      replaceContent(bar, button('Reveal Cards', 'blue', () => actions.reveal(round), { className: 'nt-reveal' }));
    } else if (phase === 'revealed') {
      const winner = h('p', { class: 'nt-winner', role: 'status' },
        'Winning Points: ', h('strong', { text: round.winner ? `${round.winner} pts` : '–' }));
      const replay = createButton({ label: 'Replay round', icon: 'rotate-ccw', iconOnly: true, variant: 'default', press: true, size: 'icon', onClick: () => actions.replay(round) }).el;
      const next = !round.active
        ? []
        : others
          ? [button('Next Round', 'purple', () => actions.nextActive(round))]
          : [button('End', 'default', () => actions.end(round)), button('Continue', 'purple', () => actions.continueRound(round))];
      revealedParts = { winner, replay, next, roundActions: h('div', { class: 'nt-round-actions' }) };
    } else if (phase === 'pending') {
      replaceContent(bar, h('p', { class: 'nt-note', text: 'Preview: this round has not been added yet.' }));
    } else if (phase === 'inactive') {
      replaceContent(bar, h('p', { class: 'nt-note', text: 'This round ended without a reveal.' }));
    } else {
      replaceContent(bar);
    }
  }

  function placeResults() {
    const open = revealedParts !== null && !overlay.minimized;
    el.classList.toggle('is-results-open', open);
    if (!revealedParts) {
      overlay.hide();
      overlay.showResults.remove();
      dock.insertBefore(extra, handSlot);
      return;
    }
    const { winner, replay, next, roundActions } = revealedParts;
    if (open) {
      overlay.showResults.remove();
      roundActions.replaceChildren(replay, ...next);
      overlay.fill(winner, [results.el, roundActions, extra]);
      overlay.show();
      replaceContent(bar);
    } else {
      // The winner, the vote pills and Replay stay in the hidden overlay; the dock keeps the round going.
      overlay.hide();
      overlay.fill(winner, [results.el, replay]);
      roundActions.replaceChildren(...next);
      replaceContent(bar, roundActions);
      dock.insertBefore(extra, handSlot);
      placeShowResults();
    }
  }

  function placeShowResults() {
    if (!revealedParts || !overlay.minimized) {
      return;
    }
    const row = extra.querySelector(INLINE_ROW);
    const target = row && !row.closest('[hidden]') ? row : revealedParts.roundActions;
    if (overlay.showResults.parentNode === target) {
      return;
    }
    if (target === row) {
      target.append(overlay.showResults);
    } else {
      target.prepend(overlay.showResults);
    }
  }

  function renderHeader() {
    const editable = phase === 'voting';
    kicker.textContent = editable || (phase === 'revealed' && round.active) ? 'Now Pointing:' : 'Viewing:';
    input.hidden = !editable;
    keyText.hidden = editable;
    input.placeholder = roundLabel(round);
    if (document.activeElement !== input) {
      input.value = round?.ticketKey ?? '';
    }
    keyText.textContent = round?.ticketKey ?? roundLabel(round);
  }

  // Shown while no feature fills the issue slot: quick sessions and teams without Jira.
  function renderFallback(meta) {
    const ticket = round?.ticketKey ?? null;
    const summary = ticket ? meta?.[ticket]?.summary ?? null : null;
    const key = JSON.stringify([ticket, summary, phase === 'voting']);
    if (key === fallbackKey) {
      return;
    }
    fallbackKey = key;
    if (ticket) {
      replaceContent(fallback,
        h('p', { class: 'nt-issue-kicker', text: roundLabel(round) }),
        h('h2', { class: 'nt-issue-text', text: ticket }),
        summary ? h('p', { class: 'nt-issue-summary', text: summary }) : null);
    } else {
      replaceContent(fallback, h('div', { class: 'nt-unlinked' },
        h('p', { class: 'nt-unlinked-title', text: 'No issue linked' }),
        h('p', { class: 'nt-unlinked-text', text: phase === 'voting' ? 'Type a ticket key above to name this round.' : 'This round has no ticket.' })));
    }
  }

  return {
    el,
    field,
    issue,
    scroller,
    results: extra,
    attachHand(hand, note) {
      handSlot.replaceChildren(hand.el);
      actionSlot.replaceChildren(hand.actions, note);
    },
    /** Puts the keyboard in the ticket key field (the "Link a Jira issue" action). */
    editTicket() {
      if (!input.hidden) {
        input.focus();
        input.select();
      }
    },
    update(state, id, { meta } = {}) {
      const switched = id !== roundId;
      const previous = switched ? null : round;
      if (switched && debounce) {
        flushTicket();
      }
      roundId = id;
      round = roundById(state, id);
      phase = roundPhase(round);
      el.dataset.phase = phase;
      const revealed = phase === 'revealed';
      const live = revealed && previous !== null && roundPhase(previous) === 'voting';
      overlay.round(state, id, { revealed, live });
      renderHeader();
      renderBar(state);
      placeResults();
      renderFallback(meta);
      extra.hidden = !revealed;
      if (revealed) {
        results.update(state, id);
      } else {
        results.clear();
      }
      if (live) {
        overlay.claimFocus();
      }
      applyLayout(seats.update(tableSeats(state, id), { revealed, animate: !switched, live }));
      if (switched) {
        scroller.scrollTop = 0;
      }
      frame ||= requestAnimationFrame(measure);
    },
    destroy() {
      clearTimeout(debounce);
      cancelAnimationFrame(frame);
      observer?.disconnect();
      inlineWatch?.disconnect();
      seats.destroy();
      el.remove();
    },
  };
}
