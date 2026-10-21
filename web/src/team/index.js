// /teams/:id (tabs: boards, poker, members, actions, kudos) and /teams/:id/settings. The team room
// carries members, action items and the list notices; the lists themselves come over HTTP.

import { h, replaceContent } from '../core/h.js';
import { getSocket, navigate } from '../screens/app.js';
import { createTabs } from '../ui/tabs.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { plural } from '../core/fmt.js';
import { model } from './model.js';
import { errorMessage, teamApi, toastError, loading } from './util.js';

// Each tab is its own chunk, so the team page stays within its budget (§g.7).
const TABS = [
  { id: 'boards', label: 'Boards', load: () => import('./boards.js').then((m) => m.createBoardsTab) },
  { id: 'poker', label: 'Poker', load: () => import('./poker-list.js').then((m) => m.createPokerTab) },
  { id: 'members', label: 'Members', load: () => import('./members.js').then((m) => m.createMembersTab) },
  { id: 'actions', label: 'Actions', load: () => import('./action-items.js').then((m) => m.createActionItemsTab) },
  { id: 'kudos', label: 'Kudos', load: () => import('./kudos.js').then((m) => m.createKudosTab) },
];
// Each tab keeps its filters in the query string; switching tabs drops the others' filters.
const TAB_PARAMS = ['archived', 'status', 'assignee', 'sort', 'period'];

function message(title, detail) {
  return h('div', { class: 'team-message card' },
    h('h1', { text: title }),
    detail ? h('p', { text: detail }) : null,
    h('a', { class: 'btn btn-secondary', href: '/teams', 'data-nav': '' }, 'All teams'));
}

export function mount(root, boot, app = {}) {
  const me = boot?.me?.principal ?? null;
  const user = boot?.me?.user ?? null;
  const page = h('div', { class: 'team-page' });
  root.replaceChildren(page);

  // One generation per team id shown; async work for an older generation is dropped.
  let generation = 0;
  let teamId = null;
  let ctx = null;
  let screen = null;
  let showing = null;
  let unwatch = [];
  let socket = app.socket ?? null;

  async function teamSocket() {
    socket ??= await getSocket();
    socket.start();
    return socket;
  }

  function setQuery(changes) {
    const url = new URL(location.href);
    for (const [key, value] of Object.entries(changes)) {
      if (value == null || value === '') {
        url.searchParams.delete(key);
      } else {
        url.searchParams.set(key, value);
      }
    }
    history.replaceState(history.state, '', url.href);
  }

  function teardown() {
    generation++;
    for (const stop of unwatch) {
      stop();
    }
    unwatch = [];
    screen?.destroy();
    screen = null;
    showing = null;
    if (ctx?.room) {
      socket?.leave(ctx.room);
    }
    ctx = null;
  }

  function fail(title, detail) {
    teardown();
    replaceContent(page, message(title, detail));
  }

  async function open(id) {
    teardown();
    teamId = id;
    const run = generation;
    let team = null;
    const context = {
      teamId: id,
      api: teamApi(id),
      room: null,
      me,
      user,
      team: () => team,
      role() {
        return context.room?.view?.members.find((m) => m.pid === me?.id)?.role ?? team?.role ?? null;
      },
      isAdmin: () => ['admin', 'owner'].includes(context.role()),
      isOwner: () => context.role() === 'owner',
      query: () => new URLSearchParams(location.search),
      setQuery,
      navigate,
      async reloadTeam() {
        try {
          team = await context.api.get('');
          screen?.teamChanged?.();
        } catch (error) {
          toastError(error, 'The team could not be reloaded.');
        }
      },
    };
    ctx = context;
    replaceContent(page, loading('Loading team…'));

    let connected;
    try {
      [team, connected] = await Promise.all([context.api.get(''), teamSocket()]);
    } catch (error) {
      if (run === generation) {
        const missing = error?.status === 404 || error?.status === 403;
        fail(missing ? 'Team not found or you do not have access.' : errorMessage(error));
      }
      return false;
    }
    if (run !== generation) {
      return false;
    }
    context.room = connected.room(`team:${id}`, model, {
      onNack: (nack) => toastError(nack, 'That change was not accepted.'),
    });
    unwatch.push(context.room.select(() => context.room.kicked, Object.is, (code) => {
      if (code) {
        fail('You no longer have access to this team.', code === 'deleted' ? 'This team was deleted.' : null);
      }
    }));
    return true;
  }

  async function show(settings) {
    const run = generation;
    showing = settings;
    let next;
    if (settings) {
      const { createSettings } = await import('./settings.js');
      if (run !== generation || showing !== settings) {
        return;
      }
      next = createSettings(ctx);
    } else {
      next = createTeamScreen(ctx);
    }
    screen?.destroy();
    screen = next;
    replaceContent(page, screen.el);
  }

  async function route(params) {
    const pathname = location.pathname;
    const settings = (params.route ?? pathname).endsWith('/settings');
    if (!user) {
      teardown();
      teamId = null;
      const back = encodeURIComponent(pathname + location.search);
      replaceContent(page, h('div', { class: 'team-message card' },
        h('h1', { text: 'Sign in to see this team' }),
        h('a', { class: 'btn btn-primary', href: `/login?return=${back}`, 'data-nav': '' }, 'Sign in')));
      return;
    }
    if (params.id !== teamId || ctx === null) {
      const run = generation + 1;
      if (!(await open(params.id)) || run !== generation) {
        return;
      }
    } else if (showing === settings) {
      screen?.queryChanged?.();
      return;
    }
    await show(settings);
  }

  return {
    update(params) {
      route(params).catch((error) => toastError(error, 'This page could not be shown.'));
    },
    unmount() {
      teardown();
      root.replaceChildren();
    },
  };
}

