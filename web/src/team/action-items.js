// Action items: the HTTP list decides which items show (filters, paging, board titles); the team
// room supplies their live state, including this tab's optimistic assign/done/delete intents.

import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { parse } from '../markup/parse.js';
import { render as renderMarkup } from '../markup/render.js';
import { createButton } from '../ui/button.js';
import { createChoice } from '../ui/choice.js';
import { confirmDialog } from '../ui/confirm.js';
import { icon } from '../ui/icons.js';
import { optimistic } from './model.js';
import { errorMessage, loading, toastError } from './util.js';

const STATUS = ['open', 'done', 'all'];
const SORT = ['newest', 'oldest'];

function filters(query) {
  return {
    status: STATUS.includes(query.get('status')) ? query.get('status') : 'open',
    assignee: query.get('assignee') ?? 'all',
    sort: SORT.includes(query.get('sort')) ? query.get('sort') : 'newest',
  };
}

function matches(item, { status, assignee }) {
  const done = item.doneAt != null;
  if ((status === 'open' && done) || (status === 'done' && !done)) {
    return false;
  }
  if (assignee === 'unassigned') {
    return item.assignedTo == null;
  }
  return assignee === 'all' || item.assignedTo === assignee;
}

export function renderBody(markup, members) {
  const container = h('div', { class: 'team-markup' });
  container.append(renderMarkup(parse(markup ?? ''), {
    resolveMention: (id) => members.find((m) => m.pid === id)?.name,
  }));
  return container;
}

