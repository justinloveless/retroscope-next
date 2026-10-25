// A demo team: the team room (members, action items, endorsement types; §c.4 team commands) and the
// HTTP lists of docs/decisions/U30.md that the team pages read, kept consistent with the live rooms.
import { DemoRoom, nack } from './room.js';
import { maskEmail, compact, newId, DAY } from './util.js';

const CLOSED_ITEMS_KEPT_MS = 30 * DAY;

export class Team extends DemoRoom {
  constructor(demo, seed) {
    super(demo, `team:${seed.id}`, { tracksPresence: false });
    this.id = seed.id;
    this.name = seed.name;
    this.description = seed.description;
    this.createdAt = seed.createdAt;
    this.members = seed.members.map(({ person, role }) => ({ pid: person.pid, role }));
    this.pointDescriptions = { ...seed.pointDescriptions };
    this.maxEndorsements = seed.maxEndorsements;
    this.endorsementTypes = seed.endorsementTypes.map((t) => ({ ...t }));
    this.pastEndorsements = seed.pastEndorsements;
    this.templates = seed.templates.map((t) => structuredClone(t));
    this.invites = seed.invites.map((i) => ({ ...i }));
    this.jira = seed.jiraConfigured ? { ...demo.jira.config, configured: true } : { configured: false, status: 'ok', membersCanEdit: true };
    this.items = [];
    this.deleted = false;
  }

  // ---- membership -------------------------------------------------------------------------------

  roleOf(pid) {
    return this.members.find((m) => m.pid === pid)?.role ?? null;
  }

  isAdmin(pid) {
    return ['admin', 'owner'].includes(this.roleOf(pid));
  }

  memberInfos() {
    return this.members.map((m) => {
      const who = this.demo.principal(m.pid);
      return compact({ pid: m.pid, name: who.name, role: m.role, avatar: who.avatar });
    });
  }

  liveEndorsementTypes() {
    return this.endorsementTypes.filter((t) => !t.archived).map((t) => ({ ...t }));
  }

  /** Posts the new roster to this room and to every live room of the team. */
  membersChanged() {
    this.tx([{ k: 'team.members', d: { members: this.memberInfos() } }]);
    for (const session of this.demo.sessionsOf(this)) {
      session.tx([{ k: 'poker.roster', d: { seats: session.roster() } }]);
    }
    for (const board of this.demo.boardsOf(this)) {
      board.tx([(viewer) => (board.isMember(viewer) ? [{ k: 'retro.members', d: { members: this.memberInfos() } }] : [])]);
    }
  }

  // ---- action items -----------------------------------------------------------------------------

  itemInfo(item) {
    return compact({
      id: item.id,
      text: item.text,
      sourceVersion: item.sourceVersion,
      createdAt: item.createdAt,
      commentCount: this.commentCount(item),
      sourceBoardId: item.board?.id,
      sourceCardId: item.cardId,
      assignedTo: item.assignedTo,
      doneAt: item.doneAt,
      doneBy: item.doneBy,
      createdBy: item.createdBy,
    });
  }

  commentCount(item) {
    if (!item.board || !item.cardId) {
      return 0;
    }
    return [...item.board.comments.values()].filter((c) => c.cardId === item.cardId).length;
  }

  item(id) {
    return this.items.find((i) => i.id === id) ?? null;
  }

  addActionItem(board, card) {
    if (this.items.some((i) => i.cardId === card.id)) {
      return;
    }
    const item = { id: newId(), text: card.body, sourceVersion: card.version, createdAt: card.createdAt, board, cardId: card.id, createdBy: card.anonymous ? undefined : card.author };
    this.items.push(item);
    this.tx([{ k: 'team.action.added', d: { item: this.itemInfo(item) } }]);
  }

  syncActionText(card) {
    const item = this.items.find((i) => i.cardId === card.id);
    if (item && card.version > item.sourceVersion) {
      item.text = card.body;
      item.sourceVersion = card.version;
      this.tx([{ k: 'team.action.text', d: { itemId: item.id, text: item.text, sourceVersion: item.sourceVersion } }]);
    }
  }

  actionCommentsChanged(card) {
    const item = this.items.find((i) => i.cardId === card.id);
    if (item) {
      this.tx([{ k: 'team.action.comments', d: { itemId: item.id, count: this.commentCount(item) } }]);
    }
  }

  boardsChanged(board) {
    this.tx([{ k: 'team.boards.changed', d: { boardId: board.id } }]);
  }

  pokerChanged(session) {
    const { pointed, total } = this.sessionCounts(session);
    this.tx([{ k: 'team.poker.changed', d: { sessionId: session.id, pointed, total } }]);
  }

  sessionCounts(session) {
    const rounds = session.rounds.filter((r) => !r.pending);
    return { pointed: rounds.filter((r) => r.state === 'revealed').length, total: rounds.length };
  }

  // ---- room ----------------------------------------------------------------------------------------

  snapshot() {
    const since = this.demo.now() - CLOSED_ITEMS_KEPT_MS;
    return {
      teamId: this.id,
      name: this.name,
      members: this.memberInfos(),
      actionItems: this.items.filter((i) => !i.doneAt || i.doneAt >= since).map((i) => this.itemInfo(i)),
      endorsementTypes: this.endorsementTypes.map((t) => ({ ...t })),
      pointDescriptions: { ...this.pointDescriptions },
      maxEndorsements: this.maxEndorsements,
    };
  }

  overlay() {
    return undefined;
  }

