// A demo poker session: the §c.4 poker commands decided in memory, projected per viewer (vote secrecy),
// with auto-reveal and simulated teammates who vote, lock in, move on after a reveal and now and then
// move the spotlight.
import { DECK } from '../../../web/src/poker/model.js';
import { DemoRoom, nack } from './room.js';
import { compact, newId } from './util.js';

const AUTO_REVEAL_DELAY_MS = 600;
const PREVIEW_AFTER_REVEAL_MS = 7_000;
const START_AFTER_PREVIEW_MS = 7_000;
const TICKET = /^[A-Z][A-Z0-9]{0,9}-[1-9][0-9]{0,8}$/;

const nextDeckValue = (points) => DECK[DECK.indexOf(points) + 1];

/** Plurality over deck values; a between vote counts for both ends; ties go to the higher value. */
export function winnerOf(votes) {
  const counts = new Map();
  for (const vote of votes) {
    if (!vote.abstained && vote.points !== undefined) {
      for (const points of vote.high ? [vote.points, vote.high] : [vote.points]) {
        counts.set(points, (counts.get(points) ?? 0) + 1);
      }
    }
  }
  let best = 0;
  let bestCount = 0;
  for (const [points, count] of counts) {
    if (count > bestCount || (count === bestCount && points > best)) {
      best = points;
      bestCount = count;
    }
  }
  return best;
}

export class PokerSession extends DemoRoom {
  /**
   * `seed`: {id, code, team?, createdBy, createdAt, presenceEnabled, followSpotlight, observers, rounds:[{…, votes: {pid: vote}}],
   * currentRoundId, spotlight?, roster, peers, queue} where `peers` are people the simulation plays and `queue` the
   * ticket keys the facilitator brings up next.
   */
  constructor(demo, seed) {
    super(demo, `poker:${seed.id}`);
    this.id = seed.id;
    this.code = seed.code;
    this.team = seed.team ?? null;
    this.createdBy = seed.createdBy;
    this.createdAt = seed.createdAt;
    this.presenceEnabled = seed.presenceEnabled ?? true;
    this.followSpotlight = seed.followSpotlight ?? true;
    this.observers = new Set(seed.observers ?? []);
    this.rounds = (seed.rounds ?? []).map((r) => ({ ...r, votes: new Map(Object.entries(r.votes ?? {})) }));
    this.currentRoundId = seed.currentRoundId ?? this.rounds.at(-1)?.id ?? null;
    this.spotlight = seed.spotlight ? { ...seed.spotlight, client: null } : null;
    this.quickRoster = new Map((seed.roster ?? []).map((who) => [who.pid, who]));
    this.peers = seed.peers ?? [];
    this.facilitator = seed.facilitator ?? null;
    this.queue = [...(seed.queue ?? [])];
    this.estimates = new Map();
    this.deleted = false;
    for (const peer of this.peers) {
      this.presence.addVirtual(demo.infoOf(peer));
    }
  }

  // ---- projection -------------------------------------------------------------------------------

  roundInfo(r) {
    return compact({
      id: r.id,
      number: r.number,
      version: r.version,
      state: r.state,
      active: r.active,
      pending: r.pending,
      autoReveal: r.autoReveal,
      ticketKey: r.ticketKey,
      parentKey: r.parentKey,
      pendingOwner: r.pendingOwner,
      winner: r.state === 'revealed' ? r.winner : undefined,
      revealedAt: r.revealedAt,
      revealedBy: r.revealedBy,
      jira: r.jira,
    });
  }

  ownVote(roundId, pid, vote) {
    return compact({ roundId, pid, points: vote.points, high: vote.high, locked: vote.locked, abstained: vote.abstained });
  }

  statusOf(vote) {
    return !vote ? 'empty' : vote.locked ? 'locked' : 'selected';
  }

  /** The vote.state event: values to the voter's own principal, the status to everyone else. */
  voteEntry(round, pid) {
    const vote = round.votes.get(pid);
    return (viewer) => [{
      k: 'poker.vote.state',
      d: viewer === pid && vote ? this.ownVote(round.id, pid, vote) : { roundId: round.id, pid, status: this.statusOf(vote) },
    }];
  }

