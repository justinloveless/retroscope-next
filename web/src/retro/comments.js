// A card's comment thread: the comments in (createdAt, id) order, delete for your own, and a composer
// whose draft survives reloads and refused posts.

import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { icon } from '../ui/icons.js';
import { commentsOf, mayPostAnonymously, uuidv7 } from './model.js';
import { authorLine, lazyComposer, paintMarkup } from './card.js';

export function createComments(cardId, ctx) {
  let view = null;
  let canPost = false;
  const list = h('ul', { class: 'retro-comment-list', 'aria-label': 'Comments' });
  const host = h('div', { class: 'retro-composer-host' });
  const composer = lazyComposer(host, ctx, {
    draftKey: ctx.draftKey(`comment:${cardId}`),
    field: 'comment',
    label: 'Add a comment',
    placeholder: 'Add a comment…',
    submitLabel: 'Comment',
    canPostAnonymously: () => mayPostAnonymously(ctx.view(), ctx.me()),
    onSubmit(body, anonymous) {
      const entry = ctx.send('retro.comment.add', { commentId: uuidv7(ctx.now()), cardId, body, anonymous }, {
        onNack(nack) {
          composer.restore(nack.sent.body, nack.sent.anonymous);
          return false;
        },
      });
      return entry !== null;
    },
  });
  const el = h('section', { class: 'retro-comments' }, list, host);

  function createItem(comment) {
    const body = h('div', { class: 'retro-comment-body' });
    const author = h('div', { class: 'retro-comment-author' });
    const remove = h('button', {
      type: 'button', class: 'btn btn-ghost btn-icon btn-sm', 'aria-label': 'Delete comment', title: 'Delete comment',
      dataset: { action: 'delete-comment', id: comment.id },
    }, icon('trash'));
    const item = h('li', { class: 'retro-comment', dataset: { id: comment.id } }, h('div', { class: 'retro-comment-meta' }, author, remove), body);
    item.paint = (next) => {
      paintMarkup(body, next.body, view, ctx);
      replaceContent(author, authorLine(next));
      remove.hidden = !(canPost && view.mineComments.has(next.id));
    };
    item.paint(comment);
    return item;
  }

  return {
    el,
    update(nextView, content) {
      view = nextView;
      canPost = content;
      reconcile(list, commentsOf(view, cardId), (c) => c.id, createItem, (item, comment) => item.paint(comment));
      host.hidden = !canPost;
      composer.refresh();
    },
    focus() {
      if (canPost) {
        composer.focus();
      }
    },
    destroy() {
      composer.destroy();
      el.remove();
    },
  };
}
