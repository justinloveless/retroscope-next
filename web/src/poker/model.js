// Poker room state on the client: the fold of the server's snapshot and tx events (the rules in
// docs/decisions/U7.md, mirrored from the C# ViewerModel), the optimistic intents, the pointer
// navigation rule (R13) and the read-only selectors the table renders from. Every function here is pure.

export const DECK = Object.freeze([1, 2, 3, 5, 8, 13, 21]);

const NO_NAV = Object.freeze({ roundId: null });
const SPOTLIGHT_CLAIMS = new Set(['poker.spotlight.take', 'poker.spotlight.move']);
const NO_CLAIM = () => false;
const INTERNAL = new Set(['me', 'votes', 'own', 'submits', 'presence', 'heldByThisTab', 'deleted', 'nav']);

// Pids and round ids compare as lower-case strings, ordinally (the server's canonical order).
function byPid(a, b) {
  const x = a.pid.toLowerCase();
  const y = b.pid.toLowerCase();
  return x < y ? -1 : x > y ? 1 : 0;
}

function without(map, key) {
  if (!map || !(key in map)) {
    return map;
  }
  const { [key]: _, ...rest } = map;
  return rest;
}

function compact(object) {
  const out = {};
  for (const [key, value] of Object.entries(object)) {
    if (value !== null && value !== undefined) {
      out[key] = value;
    }
  }
  return out;
}

function indexById(list, key) {
  const map = {};
  for (const item of list) {
    (map[item.roundId] ??= {})[item[key]] = item;
  }
  return map;
}

/** State from a `snap` frame's `d` and `o` for the viewer `me` (a pid or null). */
export function fromSnapshot(d, o = {}, me = null) {
  const { votes = [], jiraSubmits = [], presence = [], ...rest } = d;
  const own = {};
  for (const vote of o.own ?? []) {
    own[vote.roundId] = vote;
  }
  const submits = {};
  for (const submit of jiraSubmits) {
    submits[submit.roundId] = submit;
  }
  return {
    observers: [],
    rounds: [],
    roster: [],
    pointDescriptions: {},
    ...rest,
    me,
    votes: indexById(votes, 'pid'),
    own,
    submits,
    presence: [...presence].sort(byPid),
    heldByThisTab: o.heldByThisTab ?? false,
    deleted: false,
    nav: NO_NAV,
  };
}

function replaceRound(state, round) {
  const index = state.rounds.findIndex((r) => r.id === round.id);
  if (index < 0) {
    return state;
  }
  const rounds = state.rounds.slice();
  rounds[index] = round;
  return { ...state, rounds };
}

function setVote(state, roundId, pid, vote) {
  return { ...state, votes: { ...state.votes, [roundId]: { ...state.votes[roundId], [pid]: vote } } };
}

function removeRoundData(state, roundId) {
  return {
    ...state,
    votes: without(state.votes, roundId),
    own: without(state.own, roundId),
    submits: without(state.submits, roundId),
  };
}

function applyVoteState(state, d) {
  if (d.status === 'empty') {
    const votes = state.votes[d.roundId];
    const next = votes && d.pid in votes
      ? { ...state, votes: { ...state.votes, [d.roundId]: without(votes, d.pid) } }
      : state;
    return next.own[d.roundId]?.pid === d.pid ? { ...next, own: without(next.own, d.roundId) } : next;
  }
  if (d.status == null) {
    const status = d.locked ? 'locked' : 'selected';
    const next = setVote(state, d.roundId, d.pid, { roundId: d.roundId, pid: d.pid, status });
    return { ...next, own: { ...next.own, [d.roundId]: d } };
  }
  return setVote(state, d.roundId, d.pid, d);
}

function applyRevealed(state, d) {
  const votes = {};
  for (const vote of d.votes ?? []) {
    votes[vote.pid] = compact({
      roundId: d.roundId,
      pid: vote.pid,
      points: vote.points,
      high: vote.high,
      locked: true,
      abstained: vote.abstained,
    });
  }
  const round = state.rounds.find((r) => r.id === d.roundId);
  const next = {
    ...state,
    votes: { ...state.votes, [d.roundId]: votes },
    own: without(state.own, d.roundId),
  };
  if (!round) {
    return next;
  }
  const { winner: _w, revealedAt: _a, revealedBy: _b, ...rest } = round;
  return replaceRound(next, compact({ ...rest, state: 'revealed', winner: d.winner, revealedAt: d.at, revealedBy: d.by }));
}

