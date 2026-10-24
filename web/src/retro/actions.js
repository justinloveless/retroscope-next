// Action items on the board (DESIGN §c.2, §f "Create in Jira"). A card in the action-items column of a
// team board has a team action item; its assignee and done state belong to the team room, so team
// members' board pages subscribe to `team:<id>` as well and send assign/done there. "Create in Jira"
// posts the card's markup to the team's Jira proxy, which converts it with MarkupToAdf.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { parse } from '../markup/parse.js';
import { walk } from '../markup/doc.js';
import { model as teamModel, optimistic as teamIntent } from '../team/model.js';
import { createMenu } from '../ui/menu.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { isMember } from './model.js';

const SUMMARY_MAX = 255;

const TEAM_NACKS = {
  forbidden: 'Only team members can change action items.',
  not_found: 'That action item no longer exists.',
  invalid: 'Only team members can be assigned.',
};

const JIRA_ERRORS = {
  jira_not_configured: 'Jira is not connected for this team.',
  jira_forbidden: 'Jira refused to create the issue.',
  jira_validation: 'Jira refused the issue. Check the project settings.',
  jira_rate_limited: 'Jira is busy. Try again in a minute.',
  jira_unavailable: 'Jira could not be reached. Try again later.',
  forbidden: 'You are not allowed to create Jira issues for this team.',
};

const links = new WeakMap();

/**
 * The board's link to its team room, shared by the features of one board. It subscribes while the
 * viewer is a member of the board's team (the board projects `members` to members only, S12) and
 * leaves when the last feature releases it.
 */
export function teamLink(ctx) {
  let link = links.get(ctx);
  if (!link) {
    link = createTeamLink(ctx);
    links.set(ctx, link);
  }
  return link;
}

function createTeamLink(ctx) {
  const listeners = new Set();
  let users = 0;
  let teamId = null;
  let room = null;
  let stop = null;
  let jira = null;
  let jiraLoading = false;

  function notify() {
    for (const listener of listeners) {
      listener(room?.view ?? null);
    }
  }

  function leave() {
    stop?.();
    stop = null;
    if (room) {
      ctx.socket.leave(room);
      room = null;
    }
  }

  return {
    get teamId() {
      return teamId;
    },
    view: () => room?.view ?? null,

    /** Follows the board view: joins the team room once the viewer shows up as a member. */
    sync(view) {
      const next = view?.teamId && isMember(view, ctx.me()?.pid) ? view.teamId : null;
      if (next === teamId) {
        return;
      }
      leave();
      teamId = next;
      jira = null;
      jiraLoading = false;
      if (teamId) {
        room = ctx.socket.room(`team:${teamId}`, teamModel, {
          onNack: (nack) => toast(TEAM_NACKS[nack.code] ?? 'That change to the action item was not saved.', { variant: 'error' }),
        });
        stop = room.select((state) => state, Object.is, notify);
      }
      notify();
    },

    send(k, d) {
      const by = ctx.me()?.pid;
      return room?.send(k, d, { apply: (state) => teamIntent(state, k, d, Date.now(), by) }) ?? null;
    },

    /** Issue keys created from cards during this page view, by card id. */
    jiraKeys: new Map(),

    /**
     * `{configured, status, siteHost, boardId, membersCanEdit}` of the team's Jira, or null until
     * `GET /api/teams/{id}` has answered (fetched once per team; listeners hear when it arrives).
     */
    jira() {
      if (teamId && jira === null && !jiraLoading) {
        const id = teamId;
        jiraLoading = true;
        http.get(`/api/teams/${id}`).then(
          (team) => ({
            configured: team.jira?.configured === true,
            status: team.jira?.status ?? null,
            siteHost: team.jira?.siteHost ?? null,
            boardId: team.jira?.boardId ?? null,
            membersCanEdit: team.jira?.membersCanEdit !== false,
          }),
          () => ({ configured: false }),
        ).then((config) => {
          if (id === teamId) {
            jira = config;
            notify();
          }
        });
      }
      return jira;
    },

    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    use() {
      users++;
      return () => {
        users--;
        if (users === 0) {
          leave();
          teamId = null;
          links.delete(ctx);
        }
      };
    },
  };
}

/** The team item made from `cardId`, if the team room has it. */
export function itemOfCard(teamView, cardId) {
  if (!teamView) {
    return null;
  }
  for (const item of teamView.items.values()) {
    if (item.sourceCardId === cardId) {
      return item;
    }
  }
  return null;
}

/** Assignee menu button: Nobody or a team member, sent as `team.action.assign`. */
export function createAssignee(link) {
  let itemId = null;
  const label = h('span', { class: 'retro-assignee-name' });
  const button = h('button', { type: 'button', class: 'btn btn-ghost btn-sm retro-assignee' }, icon('user'), label);
  const menu = createMenu({
    items: [],
    label: 'Assign to',
    onSelect: (pid) => link.send('team.action.assign', { itemId, userId: pid === 'nobody' ? null : pid }),
  });
  menu.attach(button);
  return {
    el: button,
    update(item, members) {
      itemId = item.id;
      const assignee = members.find((m) => m.pid === item.assignedTo);
      const name = item.assignedTo ? assignee?.name ?? 'Former member' : null;
      label.textContent = name ?? 'Assign';
      button.setAttribute('aria-label', name ? `Assigned to ${name}. Change assignee` : 'Assign this action item');
      button.title = name ? `Assigned to ${name}` : 'Assign';
      menu.update({
        items: [
          { action: 'nobody', label: 'Nobody', icon: item.assignedTo ? 'circle' : 'check' },
          ...members.map((m) => ({ action: m.pid, label: m.name, icon: m.pid === item.assignedTo ? 'check' : 'circle' })),
        ],
      });
    },
    destroy: () => menu.destroy(),
  };
}

