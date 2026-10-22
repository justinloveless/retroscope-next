// The soak's client side (DESIGN §j), started by Retroscope.Soak: every tab of every room, the load model, and the
// convergence check. It speaks JSON lines: operations arrive on stdin ({op: 'burst'|'converge'|'resume'|'stop'}),
// events leave on stdout ({ev: …}); progress goes to stderr. The orchestrator owns the stack, the chaos schedule and
// the verdict; this process owns everything a browser would see.
//   node tests/Retroscope.Soak/client/soak.mjs '<config json>'     (RS_E2E_BASE_URL names the stack)
import { writeFileSync } from 'node:fs';
import readline from 'node:readline';
import { clientIp, percentile, request, sleep, user } from '../../../deploy/drills/client.mjs';
import { DECK, uuidv7 } from '../../../web/src/poker/model.js';
import { SoakTab, canonicalView } from './soak-tab.mjs';

const config = JSON.parse(process.argv[2]);
const TICK_MS = 100;
const BURST = { lockWithinMs: 1000, revealAtMs: 2000, nextAtMs: 3000 };
const SPOTLIGHT = { moveEveryMs: 3000, createEveryMs: 30_000, contestEveryMs: 120_000 };
const TIMER = { everyMs: 60_000, durationMs: 20_000 };
const LIMITS = { cards: 150, comments: 300 };
const PROBE = { budgetMs: 20_000, attemptMs: 6000, retryMs: 250 };

const emit = (event) => process.stdout.write(`${JSON.stringify(event)}\n`);
const log = (message) => process.stderr.write(`[soak-client ${new Date().toISOString().slice(11, 19)}] ${message}\n`);
const pick = (list) => list[Math.floor(Math.random() * list.length)];
const until = async (condition, timeoutMs) => {
  const deadline = Date.now() + timeoutMs;
  while (!condition() && Date.now() < deadline) {
    await sleep(50);
  }
  return condition();
};

async function pool(items, size, work) {
  const queue = [...items];
  await Promise.all(Array.from({ length: Math.min(size, queue.length) }, async () => {
    while (queue.length > 0) {
      await work(queue.shift());
    }
  }));
}

async function guest(name) {
  const ip = clientIp();
  const response = await request('POST', '/api/guest', { body: { displayName: name }, ip });
  if (response.status !== 201) {
    throw new Error(`guest ${response.status} ${response.text}`);
  }
  const cookie = (response.headers['set-cookie'] ?? []).map((c) => c.split(';')[0]).find((c) => c.startsWith('__Host-rs_guest='));
  return { name, cookie, ip };
}

async function create(path, owner, body) {
  const response = await request('POST', path, { body, cookie: owner.cookie, ip: owner.ip });
  if (response.status !== 201) {
    throw new Error(`${path} ${response.status} ${response.text}`);
  }
  return JSON.parse(response.text);
}

let ticket = 0;
const nextTicket = () => `SOAK-${++ticket}`;
let bodies = 0;
const body = (label) => `soak ${label} note ${++bodies}: what went well, what did not, and what we try next sprint`;

// Rooms and tabs ----------------------------------------------------------------------------------------------------

const rooms = [];
const tabs = [];
const violations = [];
const reconnects = [];

function observeTx(room) {
  return (tab, e, s, ev) => {
    const key = `${e}:${s}`;
    for (const { k, d } of ev) {
      if (k === 'poker.round.revealed') {
        (room.reveals[d.roundId] ??= new Set()).add(key);
      } else if (k === 'retro.timer' && d.endsAt != null) {
        const timer = (room.timers[d.endsAt] ??= { endsAt: d.endsAt, startedAt: null, expired: new Set(), endedInSnap: new Set() });
        if (d.expired) {
          timer.expired.add(key);
        } else if (d.state === 'running') {
          timer.startedAt ??= Date.now();
        }
      }
    }
  };
}

// A tab that was away when a timer ran out (every tab is, in a partition longer than the presence grace) resumes with
// a snapshot, not a replay, so it never sees the expiry frame; a snapshot taken after `endsAt` that no longer shows the
// timer running proves the expiry instead. The soak only ever starts timers, so nothing else stops one.
function observeSnap(room) {
  return (tab, d) => {
    if (room.kind !== 'board') {
      return;
    }
    const now = Date.now();
    const running = d.timer?.state === 'running' ? d.timer.endsAt : null;
    for (const timer of Object.values(room.timers)) {
      if (timer.expired.size === 0 && timer.endsAt <= now && timer.endsAt !== running) {
        timer.endedInSnap.add(tab.label);
      }
    }
  };
}

