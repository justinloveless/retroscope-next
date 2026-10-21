import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { http } from '../core/http.js';
import { confirmDialog } from '../ui/confirm.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { emptyState, loading, toastError } from './util.js';
import { topicSummary } from './topics.js';

const timeFormat = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });
const dayFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'long' });
const META_BATCH = 100;

export async function createPokerSession(ctx) {
  try {
    const session = await ctx.api.post('/poker-sessions', {});
    ctx.navigate(`/teams/${ctx.teamId}/poker/${encodeURIComponent(session.code)}`);
  } catch (error) {
    toastError(error, 'The poker session could not be created.');
  }
}

export function createPokerTab(ctx) {
  const list = h('div', { class: 'team-list' });
  const status = h('div');
  const more = createButton({ label: 'Load more', variant: 'secondary', onClick: () => load(cursor) });
  more.el.hidden = true;
  const el = h('section', { class: 'team-section', 'aria-label': 'Poker sessions' }, status, list, more.el);

  let sessions = [];
  let cursor = null;
  let token = 0;
  let reloadTimer = 0;
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

  function render() {
    more.el.hidden = cursor === null;
    if (sessions.length === 0 && status.childElementCount === 0) {
      replaceContent(status, emptyState('spade', 'No poker sessions yet', 'Start a session to point stories with your team.',
        createButton({ label: 'New poker session', icon: 'plus', variant: 'emerald', onClick: () => createPokerSession(ctx) }).el));
    }
    reconcile(list, sessions, (s) => s.id, (session) => {
      const card = h('article', { class: 'card team-card team-session', dataset: { id: session.id } });
      updateCard(card, session);
      return card;
    }, updateCard);
  }

  function updateCard(card, session) {
    const live = ctx.room.view?.poker[session.id];
    const pointed = live?.pointed ?? session.pointed;
    const total = live?.total ?? session.total;
    const named = new Map([...summaries].filter(([, summary]) => summary));
    const topics = topicSummary(session.parentKeys, named);
    replaceContent(card,
      h('span', { class: 'team-session-icon', 'aria-hidden': 'true' }, icon('spade')),
      h('div', { class: 'team-session-body' },
        h('h3', { class: 'team-card-title' }, icon('calendar'), ` ${dayFormat.format(session.createdAt)}`),
        h('p', { class: 'team-muted team-session-progress' },
          icon('hash'), ` ${pointed}/${total} ${total === 1 ? 'story' : 'stories'} pointed · Started ${timeFormat.format(session.createdAt)}`),
        topics ? h('div', { class: 'team-topics' }, topics.kind === 'various'
          ? h('span', { class: ['team-topic', `team-tone-${topics.tone}`] }, 'various topics')
          : topics.topics.map((t) => h('span', {
            class: ['team-topic', `team-tone-${t.tone}`],
            title: t.key ?? null,
            dataset: { key: t.key ?? undefined },
          }, t.label))) : null),
      h('div', { class: 'team-session-actions' },
        ctx.isAdmin()
          ? createButton({ label: 'Delete poker session', icon: 'trash', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => remove(session) }).el
          : null,
        h('a', { class: 'btn btn-outline btn-sm', href: `/teams/${ctx.teamId}/poker/${encodeURIComponent(session.code)}`, 'data-nav': '' }, 'Join')));
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

  // Progress notices patch the counts in place; a session the list does not hold, or one whose
  // round count changed (its topics may have too), is picked up by a reload.
  const stop = ctx.room.select((s) => s?.poker, Object.is, (poker) => {
    const stale = Object.entries(poker ?? {}).some(([id, p]) => {
      const session = sessions.find((s) => s.id === id);
      return !session || session.total !== p.total;
    });
    if (stale) {
      clearTimeout(reloadTimer);
      reloadTimer = setTimeout(() => load(null), 300);
    }
    render();
  });
  load(null);

  return {
    el,
    teamChanged: resolveSummaries,
    destroy() {
      stop();
      clearTimeout(reloadTimer);
      token++;
      el.remove();
    },
  };
}
