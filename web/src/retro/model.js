// Board state for the RoomStore (DESIGN §g.4). snapshot(d, o) and apply(state, event) mirror the server's
// projection (RetroViewerModel in the C# tests), so folding tx frames reaches what a fresh snapshot shows;
// toWire(state) writes it back in the server's canonical order for the golden transcripts. States are
// never mutated: every event copies what it changes.

const compareIds = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
const byCreated = (a, b) => a.createdAt - b.createdAt || compareIds(a.id, b.id);

function withoutMine(entity) {
  const { mine, ...rest } = entity;
  return rest;
}

function toggled(set, id, member) {
  if (member == null || set.has(id) === member) {
    return set;
  }
  const next = new Set(set);
  if (member) {
    next.add(id);
  } else {
    next.delete(id);
  }
  return next;
}

function withEntry(map, key, value) {
  const next = new Map(map);
  next.set(key, value);
  return next;
}

function withoutEntry(map, key) {
  if (!map.has(key)) {
    return map;
  }
  const next = new Map(map);
  next.delete(key);
  return next;
}

// An idle timer carries no instant: `endsAt` on an expiry is a one-shot signal, not state (U8 decision).
function normalizeTimer(timer) {
  if (timer.state !== 'idle') {
    return timer;
  }
  return { state: 'idle', durationMs: timer.durationMs, alarm: timer.alarm, music: timer.music };
}

export function snapshot(d, o) {
  return {
    boardId: d.boardId,
    code: d.code,
    teamId: d.teamId ?? null,
    meta: d.meta,
    config: d.config,
    stage: d.stage,
    timer: normalizeTimer(d.timer),
    expiredAt: null,
    columns: d.columns,
    cards: new Map(d.cards.map((card) => [card.id, withoutMine(card)])),
    comments: new Map(d.comments.map((comment) => [comment.id, withoutMine(comment)])),
    votes: new Map(d.votes.map((vote) => [vote.cardId, withoutMine(vote)])),
    readiness: d.readiness,
    presence: new Map(d.presence.map((p) => [p.pid, p])),
    focus: d.focus ?? null,
    members: d.members ?? null,
    endorsements: d.endorsements ? new Map(d.endorsements.map((e) => [e.id, e])) : null,
    endorsementTypes: d.endorsementTypes ?? null,
    maxEndorsements: d.maxEndorsements ?? null,
    celebrate: null,
    mineCards: new Set(o?.mineCards ?? []),
    mineComments: new Set(o?.mineComments ?? []),
    myVotes: new Set(o?.myVotes ?? []),
  };
}

function removeCard(state, cardId) {
  let { comments, mineComments } = state;
  for (const comment of state.comments.values()) {
    if (comment.cardId === cardId) {
      comments = withoutEntry(comments, comment.id);
      mineComments = toggled(mineComments, comment.id, false);
    }
  }
  return {
    ...state,
    cards: withoutEntry(state.cards, cardId),
    votes: withoutEntry(state.votes, cardId),
    mineCards: toggled(state.mineCards, cardId, false),
    myVotes: toggled(state.myVotes, cardId, false),
    comments,
    mineComments,
  };
}

function reorderColumns(columns, order) {
  const byId = new Map(columns.map((c) => [c.id, c]));
  const listed = order.filter((id) => byId.has(id)).map((id) => byId.get(id));
  const rest = columns.filter((c) => !order.includes(c.id));
  return [...listed, ...rest];
}

function setVote(state, cardId, count, voters, mine) {
  const vote = voters ? { cardId, count, voters } : { cardId, count };
  return {
    ...state,
    votes: count === 0 ? withoutEntry(state.votes, cardId) : withEntry(state.votes, cardId, vote),
    myVotes: toggled(state.myVotes, cardId, mine),
  };
}