  roster() {
    if (this.team) {
      return this.team.members.map((m) => this.demo.infoOf(m)).sort((a, b) => (a.pid < b.pid ? -1 : 1));
    }
    return [...this.quickRoster.values()].map((who) => this.demo.infoOf(who)).sort((a, b) => (a.pid < b.pid ? -1 : 1));
  }

  snapshot() {
    const votes = [];
    for (const round of this.rounds) {
      for (const pid of [...round.votes.keys()].sort()) {
        const vote = round.votes.get(pid);
        votes.push(round.state === 'voting' ? { roundId: round.id, pid, status: this.statusOf(vote) } : this.ownVote(round.id, pid, vote));
      }
    }
    const submits = this.rounds.filter((r) => r.submit).map((r) => r.submit);
    return compact({
      sessionId: this.id,
      code: this.code,
      presenceEnabled: this.presenceEnabled,
      followSpotlight: this.followSpotlight,
      observers: [...this.observers].sort(),
      rounds: this.rounds.map((r) => this.roundInfo(r)),
      votes,
      roster: this.roster(),
      presence: this.presence.list(),
      teamId: this.team?.id,
      createdBy: this.createdBy,
      spotlight: this.spotlight ? { holder: this.spotlight.holder, roundId: this.spotlight.roundId } : undefined,
      currentRoundId: this.currentRoundId,
      pointDescriptions: this.team ? this.team.pointDescriptions : undefined,
      jiraSubmits: submits.length > 0 ? submits : undefined,
    });
  }

  overlay(viewer, conn) {
    const own = this.rounds
      .filter((r) => r.state === 'voting' && r.votes.has(viewer))
      .map((r) => this.ownVote(r.id, viewer, r.votes.get(viewer)));
    const held = this.spotlight?.holder === viewer && this.spotlight.client !== null && this.spotlight.client === conn?.cid;
    return { own, heldByThisTab: held };
  }

  onSubscribe(conn) {
    super.onSubscribe(conn);
    if (conn.me && !this.team && !this.quickRoster.has(conn.me.pid)) {
      this.quickRoster.set(conn.me.pid, conn.me);
      this.tx([{ k: 'poker.roster', d: { seats: this.roster() } }]);
    }
    for (const round of this.rounds) {
      const meta = round.ticketKey && this.demo.jiraMeta(round.ticketKey);
      if (meta && conn.open) {
        conn.send({ t: 'x', r: this.key, k: 'jira.meta', d: meta });
      }
    }
  }

  // ---- helpers ----------------------------------------------------------------------------------

  round(id) {
    return this.rounds.find((r) => r.id === id) ?? null;
  }

  get current() {
    return this.round(this.currentRoundId);
  }

  isAdmin(who) {
    if (this.team) {
      return ['admin', 'owner'].includes(this.team.roleOf(who.pid));
    }
    return who.kind === 'user';
  }

  normalizeTicket(raw) {
    if (raw === undefined || raw === null) {
      return { key: null };
    }
    let key = String(raw).trim().toUpperCase();
    if (key === '') {
      return { key: null };
    }
    if (/^\d+$/.test(key)) {
      if (!this.team?.jira.ticketPrefix) {
        return { error: true };
      }
      key = `${this.team.jira.ticketPrefix.replace(/-$/, '')}-${key}`;
    }
    return TICKET.test(key) ? { key } : { error: true };
  }

  newRound(id, ticketKey, { pending = false, owner } = {}) {
    const number = Math.max(0, ...this.rounds.map((r) => r.number)) + 1;
    return {
      id,
      number,
      version: 1,
      state: 'voting',
      active: true,
      pending,
      autoReveal: true,
      ticketKey: ticketKey ?? undefined,
      parentKey: ticketKey ? this.demo.jiraMeta(ticketKey)?.parentKey : undefined,
      pendingOwner: pending ? owner : undefined,
      votes: new Map(),
    };
  }

