// "Your hand" (§g.8, docs/followups/UX1.md): the fanned deck peeks from the bottom of the stage, about a third
// of each card showing, and rises whole on hover, keyboard focus, a drag or a touch. The selected card
// stays raised a little more, with its value pill and, once locked, a "Locked" badge. Shift-click "between"
// votes merge with a 620 ms FLIP, drag-to-play goes through drag.js, the point descriptions are tooltips
// on the cards, and arrows move between cards. Lock In and Abstain sit in the dock's bar (`actions`).

import { rovingTabindex } from '../core/a11y.js';
import { h, setStyle } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { tooltip } from '../ui/tooltip.js';
import { DECK, voteLabel } from './model.js';
import { betweenEl, cardEl, reducedMotion } from './reveal.js';

export const MERGE_MS = 620;
export const TOUCH_RAISE_MS = 4000;
const MERGE_EASING = 'cubic-bezier(.22,1,.36,1)';
const HELP = 'Select a card (or press 1–7), then hold Shift and click an adjacent card to vote between those values. '
  + 'Or drag a card up onto the issue to lock it in; hold it over the gap between two cards for 2 s to play "between". '
  + 'L locks in, A abstains.';

/** What a Shift-click on `points` means given the current own vote: a between pair or a single card. */
export function shiftSelection(own, points) {
  if (own && !own.abstained && own.points != null && own.high == null) {
    const a = DECK.indexOf(own.points);
    const b = DECK.indexOf(points);
    if (Math.abs(a - b) === 1) {
      return { points: DECK[Math.min(a, b)], high: DECK[Math.max(a, b)] };
    }
  }
  return { points };
}

function fanItems(own) {
  const between = own && !own.abstained && own.high != null ? own : null;
  const items = [];
  for (const points of DECK) {
    if (between && points === between.high) {
      continue;
    }
    items.push(between && points === between.points ? { points, high: between.high } : { points, high: null });
  }
  return items;
}

function describe(item, descriptions) {
  const texts = [item.points, item.high].filter((p) => p != null && descriptions?.[p]).map((p) => `${p}: ${descriptions[p]}`);
  return texts.join(' · ');
}

/**
 * props: onVote({points, high}), onLock(locked), onAbstain(abstain), drag (drag.js), plus update(view)
 * with view = {own, locked, abstained, enabled, descriptions}. `el` is the fan, `actions` the Lock In and
 * Abstain buttons for the dock's bar.
 */
