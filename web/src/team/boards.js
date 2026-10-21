import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { formatDate } from '../core/fmt.js';
import { createButton } from '../ui/button.js';
import { createMenu } from '../ui/menu.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { http } from '../core/http.js';
import { confirmTyped, emptyState, field, formDialog, input, loading, toastError } from './util.js';

const VISIBILITY = {
  team: { label: 'Team only', icon: 'users' },
  link: { label: 'Anyone with the link', icon: 'link' },
  password: { label: 'Password', icon: 'lock' },
};

export function createBoardsTab(ctx) {
  const heading = h('h2', { class: 'team-section-title' });
  const activeButton = createButton({ label: 'Active', size: 'sm', variant: 'secondary', onClick: () => setArchived(false) });
  const archivedButton = createButton({ label: 'Archived', icon: 'archive', size: 'sm', variant: 'secondary', onClick: () => setArchived(true) });
  const list = h('div', { class: 'team-grid' });
  const status = h('div');
  const more = createButton({ label: 'Load more', variant: 'secondary', onClick: () => load(cursor) });
  more.el.hidden = true;
  const el = h('section', { class: 'team-section', 'aria-label': 'Retro boards' },
    h('div', { class: 'team-toolbar' }, heading, h('div', { class: 'team-toggle', role: 'group', 'aria-label': 'Board list' }, activeButton.el, archivedButton.el)),
    status, list, more.el);

  let archived = ctx.query().get('archived') === 'true';
  let boards = [];
  let cursor = null;
  let token = 0;

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
    heading.textContent = archived ? 'Archived boards' : 'Active boards';
    activeButton.update({ pressed: !archived, variant: archived ? 'secondary' : 'primary' });
    archivedButton.update({ pressed: archived, variant: archived ? 'primary' : 'secondary' });
    more.el.hidden = cursor === null;
    if (boards.length === 0 && status.childElementCount === 0) {
      replaceContent(status, archived
        ? emptyState('archive', 'No archived boards', null)
        : emptyState('calendar', 'No retro boards yet', 'Create the first retro board for this team.',
          createButton({ label: 'New board', icon: 'plus', variant: 'primary', onClick: () => openCreateBoard(ctx) }).el));
    }
    reconcile(list, boards, (b) => b.id, createCard, updateCard);
  }

  function createCard(board) {
    const el = h('article', { class: 'card team-card team-board', dataset: { id: board.id } });
    updateCard(el, board);
    return el;
  }

  function updateCard(el, board) {
    const visibility = VISIBILITY[board.visibility] ?? VISIBILITY.team;
    const menuButton = ctx.isAdmin()
      ? createButton({ label: `Board actions for ${board.title}`, icon: 'ellipsis', iconOnly: true, variant: 'ghost', size: 'icon' })
      : null;
    if (menuButton) {
      createMenu({
        label: 'Board actions',
        items: [
          { action: archived ? 'unarchive' : 'archive', label: archived ? 'Unarchive' : 'Archive', icon: 'archive' },
          'separator',
          { action: 'delete', label: 'Delete', icon: 'trash', destructive: true },
        ],
        onSelect: (action) => boardAction(board, action),
      }).attach(menuButton.el);
    }
    replaceContent(el,
      h('header', { class: 'team-card-header' },
        h('h3', { class: 'team-card-title', text: board.title }),
        h('span', { class: ['badge', `team-visibility-${board.visibility}`], title: visibility.label }, icon(visibility.icon), visibility.label),
        menuButton?.el),
      h('p', { class: 'team-muted' }, icon('calendar'), ` ${formatDate(board.createdAt)}`,
        board.archived && board.archivedAt ? ` · archived ${formatDate(board.archivedAt)}` : ''),
      h('a', { class: 'btn btn-primary team-card-open', href: `/retro/${encodeURIComponent(board.code)}`, 'data-nav': '' },
        board.archived ? 'View (read-only)' : 'Open board'));
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
  load(null);

  return {
    el,
    queryChanged() {
      setArchived(ctx.query().get('archived') === 'true');
    },
    destroy() {
      stop();
      token++;
      el.remove();
    },
  };
}

export function openCreateBoard(ctx) {
  const title = input({ name: 'title', required: true, maxlength: '200', placeholder: 'Sprint 42 retro' });
  const visibility = h('select', { class: 'select', name: 'visibility' },
    ...Object.entries(VISIBILITY).map(([value, { label }]) => h('option', { value }, label)));
  const password = input({ name: 'password', type: 'password', autocomplete: 'new-password', minlength: '4', maxlength: '72' });
  const passwordField = field('Board password', password);
  passwordField.hidden = true;
  const template = h('select', { class: 'select', name: 'templateId' }, h('option', { value: '' }, 'Team default'));
  visibility.addEventListener('change', () => {
    passwordField.hidden = visibility.value !== 'password';
    password.required = visibility.value === 'password';
  });

  ctx.api.get('/templates').then((page) => {
    template.append(...page.items.map((t) => h('option', { value: t.id }, t.name + (t.isDefault ? ' (default)' : ''))));
  }, () => {});

  formDialog({
    title: 'New retro board',
    content: [field('Title', title), field('Who can open it', visibility), passwordField, field('Template', template)],
    submitLabel: 'Create board',
    async submit() {
      const body = { title: title.value.trim(), visibility: visibility.value };
      if (visibility.value === 'password') {
        body.password = password.value;
      }
      if (template.value) {
        body.templateId = template.value;
      }
      const board = await ctx.api.post('/boards', body);
      ctx.navigate(`/retro/${encodeURIComponent(board.code)}`);
    },
  });
}