function addTab(room, principal, label, extra = {}) {
  const tab = Object.assign(new SoakTab(principal, room.key, {
    kind: room.kind,
    label,
    onTx: observeTx(room),
    onSnap: observeSnap(room),
    onReconnect: (t, record) => {
      reconnects.push({ ...record, tab: t.label });
      emit({ ev: 'reconnect', tab: t.label, ...record });
    },
    onViolation: (t, violation) => {
      violations.push(violation);
      emit({ ev: 'violation', ...violation });
    },
  }), { roomRef: room, principal, nextAt: Date.now() + Math.random() * 3000 }, extra);
  room.tabs.push(tab);
  tabs.push(tab);
  return tab;
}

function newRoom(kind, id, code, role) {
  const room = { key: `${kind === 'poker' ? 'poker' : 'board'}:${id}`, kind, id, code, role, tabs: [], reveals: {}, timers: {} };
  rooms.push(room);
  return room;
}

async function principals(prefix, count) {
  const facilitator = await user(`${prefix} 1`);
  const guests = [];
  for (let i = 2; i <= count; i++) {
    guests.push(await guest(`${prefix} ${i}`));
  }
  return [facilitator, ...guests];
}

async function setUp() {
  const { poker: pokerRooms, retro: retroRooms, spotlight: spotlightRooms, clients } = config.rooms;
  const plans = [
    ...Array.from({ length: pokerRooms }, (_, i) => ({ kind: 'poker', role: 'poker', name: `Poker ${i + 1}` })),
    ...Array.from({ length: retroRooms }, (_, i) => ({ kind: 'board', role: 'retro', name: `Retro ${i + 1}` })),
    ...Array.from({ length: spotlightRooms }, (_, i) => ({ kind: 'poker', role: 'spotlight', name: `Spot ${i + 1}` })),
  ];
  log(`creating ${plans.length} rooms of ${clients} clients`);
  await pool(plans, 4, async (plan) => {
    const people = await principals(plan.name, clients);
    const made = plan.kind === 'poker'
      ? await create('/api/poker', people[0], {})
      : await create('/api/retro', people[0], { title: plan.name });
    const room = newRoom(plan.kind, made.id, made.code, plan.role);
    const path = plan.kind === 'poker' ? `/api/poker/by-code/${made.code}` : `/api/retro/by-code/${made.code}`;
    for (const p of people.slice(1)) {
      await request('GET', path, { cookie: p.cookie, ip: p.ip });
    }
    people.forEach((p, i) => addTab(room, p, `${plan.name}.${i + 1}`, { facilitator: i === 0 }));
    if (plan.role === 'retro') {
      // A second tab of the facilitator edits the same cards: concurrent edits that preconditions must serialize.
      addTab(room, people[0], `${plan.name}.1b`, { twin: true });
    }
  });

  const { boards, cards, creators } = config.storm;
  log(`creating ${boards} storm boards with ${cards} cards each`);
  const owners = await Promise.all(Array.from({ length: creators }, (_, i) => guest(`Storm ${i + 1}`)));
  await pool(Array.from({ length: boards }, (_, i) => i), 4, async (i) => {
    const owner = owners[i % creators];
    const made = await create('/api/retro', owner, { title: `Storm ${i + 1}` });
    const room = newRoom('board', made.id, made.code, 'storm');
    addTab(room, owner, `Storm ${i + 1}.1`);
    addTab(room, owner, `Storm ${i + 1}.2`);
  });

  log(`connecting ${tabs.length} tabs`);
  await pool(tabs, 16, (tab) => tab.connect());
  for (const room of rooms) {
    room.initialRounds = room.kind === 'poker' ? room.tabs[0].state.rounds.map((r) => r.id) : [];
  }
  await pool(rooms.filter((r) => r.role === 'storm'), 8, fillStormBoard);
  for (const room of rooms.filter((r) => r.role === 'spotlight')) {
    const holder = room.tabs[0];
    holder.command('poker.spotlight.take', { roundId: currentRound(holder), force: true });
  }
}

async function fillStormBoard(room) {
  const tab = room.tabs[0];
  const columns = tab.state.columns.map((c) => c.id);
  for (let i = 0; i < config.storm.cards; i++) {
    await until(() => tab.outbox.size < 8, 60_000);
    // Below the 20 commands/s a connection may send (§c.7), as a person pasting cards would be.
    await sleep(70);
    tab.command('retro.card.add', { cardId: uuidv7(), columnId: columns[i % columns.length], body: body(tab.label), anonymous: false });
  }
  await until(() => tab.outbox.size === 0, 60_000);
}