  updated(round) {
    return { k: 'poker.round.updated', d: { round: this.roundInfo(round) } };
  }

  pointer(roundId, cause, nav) {
    this.currentRoundId = roundId;
    return { k: 'poker.pointer', d: { roundId, cause, nav } };
  }

  spotlightEvent() {
    return { k: 'poker.spotlight', d: this.spotlight ? { holder: this.spotlight.holder, roundId: this.spotlight.roundId } : {} };
  }

  discardPendingOf(pid) {
    const events = [];
    for (const round of this.rounds.filter((r) => r.pending && r.pendingOwner === pid)) {
      events.push(...this.removeRound(round));
    }
    return events;
  }

  /** Deletes a round and repairs the spotlight and the pointer in the same tx. */
  removeRound(round) {
    this.rounds = this.rounds.filter((r) => r !== round);
    const events = [];
    const fallback = this.rounds.filter((r) => !r.pending).at(-1) ?? this.rounds.at(-1) ?? null;
    if (this.spotlight?.roundId === round.id) {
      this.spotlight = fallback ? { ...this.spotlight, roundId: fallback.id } : null;
      events.push(this.spotlightEvent());
    }
    events.push({ k: 'poker.round.deleted', d: { roundId: round.id } });
    if (this.currentRoundId === round.id && fallback) {
      events.push(this.pointer(fallback.id, 'repair', false));
    }
    return events;
  }

  eligible(round) {
    const present = new Set(this.pids);
    const base = this.presenceEnabled ? present : new Set([...present, ...this.roster().map((s) => s.pid)]);
    const eligible = new Set([...base].filter((pid) => !this.observers.has(pid)));
    for (const [pid, vote] of round.votes) {
      if (vote.locked && !this.observers.has(pid)) {
        eligible.add(pid);
      }
    }
    return eligible;
  }

  armAutoReveal(round) {
    this.cancel(round.autoTimer);
    round.autoTimer = this.later(AUTO_REVEAL_DELAY_MS, () => {
      if (this.deleted || !this.rounds.includes(round) || round.state !== 'voting' || !round.active || round.pending || !round.autoReveal) {
        return;
      }
      const eligible = this.eligible(round);
      if (eligible.size > 0 && [...eligible].every((pid) => round.votes.get(pid)?.locked)) {
        this.tx(this.reveal(round, null));
        this.afterChange();
      }
    });
  }

  reveal(round, by) {
    const votes = [];
    for (const pid of [...round.votes.keys()].sort()) {
      const vote = round.votes.get(pid);
      if (vote.locked && !vote.abstained) {
        votes.push(compact({ pid, points: vote.points, high: vote.high, abstained: false }));
      } else {
        round.votes.set(pid, { locked: true, abstained: true });
        votes.push({ pid, abstained: true });
      }
    }
    round.state = 'revealed';
    round.winner = winnerOf(votes);
    round.revealedAt = this.demo.now();
    round.revealedBy = by ?? undefined;
    return [{ k: 'poker.round.revealed', d: compact({ roundId: round.id, votes, winner: round.winner, at: round.revealedAt, by }) }];
  }

  afterChange() {
    this.team?.pokerChanged(this);
  }

  /** Titles and parents arrive the way the server's Jira meta loop sends them: an x frame, then the parent. */
  afterTicket(round) {
    this.afterChange();
    const meta = round.ticketKey && this.demo.jiraMeta(round.ticketKey);
    if (!meta) {
      return;
    }
    this.later(250, () => {
      if (!this.rounds.includes(round) || round.ticketKey !== meta.key) {
        return;
      }
      for (const conn of this.room.subscribers) {
        conn.send({ t: 'x', r: this.key, k: 'jira.meta', d: meta });
      }
      if (meta.parentKey && round.parentKey !== meta.parentKey) {
        round.parentKey = meta.parentKey;
        this.tx([this.updated(round)]);
      }
    });
  }

  // ---- commands ---------------------------------------------------------------------------------

