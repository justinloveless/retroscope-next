// The team's Poker section (UX18 decision 11): "Start poker session", the latest session highlighted, then the
// earlier ones. Sessions never close and nothing records that one is in progress, so the list says when a
// session was last used and never that it is live.

import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { http } from '../core/http.js';
import { timeAgo } from '../core/fmt.js';
import { formatDay } from '../core/day.js';
import { confirmDialog } from '../ui/confirm.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { emptyState, loading, toastError } from './util.js';
import { topicSummary } from './topics.js';

const META_BATCH = 100;
const CLOCK_MS = 30_000;

export const sessionPath = (teamId, code) => `/teams/${encodeURIComponent(teamId)}/poker/${encodeURIComponent(code)}`;

export async function createPokerSession(ctx) {
  try {
    const session = await ctx.api.post('/poker-sessions', {});
    ctx.navigate(sessionPath(ctx.teamId, session.code));
  } catch (error) {
    toastError(error, 'The poker session could not be created.');
  }
}

/** "4 of 9 stories pointed" (`short`: "4 of 9 pointed"); a session with no stories yet says so. */
export function progressText(pointed, total, short = false) {
  if (total === 0) {
    return 'No stories yet';
  }
  return `${pointed} of ${total} ${short ? '' : total === 1 ? 'story ' : 'stories '}pointed`;
}

/** When a session was last used: the list's lastActivityAt, or a later notice from the team room, else its creation. */
export function lastActivity(session, touched = 0) {
  return Math.max(session.lastActivityAt ?? session.createdAt, touched);
}

/** The list is newest first: its first session is the latest, the rest are earlier. */
export function splitLatest(sessions) {
  return { latest: sessions[0] ?? null, earlier: sessions.slice(1) };
}

/** A session's topics as one line: the parents' names, or "various topics"; empty without real tickets. */
export function topicsLine(topics) {
  if (!topics) {
    return '';
  }
  return topics.kind === 'various' ? 'various topics' : topics.topics.map((t) => t.label).join(' · ');
}