function createTeamScreen(ctx) {
  const name = h('h1', { class: 'team-title' });
  const description = h('p', { class: 'team-description' });
  const meta = h('p', { class: 'team-meta' });
  const banner = h('div', { class: 'team-banner', role: 'alert', hidden: true });
  const settingsLink = h('a', { class: 'btn btn-secondary', href: `/teams/${ctx.teamId}/settings`, 'data-nav': '' },
    icon('settings'), h('span', { class: 'btn-label', text: 'Settings' }));
  const actions = h('div', { class: 'team-actions' },
    createButton({
      label: 'New board',
      icon: 'plus',
      variant: 'primary',
      onClick: () => import('./boards.js').then((m) => m.openCreateBoard(ctx)),
    }).el,
    createButton({
      label: 'New poker session',
      icon: 'spade',
      variant: 'emerald',
      onClick: () => import('./poker-list.js').then((m) => m.createPokerSession(ctx)),
    }).el,
    settingsLink);

  const created = new Map();
  let destroyed = false;
  const tabs = createTabs({
    tabs: TABS.map(({ id, label }) => ({ id, label })),
    selected: selectedTab(),
    label: 'Team sections',
    onChange(id) {
      ctx.setQuery(Object.fromEntries([['tab', id === 'boards' ? null : id], ...TAB_PARAMS.map((p) => [p, null])]));
      select(id);
    },
  });
  const tabsHost = h('div', { class: 'team-tabs' }, tabs.el, ...tabs.panels.values());

  function selectedTab() {
    const tab = ctx.query().get('tab');
    return TABS.some((t) => t.id === tab) ? tab : 'boards';
  }

  function select(id) {
    tabs.select(id);
    if (created.has(id)) {
      created.get(id)?.queryChanged?.();
      return;
    }
    created.set(id, null);
    const panel = tabs.panels.get(id);
    panel.replaceChildren(loading('Loading…'));
    TABS.find((t) => t.id === id).load().then((create) => {
      if (!destroyed) {
        const component = create(ctx);
        created.set(id, component);
        panel.replaceChildren(component.el);
      }
    }, (error) => {
      created.delete(id);
      toastError(error, 'This section could not be loaded.');
    });
  }

  function renderHeader() {
    const team = ctx.team();
    const state = ctx.room.view;
    name.textContent = team?.name ?? state?.name ?? '';
    description.textContent = team?.description ?? '';
    description.hidden = !team?.description;
    const members = state?.members.length ?? team?.counts?.members;
    meta.textContent = members == null ? '' : plural(members, 'member');
    settingsLink.hidden = !ctx.isAdmin();
    const status = team?.jira?.status;
    const warn = ctx.isAdmin() && (status === 'rotate_required' || status === 'auth_failed');
    banner.hidden = !warn;
    if (warn) {
      replaceContent(banner, icon('triangle-alert'),
        h('span', {
          text: status === 'rotate_required'
            ? 'The Jira API token must be replaced before Jira can be trusted again.'
            : 'Jira rejected the saved credentials.',
        }),
        h('a', { href: `/teams/${ctx.teamId}/settings#jira`, 'data-nav': '' }, 'Update Jira settings'));
    }
  }

  const stop = ctx.room.select((s) => s && [s.name, s.members], (a, b) => a?.[0] === b?.[0] && a?.[1] === b?.[1], renderHeader);
  renderHeader();
  select(selectedTab());

  const el = h('div', { class: 'team-screen' },
    h('header', { class: 'team-header' }, h('div', { class: 'team-heading' }, name, description, meta), actions),
    banner,
    tabsHost);

  return {
    el,
    teamChanged() {
      renderHeader();
      for (const component of created.values()) {
        component?.teamChanged?.();
      }
    },
    queryChanged() {
      select(selectedTab());
    },
    destroy() {
      destroyed = true;
      stop();
      for (const component of created.values()) {
        component?.destroy();
      }
      tabs.destroy();
      el.remove();
    },
  };
}
