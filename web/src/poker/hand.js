// "Your Hand" (§g.8): the fanned deck, the lifted selection with its value pill, Shift-click "between"
// votes merged by a 620 ms FLIP, drag-to-play through drag.js, and the Lock In and Abstain buttons.

import { h, replaceContent, setStyle } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { tooltip } from '../ui/tooltip.js';
import { DECK, voteLabel } from './model.js';
import { betweenEl, cardEl, reducedMotion } from './reveal.js';

export const MERGE_MS = 620;
const MERGE_EASING = 'cubic-bezier(.22,1,.36,1)';
const HELP = 'Select a card, then hold Shift and click an adjacent card to vote between those values. '
  + 'Or drag a card onto the table to lock it in; hold it over a side zone for 2 s to play "between".';

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

/**
 * props: onVote({points, high}), onLock(locked), onAbstain(abstain), drag (drag.js), plus update(view)
 * with view = {own, locked, abstained, enabled, descriptions}.
 */
export function createHand({ onVote, onLock, onAbstain, drag }) {
  const fan = h('ul', { class: 'nt-fan', 'aria-label': 'Cards' });
  const legend = h('dl', { class: 'nt-legend' });
  const info = h('button', { type: 'button', class: 'nt-hand-help', 'aria-label': 'How to vote' }, icon('info'));
  tooltip(info, HELP);
  const lock = createButton({ label: 'Lock In', variant: 'blue', press: true, big: true, className: 'nt-lock', onClick: () => onLock(!view.locked) });
  const abstain = createButton({ label: 'Abstain', variant: 'purple', press: true, big: true, className: 'nt-abstain', onClick: () => onAbstain(!view.abstained) });
  const el = h('section', { class: 'nt-hand', 'aria-label': 'Your hand' },
    h('header', { class: 'nt-hand-head' }, h('h2', { class: 'nt-hand-title', text: 'Your Hand' }), info),
    legend,
    fan,
    h('div', { class: 'nt-hand-actions' }, lock.el, abstain.el));

  let view = { own: null, locked: false, abstained: false, enabled: false, descriptions: {} };
  let shape = '';

  function choose(points, high, event) {
    if (!view.enabled || view.locked) {
      return;
    }
    if (high != null) {
      return;
    }
    onVote(event?.shiftKey ? shiftSelection(view.own, points) : { points });
  }

  function cardButton(item, index) {
    const between = item.high != null;
    const selected = view.own && !view.abstained && view.own.points === item.points && (view.own.high ?? null) === item.high;
    const label = between ? `Between ${item.points} and ${item.high}` : `${item.points} points`;
    const button = h('button', {
      type: 'button',
      class: ['nt-hand-card', between && 'is-between', selected && 'is-selected'],
      'aria-pressed': String(Boolean(selected)),
      'aria-label': label,
      'aria-disabled': view.enabled && !view.locked ? null : 'true',
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
    between ? betweenEl(item.points, item.high) : cardEl(item.points),
    selected ? h('span', { class: 'nt-hand-pill', text: voteLabel(item.points, item.high) }) : null);
    setStyle(button, { '--i': index });
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
    fan.replaceChildren(...items.map((item, i) => cardButton(item, i)));
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

  function renderLegend(descriptions) {
    const entries = DECK.filter((p) => descriptions?.[p]).map((p) => [p, descriptions[p]]);
    legend.hidden = entries.length === 0;
    replaceContent(legend, entries.map(([p, text]) => h('div', { class: 'nt-legend-row' },
      h('dt', { text: `${p}` }),
      h('dd', { text }))));
  }

  return {
    el,
    update(next) {
      const previous = view;
      view = next;
      const own = next.own;
      const key = JSON.stringify([own?.points ?? null, own?.high ?? null, own?.abstained ?? false, next.locked, next.enabled]);
      if (key !== shape) {
        shape = key;
        renderFan(previous.own);
      }
      if (next.descriptions !== previous.descriptions) {
        renderLegend(next.descriptions);
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
      drag.cancel();
      el.remove();
    },
  };
}
