// "Open action items": the team's open items from other retros, first in the column row of a team
// member's board. Assign and done go to the team room; comments live on each item's source card on its
// own board, so they are read and posted over HTTP and refetched when the team room says the count
// moved (R22).

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { reconcile } from '../core/list.js';
import { plural } from '../core/fmt.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { uuidv7 } from './model.js';
import { authorLine, lazyComposer, renderMarkup } from './card.js';
import { createAssignee, createDoneToggle, teamLink } from './actions.js';

const byCreated = (a, b) => a.createdAt - b.createdAt || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);

function createThread(ctx, link, item) {
  const path = `/api/teams/${link.teamId}/action-items/${item.id}/comments`;
  const list = h('ul', { class: 'retro-comment-list', 'aria-label': 'Comments' });
  const host = h('div', { class: 'retro-composer-host' });
  const el = h('section', { class: 'retro-comments' }, list, host);
  let token = 0;

  const composer = lazyComposer(host, ctx, {
    draftKey: ctx.draftKey(`action-comment:${item.id}`),
    field: 'comment',
    label: 'Add a comment',
    placeholder: 'Add a comment…',
    submitLabel: 'Comment',
    canPostAnonymously: () => false,
    // Images belong to the board of the card being commented on, not to this one.
    upload: item.sourceBoardId
      ? async (file) => (await http.post(`/api/retro/${item.sourceBoardId}/images`, file)).blobId
      : undefined,
    onSubmit(body) {
      http.post(path, { commentId: uuidv7(ctx.now()), body }).then(refresh, () => {
        toast('The comment could not be added.', { variant: 'error' });
        composer.restore(body, false);
      });
      return true;
    },
  });

  async function refresh() {
    const run = ++token;
    try {
      const { items } = await http.get(path);
      if (run === token) {
        paint(items);
      }
    } catch {
      if (run === token) {
        replaceContent(list, h('li', { class: 'retro-comment', text: 'The comments could not be loaded.' }));
      }
    }
  }

  function paint(comments) {
    replaceContent(list, comments.length === 0
      ? h('li', { class: 'retro-comment', text: 'No comments yet.' })
      : [...comments].sort(byCreated).map((comment) => h('li', { class: 'retro-comment', dataset: { id: comment.id } },
        h('div', { class: 'retro-comment-meta' }, h('div', { class: 'retro-comment-author' }, authorLine(comment))),
        h('div', { class: 'retro-comment-body' }, renderMarkup(comment.body, ctx)))));
  }

  list.append(h('li', { class: 'retro-comment', text: 'Loading comments…' }));
  refresh();
  return {
    el,
    refresh,
    focus: () => composer.focus(),
    destroy() {
      token++;
      composer.destroy();
      el.remove();
    },
  };
}

function createItem(ctx, link, item) {
  const id = item.id;
  let current = null;
  let thread = null;
  const body = h('div', { class: 'retro-card-body' });
  const assignee = createAssignee(link);
  const done = createDoneToggle(link);
  const count = h('span', { class: 'retro-comment-count' });
  const toggle = h('button', {
    type: 'button',
    class: 'btn btn-ghost btn-sm retro-comments-toggle',
    'aria-expanded': 'false',
    on: { click: () => toggleThread() },
  }, icon('message-square'), count);
  const source = h('span', { class: 'retro-prev-source' });
  const threadHost = h('div', { class: 'retro-card-thread' });
  const el = h('li', { class: 'retro-card retro-prev-item', dataset: { id } },
    body,
    h('div', { class: 'retro-card-meta' }, source,
      h('div', { class: 'retro-card-actions' }, assignee.el, done.el, toggle)),
    threadHost);

  function toggleThread() {
    if (thread) {
      thread.destroy();
      thread = null;
    } else if (current.sourceCardId) {
      thread = createThread(ctx, link, current);
      threadHost.append(thread.el);
      thread.focus();
    }
    toggle.setAttribute('aria-expanded', String(thread !== null));
  }

  return {
    el,
    update(next, members) {
      if (next.text !== current?.text) {
        replaceContent(body, renderMarkup(next.text, ctx));
      }
      current = next;
      assignee.update(next, members);
      done.update(next);
      el.classList.toggle('retro-card-done', next.doneAt != null);
      toggle.hidden = !next.sourceCardId;
      count.textContent = String(next.commentCount);
      toggle.setAttribute('aria-label', `Comments (${next.commentCount})`);
      source.textContent = next.sourceBoardId ? '' : 'From a deleted board';
    },
    refreshComments: () => thread?.refresh(),
    destroy() {
      thread?.destroy();
      assignee.destroy();
      el.remove();
    },
  };
}

/**
 * Open items of the team that came from other boards, plus any marked done here during this page view
 * (so "Mark done" can be undone in place). Oldest first: those have been open longest.
 */
export function visibleItems(teamView, boardId, shown) {
  if (!teamView) {
    return [];
  }
  return [...teamView.items.values()]
    .filter((item) => item.sourceBoardId !== boardId && (item.doneAt == null || shown.has(item.id)))
    .sort(byCreated);
}

export const previousActionItems = {
  board(ctx, { before }) {
    const link = teamLink(ctx);
    const release = link.use();
    const items = new Map();
    const shown = new Set();
    let revs = null;
    const title = h('h2', { class: 'retro-column-title', text: 'Open action items' });
    const count = h('span', { class: 'badge retro-column-count' });
    const list = h('ul', { class: 'retro-cards' });
    const el = h('section', { class: 'retro-column retro-prev-actions', 'aria-label': 'Open action items from earlier retros', dataset: { color: 'yellow' } },
      h('header', { class: 'retro-column-header' }, icon('clipboard-list'), title, count),
      list);

    function paint(teamView) {
      const visible = visibleItems(teamView, ctx.boardId, shown);
      const open = visible.filter((item) => item.doneAt == null);
      for (const item of open) {
        shown.add(item.id);
      }
      // Attached only while it has items, so the column row holds nothing empty.
      if (visible.length === 0) {
        el.remove();
      } else if (el.parentNode !== before) {
        // Scroll snapping keeps the snapped column in place when one is added before it; a row that
        // was at its start should show the new first column instead.
        const row = before.parentElement;
        const atStart = !row || row.scrollLeft <= 0;
        before.prepend(el);
        if (atStart && row) {
          row.scrollLeft = 0;
          requestAnimationFrame(() => {
            row.scrollLeft = 0;
          });
        }
      }
      count.textContent = String(open.length);
      count.title = `${plural(open.length, 'open item')}`;
      const members = ctx.view()?.members ?? teamView?.members ?? [];
      reconcile(list, visible, (item) => item.id, (item) => {
        const component = createItem(ctx, link, item);
        items.set(item.id, component);
        component.update(item, members);
        return component.el;
      }, (node, item) => items.get(item.id).update(item, members), {
        remove(node, finish) {
          items.get(node.dataset.id)?.destroy();
          items.delete(node.dataset.id);
          finish();
        },
      });
      const next = teamView?.commentsRev ?? null;
      for (const [id, component] of items) {
        if ((next?.[id] ?? 0) !== (revs?.[id] ?? 0)) {
          component.refreshComments();
        }
      }
      revs = next;
    }

    const unsubscribe = link.subscribe(paint);
    paint(link.view());
    return {
      update: () => paint(link.view()),
      destroy() {
        unsubscribe();
        for (const component of items.values()) {
          component.destroy();
        }
        el.remove();
        release();
      },
    };
  },
};