  decide(k, d, who) {
    if (!this.roleOf(who.pid)) {
      return nack('forbidden');
    }
    const item = this.item(d.itemId);
    if (!item) {
      return nack('not_found');
    }
    if (k === 'team.action.assign') {
      if (d.userId !== null && !this.roleOf(d.userId)) {
        return nack('invalid', { msg: 'Assign a team member.' });
      }
      if ((item.assignedTo ?? null) === d.userId) {
        return { events: [] };
      }
      item.assignedTo = d.userId ?? undefined;
    } else if (k === 'team.action.done') {
      if (Boolean(item.doneAt) === d.done) {
        return { events: [] };
      }
      item.doneAt = d.done ? this.demo.now() : undefined;
      item.doneBy = d.done ? who.pid : undefined;
    } else if (k === 'team.action.delete') {
      if (!this.isAdmin(who.pid)) {
        return nack('forbidden');
      }
      this.items = this.items.filter((i) => i !== item);
      return { events: [{ k: 'team.action.deleted', d: { itemId: item.id } }] };
    } else {
      return nack('invalid');
    }
    return { events: [{ k: 'team.action.updated', d: { item: this.itemInfo(item) } }] };
  }

  // ---- HTTP shapes ----------------------------------------------------------------------------------

  info(viewer) {
    const { configured, siteHost, projectKey, boardId, ticketPrefix, status, membersCanEdit } = this.jira;
    return {
      id: this.id,
      name: this.name,
      description: this.description,
      role: this.roleOf(viewer),
      pointDescriptions: { ...this.pointDescriptions },
      maxEndorsements: this.maxEndorsements,
      jira: compact({ configured, siteHost, projectKey, boardId, ticketPrefix, status, membersCanEdit }),
      counts: { boards: this.demo.boardsOf(this).filter((b) => !b.meta.archived).length, members: this.members.length },
    };
  }

  jiraSettings(viewer) {
    if (!this.jira.configured) {
      return { configured: false, membersCanEdit: true, status: 'ok' };
    }
    const settings = { ...this.jira, pointsFieldId: 'customfield_10016', lastCheckedAt: this.demo.now() - 3 * 60_000 };
    if (this.isAdmin(viewer)) {
      settings.emailMasked = maskEmail('jira-bot@demo-retail.example');
      settings.tokenLast4 = 'q7Xz';
    }
    return settings;
  }

  inviteInfo(invite) {
    return compact({
      id: invite.id,
      kind: invite.kind,
      email: invite.email ? maskEmail(invite.email) : undefined,
      role: invite.role,
      url: invite.kind === 'link' ? `${this.demo.publicOrigin()}/invite#t=${invite.token}` : undefined,
      expiresAt: invite.expiresAt,
      createdAt: invite.createdAt,
    });
  }

  /** Open action items, as `me.teams[].openActions` counts them for the header. */
  openActions() {
    return this.items.filter((i) => !i.doneAt).length;
  }

  boardList(archived) {
    return this.demo.boardsOf(this)
      .filter((b) => b.meta.archived === archived)
      .sort((a, b) => b.createdAt - a.createdAt)
      .map((b) => compact({
        id: b.id,
        code: b.code,
        title: b.meta.title,
        archived: b.meta.archived,
        archivedAt: b.archivedAt,
        createdAt: b.createdAt,
        stage: b.stage,
        stagesEnabled: b.config.stagesEnabled,
        cardCount: b.cards.size,
        lastActivityAt: b.lastActivityAt,
      }));
  }

  sessionList() {
    return this.demo.sessionsOf(this)
      .sort((a, b) => b.createdAt - a.createdAt)
      .map((s) => {
        const keys = new Map();
        for (const round of s.rounds.filter((r) => r.ticketKey && !r.pending)) {
          const key = round.parentKey ?? null;
          keys.set(key, (keys.get(key) ?? 0) + 1);
        }
        return {
          id: s.id,
          code: s.code,
          createdAt: s.createdAt,
          ...this.sessionCounts(s),
          parentKeys: [...keys].map(([key, count]) => ({ key, count })),
          lastActivityAt: s.lastActivityAt,
        };
      });
  }

  actionItemList(query) {
    const status = query.get('status') ?? 'open';
    const assignee = query.get('assignee');
    const newest = (query.get('sort') ?? 'newest') === 'newest';
    return this.items
      .filter((i) => status === 'all' || (status === 'done') === Boolean(i.doneAt))
      .filter((i) => !assignee || (assignee === 'unassigned' ? !i.assignedTo : i.assignedTo === assignee))
      .sort((a, b) => (newest ? b.createdAt - a.createdAt : a.createdAt - b.createdAt))
      .map((i) => compact({ ...this.itemInfo(i), boardTitle: i.board?.meta.title }));
  }

  allEndorsements() {
    const live = this.demo.boardsOf(this).flatMap((b) => b.endorsements);
    return [...this.pastEndorsements, ...live];
  }

  leaderboard(period) {
    const days = { '30d': 30, '90d': 90 }[period];
    const since = days ? this.demo.now() - days * DAY : -Infinity;
    const totals = new Map();
    for (const e of this.allEndorsements().filter((x) => x.createdAt >= since && this.roleOf(x.to))) {
      const entry = totals.get(e.to) ?? { pid: e.to, total: 0, counts: {} };
      entry.total += 1;
      entry.counts[e.typeId] = (entry.counts[e.typeId] ?? 0) + 1;
      totals.set(e.to, entry);
    }
    return [...totals.values()]
      .sort((a, b) => b.total - a.total || (a.pid < b.pid ? -1 : 1))
      .map((entry) => {
        const who = this.demo.principal(entry.pid);
        return compact({ ...entry, name: who.name, avatar: who.avatar });
      });
  }
}