function applyReplayed(state, d) {
  const votes = state.votes[d.roundId];
  if (!votes) {
    return state;
  }
  const locked = {};
  for (const pid of Object.keys(votes)) {
    locked[pid] = { roundId: d.roundId, pid, status: 'locked' };
  }
  return { ...state, votes: { ...state.votes, [d.roundId]: locked } };
}

function applyDeleted(state, roundId) {
  const next = removeRoundData({ ...state, rounds: state.rounds.filter((r) => r.id !== roundId) }, roundId);
  return state.currentRoundId === roundId ? { ...next, currentRoundId: null } : next;
}

function applyOrder(state, order) {
  const byId = new Map(state.rounds.map((r) => [r.id, r]));
  const rounds = order.map((id) => byId.get(id)).filter(Boolean);
  for (const round of state.rounds) {
    if (!order.includes(round.id)) {
      rounds.push(round);
    }
  }
  return { ...state, rounds };
}

// R13: a change of the spotlight's round moves followers, never the holder. The server binds the tab
// whose take or move produced the event; that tab still has the command in flight, because the ack
// follows its tx frame on the same queue. Any other change to a spotlight I hold keeps the binding,
// except that the spotlight becoming mine without a claim (a give, another tab's take) leaves this tab
// unbound.
function applySpotlight(state, d, claimed) {
  const spotlight = d.holder && d.roundId ? { holder: d.holder, roundId: d.roundId } : null;
  const follow = d.holder
    && d.holder !== state.me
    && d.roundId !== (state.spotlight?.roundId ?? null)
    && state.followSpotlight;
  let heldByThisTab = false;
  if (d.holder && d.holder === state.me) {
    heldByThisTab = claimed(d) || (state.spotlight?.holder === state.me && state.heldByThisTab);
  }
  return {
    ...state,
    spotlight,
    heldByThisTab,
    nav: follow ? { roundId: d.roundId } : state.nav,
  };
}

function upsertPresence(state, info) {
  const presence = state.presence.filter((p) => p.pid !== info.pid);
  presence.push(info);
  return { ...state, presence: presence.sort(byPid) };
}

/** Folds one wire event `{k, d}`. Unknown kinds leave the state alone. */
export function apply(state, event) {
  return fold(state, event, NO_CLAIM);
}

/**
 * True when `pending` (this tab's outbox entries for the room) holds a spotlight take or move for the
 * round a `poker.spotlight` event names, i.e. the event is the result of this tab's own command.
 */
export function claimsSpotlight(pending, d) {
  return pending.some((entry) => SPOTLIGHT_CLAIMS.has(entry.k) && entry.d?.roundId === d.roundId);
}

function fold(state, { k, d = {} }, claimed) {
  switch (k) {
    case 'poker.vote.state':
      return applyVoteState(state, d);
    case 'poker.round.revealed':
      return applyRevealed(state, d);
    case 'poker.round.replayed':
      return applyReplayed(state, d);
    case 'poker.round.created':
      return state.rounds.some((r) => r.id === d.round.id) ? replaceRound(state, d.round) : { ...state, rounds: [...state.rounds, d.round] };
    case 'poker.round.updated':
      return replaceRound(state, d.round);
    case 'poker.round.deleted':
      return applyDeleted(state, d.roundId);
    case 'poker.round.order':
      return applyOrder(state, d.order);
    case 'poker.rounds.reset':
      return { ...state, rounds: [d.round], votes: {}, own: {}, submits: {}, currentRoundId: null };
    case 'poker.pointer':
      return { ...state, currentRoundId: d.roundId ?? null, nav: d.nav ? { roundId: d.roundId } : state.nav };
    case 'poker.spotlight':
      return applySpotlight(state, d, claimed);
    case 'poker.settings':
      return {
        ...state,
        presenceEnabled: d.presenceEnabled ?? state.presenceEnabled,
        followSpotlight: d.followSpotlight ?? state.followSpotlight,
      };
    case 'poker.observers':
      return { ...state, observers: d.ids };
    case 'poker.roster':
      return { ...state, roster: d.seats };
    case 'team.settings':
      return d.pointDescriptions ? { ...state, pointDescriptions: d.pointDescriptions } : state;
    case 'poker.jira.points':
      return d.status === 'ok'
        ? { ...state, submits: without(state.submits, d.roundId) }
        : { ...state, submits: { ...state.submits, [d.roundId]: d } };
    case 'poker.session.deleted':
      return { ...state, deleted: true };
    case 'presence.join':
      return upsertPresence(state, d);
    case 'presence.leave':
      return { ...state, presence: state.presence.filter((p) => p.pid !== d.pid) };
    case 'presence.update': {
      const current = state.presence.find((p) => p.pid === d.pid);
      return current ? upsertPresence(state, { ...current, ...compact({ name: d.name, avatar: d.avatar }) }) : state;
    }
    default:
      return state;
  }
}