/** Folds one wire event `{k, d}`; added events are upserts by id (U8 decision 15). */
export function apply(state, { k, d }) {
  switch (k) {
    case 'retro.card.added':
      return {
        ...state,
        cards: withEntry(state.cards, d.card.id, withoutMine(d.card)),
        mineCards: toggled(state.mineCards, d.card.id, d.card.mine),
      };
    case 'retro.card.edited': {
      const card = state.cards.get(d.cardId);
      return card ? { ...state, cards: withEntry(state.cards, d.cardId, { ...card, body: d.body, version: d.version }) } : state;
    }
    case 'retro.card.deleted':
      return removeCard(state, d.cardId);
    case 'retro.vote':
      return setVote(state, d.cardId, d.count, d.voters, d.mine);
    case 'retro.comment.added':
      return {
        ...state,
        comments: withEntry(state.comments, d.comment.id, withoutMine(d.comment)),
        mineComments: toggled(state.mineComments, d.comment.id, d.comment.mine),
      };
    case 'retro.comment.deleted':
      return {
        ...state,
        comments: withoutEntry(state.comments, d.commentId),
        mineComments: toggled(state.mineComments, d.commentId, false),
      };
    case 'retro.column.added':
    case 'retro.column.updated': {
      const index = state.columns.findIndex((c) => c.id === d.column.id);
      const columns = index < 0 ? [...state.columns, d.column] : state.columns.map((c, i) => (i === index ? d.column : c));
      return { ...state, columns };
    }
    case 'retro.column.deleted': {
      let next = { ...state, columns: state.columns.filter((c) => c.id !== d.columnId) };
      for (const card of state.cards.values()) {
        if (card.columnId === d.columnId) {
          next = removeCard(next, card.id);
        }
      }
      return next;
    }
    case 'retro.column.order':
      return { ...state, columns: reorderColumns(state.columns, d.order) };
    case 'retro.column.actionItems':
      return { ...state, columns: state.columns.map((c) => ({ ...c, actionItems: c.id === d.columnId })) };
    case 'retro.board.meta':
      return { ...state, meta: d };
    case 'retro.config':
      return { ...state, config: d.config };
    case 'retro.stage':
      return { ...state, stage: d.stage };
    case 'retro.readiness':
      return { ...state, readiness: d };
    case 'retro.focus':
      return { ...state, focus: d.cardId ?? null };
    case 'retro.timer':
      return { ...state, timer: normalizeTimer(d), expiredAt: d.expired ? d.endsAt ?? null : state.expiredAt };
    case 'retro.endorsement.added':
      return {
        ...state,
        endorsements: withEntry(state.endorsements ?? new Map(), d.endorsement.id, d.endorsement),
        celebrate: d.celebrate ? d.endorsement.id : state.celebrate,
      };
    case 'retro.endorsement.removed':
      return state.endorsements ? { ...state, endorsements: withoutEntry(state.endorsements, d.endorsementId) } : state;
    case 'retro.members':
      return { ...state, members: d.members };
    case 'team.settings':
      return d.maxEndorsements == null ? state : { ...state, maxEndorsements: d.maxEndorsements };
    case 'presence.join':
      return { ...state, presence: withEntry(state.presence, d.pid, d) };
    case 'presence.update': {
      const present = state.presence.get(d.pid);
      if (!present) {
        return state;
      }
      const { avatar, ...rest } = present;
      return { ...state, presence: withEntry(state.presence, d.pid, { ...rest, ...d }) };
    }
    case 'presence.leave':
      return { ...state, presence: withoutEntry(state.presence, d.pid) };
    default:
      return state;
  }
}

function authorOf(state, me, anonymous) {
  if (anonymous || !state.config.showAuthorNames || !me) {
    return null;
  }
  const author = { name: me.name, guest: me.kind === 'guest', pid: me.pid };
  return me.avatar ? { ...author, avatar: me.avatar } : author;
}

function post(state, me, now, fields) {
  const author = authorOf(state, me, fields.anonymous);
  return author ? { ...fields, createdAt: now, author } : { ...fields, createdAt: now };
}

/**
 * The optimistic effect of a command: what the view shows until the server's tx arrives. `me` is the
 * principal sending it and `now` the corrected server time, for the sort order of new posts.
 */
export function intent(state, k, d, { me, now }) {
  switch (k) {
    case 'retro.card.add':
      return apply(state, {
        k: 'retro.card.added',
        d: { card: { ...post(state, me, now, { id: d.cardId, columnId: d.columnId, body: d.body, anonymous: d.anonymous, version: 1 }), mine: true } },
      });
    case 'retro.card.edit': {
      const card = state.cards.get(d.cardId);
      return card ? apply(state, { k: 'retro.card.edited', d: { cardId: d.cardId, body: d.body, version: card.version + 1 } }) : state;
    }
    case 'retro.card.delete':
      return removeCard(state, d.cardId);
    case 'retro.vote.set': {
      if (!state.cards.has(d.cardId) || state.myVotes.has(d.cardId) === d.on) {
        return state;
      }
      const vote = state.votes.get(d.cardId);
      const count = Math.max(0, (vote?.count ?? 0) + (d.on ? 1 : -1));
      const voters = vote?.voters && me
        ? (d.on ? [...vote.voters, me.pid].sort(compareIds) : vote.voters.filter((pid) => pid !== me.pid))
        : vote?.voters;
      return setVote(state, d.cardId, count, voters, d.on);
    }
    case 'retro.comment.add':
      return apply(state, {
        k: 'retro.comment.added',
        d: { comment: { ...post(state, me, now, { id: d.commentId, cardId: d.cardId, body: d.body, anonymous: d.anonymous }), mine: true } },
      });
    case 'retro.comment.delete':
      return apply(state, { k: 'retro.comment.deleted', d: { commentId: d.commentId } });
    case 'retro.column.reorder':
      return { ...state, columns: reorderColumns(state.columns, d.order) };
    case 'retro.ready.set': {
      if (d.stage !== state.readiness.stage || !me) {
        return state;
      }
      const others = state.readiness.ready.filter((pid) => pid !== me.pid);
      const ready = d.ready ? [...others, me.pid].sort(compareIds) : others;
      return { ...state, readiness: { stage: d.stage, ready } };
    }
    case 'retro.focus.set':
      return { ...state, focus: d.cardId ?? null };
    default:
      return state;
  }
}

