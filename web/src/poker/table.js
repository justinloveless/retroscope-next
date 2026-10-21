// The playing field (§g.8): the ticket header with the debounced key editor, Reveal or the results bar
// with Replay and End/Continue/Next Round, and the table itself: seats while voting, stacks once revealed.

import { h, replaceContent, setStyle } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { cardScale, roundById, roundPhase, seatsOf } from './model.js';
import { createReveal } from './reveal.js';
import { createSeats } from './seats.js';

export const TICKET_DEBOUNCE_MS = 500;
const MOBILE = '(max-width: 767px)';

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

/**
 * actions: reveal(round), replay(round), end(round), nextActive(round) (end and go to the next active
 * round), continueRound(round) (dialog, then the atomic poker.round.next), setTicket(round, key).
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
  const summary = h('p', { class: 'nt-ticket-summary' });
  const header = h('header', { class: 'nt-ticket' }, kicker, input, keyText, summary);
  const bar = h('div', { class: 'nt-actions' });
  const seats = createSeats();
  const reveal = createReveal();
  const field = h('div', { class: 'nt-field' }, seats.el, reveal.el);
  const el = h('section', { class: 'nt-table', 'aria-label': 'Table' }, header, bar, field);

  let round = null;
  let roundId = null;
  let phase = 'missing';
  let barKey = '';
  let debounce = 0;
  let sentKey = null;

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
    if (phase === 'voting') {
      replaceContent(bar, button('Reveal Cards', 'blue', () => actions.reveal(round), { big: true, className: 'nt-reveal' }));
    } else if (phase === 'revealed') {
      const winner = h('p', { class: 'nt-winner', role: 'status' },
        'Winning Points: ', h('strong', { text: round.winner ? `${round.winner} pts` : '–' }));
      const replay = createButton({ label: 'Replay round', icon: 'rotate-ccw', iconOnly: true, variant: 'default', press: true, size: 'icon', onClick: () => actions.replay(round) }).el;
      const next = !round.active
        ? []
        : others
          ? [button('Next Round', 'purple', () => actions.nextActive(round))]
          : [button('End', 'default', () => actions.end(round)), button('Continue', 'purple', () => actions.continueRound(round))];
      replaceContent(bar, winner, replay, ...next);
    } else if (phase === 'pending') {
      replaceContent(bar, h('p', { class: 'nt-note', text: 'Preview: this round has not been added yet.' }));
    } else if (phase === 'inactive') {
      replaceContent(bar, h('p', { class: 'nt-note', text: 'This round ended without a reveal.' }));
    } else {
      replaceContent(bar);
    }
  }

  function renderHeader(meta) {
    const editable = phase === 'voting';
    kicker.textContent = editable || (phase === 'revealed' && round.active) ? 'Now Pointing:' : 'Viewing:';
    input.hidden = !editable;
    keyText.hidden = editable;
    input.placeholder = roundLabel(round);
    if (document.activeElement !== input) {
      input.value = round?.ticketKey ?? '';
    }
    keyText.textContent = round?.ticketKey ?? roundLabel(round);
    const title = round?.ticketKey ? meta?.[round.ticketKey]?.summary : null;
    summary.textContent = title ?? '';
    summary.hidden = !title;
  }

  return {
    el,
    field,
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
      renderHeader(meta);
      renderBar(state);

      const shown = seatsOf(state, id);
      const mobile = globalThis.matchMedia?.(MOBILE).matches ?? false;
      setStyle(field, { '--scale': cardScale(phase === 'revealed' ? Math.max(1, shown.length) : shown.length, mobile) });
      const revealed = phase === 'revealed';
      seats.el.hidden = revealed;
      reveal.el.hidden = !revealed;
      if (revealed) {
        const live = previous !== null && roundPhase(previous) === 'voting';
        reveal.update(state, id, { live });
      } else {
        reveal.clear();
        seats.update(shown, { animate: !switched });
      }
    },
    destroy() {
      clearTimeout(debounce);
      reveal.destroy();
      seats.destroy();
      el.remove();
    },
  };
}