// Load model ---------------------------------------------------------------------------------------------------------

function currentRound(tab) {
  const state = tab.state;
  return state.currentRoundId ?? state.rounds[0]?.id ?? null;
}

function voting(tab, roundId) {
  const round = tab.state.rounds.find((r) => r.id === roundId);
  return round !== undefined && round.state === 'voting' && round.active && !round.pending;
}

function pokerVote(tab, roundId, lock) {
  if (roundId === null || !voting(tab, roundId) || tab.state.own[roundId]?.locked) {
    return;
  }
  tab.command('poker.vote.set', { roundId, points: pick(DECK), ...(lock ? { lock: true } : {}) });
}

function pokerStep(tab) {
  const spotlightRoom = tab.roomRef.role === 'spotlight';
  const roundId = spotlightRoom ? (tab.state.spotlight?.roundId ?? currentRound(tab)) : currentRound(tab);
  if (Math.random() < 0.15 && roundId !== null && voting(tab, roundId)) {
    const round = tab.state.rounds.find((r) => r.id === roundId);
    tab.command('poker.round.setTicket', { roundId, ticketKey: nextTicket(), baseVersion: round.version });
    return;
  }
  pokerVote(tab, roundId, false);
}

function retroStep(tab) {
  const state = tab.state;
  const cards = [...state.cards.values()];
  const mine = cards.filter((c) => state.mineCards.has(c.id));
  const roll = tab.twin ? 0.7 : Math.random();
  if (roll < 0.35 && cards.length < LIMITS.cards) {
    tab.command('retro.card.add', { cardId: uuidv7(), columnId: pick(state.columns).id, body: body(tab.label), anonymous: false });
  } else if (roll < 0.6 && cards.length > 0) {
    tab.command('retro.vote.set', { cardId: pick(cards).id, on: Math.random() < 0.6 });
  } else if (roll < 0.8 && mine.length > 0) {
    const card = pick(mine);
    tab.command('retro.card.edit', { cardId: card.id, body: body(tab.label), baseVersion: card.version });
  } else if (cards.length > 0 && state.comments.size < LIMITS.comments) {
    tab.command('retro.comment.add', { commentId: uuidv7(), cardId: pick(cards).id, body: body(tab.label), anonymous: false });
  }
}

function periodic(tab, now) {
  const role = tab.roomRef.role;
  if (role === 'retro' && tab.facilitator && now >= (tab.timerAt ??= now + Math.random() * TIMER.everyMs)) {
    tab.timerAt = now + TIMER.everyMs;
    if (tab.state.timer.state === 'idle') {
      tab.command('retro.timer.start', { durationMs: TIMER.durationMs, alarm: true, music: false, from: 'idle' });
    }
  }
  if (role !== 'spotlight') {
    return;
  }
  if (tab.facilitator && now >= (tab.moveAt ??= now + SPOTLIGHT.moveEveryMs)) {
    tab.moveAt = now + SPOTLIGHT.moveEveryMs;
    if (tab.state.spotlight?.holder === tab.pid && tab.state.rounds.length > 1) {
      tab.command('poker.spotlight.move', { roundId: pick(tab.state.rounds).id });
    }
  }
  if (tab.facilitator && now >= (tab.createAt ??= now + SPOTLIGHT.createEveryMs)) {
    tab.createAt = now + SPOTLIGHT.createEveryMs;
    tab.command('poker.round.create', { roundId: uuidv7(), ticketKey: nextTicket(), forceNew: true });
  }
  if (!tab.facilitator && now >= (tab.contestAt ??= now + Math.random() * SPOTLIGHT.contestEveryMs * tab.roomRef.tabs.length)) {
    tab.contestAt = now + SPOTLIGHT.contestEveryMs * tab.roomRef.tabs.length;
    tab.command('poker.spotlight.take', { roundId: currentRound(tab), force: false });
  }
}

let paused = true;
const meanGapMs = 1000 / config.rate;

function tick() {
  if (paused) {
    return;
  }
  const now = Date.now();
  for (const tab of tabs) {
    if (tab.roomRef.role === 'storm' || tab.state === null) {
      continue;
    }
    periodic(tab, now);
    if (now < tab.nextAt) {
      continue;
    }
    tab.nextAt = now - Math.log(1 - Math.random()) * meanGapMs;
    if (tab.kind === 'poker') {
      pokerStep(tab);
    } else {
      retroStep(tab);
    }
  }
}