export function createActionItemsTab(ctx) {
  const pick = (key, fallback) => (value) => {
    ctx.setQuery({ [key]: value === fallback ? null : value });
    current = filters(ctx.query());
    load(null);
  };
  const statusChoice = createChoice({
    label: 'Status',
    name: 'status',
    options: [{ value: 'open', label: 'Open' }, { value: 'done', label: 'Done' }, { value: 'all', label: 'All' }],
    onChange: pick('status', 'open'),
  });
  const assigneeChoice = createChoice({ label: 'Assignee', name: 'assignee', options: [], onChange: pick('assignee', 'all') });
  const sortChoice = createChoice({
    label: 'Sort',
    name: 'sort',
    options: [{ value: 'newest', label: 'Newest first' }, { value: 'oldest', label: 'Oldest first' }],
    onChange: pick('sort', 'newest'),
  });
  const groups = h('div', { class: 'team-groups' });
  const status = h('div');
  const more = createButton({ label: 'Load more', variant: 'secondary', onClick: () => load(cursor) });
  more.el.hidden = true;
  const el = h('section', { class: 'team-section', 'aria-label': 'Action items' },
    h('div', { class: 'card team-filters' },
      h('label', { class: 'team-filter' }, h('span', { text: 'Status' }), statusChoice.el),
      h('label', { class: 'team-filter' }, h('span', { text: 'Assignee' }), assigneeChoice.el),
      h('label', { class: 'team-filter' }, h('span', { text: 'Sort' }), sortChoice.el)),
    status, groups, more.el);

  let current = filters(ctx.query());
  let fetched = [];
  let cursor = null;
  let token = 0;
  let reloadTimer = 0;
  let destroyed = false;
  const panels = new Map();

  function query(after) {
    const params = new URLSearchParams({ status: current.status, sort: current.sort });
    if (current.assignee !== 'all') {
      params.set('assignee', current.assignee);
    }
    if (after) {
      params.set('cursor', after);
    }
    return params;
  }

  async function load(after) {
    const run = ++token;
    if (after === null) {
      replaceContent(status, loading('Loading action items…'));
    }
    try {
      const page = await ctx.api.get(`/action-items?${query(after)}`);
      if (run !== token) {
        return;
      }
      fetched = after === null ? page.items : [...fetched, ...page.items.filter((i) => !fetched.some((x) => x.id === i.id))];
      cursor = page.nextCursor ?? null;
      status.replaceChildren();
      render();
    } catch (error) {
      if (run === token) {
        replaceContent(status, h('p', { class: 'team-muted', text: errorMessage(error, 'The action items could not be loaded.') }));
      }
    }
  }

  function scheduleReload() {
    clearTimeout(reloadTimer);
    reloadTimer = setTimeout(() => load(null), 300);
  }

  function members() {
    return ctx.room.view?.members ?? [];
  }

  function renderAssignees() {
    const options = [{ value: 'all', label: 'Everyone' }, { value: 'unassigned', label: 'Unassigned' }, ...members().map((m) => ({ value: m.pid, label: m.name }))];
    if (!options.some((o) => o.value === current.assignee)) {
      options.push({ value: current.assignee, label: 'Former member' });
    }
    assigneeChoice.update({ options, value: current.assignee });
  }

  function visible() {
    const live = ctx.room.view;
    const rows = fetched
      .filter((item) => !live?.removed?.has(item.id) && !(live && !live.items.has(item.id) && item.doneAt == null))
      .map((item) => ({ ...item, ...live?.items.get(item.id), boardTitle: item.boardTitle }))
      .filter((item) => matches(item, current));
    rows.sort((a, b) => (current.sort === 'newest' ? b.createdAt - a.createdAt : a.createdAt - b.createdAt));
    const byBoard = new Map();
    for (const item of rows) {
      const key = item.sourceBoardId ?? 'other';
      if (!byBoard.has(key)) {
        byBoard.set(key, { key, title: item.boardTitle ?? 'Other', items: [] });
      }
      byBoard.get(key).items.push(item);
    }
    return [...byBoard.values()];
  }

  function render() {
    statusChoice.update({ value: current.status });
    sortChoice.update({ value: current.sort });
    renderAssignees();
    more.el.hidden = cursor === null;
    const grouped = visible();
    if (grouped.length === 0 && status.childElementCount === 0) {
      replaceContent(status, h('p', { class: 'team-muted', text: 'No action items match these filters.' }));
    } else if (grouped.length > 0 && status.querySelector('[role=status]') === null) {
      status.replaceChildren();
    }
    reconcile(groups, grouped, (g) => g.key, (group) => {
      const list = h('div', { class: 'team-list' });
      const section = h('section', { class: 'card team-group', dataset: { board: group.key } },
        h('h3', { class: 'team-group-title' }), list);
      updateGroup(section, group);
      return section;
    }, updateGroup);
  }

  function updateGroup(section, group) {
    section.firstChild.textContent = group.title;
    reconcile(section.lastChild, group.items, (i) => i.id, (item) => {
      const card = h('article', { class: 'team-item', dataset: { id: item.id } },
        h('div', { class: 'team-item-text' }), h('div', { class: 'team-item-bar' }), h('div'), h('div'));
      card.assignee = createChoice({
        label: 'Assigned to',
        className: 'team-assignee',
        options: [],
        onChange: (pid) => send('team.action.assign', { itemId: item.id, userId: pid || null }),
      });
      updateItem(card, item);
      return card;
    }, updateItem);
  }

  // The comments panel keeps its node across updates, so a draft in its editor keeps focus.
  function updateItem(card, item) {
    const [text, bar, toggle, panelHost] = card.children;
    const done = item.doneAt != null;
    card.classList.toggle('team-item-done', done);
    const { assignee } = card;
    const people = [{ value: '', label: 'Nobody' }, ...members().map((m) => ({ value: m.pid, label: m.name }))];
    if (item.assignedTo && !people.some((p) => p.value === item.assignedTo)) {
      people.push({ value: item.assignedTo, label: 'Former member' });
    }
    assignee.update({ options: people, value: item.assignedTo ?? '' });
    replaceContent(text, renderBody(item.text, members()));
    replaceContent(bar,
      h('label', { class: 'team-filter' }, h('span', { text: 'Assigned' }), assignee.el),
      createButton({
        label: done ? 'Re-open' : 'Mark done',
        icon: done ? 'rotate-ccw' : 'check',
        size: 'sm',
        variant: 'secondary',
        onClick: () => send('team.action.done', { itemId: item.id, done: !done }),
      }).el,
      ctx.isAdmin()
        ? createButton({ label: 'Delete action item', icon: 'trash', iconOnly: true, size: 'icon', variant: 'ghost', onClick: () => remove(item) }).el
        : null);
    replaceContent(toggle, item.sourceCardId ? commentsToggle(item) : null);
    const panel = item.sourceCardId ? panels.get(item.id) : null;
    if (panel && panel.el.parentNode !== panelHost) {
      panelHost.replaceChildren(panel.el);
    } else if (!panel) {
      panelHost.replaceChildren();
    }
  }

  function commentsToggle(item) {
    const open = panels.has(item.id);
    return createButton({
      label: item.commentCount > 0 ? `${item.commentCount} ${item.commentCount === 1 ? 'comment' : 'comments'}` : 'Add comment',
      icon: 'message-square',
      size: 'sm',
      variant: 'ghost',
      pressed: open,
      className: 'team-comments-toggle',
      onClick() {
        if (panels.has(item.id)) {
          panels.get(item.id).destroy();
          panels.delete(item.id);
          render();
        } else {
          import('./comments.js').then(({ createComments }) => {
            if (!destroyed && !panels.has(item.id)) {
              panels.set(item.id, createComments(ctx, item, members));
              render();
            }
          }, (error) => toastError(error, 'The comments could not be loaded.'));
        }
      },
    }).el;
  }

  function send(k, d) {
    ctx.room.send(k, d, { apply: (state) => optimistic(state, k, d, Date.now(), ctx.me?.id) });
  }

  async function remove(item) {
    if (await confirmDialog({ title: 'Delete this action item?', message: 'It disappears from the team list. The retro card stays.', confirmLabel: 'Delete', destructive: true })) {
      send('team.action.delete', { itemId: item.id });
    }
  }

  // Items the room learns about that the fetched list lacks (new, or newly matching) need the
  // server's list for their board title and paging position.
  const stopItems = ctx.room.select((s) => s?.items, Object.is, (items) => {
    if (items && [...items.values()].some((i) => matches(i, current) && !fetched.some((f) => f.id === i.id))) {
      scheduleReload();
    }
    render();
  });
  const stopMembers = ctx.room.select((s) => s?.members, Object.is, render);
  const stopComments = ctx.room.select((s) => s?.commentsRev, Object.is, (revs, previous) => {
    for (const [id, panel] of panels) {
      if ((revs?.[id] ?? 0) !== (previous?.[id] ?? 0)) {
        panel.refresh();
      }
    }
  });
  load(null);

  return {
    el,
    queryChanged() {
      const next = filters(ctx.query());
      if (JSON.stringify(next) !== JSON.stringify(current)) {
        current = next;
        load(null);
      }
    },
    destroy() {
      destroyed = true;
      stopItems();
      stopMembers();
      stopComments();
      clearTimeout(reloadTimer);
      token++;
      for (const panel of panels.values()) {
        panel.destroy();
      }
      for (const choice of [statusChoice, assigneeChoice, sortChoice]) {
        choice.destroy();
      }
      el.remove();
    },
  };
}