/** Done toggle, sent as `team.action.done` (set semantics). */
export function createDoneToggle(link) {
  let item = null;
  const text = h('span');
  const button = h('button', {
    type: 'button',
    class: 'btn btn-ghost btn-sm retro-done',
    on: { click: () => item && link.send('team.action.done', { itemId: item.id, done: item.doneAt == null }) },
  }, icon('circle-check-big'), text);
  return {
    el: button,
    update(next) {
      item = next;
      const done = item.doneAt != null;
      text.textContent = done ? 'Done' : 'Mark done';
      button.setAttribute('aria-pressed', String(done));
      button.title = done ? 'Done. Click to re-open' : 'Mark done';
    },
  };
}

/** The issue summary: the card's first line of text, at most 255 characters. */
export function summaryOf(markup) {
  const lines = [];
  for (const block of parse(markup).c) {
    let line = '';
    for (const node of walk(block)) {
      if (node.t === 'text' || node.t === 'code') {
        line += node.v;
      } else if (node.t === 'mention') {
        line += `@${node.name}`;
      } else if (node.t === 'br') {
        line += '\n';
      }
    }
    lines.push(...line.split('\n'));
  }
  const first = lines.map((l) => l.replace(/\s+/g, ' ').trim()).find((l) => l !== '') ?? 'Action item';
  return first.length > SUMMARY_MAX ? `${first.slice(0, SUMMARY_MAX - 1)}…` : first;
}

function browseUrl(siteHost, key) {
  return siteHost ? `https://${siteHost}/browse/${encodeURIComponent(key)}` : null;
}

function createJiraAction(link, created) {
  let card = null;
  let config = null;
  let busy = false;
  const host = h('span', { class: 'retro-jira' });
  const button = h('button', {
    type: 'button',
    class: 'btn btn-ghost btn-sm retro-jira-create',
    on: { click: () => create() },
  }, icon('external-link'), h('span', { text: 'Create in Jira' }));

  async function create() {
    if (busy || !card) {
      return;
    }
    const { id, body } = card;
    busy = true;
    button.disabled = true;
    button.setAttribute('aria-busy', 'true');
    try {
      const issue = await http.post(`/api/teams/${link.teamId}/jira/issues`, { summary: summaryOf(body), description: body });
      created.set(id, issue.key);
      toast(`Created ${issue.key} in Jira.`, { variant: 'success' });
    } catch (error) {
      toast(JIRA_ERRORS[error?.code] ?? 'The Jira issue could not be created.', { variant: 'error' });
    } finally {
      busy = false;
      button.disabled = false;
      button.removeAttribute('aria-busy');
      paint();
    }
  }

  function paint() {
    const key = card ? created.get(card.id) : null;
    if (key) {
      const href = browseUrl(config?.siteHost, key);
      replaceContent(host, href
        ? h('a', { class: 'badge retro-jira-key', href, target: '_blank', rel: 'noopener noreferrer', title: `Open ${key} in Jira` }, icon('external-link'), key)
        : h('span', { class: 'badge retro-jira-key', text: key }));
    } else {
      replaceContent(host, button);
    }
  }

  return {
    el: host,
    update(next, nextConfig) {
      card = next;
      config = nextConfig;
      paint();
    },
  };
}

function canWriteJira(config, view, me) {
  if (!config?.configured) {
    return false;
  }
  const role = view.members?.find((m) => m.pid === me?.pid)?.role;
  return config.membersCanEdit || role === 'admin' || role === 'owner';
}

/**
 * The board feature: keeps the team link in step with the board view. Card extras show the item's
 * assignee, done state and "Create in Jira" on action-items cards of team members' boards.
 */
export const actionItems = {
  board(ctx) {
    const link = teamLink(ctx);
    const release = link.use();
    link.sync(ctx.view());
    return {
      update: (view) => link.sync(view),
      destroy: release,
    };
  },

  card(ctx, initial) {
    const link = teamLink(ctx);
    const release = link.use();
    const assignee = createAssignee(link);
    const done = createDoneToggle(link);
    const jira = createJiraAction(link, link.jiraKeys);
    const el = h('div', { class: 'retro-card-meta retro-action-item', hidden: true }, assignee.el, done.el, jira.el);
    let card = initial;
    let view = ctx.view();

    function paint() {
      // The card's action row has no room for three more controls, so they get a row of their own
      // under the card's meta row.
      const meta = el.closest('.retro-card')?.querySelector(':scope > .retro-card-meta');
      if (meta && el.previousElementSibling !== meta) {
        meta.after(el);
      }
      const column = view?.columns.find((c) => c.id === card.columnId);
      const item = column?.actionItems ? itemOfCard(link.view(), card.id) : null;
      el.hidden = item === null;
      el.closest('.retro-card')?.classList.toggle('retro-card-done', item?.doneAt != null);
      if (!item) {
        return;
      }
      assignee.update(item, view.members ?? []);
      done.update(item);
      const config = link.jira();
      jira.el.hidden = !canWriteJira(config, view, ctx.me());
      jira.update(card, config);
    }

    const unsubscribe = link.subscribe(paint);
    return {
      el,
      update(next, nextView) {
        card = next;
        view = nextView;
        paint();
      },
      destroy() {
        unsubscribe();
        assignee.destroy();
        release();
      },
    };
  },
};