export function hasRound(state, id) {
  return id != null && state.rounds.some((r) => r.id === id);
}

function fallbackRound(state) {
  return state.currentRoundId ?? state.rounds[0]?.id ?? null;
}

/**
 * Where a joiner lands: a `?round=N` deep link, else the spotlight round when following someone else's
 * spotlight, else the pointer round.
 */
export function initialRound(state, roundNumber = null) {
  const linked = roundNumber == null ? null : state.rounds.find((r) => r.number === roundNumber);
  if (linked) {
    return linked.id;
  }
  const spot = state.spotlight;
  if (state.followSpotlight && spot && spot.holder !== state.me && hasRound(state, spot.roundId)) {
    return spot.roundId;
  }
  return fallbackRound(state);
}

/**
 * Where the transition from `prev` to `next` sends a viewer looking at `viewed`, or null to stay. A
 * target that no longer exists is dropped; a viewer whose round is gone goes to the pointer round.
 */
export function resolveNav(prev, next, viewed) {
  let target = next.nav !== prev.nav ? next.nav.roundId : null;
  if (!hasRound(next, target)) {
    target = null;
  }
  const current = target ?? viewed;
  if (!hasRound(next, current)) {
    target = fallbackRound(next);
  }
  return target;
}

/** One tx frame's navigation (R13): `viewer` is `{viewed}`. Returns the target round id or null. */
export function navTarget(prev, events, viewer) {
  return resolveNav(prev, events.reduce(apply, prev), viewer.viewed);
}

/** The state in the server's canonical snapshot form (`{d, o}`), for tests and debugging. */
export function toWire(state) {
  const d = {};
  for (const [key, value] of Object.entries(state)) {
    if (!INTERNAL.has(key)) {
      d[key] = value;
    }
  }
  const order = state.rounds.map((r) => r.id);
  const ordered = (map) => order.flatMap((id) => Object.values(map[id] ?? {}).sort(byPid));
  d.votes = ordered(state.votes);
  d.presence = state.presence;
  d.jiraSubmits = order.filter((id) => state.submits[id]).map((id) => state.submits[id]);
  const own = order.filter((id) => state.own[id]).map((id) => state.own[id]);
  return { d: compact(d), o: { own, heldByThisTab: state.heldByThisTab } };
}

// Optimistic intents (§g.4): vote, lock, abstain and ticket edits only.

export function voteIntent(roundId, { points, high, lock }) {
  return (state) => {
    const own = compact({ roundId, pid: state.me, points, high, locked: Boolean(lock), abstained: false });
    return applyVoteState(state, own);
  };
}

export function lockIntent(roundId, locked) {
  return (state) => {
    const own = state.own[roundId];
    return own ? applyVoteState(state, { ...own, locked }) : state;
  };
}

export function abstainIntent(roundId, abstain) {
  return (state) => applyVoteState(state, abstain
    ? { roundId, pid: state.me, locked: true, abstained: true }
    : { roundId, pid: state.me, status: 'empty' });
}

export function ticketIntent(roundId, ticketKey) {
  return (state) => {
    const round = state.rounds.find((r) => r.id === roundId);
    if (!round) {
      return state;
    }
    const { ticketKey: _t, parentKey: _p, ...rest } = round;
    return replaceRound(state, compact({ ...rest, ticketKey, version: round.version + 1 }));
  };
}

/** Re-derives the intent of an outbox entry restored after a reload. */
export function optimistic(state, k, d) {
  switch (k) {
    case 'poker.vote.set':
      return voteIntent(d.roundId, d)(state);
    case 'poker.lock.set':
      return lockIntent(d.roundId, d.locked)(state);
    case 'poker.abstain.set':
      return abstainIntent(d.roundId, d.abstain)(state);
    case 'poker.round.setTicket':
      return ticketIntent(d.roundId, d.ticketKey)(state);
    default:
      return state;
  }
}

/**
 * The RoomStore model (U28 contract) for the viewer `me`. `pending()` returns the room's outbox entries
 * (`room.pending()`); it tells this tab's own spotlight take or move apart from everyone else's.
 */
export function pokerModel(me, { pending = () => [] } = {}) {
  const claimed = (d) => claimsSpotlight(pending(), d);
  return {
    snapshot: (d, o) => fromSnapshot(d, o, me),
    apply: (state, event) => fold(state, event, claimed),
    optimistic,
  };
}

