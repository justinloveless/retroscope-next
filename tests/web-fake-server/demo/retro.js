// A demo retro board: the §c.4 board commands decided in memory with the stage gates of StageRules,
// projected per viewer (`mine`, anonymous authors, voter lists, the recipient's celebration), a shared timer
// on the server clock, and simulated teammates who act by stage. Every board belongs to a team, and only the
// team's members act on it (UX18).
import { DemoRoom, nack } from './room.js';
import { authorInfo, personByPid } from './people.js';
import { compact, newId } from './util.js';

const MAX_BODY = 8000;
const STAGES = ['thinking', 'voting', 'discussing', 'closed'];
const TIMER_RESTART_AFTER_MS = 15_000;

export class RetroBoard extends DemoRoom {
  constructor(demo, seed) {
    super(demo, `board:${seed.id}`);
    this.id = seed.id;
    this.code = seed.code;
    this.team = seed.team;
    this.createdBy = seed.createdBy;
    this.createdAt = seed.createdAt;
    this.meta = { title: seed.title, archived: seed.archived ?? false };
    this.archivedAt = seed.archivedAt;
    this.config = { ...seed.config };
    this.stage = seed.stage ?? 'thinking';
    this.timer = { state: 'idle', durationMs: 600_000, alarm: true, music: false, ...seed.timer };
    this.timerEndedAt = 0;
    this.columns = seed.columns.map((c) => ({ version: 1, actionItems: false, ...c }));
    this.cards = new Map();
    this.comments = new Map();
    this.ready = new Set(seed.ready ?? []);
    this.focus = seed.focus ?? null;
    this.endorsements = [...(seed.endorsements ?? [])];
    this.peers = seed.peers ?? [];
    this.facilitator = seed.facilitator ?? null;
    this.pool = [...(seed.pool ?? [])];
    this.commentPool = [...(seed.commentPool ?? [])];
    this.deleted = false;
    for (const card of seed.cards ?? []) {
      this.cards.set(card.id, { version: 1, anonymous: false, ...card, voters: new Set(card.votes ?? []) });
    }
    for (const comment of seed.comments ?? []) {
      this.comments.set(comment.id, { anonymous: false, ...comment });
    }
    for (const peer of this.peers) {
      this.presence.addVirtual(demo.infoOf(peer));
    }
    if (this.timer.state === 'running') {
      this.armTimer();
    }
    const written = [...this.cards.values(), ...this.comments.values(), ...this.endorsements].map((e) => e.createdAt);
    this.lastActivityAt = Math.max(this.createdAt, this.archivedAt ?? 0, ...written);
  }

  // ---- projection -------------------------------------------------------------------------------

  isMember(pid) {
    return Boolean(this.team.roleOf(pid));
  }

  showsVoters() {
    return this.meta.archived || !this.config.stagesEnabled || this.stage === 'discussing' || this.stage === 'closed';
  }

  authorOf(entity) {
    if (entity.anonymous || !this.config.showAuthorNames || !entity.author) {
      return undefined;
    }
    return authorInfo(personByPid(entity.author) ?? this.demo.principal(entity.author) ?? { pid: entity.author, name: 'Someone', kind: 'guest' });
  }

  cardInfo(card, viewer) {
    return compact({
      id: card.id,
      columnId: card.columnId,
      body: card.body,
      anonymous: card.anonymous,
      version: card.version,
      createdAt: card.createdAt,
      author: this.authorOf(card),
      mine: viewer === undefined ? undefined : card.author === viewer,
    });
  }

  commentInfo(comment, viewer) {
    return compact({
      id: comment.id,
      cardId: comment.cardId,
      body: comment.body,
      anonymous: comment.anonymous,
      createdAt: comment.createdAt,
      author: this.authorOf(comment),
      mine: viewer === undefined ? undefined : comment.author === viewer,
    });
  }

  votesOf(card, viewer) {
    return compact({
      cardId: card.id,
      count: card.voters.size,
      voters: this.showsVoters() ? [...card.voters].sort() : undefined,
      mine: viewer === undefined ? undefined : card.voters.has(viewer),
    });
  }

  timerWire() {
    return compact({ ...this.timer });
  }

  get orderedCards() {
    return [...this.cards.values()].sort((a, b) => a.createdAt - b.createdAt || (a.id < b.id ? -1 : 1));
  }

  get orderedComments() {
    return [...this.comments.values()].sort((a, b) => a.createdAt - b.createdAt || (a.id < b.id ? -1 : 1));
  }