export function createPokerTab(ctx) {
  const start = createButton({ label: 'Start poker session', icon: 'plus', variant: 'emerald', size: 'lg', onClick: () => createPokerSession(ctx) });
  const latestSlot = h('div');
  const earlierTitle = h('h2', { class: 'team-section-title', text: 'Earlier sessions', hidden: true });
  const list = h('ul', { class: 'card team-session-rows', 'aria-label': 'Earlier sessions', hidden: true });
  const status = h('div');
  const more = createButton({ label: 'Load more', variant: 'secondary', onClick: () => load(cursor) });
  more.el.hidden = true;
  const el = h('section', { class: 'team-section', 'aria-label': 'Poker sessions' },
    h('div', { class: 'team-toolbar' }, h('h1', { class: 'team-title', text: 'Poker' }), start.el),
    status, latestSlot, earlierTitle, list, more.el);

  let sessions = [];
  let cursor = null;
  let token = 0;
  let reloadTimer = 0;
  // The team room's progress notices ({pointed, total} by session id), which are newer than the list.
  let live = ctx.room.view?.poker ?? {};
  // Session id → when a notice for it arrived: activity the list fetched before it does not know of.
  const touched = new Map();
  // Parent key → summary, or null once Jira could not name it (the badge then shows the key).
  const summaries = new Map();

  async function load(after) {
    const run = ++token;
    if (after === null && sessions.length === 0) {
      replaceContent(status, loading('Loading sessions…'));
    }
    try {
      const page = await ctx.api.get(`/poker-sessions${after ? `?cursor=${encodeURIComponent(after)}` : ''}`);
      if (run !== token) {
        return;
      }
      sessions = after === null ? page.items : [...sessions, ...page.items.filter((s) => !sessions.some((x) => x.id === s.id))];
      cursor = page.nextCursor ?? null;
      status.replaceChildren();
      render();
      resolveSummaries();
    } catch (error) {
      if (run === token) {
        status.replaceChildren();
        toastError(error, 'The poker sessions could not be loaded.');
      }
    }
  }

  async function resolveSummaries() {
    if (!ctx.team()?.jira?.configured) {
      return;
    }
    const wanted = [...new Set(sessions.flatMap((s) => s.parentKeys.map((p) => p.key?.trim()).filter(Boolean)))]
      .filter((key) => !summaries.has(key));
    if (wanted.length === 0) {
      return;
    }
    for (const key of wanted) {
      summaries.set(key, null);
    }
    for (let i = 0; i < wanted.length; i += META_BATCH) {
      try {
        const { items } = await ctx.api.post('/jira/meta', { keys: wanted.slice(i, i + META_BATCH) });
        for (const item of items) {
          summaries.set(item.key, item.summary ?? null);
        }
      } catch {
        // Jira unreachable or not configured after all: the badges keep their keys.
      }
    }
    render();
  }

  function counts(session) {
    const notice = live[session.id];
    return { pointed: notice?.pointed ?? session.pointed, total: notice?.total ?? session.total };
  }

  function topicsOf(session) {
    return topicSummary(session.parentKeys, new Map([...summaries].filter(([, summary]) => summary)));
  }

  function deleteButton(session) {
    return ctx.isAdmin()
      ? createButton({
        label: `Delete the session of ${formatDay(session.createdAt)}`,
        icon: 'trash',
        iconOnly: true,
        variant: 'ghost',
        size: 'icon',
        onClick: () => remove(session),
      }).el
      : null;
  }

  function renderLatest() {
    const session = sessions[0];
    if (!session) {
      latestSlot.replaceChildren();
      return;
    }
    const { pointed, total } = counts(session);
    const topics = topicsOf(session);
    replaceContent(latestSlot, h('article', { class: 'team-latest', dataset: { id: session.id } },
      h('span', { class: 'team-latest-icon', 'aria-hidden': 'true' }, icon('spade')),
      h('div', { class: 'team-latest-body' },
        h('div', { class: 'team-latest-heading' },
          h('span', { class: 'team-eyebrow', text: 'Latest session' }),
          h('h2', { class: 'team-latest-title', text: formatDay(session.createdAt) })),
        h('p', { class: 'team-latest-progress', text: `${progressText(pointed, total)} · last activity ${timeAgo(lastActivity(session, touched.get(session.id)))}` }),
        topics ? h('div', { class: 'team-topics' }, topics.kind === 'various'
          ? h('span', { class: ['team-topic', `team-tone-${topics.tone}`] }, 'various topics')
          : topics.topics.map((t) => h('span', {
            class: ['team-topic', `team-tone-${t.tone}`],
            title: t.key ?? null,
            dataset: { key: t.key ?? undefined },
          }, t.label))) : null),
      h('div', { class: 'team-session-actions' },
        deleteButton(session),
        h('a', { class: 'btn btn-emerald btn-lg', href: sessionPath(ctx.teamId, session.code), 'data-nav': '' }, 'Join'))));
  }

  function updateRow(row, session) {
    const { pointed, total } = counts(session);
    replaceContent(row,
      h('a', { class: 'team-session-link', href: sessionPath(ctx.teamId, session.code), 'data-nav': '' },
        h('span', { class: 'team-session-icon', 'aria-hidden': 'true' }, icon('spade')),
        h('span', { class: 'team-session-day', text: formatDay(session.createdAt) }),
        h('span', { class: 'team-session-progress', text: progressText(pointed, total, true) }),
        h('span', { class: 'team-session-topics', text: topicsLine(topicsOf(session)) }),
        icon('chevron-right')),
      deleteButton(session));
  }

  function render() {
    more.el.hidden = cursor === null;
    if (sessions.length === 0 && status.childElementCount === 0) {
      replaceContent(status, emptyState('spade', 'No poker sessions yet', 'Start a session to point stories with your team.'));
    } else if (sessions.length > 0 && status.querySelector('.team-empty')) {
      status.replaceChildren();
    }
    const { earlier } = splitLatest(sessions);
    renderLatest();
    earlierTitle.hidden = earlier.length === 0;
    list.hidden = earlier.length === 0;
    reconcile(list, earlier, (s) => s.id, (session) => {
      const row = h('li', { class: 'team-session-row', dataset: { id: session.id } });
      updateRow(row, session);
      return row;
    }, updateRow);
  }

  async function remove(session) {
    const ok = await confirmDialog({
      title: 'Delete this poker session?',
      message: 'This removes the whole session with all its rounds. Anyone in the room loses access. This cannot be undone.',
      confirmLabel: 'Delete session',
      destructive: true,
    });
    if (!ok) {
      return;
    }
    try {
      await http.del(`/api/poker/${session.id}`);
      sessions = sessions.filter((s) => s.id !== session.id);
      render();
      toast('Poker session deleted.', { variant: 'success', duration: 3000 });
    } catch (error) {
      toastError(error, 'The session could not be deleted.');
    }
  }

  // Progress notices patch the counts in place and mark the session as used just now; a session the list does
  // not hold, or one whose round count changed (its topics may have too), is picked up by a reload.
  const stop = ctx.room.select((s) => s?.poker, Object.is, (poker) => {
    const next = poker ?? {};
    const now = Date.now();
    for (const [id, progress] of Object.entries(next)) {
      if (live[id] !== progress) {
        touched.set(id, now);
      }
    }
    live = next;
    const stale = Object.entries(live).some(([id, p]) => {
      const session = sessions.find((s) => s.id === id);
      return !session || session.total !== p.total;
    });
    if (stale) {
      clearTimeout(reloadTimer);
      reloadTimer = setTimeout(() => load(null), 300);
    }
    render();
  });
  // "last activity 3 minutes ago" moves on while the page stays open.
  const clock = setInterval(renderLatest, CLOCK_MS);
  load(null);

  return {
    el,
    teamChanged: resolveSummaries,
    destroy() {
      stop();
      clearTimeout(reloadTimer);
      clearInterval(clock);
      token++;
      el.remove();
    },
  };
}