  decide(k, d, who, conn) {
    if (this.deleted) {
      return nack('not_found');
    }
    const handler = COMMANDS[k];
    if (!handler) {
      return nack('invalid', { msg: 'Not a poker command.' });
    }
    return handler.call(this, d, who, conn);
  }

  votable(round, who) {
    if (this.observers.has(who.pid)) {
      return nack('observer');
    }
    if (!round) {
      return nack('not_found');
    }
    if (round.pending) {
      return nack('round_pending');
    }
    if (round.state !== 'voting') {
      return nack('round_revealed');
    }
    if (!round.active) {
      return nack('round_inactive');
    }
    return null;
  }

  voted(round, pid) {
    return { events: [this.voteEntry(round, pid)], after: () => this.armAutoReveal(round) };
  }

  // ---- simulation -------------------------------------------------------------------------------

  estimate(round) {
    if (!this.estimates.has(round.id)) {
      const seed = [...(round.ticketKey ?? round.id)].reduce((sum, ch) => sum + ch.charCodeAt(0), 0);
      this.estimates.set(round.id, 1 + (seed % 4));
    }
    return this.estimates.get(round.id);
  }

  simulated(peer) {
    return this.presence.has(peer.pid) && !this.presence.isReal(peer.pid) && !this.observers.has(peer.pid);
  }

  /** The round the table is about: the pointer's, unless it points at a preview. */
  focusRound() {
    const current = this.current;
    return current && !current.pending ? current : this.rounds.filter((r) => r.active && !r.pending).at(-1) ?? null;
  }

  /** One step of the simulated teammates; runs only while someone real is watching. */
  simTick(random) {
    if (this.deleted || this.presence.realCount() === 0) {
      return;
    }
    const round = this.focusRound();
    if (!round?.active) {
      return;
    }
    if (round.state === 'revealed') {
      this.moveOn(round);
      return;
    }
    for (const peer of this.peers.filter((p) => this.simulated(p))) {
      const vote = round.votes.get(peer.pid);
      if (!vote && random.chance(0.22)) {
        if (peer.abstains && random.chance(0.5)) {
          this.act(peer, 'poker.abstain.set', { roundId: round.id, abstain: true });
          continue;
        }
        const index = Math.max(0, Math.min(DECK.length - 2, this.estimate(round) + random.between(-1, 1)));
        const between = random.chance(0.12);
        this.act(peer, 'poker.vote.set', compact({ roundId: round.id, points: DECK[index], high: between ? DECK[index + 1] : undefined, lock: random.chance(0.2) || undefined }));
      } else if (vote && !vote.locked && random.chance(0.35)) {
        this.act(peer, 'poker.lock.set', { roundId: round.id, locked: true });
      }
    }
    this.maybeMoveSpotlight(round, random);
  }

  // After a reveal the facilitator previews the next ticket (followers see it through the spotlight), then
  // ends the revealed round and starts the preview. Without the spotlight she starts the next round outright.
  moveOn(round) {
    const lead = this.facilitator;
    if (!lead || !this.simulated(lead)) {
      return;
    }
    const waited = this.demo.now() - round.revealedAt;
    const preview = this.rounds.find((r) => r.pending && r.pendingOwner === lead.pid);
    const holds = this.spotlight?.holder === lead.pid;
    if (preview) {
      if (waited > PREVIEW_AFTER_REVEAL_MS + START_AFTER_PREVIEW_MS) {
        this.act(lead, 'poker.round.end', { roundId: round.id });
        this.act(lead, 'poker.round.commitPending', { roundId: preview.id });
      }
    } else if (this.queue.length > 0 && holds && waited > PREVIEW_AFTER_REVEAL_MS) {
      this.act(lead, 'poker.round.create', { roundId: newId(), ticketKey: this.queue.shift(), pending: true });
    } else if (this.queue.length > 0 && !holds && waited > PREVIEW_AFTER_REVEAL_MS + START_AFTER_PREVIEW_MS) {
      this.act(lead, 'poker.round.next', { fromRoundId: round.id, newRoundId: newId(), ticketKey: this.queue.shift() });
    }
  }