/** Every client votes and locks within a second; then each poker room's facilitator reveals and starts the next round. */
function burst() {
  for (const room of rooms.filter((r) => r.role === 'poker' || r.role === 'retro')) {
    const roundId = room.kind === 'poker' ? currentRound(room.tabs[0]) : null;
    for (const tab of room.tabs.filter((t) => !t.twin)) {
      setTimeout(() => {
        if (paused) {
          return;
        }
        if (room.kind === 'poker') {
          pokerVote(tab, roundId, true);
        } else if (tab.state.cards.size > 0) {
          tab.command('retro.vote.set', { cardId: pick([...tab.state.cards.values()]).id, on: true });
        }
      }, Math.random() * BURST.lockWithinMs);
    }
    if (room.kind === 'poker' && roundId !== null) {
      const facilitator = room.tabs[0];
      const later = (ms, k, d) => setTimeout(() => paused || facilitator.command(k, d()), ms);
      later(BURST.revealAtMs, 'poker.round.reveal', () => ({ roundId }));
      later(BURST.nextAtMs, 'poker.round.next', () => ({ fromRoundId: roundId, newRoundId: uuidv7(), ticketKey: nextTicket() }));
    }
  }
}

// Convergence --------------------------------------------------------------------------------------------------------

/**
 * A fresh snapshot of `room` as `principal` sees it, from a new connection: `{snap, ms, attempts}`, `snap` null when
 * none came within `PROBE.budgetMs`. Each attempt is cut after `PROBE.attemptMs` and retried (after `bye.retryMs` when
 * the server sent one); every failed attempt records where it stopped (`connect`: TCP, TLS or upgrade; `welcome`;
 * `snap`), why (the close code or error) and the bye, kick and nack frames it got, so a `no_snapshot` names its cause.
 * `Tab.connect` retries forever and keeps none of that.
 */
async function freshSnapshot(principal, room) {
  const started = Date.now();
  const attempts = [];
  for (let left = PROBE.budgetMs; left > 0; left = PROBE.budgetMs - (Date.now() - started)) {
    const probe = new SoakTab(principal, room.key, { kind: room.kind, label: 'verifier' });
    const frames = [];
    const receive = probe.receive.bind(probe);
    probe.receive = (f, settle) => {
      if (f.t === 'bye' || f.t === 'kick' || f.t === 'nack') {
        frames.push({ t: f.t, code: f.code, ...(f.retryMs !== undefined ? { retryMs: f.retryMs } : {}) });
      }
      receive(f, settle);
    };
    const at = Date.now();
    const opening = probe.open();
    // `closed()` clears the socket before it rejects with "closed <code> …", so that message also means it was upgraded.
    const failed = (error) => ({
      stage: probe.me !== null ? 'snap' : probe.socket !== null || error.startsWith('closed ') ? 'welcome' : 'connect',
      error,
    });
    const failure = await Promise.race([
      opening.then(() => null, (error) => failed(error.message)),
      sleep(Math.min(PROBE.attemptMs, left)).then(() => failed('timeout')),
    ]);
    probe.close();
    opening.then(() => probe.close(), () => probe.close());
    if (failure === null) {
      return { snap: probe.snap, ms: Date.now() - started, attempts };
    }
    attempts.push({ ms: Date.now() - at, ...failure, frames });
    await sleep(Math.min(probe.retryMs ?? PROBE.retryMs, Math.max(0, PROBE.budgetMs - (Date.now() - started))));
  }
  return { snap: null, ms: Date.now() - started, attempts };
}

/**
 * Pauses the load, waits for every tab to be back in its room and for every outbox to empty, then compares each tab's
 * folded view with a fresh snapshot taken by the same principal at the same (epoch, seq). `convergedMs` is how long
 * after the last tab got back in the last tab's view changed.
 */