// Selectors

export function roundById(state, id) {
  return state.rounds.find((r) => r.id === id) ?? null;
}

export function isObserver(state, pid = state.me) {
  return state.observers.includes(pid);
}

export function isPresent(state, pid) {
  return !state.presenceEnabled || state.presence.some((p) => p.pid === pid);
}

/**
 * The seats of a round (§c.4): roster ∪ present ∪ voted − observers. Each seat carries the public vote and
 * whether the principal is present.
 */
export function seatsOf(state, roundId) {
  const votes = state.votes[roundId] ?? {};
  const info = new Map();
  const add = (p) => {
    if (p && !info.has(p.pid)) {
      info.set(p.pid, p);
    }
  };
  state.roster.forEach(add);
  state.presence.forEach(add);
  for (const pid of Object.keys(votes)) {
    add(state.roster.find((p) => p.pid === pid) ?? { pid, kind: 'user', name: 'Unknown' });
  }
  const observers = new Set(state.observers);
  return [...info.values()]
    .filter((p) => !observers.has(p.pid))
    .map((p) => ({
      pid: p.pid,
      name: p.name ?? '',
      kind: p.kind ?? 'user',
      avatar: p.avatar ?? null,
      present: isPresent(state, p.pid),
      vote: votes[p.pid] ?? null,
      me: p.pid === state.me,
    }));
}

export function myVote(state, roundId) {
  return state.own[roundId] ?? null;
}

/** 'voting' | 'revealed' | 'pending' | 'inactive' | 'missing' for the round the hand acts on. */
export function roundPhase(round) {
  if (!round) {
    return 'missing';
  }
  if (round.pending) {
    return 'pending';
  }
  if (round.state === 'revealed') {
    return 'revealed';
  }
  return round.active ? 'voting' : 'inactive';
}

export function nextDeckValue(points) {
  const i = DECK.indexOf(points);
  return i >= 0 && i < DECK.length - 1 ? DECK[i + 1] : null;
}

export function voteLabel(points, high) {
  if (points == null) {
    return '';
  }
  return high == null ? `${points} pts` : `${points}–${high}`;
}

/**
 * Revealed votes grouped by value for the stacks: ascending by the low value, a between group after the
 * single value with the same low; abstains are hidden unless everyone abstained.
 */
export function revealGroups(state, roundId) {
  const names = new Map([...state.roster, ...state.presence].map((p) => [p.pid, p.name]));
  const votes = Object.values(state.votes[roundId] ?? {});
  const cast = votes.filter((v) => !v.abstained && v.points != null);
  const shown = cast.length > 0 ? cast : votes;
  const groups = new Map();
  for (const vote of shown) {
    const abstain = vote.abstained || vote.points == null;
    const key = abstain ? 'abstain' : `${vote.points}:${vote.high ?? ''}`;
    let group = groups.get(key);
    if (!group) {
      group = { key, points: abstain ? null : vote.points, high: abstain ? null : vote.high ?? null, abstain, voters: [] };
      groups.set(key, group);
    }
    group.voters.push({ pid: vote.pid, name: names.get(vote.pid) ?? 'Unknown' });
  }
  return [...groups.values()].sort((a, b) => {
    if (a.abstain !== b.abstain) {
      return a.abstain ? 1 : -1;
    }
    return (a.points - b.points) || ((a.high ?? -1) - (b.high ?? -1));
  });
}

const SCALES = [[4, 1.6, 1.4], [6, 1.4, 1.2], [8, 1.2, 1.0], [12, 1.0, 0.8]];

/** Card scale by player count (§g.8): desktop 1.6/1.4/1.2/1.0/0.8, mobile 1.4/1.2/1.0/0.8/0.8. */
export function cardScale(players, mobile = false) {
  for (const [max, desktop, phone] of SCALES) {
    if (players <= max) {
      return mobile ? phone : desktop;
    }
  }
  return 0.8;
}

/** The milliseconds a UUIDv7 starts with, or null for any other id (an imported session's, say). */
export function uuidv7Time(id) {
  const hex = String(id ?? '').replace(/-/g, '');
  return /^[0-9a-f]{12}7[0-9a-f]{19}$/i.test(hex) ? parseInt(hex.slice(0, 12), 16) : null;
}

/** A UUIDv7 for client-minted round ids (§c.4). */
export function uuidv7(now = Date.now(), random = (n) => crypto.getRandomValues(new Uint8Array(n))) {
  const bytes = random(16);
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