  snapshot() {
    return compact({
      boardId: this.id,
      code: this.code,
      meta: { ...this.meta },
      config: { ...this.config },
      stage: this.stage,
      timer: this.timerWire(),
      columns: this.columns.map((c) => ({ ...c })),
      cards: this.orderedCards.map((c) => this.cardInfo(c)),
      comments: this.orderedComments.map((c) => this.commentInfo(c)),
      votes: this.orderedCards.filter((c) => c.voters.size > 0).map((c) => this.votesOf(c)),
      readiness: { stage: this.stage, ready: [...this.ready].sort() },
      presence: this.presence.list(),
      teamId: this.team.id,
      focus: this.focus ?? undefined,
      members: this.team.memberInfos(),
      endorsements: this.endorsements.map((e) => ({ ...e })),
      endorsementTypes: this.team.liveEndorsementTypes(),
      maxEndorsements: this.team.maxEndorsements,
    });
  }

  overlay(viewer) {
    const cards = this.orderedCards;
    return {
      mineCards: cards.filter((c) => c.author === viewer).map((c) => c.id),
      mineComments: this.orderedComments.filter((c) => c.author === viewer).map((c) => c.id),
      myVotes: cards.filter((c) => c.voters.has(viewer)).map((c) => c.id),
    };
  }

  cardAdded(card) {
    return (viewer) => [{ k: 'retro.card.added', d: { card: this.cardInfo(card, viewer) } }];
  }

  commentAdded(comment) {
    return (viewer) => [{ k: 'retro.comment.added', d: { comment: this.commentInfo(comment, viewer) } }];
  }

  voteEvent(card) {
    return (viewer) => [{ k: 'retro.vote', d: this.votesOf(card, viewer) }];
  }

  readinessEvent() {
    return { k: 'retro.readiness', d: { stage: this.stage, ready: [...this.ready].sort() } };
  }

  /** Every voted card again when voter identities appear or disappear. */
  votesIfVisibilityChanged(shownBefore) {
    if (shownBefore === this.showsVoters()) {
      return [];
    }
    return this.orderedCards.filter((c) => c.voters.size > 0).map((c) => this.voteEvent(c));
  }

  // ---- rules ------------------------------------------------------------------------------------

  column(id) {
    return this.columns.find((c) => c.id === id) ?? null;
  }

  allowsContent(column) {
    if (!this.config.stagesEnabled) {
      return true;
    }
    return this.stage === 'thinking' || (this.stage === 'discussing' && column.actionItems);
  }

  allowsVoting() {
    return this.config.votingEnabled && (!this.config.stagesEnabled || this.stage === 'voting');
  }

  isFacilitator(who) {
    return this.isMember(who.pid);
  }

  contentGate(column) {
    if (this.meta.archived) {
      return nack('board_archived');
    }
    if (!column) {
      return nack('not_found');
    }
    return this.allowsContent(column) ? null : nack('stage_forbids');
  }

  body(text) {
    return typeof text === 'string' && text.trim() !== '' && text.length <= MAX_BODY;
  }

  armTimer() {
    this.cancel(this.timerHandle);
    const endsAt = this.timer.endsAt;
    this.timerHandle = this.later(Math.max(0, endsAt - this.demo.now()), () => {
      if (this.timer.state === 'running' && this.timer.endsAt === endsAt) {
        this.timer = { state: 'idle', durationMs: this.timer.durationMs, alarm: this.timer.alarm, music: this.timer.music };
        this.timerEndedAt = this.demo.now();
        this.tx([{ k: 'retro.timer', d: { ...this.timerWire(), endsAt, expired: true } }]);
      }
    });
  }

  timerChanged() {
    if (this.timer.state === 'running') {
      this.armTimer();
    } else {
      this.cancel(this.timerHandle);
    }
    return { events: [{ k: 'retro.timer', d: this.timerWire() }] };
  }

  // ---- commands ---------------------------------------------------------------------------------

  decide(k, d, who) {
    if (this.deleted) {
      return nack('not_found');
    }
    if (!this.isMember(who.pid)) {
      return nack('forbidden');
    }
    const handler = COMMANDS[k];
    if (!handler) {
      return nack('invalid', { msg: 'Not a board command.' });
    }
    return handler.call(this, d, who);
  }

  // ---- simulation -------------------------------------------------------------------------------

  simulatedPeers() {
    return this.peers.filter((p) => this.presence.has(p.pid) && !this.presence.isReal(p.pid));
  }