/**
 * The RoomStore model. `me()` and `now()` are read when an intent is re-derived for an outbox entry
 * restored after a reload.
 */
export function createRetroModel({ me = () => null, now = () => Date.now() } = {}) {
  return {
    snapshot,
    apply,
    optimistic: (state, k, d) => intent(state, k, d, { me: me(), now: now() }),
  };
}

/** The state as `{d, o}` in the server's canonical order (cards and comments by createdAt, then id). */
export function toWire(state) {
  const cards = [...state.cards.values()].sort(byCreated);
  const comments = [...state.comments.values()].sort(byCreated);
  const d = {
    boardId: state.boardId,
    code: state.code,
    meta: state.meta,
    config: state.config,
    stage: state.stage,
    timer: state.timer,
    columns: state.columns,
    cards,
    comments,
    votes: cards.filter((c) => state.votes.has(c.id)).map((c) => state.votes.get(c.id)),
    readiness: state.readiness,
    presence: [...state.presence.values()],
  };
  const optional = {
    teamId: state.teamId,
    focus: state.focus,
    members: state.members,
    endorsements: state.endorsements && [...state.endorsements.values()].sort(byCreated),
    endorsementTypes: state.endorsementTypes,
    maxEndorsements: state.maxEndorsements,
  };
  for (const [key, value] of Object.entries(optional)) {
    if (value != null) {
      d[key] = value;
    }
  }
  const o = {
    mineCards: cards.map((c) => c.id).filter((id) => state.mineCards.has(id)),
    mineComments: comments.map((c) => c.id).filter((id) => state.mineComments.has(id)),
    myVotes: cards.map((c) => c.id).filter((id) => state.myVotes.has(id)),
  };
  return { d, o };
}

// Roles and stage gates as the decider applies them (RetroRoles, StageRules); the server stays the
// authority, these only decide what the page offers.

export function isMember(state, pid) {
  return state.members?.some((m) => m.pid === pid) ?? false;
}

export function isFacilitator(state, me) {
  if (!me || me.kind !== 'user') {
    return false;
  }
  return state.teamId ? isMember(state, me.pid) : true;
}

export function allowsContent(state, column) {
  if (state.meta.archived) {
    return false;
  }
  if (!state.config.stagesEnabled || state.stage === 'thinking') {
    return true;
  }
  return state.stage === 'discussing' && column?.actionItems === true;
}

export function allowsVoting(state) {
  if (state.meta.archived || !state.config.votingEnabled) {
    return false;
  }
  return !state.config.stagesEnabled || state.stage === 'voting';
}

export function mayPostAnonymously(state, me) {
  return state.config.allowAnonymous && me?.kind === 'user';
}

export function votesLeft(state) {
  return state.config.maxVotes == null ? Infinity : Math.max(0, state.config.maxVotes - state.myVotes.size);
}

export function authorName(entity) {
  return entity.author?.name ?? (entity.anonymous ? 'Anonymous' : '');
}

export function defaultSort(config) {
  return config.sortChronologically ? 'time-asc' : 'votes-desc';
}

/** Port of sortItems (RetroColumn.tsx:50-64): items are `{votes, createdAt, author}`; the sort is stable. */
export function sortItems(items, key) {
  const compare = {
    'votes-desc': (a, b) => (b.votes !== a.votes ? b.votes - a.votes : a.createdAt - b.createdAt),
    'votes-asc': (a, b) => (a.votes !== b.votes ? a.votes - b.votes : a.createdAt - b.createdAt),
    'time-asc': (a, b) => a.createdAt - b.createdAt,
    'time-desc': (a, b) => b.createdAt - a.createdAt,
    'author-asc': (a, b) => (a.author || '').localeCompare(b.author || ''),
    'author-desc': (a, b) => (b.author || '').localeCompare(a.author || ''),
  }[key] ?? (() => 0);
  return [...items].sort(compare);
}

export function sortedCards(state, columnId, key) {
  const items = [];
  for (const card of state.cards.values()) {
    if (card.columnId === columnId) {
      items.push({ card, votes: state.votes.get(card.id)?.count ?? 0, createdAt: card.createdAt, author: authorName(card) });
    }
  }
  return sortItems(items.sort((a, b) => byCreated(a.card, b.card)), key).map((item) => item.card);
}

export function commentsOf(state, cardId) {
  return [...state.comments.values()].filter((c) => c.cardId === cardId).sort(byCreated);
}

/** A time-ordered UUIDv7 for client-minted ids (DESIGN §c.4). */
export function uuidv7(now = Date.now()) {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  let ms = now;
  for (let i = 5; i >= 0; i--) {
    bytes[i] = ms % 256;
    ms = Math.floor(ms / 256);
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x70;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
