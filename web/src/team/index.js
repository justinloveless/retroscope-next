// /teams/:id (sections: poker, retros, actions, kudos, members, chosen by ?tab=) and /teams/:id/settings.
// The team room carries members, action items and the list notices; the lists themselves come over HTTP.
// The app header owns the team's name and its sections (UX18 decisions 9 and 11): this page shows one
// section in its <main>, with that section's h1, and keeps the header's team name, role and open
// action-item count live.

import { h, replaceContent } from '../core/h.js';
import { getSocket, navigate, patchTeam } from '../screens/app.js';
import { SECTIONS, sectionOf } from '../screens/header.js';
import { showNoAccess } from '../screens/no-access.js';
import { icon } from '../ui/icons.js';
import { model, openActions } from './model.js';
import { errorMessage, teamApi, toastError, loading } from './util.js';

// Each tab is its own chunk, so the team page stays within its budget (§g.7).
const LOADERS = {
  poker: () => import('./poker-list.js').then((m) => m.createPokerTab),
  retros: () => import('./boards.js').then((m) => m.createBoardsTab),
  actions: () => import('./action-items.js').then((m) => m.createActionItemsTab),
  kudos: () => import('./kudos.js').then((m) => m.createKudosTab),
  members: () => import('./members.js').then((m) => m.createMembersTab),
};
const TABS = SECTIONS.map(([id, label]) => ({ id, label, load: LOADERS[id] }));

function message(title, retry) {
  return h('main', { class: 'team-screen' }, h('div', { class: 'team-message card' },
    h('h1', { text: title }),
    h('button', { type: 'button', class: 'btn btn-secondary', on: { click: retry } }, 'Try again')));
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
  let shownParams = null;

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

  // A missing team and one the user is not in look the same (S9, UX18 decision 2).
  function fail(error) {
    teardown();
    if (error === 'removed' || error?.status === 404 || error?.status === 403) {
      showNoAccess(page, { removed: error === 'removed' });
    } else {
      replaceContent(page, message(errorMessage(error), () => route(shownParams)));
    }
  }

  // The header's values for this team, live: the role from the roster, the name from the team's details
  // (the room's name is set only when its actor loads, so it can be older) and the open action items.
  function patchHeader(context) {
    const state = context.room?.view;
    patchTeam(context.teamId, {
      name: context.team()?.name,
      role: context.role() ?? undefined,
      openActions: state ? openActions(state) : undefined,
    });
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
          patchHeader(context);
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
        fail(error);
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
        // A tab over its room limit can come back; every other kick ends this user's access.
        fail(code === 'too_many_rooms' ? { code } : 'removed');
      }
    }));
    unwatch.push(context.room.select((s) => s, Object.is, () => patchHeader(context)));
    patchHeader(context);
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
    shownParams = params;
    const pathname = location.pathname;
    const settings = (params.route ?? pathname).endsWith('/settings');
    if (!user) {
      // The router sends signed-out visitors to sign in first; this covers a stale boot JSON.
      navigate(`/login?return=${encodeURIComponent(pathname + location.search)}`, { replace: true });
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

// One section at a time inside the page's <main>; the header's links choose it (UX18 decision 11). A section
// loaded once stays alive while the page shows the team, so switching back to it is instant.
function createTeamScreen(ctx) {
  const banner = h('div', { class: 'team-banner', role: 'alert', hidden: true });
  const host = h('div', { class: 'team-section-host' });
  const el = h('main', { class: 'team-screen' }, banner, host);

  const created = new Map();
  let selected = null;
  let destroyed = false;

  function selectedTab() {
    return sectionOf(ctx.query());
  }

  function retitle() {
    const label = TABS.find((t) => t.id === selected)?.label;
    const team = ctx.team()?.name ?? ctx.room.view?.name;
    document.title = [label, team, 'Retroscope'].filter(Boolean).join(' · ');
  }

  function failed(id, error) {
    created.delete(id);
    toastError(error, 'This section could not be loaded.');
    if (selected === id && !destroyed) {
      host.replaceChildren(h('div', { class: 'team-message card' },
        h('h1', { text: 'This section could not be loaded.' }),
        h('button', { type: 'button', class: 'btn btn-secondary', on: { click: () => select(id, true) } }, 'Try again')));
    }
  }

  function select(id, force = false) {
    if (id === selected && !force) {
      created.get(id)?.queryChanged?.();
      return;
    }
    selected = id;
    retitle();
    if (created.has(id)) {
      const component = created.get(id);
      host.replaceChildren(component ? component.el : loading('Loading…'));
      component?.queryChanged?.();
      return;
    }
    created.set(id, null);
    host.replaceChildren(loading('Loading…'));
    TABS.find((t) => t.id === id).load().then((create) => {
      if (destroyed) {
        return;
      }
      const component = create(ctx);
      created.set(id, component);
      if (selected === id) {
        host.replaceChildren(component.el);
      }
    }, (error) => failed(id, error));
  }

  // The Jira banner is for the team's admins: a saved token that Jira refuses or that must be replaced.
  function renderBanner() {
    const status = ctx.team()?.jira?.status;
    const warn = ctx.isAdmin() && (status === 'rotate_required' || status === 'auth_failed');
    banner.hidden = !warn;
    if (warn) {
      replaceContent(banner, icon('triangle-alert'),
        h('span', {
          text: status === 'rotate_required'
            ? 'The Jira API token must be replaced before Jira can be trusted again.'
            : 'Jira rejected the saved credentials.',
        }),
        h('a', { href: `/teams/${encodeURIComponent(ctx.teamId)}/settings#jira`, 'data-nav': '' }, 'Update Jira settings'));
    }
  }

  const stop = ctx.room.select((s) => s && [s.name, s.members], (a, b) => a?.[0] === b?.[0] && a?.[1] === b?.[1], () => {
    renderBanner();
    retitle();
  });
  renderBanner();
  select(selectedTab());

  return {
    el,
    teamChanged() {
      renderBanner();
      retitle();
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
      el.remove();
    },
  };
}