  maybeMoveSpotlight(round, random) {
    const lead = this.facilitator;
    if (!lead || this.spotlight?.holder !== lead.pid || !this.simulated(lead) || this.spotlight.roundId !== round.id || !random.chance(0.02)) {
      return;
    }
    const earlier = this.rounds.filter((r) => r.state === 'revealed' && !r.pending);
    if (earlier.length === 0) {
      return;
    }
    this.act(lead, 'poker.spotlight.move', { roundId: random.pick(earlier).id });
    this.later(6000, () => {
      if (this.spotlight?.holder === lead.pid && this.simulated(lead) && this.round(round.id)) {
        this.act(lead, 'poker.spotlight.move', { roundId: this.currentRoundId });
      }
    });
  }
}

const COMMANDS = {
  'poker.vote.set'(d, who) {
    const round = this.round(d.roundId);
    const refused = this.votable(round, who);
    if (refused) {
      return refused;
    }
    if (!DECK.includes(d.points) || (d.high !== undefined && d.high !== nextDeckValue(d.points))) {
      return nack('invalid', { msg: 'Points must be a deck value.' });
    }
    const existing = round.votes.get(who.pid);
    if (existing?.locked) {
      const same = existing.points === d.points && existing.high === d.high && !existing.abstained;
      return same ? { events: [] } : nack('vote_locked');
    }
    round.votes.set(who.pid, compact({ points: d.points, high: d.high, locked: Boolean(d.lock), abstained: false }));
    return this.voted(round, who.pid);
  },

  'poker.lock.set'(d, who) {
    const round = this.round(d.roundId);
    const refused = this.votable(round, who);
    if (refused) {
      return refused;
    }
    const vote = round.votes.get(who.pid);
    if (!vote || vote.abstained || vote.points === undefined) {
      return nack('invalid', { msg: 'Only a vote with points can be locked.' });
    }
    if (vote.locked === d.locked) {
      return { events: [] };
    }
    round.votes.set(who.pid, { ...vote, locked: d.locked });
    return this.voted(round, who.pid);
  },

  'poker.abstain.set'(d, who) {
    const round = this.round(d.roundId);
    const refused = this.votable(round, who);
    if (refused) {
      return refused;
    }
    if (d.abstain) {
      round.votes.set(who.pid, { locked: true, abstained: true });
    } else if (round.votes.get(who.pid)?.abstained) {
      round.votes.delete(who.pid);
    } else {
      return { events: [] };
    }
    return this.voted(round, who.pid);
  },

  'poker.round.reveal'(d, who) {
    const round = this.round(d.roundId);
    if (!round) {
      return nack('not_found');
    }
    if (round.pending) {
      return nack('round_pending');
    }
    if (round.state === 'revealed') {
      return { events: [] };
    }
    return { events: this.reveal(round, who.pid), after: () => this.afterChange() };
  },

  'poker.round.replay'(d) {
    const round = this.round(d.roundId);
    if (!round) {
      return nack('not_found');
    }
    if (round.state !== 'revealed') {
      return nack('round_not_voting');
    }
    for (const [pid, vote] of [...round.votes]) {
      if (vote.abstained) {
        round.votes.delete(pid);
      } else {
        round.votes.set(pid, { ...vote, locked: true });
      }
    }
    Object.assign(round, { state: 'voting', winner: undefined, revealedAt: undefined, revealedBy: undefined, autoReveal: false, active: true, version: round.version + 1 });
    const own = [...round.votes.keys()].map((pid) => (viewer) => (viewer === pid ? [{ k: 'poker.vote.state', d: this.ownVote(round.id, pid, round.votes.get(pid)) }] : []));
    return { events: [{ k: 'poker.round.replayed', d: { roundId: round.id } }, this.updated(round), ...own], after: () => this.afterChange() };
  },

  'poker.round.create'(d, who) {
    if (this.round(d.roundId)) {
      return { events: [] };
    }
    const ticket = this.normalizeTicket(d.ticketKey);
    if (ticket.error) {
      return nack('invalid', { msg: 'Not a Jira issue key.' });
    }
    if (d.pending && this.spotlight?.holder !== who.pid) {
      return nack('not_holder', { msg: 'Only the spotlight holder can preview a pending round.' });
    }
    const current = this.current;
    if (!d.forceNew && !d.pending && ticket.key && current && !current.ticketKey && current.state === 'voting' && current.votes.size === 0 && !current.pending) {
      Object.assign(current, { ticketKey: ticket.key, parentKey: this.demo.jiraMeta(ticket.key)?.parentKey, version: current.version + 1 });
      return { events: [this.updated(current), this.pointer(current.id, 'create', true)], after: () => this.afterTicket(current) };
    }
    const round = this.newRound(d.roundId, ticket.key, { pending: Boolean(d.pending), owner: who.pid });
    this.rounds.push(round);
    const events = [{ k: 'poker.round.created', d: { round: this.roundInfo(round) } }];
    if (round.pending) {
      events.push(this.pointer(round.id, 'create', false));
      this.spotlight = { ...this.spotlight, roundId: round.id };
      events.push(this.spotlightEvent());
    } else {
      events.push(this.pointer(round.id, 'create', true));
    }
    return { events, after: () => this.afterTicket(round) };
  },

  'poker.round.createMany'(d, who) {
    const events = [];
    let last = null;
    for (const entry of d.rounds) {
      const ticket = this.normalizeTicket(entry.ticketKey);
      if (ticket.error || this.round(entry.roundId)) {
        return nack('invalid', { msg: 'Round ids must be new and ticket keys valid.' });
      }
    }
    for (const entry of d.rounds) {
      const ticket = this.normalizeTicket(entry.ticketKey);
      const current = this.current;
      if (last === null && current && !current.ticketKey && current.state === 'voting' && current.votes.size === 0 && !current.pending) {
        Object.assign(current, { ticketKey: ticket.key, parentKey: this.demo.jiraMeta(ticket.key)?.parentKey, version: current.version + 1 });
        events.push(this.updated(current));
        last = current;
        continue;
      }
      const round = this.newRound(entry.roundId, ticket.key, { owner: who.pid });
      this.rounds.push(round);
      events.push({ k: 'poker.round.created', d: { round: this.roundInfo(round) } });
      last = round;
    }
    events.push(this.pointer(last.id, 'createMany', true));
    return { events, after: () => this.afterChange() };
  },

  'poker.round.next'(d, who) {
    const from = this.round(d.fromRoundId);
    if (!from || !from.active) {
      return nack('stale', { d: { current: compact({ roundId: this.currentRoundId }) } });
    }
    if (this.round(d.newRoundId)) {
      return { events: [] };
    }
    const ticket = this.normalizeTicket(d.ticketKey);
    if (ticket.error) {
      return nack('invalid', { msg: 'Not a Jira issue key.' });
    }
    const events = [];
    for (const round of this.rounds.filter((r) => r.pending)) {
      this.rounds = this.rounds.filter((r) => r !== round);
      events.push({ k: 'poker.round.deleted', d: { roundId: round.id } });
    }
    for (const round of this.rounds.filter((r) => r.active)) {
      round.active = false;
      events.push(this.updated(round));
    }
    const round = this.newRound(d.newRoundId, ticket.key);
    this.rounds.push(round);
    events.push({ k: 'poker.round.created', d: { round: this.roundInfo(round) } }, this.pointer(round.id, 'next', true));
    if (this.spotlight?.holder === who.pid) {
      this.spotlight.roundId = round.id;
      events.push(this.spotlightEvent());
    } else if (this.spotlight && !this.round(this.spotlight.roundId)) {
      this.spotlight.roundId = round.id;
      events.push(this.spotlightEvent());
    }
    return { events, after: () => this.afterTicket(round) };
  },

  'poker.round.end'(d) {
    const round = this.round(d.roundId);
    if (!round) {
      return nack('not_found');
    }
    if (!round.active) {
      return { events: [] };
    }
    round.active = false;
    return { events: [this.updated(round)], after: () => this.afterChange() };
  },

  'poker.round.activate'(d) {
    const round = this.round(d.roundId);
    if (!round) {
      return nack('not_found');
    }
    if (round.pending) {
      return nack('round_pending');
    }
    if (round.state !== 'voting') {
      return nack('round_not_voting');
    }
    if (round.active) {
      return { events: [] };
    }
    round.active = true;
    return { events: [this.updated(round)] };
  },

  'poker.round.setTicket'(d) {
    const round = this.round(d.roundId);
    if (!round) {
      return nack('not_found');
    }
    if (round.state !== 'voting') {
      return nack('round_revealed');
    }
    if (!round.active) {
      return nack('round_inactive');
    }
    const ticket = this.normalizeTicket(d.ticketKey);
    if (ticket.error) {
      return nack('invalid', { msg: 'Not a Jira issue key.' });
    }
    if (d.baseVersion !== round.version) {
      return nack('stale', { d: { current: compact({ ticketKey: round.ticketKey, version: round.version }) } });
    }
    round.ticketKey = ticket.key ?? undefined;
    round.parentKey = undefined;
    round.version += 1;
    return { events: [this.updated(round)], after: () => this.afterTicket(round) };
  },

  'poker.round.commitPending'(d, who) {
    const round = this.round(d.roundId);
    if (!round) {
      return nack('not_found');
    }
    if (!round.pending) {
      return nack('invalid', { msg: 'The round was already committed.' });
    }
    if (round.pendingOwner !== who.pid) {
      return nack('not_holder', { msg: "Only the round's previewer can commit it." });
    }
    round.pending = false;
    round.pendingOwner = undefined;
    const events = [this.updated(round), this.pointer(round.id, 'commit', true)];
    if (this.spotlight?.holder === who.pid && this.spotlight.roundId !== round.id) {
      this.spotlight.roundId = round.id;
      events.push(this.spotlightEvent());
    }
    return { events, after: () => this.afterChange() };
  },

  'poker.round.discardPending'(d, who) {
    const round = this.round(d.roundId);
    if (!round) {
      return { events: [] };
    }
    if (!round.pending) {
      return nack('invalid', { msg: 'The round was already committed.' });
    }
    if (round.pendingOwner !== who.pid) {
      return nack('not_holder', { msg: "Only the round's previewer can discard it." });
    }
    return { events: this.removeRound(round) };
  },

  'poker.round.delete'(d, who) {
    const round = this.round(d.roundId);
    if (!round) {
      return { events: [] };
    }
    if (!this.isAdmin(who)) {
      return nack('forbidden');
    }
    if (this.rounds.length < 2) {
      return nack('invalid', { msg: 'The last round cannot be deleted.' });
    }
    return { events: this.removeRound(round), after: () => this.afterChange() };
  },

  'poker.round.reorder'(d) {
    const ids = this.rounds.map((r) => r.id);
    if (d.order.length !== ids.length || !ids.every((id) => d.order.includes(id))) {
      return nack('stale', { d: { current: { order: ids } } });
    }
    this.rounds = d.order.map((id) => this.round(id));
    return { events: [{ k: 'poker.round.order', d: { order: d.order } }] };
  },

  'poker.rounds.clearAll'(d, who) {
    if (!this.isAdmin(who)) {
      return nack('forbidden');
    }
    this.rounds = [];
    const round = this.newRound(d.newRoundId, null);
    this.rounds = [round];
    const events = [{ k: 'poker.rounds.reset', d: { round: this.roundInfo(round) } }, this.pointer(round.id, 'reset', true)];
    if (this.spotlight) {
      this.spotlight.roundId = round.id;
      events.push(this.spotlightEvent());
    }
    return { events, after: () => this.afterChange() };
  },

  'poker.settings.update'(d) {
    const current = { presenceEnabled: this.presenceEnabled, followSpotlight: this.followSpotlight };
    for (const field of ['presenceEnabled', 'followSpotlight']) {
      if (d[field] !== undefined && d.expected[field] !== current[field]) {
        return nack('stale', { d: { current } });
      }
    }
    this.presenceEnabled = d.presenceEnabled ?? this.presenceEnabled;
    this.followSpotlight = d.followSpotlight ?? this.followSpotlight;
    return { events: [{ k: 'poker.settings', d: { presenceEnabled: this.presenceEnabled, followSpotlight: this.followSpotlight } }] };
  },

  'poker.observer.set'(d) {
    if (this.observers.has(d.principalId) === d.observer) {
      return { events: [] };
    }
    const events = [];
    if (d.observer) {
      this.observers.add(d.principalId);
      for (const round of this.rounds.filter((r) => r.state === 'voting' && r.votes.has(d.principalId))) {
        round.votes.delete(d.principalId);
        events.push(this.voteEntry(round, d.principalId));
      }
    } else {
      this.observers.delete(d.principalId);
    }
    events.unshift({ k: 'poker.observers', d: { ids: [...this.observers].sort() } });
    return { events, after: () => this.rounds.forEach((r) => this.armAutoReveal(r)) };
  },

  'poker.spotlight.take'(d, who, conn) {
    if (!this.round(d.roundId)) {
      return nack('not_found');
    }
    const holder = this.spotlight?.holder;
    if (holder && !d.force && (holder !== who.pid || (this.spotlight.client && this.spotlight.client !== conn?.cid))) {
      return nack('spotlight_held', { d: { holder } });
    }
    const events = holder && holder !== who.pid ? this.discardPendingOf(holder) : [];
    this.spotlight = { holder: who.pid, roundId: d.roundId, client: conn?.cid ?? null };
    events.unshift(this.spotlightEvent());
    return { events };
  },

  'poker.spotlight.move'(d, who, conn) {
    if (this.spotlight?.holder !== who.pid || (conn && this.spotlight.client && this.spotlight.client !== conn.cid)) {
      return nack('not_holder');
    }
    if (!this.round(d.roundId)) {
      return nack('not_found');
    }
    if (conn && !this.spotlight.client) {
      this.spotlight.client = conn.cid;
    }
    if (this.spotlight.roundId === d.roundId) {
      return { events: [] };
    }
    this.spotlight.roundId = d.roundId;
    return { events: [this.spotlightEvent()] };
  },

  'poker.spotlight.give'(d, who) {
    if (this.spotlight && this.spotlight.holder !== who.pid) {
      return nack('not_holder');
    }
    if (!this.presence.has(d.principalId)) {
      return nack('not_found', { msg: 'They are not here.' });
    }
    const events = this.discardPendingOf(who.pid);
    this.spotlight = { holder: d.principalId, roundId: this.spotlight?.roundId ?? this.currentRoundId, client: null };
    events.unshift(this.spotlightEvent());
    return { events };
  },

  'poker.spotlight.release'(d, who) {
    if (!this.spotlight) {
      return { events: [] };
    }
    if (this.spotlight.holder !== who.pid) {
      return nack('not_holder');
    }
    const events = this.discardPendingOf(who.pid);
    this.spotlight = null;
    events.unshift(this.spotlightEvent());
    return { events };
  },

  'poker.jira.submitPoints'(d, who) {
    const round = this.round(d.roundId);
    if (!this.team?.jira.configured) {
      return nack('jira_not_configured');
    }
    if (!round || round.state !== 'revealed' || !round.ticketKey) {
      return nack('invalid', { msg: 'Reveal a round with a ticket first.' });
    }
    if (round.submit?.status === 'pending') {
      return nack('jira_pending');
    }
    round.submit = { roundId: round.id, key: round.ticketKey, points: d.points, status: 'pending', by: who.pid };
    return {
      events: [{ k: 'poker.jira.points', d: round.submit }],
      after: () => this.later(900, () => {
        this.demo.jira.issues.get(round.ticketKey).points = d.points;
        round.submit = { ...round.submit, status: 'ok' };
        round.jira = { points: d.points, at: this.demo.now(), by: who.pid };
        this.tx([{ k: 'poker.jira.points', d: round.submit }, this.updated(round)]);
      }),
    };
  },
};
