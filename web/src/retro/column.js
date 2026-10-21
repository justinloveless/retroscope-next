// One column: header with the facilitator's grip and menu, the viewer's own sort, the cards and the
// add-card composer. A column whose composer holds a draft opens it on load.

import { h } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { createMenu } from '../ui/menu.js';
import { icon } from '../ui/icons.js';
import { allowsContent, defaultSort, isFacilitator, mayPostAnonymously, sortedCards, uuidv7 } from './model.js';
import { createCard, lazyComposer } from './card.js';

export const COLUMN_COLORS = ['green', 'red', 'blue', 'yellow', 'purple', 'pink', 'indigo', 'orange', 'gray'];

const SORT_LABELS = {
  'votes-desc': 'Most votes',
  'votes-asc': 'Fewest votes',
  'time-asc': 'Oldest first',
  'time-desc': 'Newest first',
  'author-asc': 'Author A–Z',
  'author-desc': 'Author Z–A',
};

export function createColumn(column, ctx) {
  const id = column.id;
  const cards = new Map();
  let current = column;
  let view = ctx.view();
  let sort = null;
  let composer = null;

  const grip = h('button', {
    type: 'button', class: 'btn btn-ghost btn-icon btn-sm retro-column-grip', dataset: { id },
  }, icon('grip-vertical'));
  const title = h('h2', { class: 'retro-column-title' });
  const count = h('span', { class: 'badge retro-column-count' });
  const actionBadge = h('span', { class: 'badge retro-action-badge', text: 'Action items' });
  // A menu rather than a <select>: WebKit's <select> trips the CSP's style-src-attr (decisions/U34.md).
  const sortButton = h('button', { type: 'button', class: 'btn btn-ghost btn-icon btn-sm retro-sort' }, icon('arrow-up-down'));
  const sortMenu = createMenu({
    items: [],
    label: 'Sort cards',
    onSelect(key) {
      sort = key;
      update(current, view);
    },
  });
  sortMenu.attach(sortButton);
  const menuButton = h('button', { type: 'button', class: 'btn btn-ghost btn-icon btn-sm retro-column-menu' }, icon('ellipsis'));
  const menu = createMenu({ items: [], label: 'Column actions', onSelect: (action) => ctx.columnAction(action, id) });
  menu.attach(menuButton);
  const list = h('ul', { class: 'retro-cards' });
  const addButton = h('button', { type: 'button', class: 'btn btn-ghost retro-add-card', dataset: { action: 'add-card', id } }, icon('plus'), 'Add a card');
  const footer = h('div', { class: 'retro-column-footer' }, addButton);
  const el = h('section', { class: 'retro-column', dataset: { id } },
    h('header', { class: 'retro-column-header' }, grip, title, count, actionBadge, h('span', { class: 'retro-spacer' }), sortButton, menuButton),
    list,
    footer);

  function openComposer() {
    if (!composer) {
      const host = h('div', { class: 'retro-composer-host' });
      footer.replaceChildren(host);
      composer = lazyComposer(host, ctx, {
        draftKey: ctx.draftKey(`card:${id}`),
        field: 'card',
        label: `Add a card to ${current.title}`,
        placeholder: 'Type your card…',
        submitLabel: 'Add card',
        canPostAnonymously: () => mayPostAnonymously(ctx.view(), ctx.me()),
        onSubmit(body, anonymous) {
          const entry = ctx.send('retro.card.add', { cardId: uuidv7(ctx.now()), columnId: id, body, anonymous }, {
            onNack(nack) {
              openComposer().restore(nack.sent.body, nack.sent.anonymous);
              return false;
            },
          });
          return entry !== null;
        },
        onCancel: () => closeComposer(true),
      });
    }
    return composer;
  }

  function closeComposer(refocus = false) {
    composer?.destroy();
    composer = null;
    footer.replaceChildren(addButton);
    if (refocus) {
      addButton.focus();
    }
  }

  function update(next, nextView) {
    current = next;
    view = nextView;
    const facilitator = isFacilitator(view, ctx.me()) && !view.meta.archived;
    const content = allowsContent(view, current);
    el.dataset.color = COLUMN_COLORS.includes(current.color) ? current.color : 'gray';
    el.setAttribute('aria-label', current.title);
    title.textContent = current.title;
    actionBadge.hidden = !current.actionItems;
    grip.hidden = !facilitator;
    grip.setAttribute('aria-label', `Move column ${current.title}`);
    menuButton.hidden = !facilitator;
    menuButton.setAttribute('aria-label', `Actions for ${current.title}`);
    menu.update({
      items: [
        { action: 'edit-column', label: 'Edit column', icon: 'pencil' },
        current.actionItems
          ? { action: 'unset-action-items', label: 'Not the action items column', icon: 'clipboard-list' }
          : { action: 'set-action-items', label: 'Use for action items', icon: 'clipboard-list' },
        'separator',
        { action: 'delete-column', label: 'Delete column', icon: 'trash', destructive: true },
      ],
    });
    const key = sort ?? defaultSort(view.config);
    sortButton.setAttribute('aria-label', `Sort ${current.title}: ${SORT_LABELS[key]}`);
    sortButton.title = `Sorted by ${SORT_LABELS[key].toLowerCase()}`;
    sortMenu.update({
      items: Object.entries(SORT_LABELS).map(([action, label]) => ({ action, label, icon: action === key ? 'check' : 'circle' })),
    });
    const ordered = sortedCards(view, id, key);
    count.textContent = String(ordered.length);
    reconcile(list, ordered, (c) => c.id, (card) => {
      const component = createCard(card, ctx);
      cards.set(card.id, component);
      return component.el;
    }, (node, card) => cards.get(card.id).update(card, view), {
      flip: true,
      remove(node, done) {
        const component = cards.get(node.dataset.id);
        cards.delete(node.dataset.id);
        component?.destroy();
        done();
      },
    });
    footer.hidden = !content;
    if (!content && composer) {
      closeComposer();
    }
    composer?.refresh();
  }

  update(column, view);
  if (allowsContent(view, column) && ctx.drafts.read(ctx.draftKey(`card:${id}`))) {
    openComposer();
  }

  return {
    el,
    grip,
    update,
    card: (cardId) => cards.get(cardId),
    addCard() {
      openComposer().focus();
    },
    destroy() {
      composer?.destroy();
      menu.destroy();
      sortMenu.destroy();
      for (const component of cards.values()) {
        component.destroy();
      }
      el.remove();
    },
  };
}
