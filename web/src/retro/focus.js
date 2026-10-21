// The focused card (DESIGN §c.4 `retro.focus.set`, §g.8): a Focus button on each card and a banner above the
// columns. Focus is board state, persisted by the server, so a late joiner's snapshot already carries it.
// `from` makes the change a compare-and-set; a stale one is dropped silently because the fresh focus is
// already on screen (§g.4).

import { h, replaceContent } from '../core/h.js';
import { plural } from '../core/fmt.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { commentsOf, defaultSort, sortedCards } from './model.js';
import { authorLine, paintMarkup } from './card.js';
import { mayPace } from './readiness.js';

export function allowsFocus(view) {
  return !view.meta.archived && (!view.config.stagesEnabled || view.stage === 'discussing');
}

function canFocus(view, me) {
  return allowsFocus(view) && mayPace(view, me);
}

function setFocus(ctx, cardId) {
  const view = ctx.view();
  if (view.focus !== cardId) {
    ctx.send('retro.focus.set', { cardId, from: view.focus });
  }
}

export const focusFeature = {
  board(ctx, { top }) {
    const badge = h('span', { class: 'badge retro-focus-column' });
    const author = h('span', { class: 'retro-focus-author' });
    const votes = h('span', { class: 'retro-focus-votes' });
    const body = h('div', { class: 'retro-card-body retro-focus-body' });
    const comments = h('ul', { class: 'retro-comment-list retro-focus-comments', 'aria-label': 'Comments' });
    const thread = h('button', { type: 'button', class: 'btn btn-ghost btn-sm retro-focus-thread', dataset: { action: 'comments' } }, icon('message-square'), h('span'));
    const prev = createButton({ label: 'Previous card', icon: 'chevron-left', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => step(-1) });
    const next = createButton({ label: 'Next card', icon: 'chevron-right', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => step(1) });
    const close = createButton({ label: 'Stop focusing', icon: 'x', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => setFocus(ctx, null) });
    const el = h('section', { class: 'retro-focus', role: 'region', 'aria-label': 'Focused card', hidden: true },
      h('header', { class: 'retro-focus-header' }, icon('crosshair'), badge, author, votes,
        h('span', { class: 'retro-spacer' }), prev, next, close),
      body, comments, thread);
    top.append(el);
    let shownFor = null;

    // The viewer's own column sort lives in the column's DOM order; the banner steps through the same order.
    function order(view, columnId) {
      const column = el.closest('.retro-board')?.querySelector(`.retro-column[data-id="${columnId}"]`);
      const ids = column ? [...column.querySelectorAll('.retro-cards > .retro-card')].map((node) => node.dataset.id) : [];
      return ids.length > 0 ? ids : sortedCards(view, columnId, defaultSort(view.config)).map((c) => c.id);
    }

    function step(delta) {
      const view = ctx.view();
      const card = view.cards.get(view.focus);
      if (!card) {
        return;
      }
      const ids = order(view, card.columnId);
      const target = ids[ids.indexOf(card.id) + delta];
      if (target) {
        setFocus(ctx, target);
      }
    }

    return {
      update(view) {
        const card = view.focus && allowsFocus(view) ? view.cards.get(view.focus) : null;
        el.hidden = !card;
        if (!card) {
          shownFor = null;
          return;
        }
        const column = view.columns.find((c) => c.id === card.columnId);
        el.dataset.color = column?.color ?? 'gray';
        badge.textContent = column?.title ?? '';
        replaceContent(author, authorLine(card));
        const count = view.votes.get(card.id)?.count ?? 0;
        votes.textContent = view.config.votingEnabled ? `${view.config.voteEmoji} ${count}` : '';
        votes.setAttribute('aria-label', plural(count, 'vote'));
        paintMarkup(body, card.body, view, ctx);
        const list = commentsOf(view, card.id);
        replaceContent(comments, list.map((comment) => {
          const text = h('div', { class: 'retro-comment-body' });
          paintMarkup(text, comment.body, view, ctx);
          return h('li', { class: 'retro-comment' }, h('div', { class: 'retro-comment-meta' }, authorLine(comment)), text);
        }));
        thread.dataset.id = card.id;
        thread.lastChild.textContent = list.length === 0 ? 'Comment' : `Open the thread (${list.length})`;
        const pacing = canFocus(view, ctx.me());
        const ids = order(view, card.columnId);
        const index = ids.indexOf(card.id);
        prev.el.hidden = !pacing;
        next.el.hidden = !pacing;
        close.el.hidden = !pacing;
        prev.update({ disabled: index <= 0 });
        next.update({ disabled: index < 0 || index >= ids.length - 1 });
        if (shownFor !== card.id) {
          shownFor = card.id;
          const node = el.closest('.retro-board')?.querySelector(`.retro-card[data-id="${card.id}"]`);
          node?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
        }
      },
      destroy() {
        el.remove();
      },
    };
  },

  card(ctx, card) {
    const id = card.id;
    const button = h('button', { type: 'button', class: 'btn btn-ghost btn-icon btn-sm retro-focus-toggle' }, icon('crosshair'));
    button.addEventListener('click', () => setFocus(ctx, ctx.view().focus === id ? null : id));
    return {
      el: button,
      update(next, view) {
        const focused = view.focus === id;
        button.hidden = !canFocus(view, ctx.me());
        button.setAttribute('aria-pressed', String(focused));
        const label = focused ? 'Stop focusing on this card' : 'Focus on this card';
        button.setAttribute('aria-label', label);
        button.title = label;
      },
      destroy() {
        button.remove();
      },
    };
  },
};
