// The team's Retros section (UX18 decision 11): Active and Archived boards, "New board" (a title and a template),
// and a card per board with its title, its date and, when the board uses stages, the stage it is in. Boards
// are team-only: every member of the team can open one, and nobody else.

import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { formatDate } from '../core/fmt.js';
import { createButton } from '../ui/button.js';
import { createChoice } from '../ui/choice.js';
import { createMenu } from '../ui/menu.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { http } from '../core/http.js';
import { confirmTyped, emptyState, field, formDialog, input, loading, toastError } from './util.js';

const STAGES = { thinking: 'Thinking', voting: 'Voting', discussing: 'Discussing', closed: 'Closed' };

export const boardPath = (code) => `/retro/${encodeURIComponent(code)}`;

/** The card's date line: created, archived when it is, and the stage when the board uses stages. */
export function boardMeta(board) {
  const parts = [formatDate(board.createdAt)];
  if (board.archived && board.archivedAt) {
    parts.push(`archived ${formatDate(board.archivedAt)}`);
  }
  if (board.stagesEnabled && STAGES[board.stage]) {
    parts.push(STAGES[board.stage]);
  }
  return parts.join(' · ');
}

export function createBoardsTab(ctx) {
  const activeButton = h('button', { type: 'button', on: { click: () => setArchived(false) } }, 'Active');
  const archivedButton = h('button', { type: 'button', on: { click: () => setArchived(true) } }, 'Archived');
  const create = createButton({ label: 'New board', icon: 'plus', variant: 'indigo', size: 'lg', onClick: () => openCreateBoard(ctx) });
  const list = h('div', { class: 'team-grid' });
  const status = h('div');
  const more = createButton({ label: 'Load more', variant: 'secondary', onClick: () => load(cursor) });
  more.el.hidden = true;
  const el = h('section', { class: 'team-section', 'aria-label': 'Retro boards' },
    h('div', { class: 'team-toolbar' },
      h('h1', { class: 'team-title', text: 'Retros' }),
      h('div', { class: 'team-actions' },
        h('div', { class: 'team-segmented', role: 'group', 'aria-label': 'Board list' }, activeButton, archivedButton),
        create.el)),
    status, list, more.el);

  let archived = ctx.query().get('archived') === 'true';
  let boards = [];
  let cursor = null;
  let token = 0;
  const menus = new Map();

  function setArchived(value) {
    if (value !== archived) {
      archived = value;
      ctx.setQuery({ archived: value ? 'true' : null });
      boards = [];
      render();
      load(null);
    }
  }

  async function load(after) {
    const run = ++token;
    if (after === null) {
      replaceContent(status, loading('Loading boards…'));
    }
    try {
      const page = await ctx.api.get(`/boards?archived=${archived}${after ? `&cursor=${encodeURIComponent(after)}` : ''}`);
      if (run !== token) {
        return;
      }
      boards = after === null ? page.items : [...boards, ...page.items.filter((b) => !boards.some((x) => x.id === b.id))];
      cursor = page.nextCursor ?? null;
      status.replaceChildren();
      render();
    } catch (error) {
      if (run === token) {
        status.replaceChildren();
        toastError(error, 'The boards could not be loaded.');
      }
    }
  }

  function render() {
    activeButton.setAttribute('aria-pressed', String(!archived));
    archivedButton.setAttribute('aria-pressed', String(archived));
    more.el.hidden = cursor === null;
    if (boards.length === 0 && status.childElementCount === 0) {
      replaceContent(status, archived
        ? emptyState('archive', 'No archived boards', null)
        : emptyState('calendar', 'No retro boards yet', 'Create the first retro board for this team.'));
    }
    reconcile(list, boards, (b) => b.id, createCard, updateCard, {
      remove(node, done) {
        menus.get(node.dataset.id)?.destroy();
        menus.delete(node.dataset.id);
        done();
      },
    });
  }

  function createCard(board) {
    const el = h('article', { class: 'card team-card team-board', dataset: { id: board.id } });
    updateCard(el, board);
    return el;
  }

  // The title is the card's link and covers the whole card; the admins' menu button sits on top of it.
  function updateCard(el, board) {
    let menuButton = null;
    if (ctx.isAdmin()) {
      menuButton = createButton({ label: `Board actions for ${board.title}`, icon: 'ellipsis', iconOnly: true, variant: 'ghost', size: 'icon', className: 'team-board-menu' });
      menus.get(board.id)?.destroy();
      const menu = createMenu({
        label: 'Board actions',
        items: [
          { action: board.archived ? 'unarchive' : 'archive', label: board.archived ? 'Unarchive' : 'Archive', icon: 'archive' },
          'separator',
          { action: 'delete', label: 'Delete', icon: 'trash', destructive: true },
        ],
        onSelect: (action) => boardAction(board, action),
      });
      menu.attach(menuButton.el);
      menus.set(board.id, menu);
    }
    replaceContent(el,
      h('span', { class: 'team-board-preview', 'aria-hidden': 'true' }, h('span'), h('span'), h('span')),
      h('div', { class: 'team-card-header' },
        h('h2', { class: 'team-card-title' },
          h('a', { class: 'team-board-link', href: boardPath(board.code), 'data-nav': '', text: board.title })),
        menuButton?.el),
      h('p', { class: 'team-muted', text: boardMeta(board) }));
  }

  async function boardAction(board, action) {
    if (action === 'delete') {
      confirmTyped({
        title: 'Delete this board?',
        message: 'Everyone on the board loses access at once. This cannot be undone.',
        expected: board.title,
        confirmLabel: 'Delete board',
        submit: async () => {
          await http.request('DELETE', `/api/retro/${board.id}`, { body: { confirmTitle: board.title } });
          remove(board.id);
          toast('Board deleted.', { variant: 'success', duration: 3000 });
        },
      });
      return;
    }
    try {
      await http.post(`/api/retro/${board.id}/${action}`, {});
      remove(board.id);
      toast(action === 'archive' ? 'Board archived.' : 'Board restored.', { variant: 'success', duration: 3000 });
    } catch (error) {
      toastError(error, 'The board could not be changed.');
    }
  }

  function remove(id) {
    boards = boards.filter((b) => b.id !== id);
    render();
  }

  const stop = ctx.room.select((s) => s?.boardsRev, Object.is, () => load(null));
  render();
  load(null);

  return {
    el,
    queryChanged() {
      setArchived(ctx.query().get('archived') === 'true');
    },
    destroy() {
      stop();
      token++;
      for (const menu of menus.values()) {
        menu.destroy();
      }
      el.remove();
    },
  };
}

export function openCreateBoard(ctx) {
  const title = input({ name: 'title', required: true, maxlength: '200', placeholder: 'Sprint 42 retro' });
  const template = createChoice({ label: 'Template', name: 'templateId', options: [{ value: '', label: 'Team default' }] });

  ctx.api.get('/templates').then((page) => {
    template.update({
      options: [{ value: '', label: 'Team default' }, ...page.items.map((t) => ({ value: t.id, label: t.name + (t.isDefault ? ' (default)' : '') }))],
    });
  }, () => {});

  const team = ctx.team()?.name;
  formDialog({
    title: 'New retro board',
    content: [
      field('Title', title),
      field('Template', template.el),
      h('p', { class: 'team-muted', text: team ? `Only members of ${team} can open it.` : 'Only members of this team can open it.' }),
    ],
    submitLabel: 'Create board',
    async submit() {
      const body = { title: title.value.trim() };
      if (template.value) {
        body.templateId = template.value;
      }
      const board = await ctx.api.post('/boards', body);
      ctx.navigate(boardPath(board.code));
    },
  });
}