  simTick(random) {
    if (this.deleted || this.meta.archived || this.presence.realCount() === 0) {
      return;
    }
    const peers = this.simulatedPeers();
    if (peers.length === 0) {
      return;
    }
    const peer = random.pick(peers);
    if (this.stage === 'thinking') {
      this.thinkingStep(peer, random);
    } else if (this.stage === 'voting') {
      this.votingStep(peer, random);
    } else if (this.stage === 'discussing') {
      this.discussingStep(peer, random);
    }
  }

  thinkingStep(peer, random) {
    if (this.pool.length > 0 && random.chance(0.09)) {
      const idea = this.pool.shift();
      this.act(peer, 'retro.card.add', { cardId: newId(), columnId: idea.columnId, body: idea.body, anonymous: Boolean(idea.anonymous) });
    } else if (this.commentPool.length > 0 && this.cards.size > 0 && random.chance(0.025)) {
      const card = random.pick(this.orderedCards);
      this.act(peer, 'retro.comment.add', { commentId: newId(), cardId: card.id, body: this.commentPool.shift(), anonymous: false });
    } else if (!this.ready.has(peer.pid) && [...this.cards.values()].some((c) => c.author === peer.pid) && random.chance(0.02)) {
      this.act(peer, 'retro.ready.set', { stage: this.stage, ready: true });
    }
    const lead = this.facilitator;
    if (this.timer.state === 'idle' && lead && this.simulatedPeers().includes(lead) && this.demo.now() - this.timerEndedAt > TIMER_RESTART_AFTER_MS) {
      this.act(lead, 'retro.timer.start', { durationMs: 600_000, alarm: this.timer.alarm, music: this.timer.music, from: 'idle' });
    }
  }

  votingStep(peer, random) {
    const used = [...this.cards.values()].filter((c) => c.voters.has(peer.pid)).length;
    const left = this.config.maxVotes == null ? 3 - used : this.config.maxVotes - used;
    const choices = this.orderedCards.filter((c) => !c.voters.has(peer.pid) && (this.config.allowSelfVotes || c.author !== peer.pid) && !this.column(c.columnId)?.actionItems);
    if (left > 0 && choices.length > 0 && random.chance(0.14)) {
      this.act(peer, 'retro.vote.set', { cardId: random.pick(choices).id, on: true });
    } else if (left <= 0 && !this.ready.has(peer.pid) && random.chance(0.05)) {
      this.act(peer, 'retro.ready.set', { stage: this.stage, ready: true });
    }
  }

  discussingStep(peer, random) {
    const focused = this.focus && this.cards.get(this.focus);
    const actionColumn = this.columns.find((c) => c.actionItems);
    if (focused && this.commentPool.length > 0 && random.chance(0.03)) {
      this.act(peer, 'retro.comment.add', { commentId: newId(), cardId: focused.id, body: this.commentPool.shift(), anonymous: false });
    } else if (actionColumn && this.pool.length > 0 && random.chance(0.015)) {
      const idea = this.pool.shift();
      this.act(peer, 'retro.card.add', { cardId: newId(), columnId: actionColumn.id, body: idea.body, anonymous: false });
    } else if (random.chance(0.008)) {
      const types = this.team.liveEndorsementTypes();
      const others = this.team.members.filter((m) => m.pid !== peer.pid);
      const given = this.endorsements.filter((e) => e.from === peer.pid).length;
      if (types.length > 0 && others.length > 0 && given < this.team.maxEndorsements) {
        this.act(peer, 'retro.endorse.give', { endorsementId: newId(), toUserId: random.pick(others).pid, typeId: random.pick(types).id });
      }
    }
  }
}

function owns(entity, who) {
  return entity.author === who.pid;
}