export function createHand({ onVote, onLock, onAbstain, drag }) {
  const fan = h('ul', { class: 'nt-fan', 'aria-label': 'Cards' });
  const el = h('section', { class: 'nt-hand', 'aria-label': 'Your hand' }, h('div', { class: 'nt-hand-tray' }, fan));
  const info = h('button', { type: 'button', class: 'nt-hand-help', 'aria-label': 'How to vote' }, icon('info'));
  const help = tooltip(info, HELP);
  const lock = createButton({ label: 'Lock In', variant: 'blue', press: true, className: 'nt-lock', onClick: () => onLock(!view.locked) });
  const abstain = createButton({ label: 'Abstain', variant: 'purple', press: true, className: 'nt-abstain', onClick: () => onAbstain(!view.abstained) });
  const actions = h('div', { class: 'nt-hand-actions' }, info, lock.el, abstain.el);
  const roving = rovingTabindex(fan, { selector: '.nt-hand-card' });

  let view = { own: null, locked: false, abstained: false, enabled: false, descriptions: {} };
  let shape = '';
  let tips = [];
  let touchTimer = 0;

  function raise(on) {
    clearTimeout(touchTimer);
    el.classList.toggle('is-raised', on);
    if (on) {
      touchTimer = setTimeout(() => el.classList.remove('is-raised'), TOUCH_RAISE_MS);
    }
  }

  // A touch has no hover: the first touch on the fan raises it, a touch elsewhere lowers it again.
  el.addEventListener('pointerdown', (event) => {
    if (event.pointerType !== 'mouse') {
      raise(true);
    }
  });
  const lower = (event) => {
    if (event.pointerType !== 'mouse' && !el.contains(event.target)) {
      raise(false);
    }
  };
  document.addEventListener('pointerdown', lower, true);

  function choose(points, high, event) {
    if (!view.enabled || view.locked || high != null) {
      return;
    }
    onVote(event?.shiftKey ? shiftSelection(view.own, points) : { points });
  }

  function cardButton(item, index, active) {
    const between = item.high != null;
    const selected = view.own && !view.abstained && view.own.points === item.points && (view.own.high ?? null) === item.high;
    const label = between ? `Between ${item.points} and ${item.high}` : `${item.points} points`;
    const button = h('button', {
      type: 'button',
      class: ['nt-hand-card', between && 'is-between', selected && 'is-selected'],
      'aria-pressed': String(Boolean(selected)),
      'aria-label': label,
      'aria-disabled': view.enabled && !view.locked ? null : 'true',
      tabindex: index === active ? '0' : '-1',
      dataset: { points: item.points, high: item.high },
      on: {
        pointerdown(event) {
          if (!view.enabled || view.locked) {
            return;
          }
          drag.press(event, {
            points: item.points,
            high: item.high,
            source: button,
            onClick: (e) => choose(item.points, item.high, e),
          });
        },
        click(event) {
          if (event.detail === 0) {
            choose(item.points, item.high, event);
          }
        },
      },
    },
    // At rest the button is only the strip that peeks out; the face inside it shows the card's top.
    h('span', { class: 'nt-hand-face' },
      between ? betweenEl(item.points, item.high) : cardEl(item.points),
      selected && view.locked ? h('span', { class: 'nt-hand-locked' }, icon('lock'), h('span', { text: 'Locked' })) : null),
    selected ? h('span', { class: 'nt-hand-pill', text: voteLabel(item.points, item.high) }) : null);
    setStyle(button, { '--i': index });
    const text = describe(item, view.descriptions);
    if (text) {
      tips.push(tooltip(button, text));
    }
    return h('li', { class: 'nt-fan-slot' }, button);
  }

  function rects() {
    const map = new Map();
    for (const card of fan.querySelectorAll('.nt-hand-card:not(.is-between)')) {
      map.set(Number(card.dataset.points), card.getBoundingClientRect());
    }
    return map;
  }

  // The two cards fly to the merged slot and fade while it fades in.
  function playMerge(before, own) {
    const target = fan.querySelector('.nt-hand-card.is-between');
    const from = [before.get(own.points), before.get(own.high)];
    if (!target?.animate || from.some((r) => !r)) {
      return;
    }
    const to = target.getBoundingClientRect();
    const cx = to.left + to.width / 2;
    const cy = to.top + to.height / 2;
    target.animate([{ opacity: 0 }, { opacity: 0, offset: 0.5 }, { opacity: 1 }], { duration: MERGE_MS, easing: MERGE_EASING });
    [own.points, own.high].forEach((points, i) => {
      const r = from[i];
      const ghost = h('span', { class: 'nt-merge-ghost', 'aria-hidden': 'true' }, cardEl(points));
      setStyle(ghost, { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` });
      document.body.append(ghost);
      const dx = cx - (r.left + r.width / 2) + (i === 0 ? -r.width / 4 : r.width / 4);
      const dy = cy - (r.top + r.height / 2);
      ghost.animate([
        { transform: 'none', opacity: 1 },
        { transform: `translate(${dx}px, ${dy}px)`, opacity: 0 },
      ], { duration: MERGE_MS, easing: MERGE_EASING }).finished.finally(() => ghost.remove());
    });
  }

  function renderFan(previousOwn) {
    const items = fanItems(view.own);
    const before = rects();
    const focused = fan.contains(document.activeElement) ? Number(document.activeElement.dataset.points) : null;
    const picked = items.findIndex((item) => item.points === (focused ?? view.own?.points));
    tips.forEach((tip) => tip.destroy());
    tips = [];
    fan.replaceChildren(...items.map((item, i) => cardButton(item, i, picked >= 0 ? picked : Math.floor(items.length / 2))));
    roving.refresh();
    if (focused != null) {
      fan.querySelector(`[data-points="${focused}"]`)?.focus();
    }
    setStyle(fan, { '--n': items.length });
    const own = view.own;
    const merged = own && !own.abstained && own.high != null;
    const wasMerged = previousOwn && !previousOwn.abstained && previousOwn.high != null
      && previousOwn.points === own?.points && previousOwn.high === own?.high;
    if (merged && !wasMerged && !reducedMotion()) {
      playMerge(before, own);
    }
  }

  return {
    el,
    actions,
    /** The hand's cards (for drag.js): the gaps between neighbours are the "between" zones. */
    cards: () => [...fan.querySelectorAll('.nt-hand-card:not(.is-between)')],
    update(next) {
      const previous = view;
      view = next;
      const own = next.own;
      const key = JSON.stringify([own?.points ?? null, own?.high ?? null, own?.abstained ?? false, next.locked, next.enabled, next.descriptions]);
      if (key !== shape) {
        shape = key;
        renderFan(previous.own);
      }
      el.classList.toggle('is-locked', next.locked);
      lock.update({
        label: next.locked && !next.abstained ? 'Unlock' : 'Lock In',
        variant: next.locked && !next.abstained ? 'default' : 'blue',
        disabled: !next.enabled || next.abstained || own?.points == null,
      });
      abstain.update({ label: next.abstained ? 'Unabstain' : 'Abstain', disabled: !next.enabled });
    },
    destroy() {
      clearTimeout(touchTimer);
      document.removeEventListener('pointerdown', lower, true);
      tips.forEach((tip) => tip.destroy());
      help.destroy();
      roving.destroy();
      drag.cancel();
      el.remove();
      actions.remove();
    },
  };
}