async function converge() {
  paused = true;
  const started = Date.now();
  const connected = await until(() => tabs.every((t) => t.connected), 120_000);
  const allIn = Date.now();
  const drained = await until(() => tabs.every((t) => t.outbox.size === 0), 60_000);
  const diverged = [];
  const retriedProbes = [];
  const groups = new Map();
  for (const tab of tabs) {
    const key = `${tab.room}|${tab.principal.cookie}`;
    groups.set(key, [...(groups.get(key) ?? []), tab]);
  }
  await pool([...groups.values()], 12, async (group) => {
    const room = group[0].roomRef;
    for (let attempt = 1; ; attempt++) {
      const { snap, ms, attempts } = await freshSnapshot(group[0].principal, room);
      if (attempts.length > 0) {
        retriedProbes.push({ room: room.key, ok: snap !== null, ms, attempts });
      }
      if (snap === null) {
        diverged.push({ room: room.key, reason: 'no_snapshot', ms, attempts });
        return;
      }
      await until(() => group.every((t) => t.s >= snap.s || t.e !== snap.e), 3000);
      const behind = group.filter((t) => t.e !== snap.e || t.s !== snap.s);
      if (behind.some((t) => t.e === snap.e && t.s > snap.s) && attempt < 4) {
        continue;
      }
      const expected = canonicalView(room.kind, snap.d, snap.o);
      for (const tab of group) {
        if (tab.e !== snap.e || tab.s !== snap.s) {
          diverged.push({ room: room.key, tab: tab.label, reason: 'position', have: [tab.e, tab.s], fresh: [snap.e, snap.s] });
        } else if (tab.view() !== expected) {
          diverged.push({ room: room.key, tab: tab.label, reason: 'content', s: snap.s, have: tab.view(), fresh: expected });
        }
      }
      return;
    }
  });
  const settled = Math.max(allIn, ...tabs.map((t) => t.settledAt));
  return {
    ok: connected && drained && diverged.length === 0,
    connected,
    drained,
    reconnectMs: allIn - started,
    convergedMs: settled - allIn,
    unacked: tabs.reduce((n, t) => n + t.outbox.size, 0),
    diverged: diverged.slice(0, 20),
    divergedCount: diverged.length,
    retriedProbes: retriedProbes.slice(0, 20),
  };
}

// Results ------------------------------------------------------------------------------------------------------------

function results() {
  const latencies = tabs.flatMap((t) => t.latencies);
  return {
    rooms: rooms.map((room) => ({
      key: room.key,
      kind: room.kind,
      role: room.role,
      id: room.id,
      initialRounds: room.initialRounds,
      reveals: Object.fromEntries(Object.entries(room.reveals).map(([id, seen]) => [id, seen.size])),
      timers: Object.values(room.timers).map((t) => ({
        endsAt: t.endsAt,
        startedAt: t.startedAt,
        expired: t.expired.size,
        endedInSnap: t.endedInSnap.size,
      })),
    })),
    tabs: tabs.map((tab) => ({
      room: tab.room,
      label: tab.label,
      pid: tab.pid,
      twin: Boolean(tab.twin),
      intents: tab.outcomes(),
    })),
    latency: {
      acks: latencies.length,
      p50: percentile(latencies, 50) || 0,
      p95: percentile(latencies, 95) || 0,
      p99: percentile(latencies, 99) || 0,
      max: latencies.reduce((a, b) => Math.max(a, b), 0),
    },
    nackCodes: tabs.flatMap((t) => t.rejected).reduce((m, x) => ({ ...m, [x.code]: (m[x.code] ?? 0) + 1 }), {}),
    reconnects,
    violations,
  };
}

// Control loop -------------------------------------------------------------------------------------------------------

async function main() {
  await setUp();
  const timer = setInterval(tick, TICK_MS);
  emit({ ev: 'ready', rooms: rooms.length, tabs: tabs.length });
  for await (const line of readline.createInterface({ input: process.stdin })) {
    const { op, id } = JSON.parse(line);
    switch (op) {
      case 'resume':
        paused = false;
        emit({ ev: 'resumed', id });
        break;
      case 'burst':
        burst();
        emit({ ev: 'burst', id });
        break;
      case 'converge':
        emit({ ev: 'converged', id, ...(await converge()) });
        break;
      case 'stop': {
        const final = await converge();
        clearInterval(timer);
        tabs.forEach((t) => t.close());
        writeFileSync(config.resultsPath, JSON.stringify(results()));
        process.stdout.write(`${JSON.stringify({ ev: 'stopped', id, final, resultsPath: config.resultsPath })}\n`, () => process.exit(0));
        return;
      }
      default:
        log(`unknown op ${op}`);
    }
  }
}

process.on('unhandledRejection', (error) => {
  log(`unhandled: ${error?.stack ?? error}`);
  emit({ ev: 'error', message: String(error?.message ?? error) });
});
main().catch((error) => {
  log(error.stack ?? String(error));
  emit({ ev: 'error', message: String(error.message ?? error) });
  process.exit(1);
});