const COMMANDS = {
  'retro.card.add'(d, who) {
    const column = this.column(d.columnId);
    const refused = this.contentGate(column);
    if (refused) {
      return refused;
    }
    if (this.cards.has(d.cardId)) {
      return { events: [] };
    }
    if (d.anonymous && (!this.config.allowAnonymous || who.kind !== 'user')) {
      return nack('forbidden', { msg: 'Anonymous cards are off on this board.' });
    }
    if (!this.body(d.body)) {
      return nack(d.body.length > MAX_BODY ? 'too_large' : 'invalid');
    }
    const card = { id: d.cardId, columnId: column.id, body: d.body, anonymous: d.anonymous, version: 1, createdAt: this.demo.now(), author: who.pid, voters: new Set() };
    this.cards.set(card.id, card);
    return {
      events: [this.cardAdded(card)],
      after: () => {
        if (column.actionItems) {
          this.team.addActionItem(this, card);
        }
      },
    };
  },

  'retro.card.edit'(d, who) {
    const card = this.cards.get(d.cardId);
    if (!card) {
      return nack('not_found');
    }
    const refused = this.contentGate(this.column(card.columnId));
    if (refused) {
      return refused;
    }
    if (!owns(card, who)) {
      return nack('forbidden');
    }
    if (!this.body(d.body)) {
      return nack('invalid');
    }
    if (d.baseVersion !== card.version) {
      return nack('stale', { d: { current: { body: card.body, version: card.version } } });
    }
    card.body = d.body;
    card.version += 1;
    return {
      events: [{ k: 'retro.card.edited', d: { cardId: card.id, body: card.body, version: card.version } }],
      after: () => this.team.syncActionText(card),
    };
  },

  'retro.card.delete'(d, who) {
    const card = this.cards.get(d.cardId);
    if (!card) {
      return { events: [] };
    }
    const column = this.column(card.columnId);
    const refused = this.contentGate(column);
    if (refused) {
      return refused;
    }
    if (!owns(card, who) && !(column.actionItems && this.isFacilitator(who))) {
      return nack('forbidden');
    }
    this.cards.delete(card.id);
    for (const comment of [...this.comments.values()].filter((c) => c.cardId === card.id)) {
      this.comments.delete(comment.id);
    }
    const events = [{ k: 'retro.card.deleted', d: { cardId: card.id } }];
    if (this.focus === card.id) {
      this.focus = null;
      events.push({ k: 'retro.focus', d: {} });
    }
    return { events };
  },

  'retro.vote.set'(d, who) {
    const card = this.cards.get(d.cardId);
    if (!card) {
      return nack('not_found');
    }
    if (this.meta.archived) {
      return nack('board_archived');
    }
    if (!this.allowsVoting()) {
      return nack('stage_forbids');
    }
    if (card.voters.has(who.pid) === d.on) {
      return { events: [] };
    }
    if (d.on && !this.config.allowSelfVotes && owns(card, who)) {
      return nack('self_vote');
    }
    const used = [...this.cards.values()].filter((c) => c.voters.has(who.pid)).length;
    if (d.on && this.config.maxVotes != null && used >= this.config.maxVotes) {
      return nack('vote_limit');
    }
    if (d.on) {
      card.voters.add(who.pid);
    } else {
      card.voters.delete(who.pid);
    }
    return { events: [this.voteEvent(card)] };
  },

  'retro.comment.add'(d, who) {
    const card = this.cards.get(d.cardId);
    if (!card) {
      return nack('not_found');
    }
    const refused = this.contentGate(this.column(card.columnId));
    if (refused) {
      return refused;
    }
    if (this.comments.has(d.commentId)) {
      return { events: [] };
    }
    if (d.anonymous && (!this.config.allowAnonymous || who.kind !== 'user')) {
      return nack('forbidden');
    }
    if (!this.body(d.body)) {
      return nack('invalid');
    }
    const comment = { id: d.commentId, cardId: card.id, body: d.body, anonymous: d.anonymous, createdAt: this.demo.now(), author: who.pid };
    this.comments.set(comment.id, comment);
    return {
      events: [this.commentAdded(comment)],
      after: () => this.team.actionCommentsChanged(card),
    };
  },

  'retro.comment.delete'(d, who) {
    const comment = this.comments.get(d.commentId);
    if (!comment) {
      return { events: [] };
    }
    if (!owns(comment, who)) {
      return nack('forbidden');
    }
    const card = this.cards.get(comment.cardId);
    const refused = this.contentGate(this.column(card?.columnId));
    if (refused) {
      return refused;
    }
    this.comments.delete(comment.id);
    return {
      events: [{ k: 'retro.comment.deleted', d: { commentId: comment.id, cardId: comment.cardId } }],
      after: () => card && this.team.actionCommentsChanged(card),
    };
  },

  'retro.column.add'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    if (this.column(d.columnId)) {
      return { events: [] };
    }
    const column = { id: d.columnId, title: d.title.trim() || 'Untitled', color: d.color, actionItems: false, version: 1 };
    this.columns.push(column);
    return { events: [{ k: 'retro.column.added', d: { column: { ...column } } }] };
  },

  'retro.column.update'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    const column = this.column(d.columnId);
    if (!column) {
      return nack('not_found');
    }
    if (d.baseVersion !== column.version) {
      return nack('stale', { d: { current: { ...column } } });
    }
    Object.assign(column, compact({ title: d.title?.trim() || undefined, color: d.color }));
    column.version += 1;
    return { events: [{ k: 'retro.column.updated', d: { column: { ...column } } }] };
  },

  'retro.column.delete'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    const column = this.column(d.columnId);
    if (!column) {
      return { events: [] };
    }
    this.columns = this.columns.filter((c) => c !== column);
    const events = [{ k: 'retro.column.deleted', d: { columnId: column.id } }];
    for (const card of [...this.cards.values()].filter((c) => c.columnId === column.id)) {
      this.cards.delete(card.id);
      if (this.focus === card.id) {
        this.focus = null;
        events.push({ k: 'retro.focus', d: {} });
      }
    }
    return { events };
  },

  'retro.column.reorder'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    const ids = this.columns.map((c) => c.id);
    if (d.order.length !== ids.length || !ids.every((id) => d.order.includes(id))) {
      return nack('stale', { d: { current: ids } });
    }
    this.columns = d.order.map((id) => this.column(id));
    return { events: [{ k: 'retro.column.order', d: { order: d.order } }] };
  },

  'retro.column.setActionItems'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    for (const column of this.columns) {
      column.actionItems = column.id === d.columnId;
    }
    return { events: [{ k: 'retro.column.actionItems', d: compact({ columnId: d.columnId }) }] };
  },

  'retro.board.rename'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    if (d.expected !== this.meta.title) {
      return nack('stale', { d: { current: { title: this.meta.title } } });
    }
    this.meta.title = d.title.trim() || this.meta.title;
    return { events: [{ k: 'retro.board.meta', d: { ...this.meta } }], after: () => this.team.boardsChanged(this) };
  },

  'retro.config.update'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    for (const field of Object.keys(d.patch)) {
      if (d.expected[field] !== this.config[field]) {
        return nack('stale', { d: { current: { ...this.config } } });
      }
    }
    const shownBefore = this.showsVoters();
    const namesBefore = this.config.showAuthorNames;
    Object.assign(this.config, d.patch);
    const events = [{ k: 'retro.config', d: { config: { ...this.config } } }];
    if (namesBefore !== this.config.showAuthorNames) {
      events.push(...this.orderedCards.map((c) => this.cardAdded(c)), ...this.orderedComments.map((c) => this.commentAdded(c)));
    }
    events.push(...this.votesIfVisibilityChanged(shownBefore));
    return { events };
  },

  'retro.stage.set'(d, who) {
    if (!this.isFacilitator(who)) {
      return nack('forbidden');
    }
    if (d.from !== this.stage) {
      return nack('stale', { d: { current: { stage: this.stage } } });
    }
    if (d.to === this.stage) {
      return { events: [] };
    }
    if (this.config.enforceReadiness) {
      const advance = STAGES.indexOf(d.to) === STAGES.indexOf(this.stage) + 1;
      const always = d.to === 'thinking' || (this.stage === 'closed' && d.to === 'discussing');
      if (!advance && !always) {
        return nack('invalid', { msg: 'Move one stage at a time.' });
      }
      if (advance && !this.pids.every((pid) => this.ready.has(pid))) {
        return nack('invalid', { msg: 'Not everyone is ready yet.' });
      }
    }
    const shownBefore = this.showsVoters();
    this.stage = d.to;
    this.ready.clear();
    return { events: [{ k: 'retro.stage', d: { stage: this.stage } }, this.readinessEvent(), ...this.votesIfVisibilityChanged(shownBefore)] };
  },

  'retro.ready.set'(d, who) {
    if (d.stage !== this.stage) {
      return nack('stale', { d: { current: { stage: this.stage } } });
    }
    if (this.ready.has(who.pid) === d.ready) {
      return { events: [] };
    }
    if (d.ready) {
      this.ready.add(who.pid);
    } else {
      this.ready.delete(who.pid);
    }
    return { events: [this.readinessEvent()] };
  },

  'retro.focus.set'(d) {
    if (this.config.stagesEnabled && this.stage !== 'discussing') {
      return nack('stage_forbids');
    }
    if ((d.from ?? null) !== this.focus) {
      return nack('stale', { d: { current: compact({ cardId: this.focus }) } });
    }
    if (d.cardId !== null && !this.cards.has(d.cardId)) {
      return nack('not_found');
    }
    this.focus = d.cardId;
    return { events: [{ k: 'retro.focus', d: compact({ cardId: this.focus }) }] };
  },

  'retro.timer.start'(d, who) {
    if (who.kind !== 'user') {
      return nack('forbidden');
    }
    if (d.from !== this.timer.state) {
      return nack('stale', { d: { current: this.timerWire() } });
    }
    if (d.durationMs < 1000 || d.durationMs > 7_200_000) {
      return nack('invalid');
    }
    this.timer = { state: 'running', durationMs: d.durationMs, alarm: d.alarm, music: d.music, endsAt: this.demo.now() + d.durationMs };
    return this.timerChanged();
  },

  'retro.timer.pause'(d, who) {
    if (who.kind !== 'user') {
      return nack('forbidden');
    }
    if (d.from !== this.timer.state) {
      return nack('stale', { d: { current: this.timerWire() } });
    }
    if (this.timer.state !== 'running') {
      return { events: [] };
    }
    const remainingMs = Math.max(1, this.timer.endsAt - this.demo.now());
    this.timer = { state: 'paused', durationMs: this.timer.durationMs, alarm: this.timer.alarm, music: this.timer.music, remainingMs };
    return this.timerChanged();
  },

  'retro.timer.resume'(d, who) {
    if (who.kind !== 'user') {
      return nack('forbidden');
    }
    if (d.from !== this.timer.state) {
      return nack('stale', { d: { current: this.timerWire() } });
    }
    if (this.timer.state !== 'paused') {
      return { events: [] };
    }
    const { remainingMs, ...rest } = this.timer;
    this.timer = { ...rest, state: 'running', endsAt: this.demo.now() + remainingMs };
    return this.timerChanged();
  },

  'retro.timer.reset'(d, who) {
    if (who.kind !== 'user') {
      return nack('forbidden');
    }
    if (d.from !== this.timer.state) {
      return nack('stale', { d: { current: this.timerWire() } });
    }
    if (this.timer.state === 'idle') {
      return { events: [] };
    }
    this.timer = { state: 'idle', durationMs: this.timer.durationMs, alarm: this.timer.alarm, music: this.timer.music };
    this.timerEndedAt = this.demo.now();
    return this.timerChanged();
  },

  'retro.timer.setFlags'(d, who) {
    if (who.kind !== 'user') {
      return nack('forbidden');
    }
    this.timer = { ...this.timer, alarm: d.alarm ?? this.timer.alarm, music: d.music ?? this.timer.music };
    return { events: [{ k: 'retro.timer', d: this.timerWire() }] };
  },

  'retro.endorse.give'(d, who) {
    if (!this.isMember(d.toUserId)) {
      return nack('forbidden');
    }
    if (d.toUserId === who.pid) {
      return nack('invalid', { msg: 'You cannot endorse yourself.' });
    }
    if (this.endorsements.some((e) => e.id === d.endorsementId)) {
      return { events: [] };
    }
    if (!this.team.liveEndorsementTypes().some((t) => t.id === d.typeId)) {
      return nack('not_found');
    }
    if (this.endorsements.some((e) => e.from === who.pid && e.to === d.toUserId && e.typeId === d.typeId)) {
      return nack('invalid', { msg: 'You already gave that.' });
    }
    if (this.endorsements.filter((e) => e.from === who.pid).length >= this.team.maxEndorsements) {
      return nack('endorse_limit');
    }
    const endorsement = { id: d.endorsementId, typeId: d.typeId, from: who.pid, to: d.toUserId, createdAt: this.demo.now() };
    this.endorsements.push(endorsement);
    return {
      events: [(viewer) => (this.isMember(viewer) ? [{ k: 'retro.endorsement.added', d: compact({ endorsement, celebrate: viewer === endorsement.to || undefined }) }] : [])],
    };
  },

  'retro.endorse.revoke'(d, who) {
    const endorsement = this.endorsements.find((e) => e.id === d.endorsementId);
    if (!endorsement) {
      return { events: [] };
    }
    if (endorsement.from !== who.pid) {
      return nack('forbidden');
    }
    this.endorsements = this.endorsements.filter((e) => e !== endorsement);
    return { events: [(viewer) => (this.isMember(viewer) ? [{ k: 'retro.endorsement.removed', d: { endorsementId: endorsement.id } }] : [])] };
  },
};
