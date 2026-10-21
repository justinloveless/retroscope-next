// Shared setup for the strip, spotlight, settings, history and mobile specs: a session with several
// rounds and a small scripted server that decides the strip's commands the way the real decider does
// (U7 decisions 3–6) for whoever sends them, so two pages in one test see each other's changes.
import { BOB, CODE, DOT, GUS, ME, ROOM, SESSION, round, snapshot } from '../poker-table/_poker.js';

export { BOB, CODE, DOT, GUS, ME, ROOM, SESSION, round };

export const TEAM = '0a000000-0000-4000-8000-0000000000b0';
export const roundId = (n) => `0a000000-0000-4000-8000-0000000010${String(n).padStart(2, '0')}`;

export function rounds(count = 6) {
  return Array.from({ length: count }, (_, i) => round(roundId(i + 1), i + 1, {
    ticketKey: `ABC-${i + 1}`,
    active: i === count - 1,
  }));
}

/**
 * Serves the session to `viewer` (with `role` in the team, for the delete and clear-all rights) and
 * answers reorder, activate, delete, create, clearAll, settings, observer and spotlight commands.
 * Returns the state the fake holds; `state.sent` lists every command payload by kind.
 */
export function setupStrip(fake, { viewer = ME, role = 'member', theme = 'light', snap = {}, spotlightClient = null } = {}) {
  const base = snapshot({ rounds: rounds(), currentRoundId: roundId(6), ...snap });
  const state = {
    rounds: base.rounds,
    currentRoundId: base.currentRoundId,
    spotlight: base.spotlight ? { ...base.spotlight, client: spotlightClient } : null,
    observers: [...base.observers],
    settings: { presenceEnabled: base.presenceEnabled, followSpotlight: base.followSpotlight },
    sent: {},
  };
  fake.me = viewer;
  fake.boot = (path) => ({
    build: 'fake',
    theme,
    me: { principal: { id: viewer.pid, kind: viewer.kind, name: viewer.name }, teams: [{ id: TEAM, name: 'Team', role }] },
    path,
  });
  fake.route('GET', '/api/poker/by-code/:code', { body: { id: SESSION, access: 'granted' } });
  fake.room(ROOM, {
    snapshot: () => ({
      ...base,
      rounds: state.rounds,
      currentRoundId: state.currentRoundId,
      observers: state.observers,
      ...state.settings,
      spotlight: state.spotlight ? { holder: state.spotlight.holder, roundId: state.spotlight.roundId } : undefined,
    }),
    overlay: (conn) => ({ own: [], heldByThisTab: Boolean(state.spotlight && state.spotlight.holder === viewer.pid && state.spotlight.client === conn?.cid) }),
  });

  const record = (k, d) => (state.sent[k] ??= []).push(d);
  const spotlightEvent = () => ({ k: 'poker.spotlight', d: state.spotlight ? { holder: state.spotlight.holder, roundId: state.spotlight.roundId } : {} });
  const handle = (k, fn) => fake.onCommand(k, (ctx) => {
    record(k, ctx.frame.d);
    fn(ctx, ctx.frame.d);
  });

  handle('poker.round.reorder', (ctx, d) => {
    const ids = state.rounds.map((r) => r.id);
    if (d.order.length !== ids.length || !ids.every((id) => d.order.includes(id))) {
      ctx.nack('stale', { d: { current: { order: ids } } });
      return;
    }
    state.rounds = d.order.map((id) => state.rounds.find((r) => r.id === id));
    ctx.commit([{ k: 'poker.round.order', d: { order: d.order } }]);
  });
  handle('poker.round.activate', (ctx, d) => {
    state.rounds = state.rounds.map((r) => (r.id === d.roundId ? { ...r, active: true } : r));
    ctx.commit([{ k: 'poker.round.updated', d: { round: state.rounds.find((r) => r.id === d.roundId) } }]);
  });
  handle('poker.round.delete', (ctx, d) => {
    state.rounds = state.rounds.filter((r) => r.id !== d.roundId);
    ctx.commit([{ k: 'poker.round.deleted', d: { roundId: d.roundId } }]);
  });
  handle('poker.round.create', (ctx, d) => {
    const created = round(d.roundId, Math.max(...state.rounds.map((r) => r.number)) + 1, d.ticketKey ? { ticketKey: d.ticketKey } : {});
    state.rounds = [...state.rounds, created];
    state.currentRoundId = created.id;
    ctx.commit([{ k: 'poker.round.created', d: { round: created } }, { k: 'poker.pointer', d: { roundId: created.id, cause: 'create', nav: true } }]);
  });
  handle('poker.rounds.clearAll', (ctx, d) => {
    const first = round(d.newRoundId, 1);
    state.rounds = [first];
    state.currentRoundId = first.id;
    ctx.commit([{ k: 'poker.rounds.reset', d: { round: first } }, { k: 'poker.pointer', d: { roundId: first.id, cause: 'reset', nav: true } }]);
  });
  handle('poker.settings.update', (ctx, d) => {
    const current = { ...state.settings };
    if (Object.entries(d.expected).some(([field, value]) => current[field] !== value)) {
      ctx.nack('stale', { d: { current } });
      return;
    }
    for (const field of Object.keys(d.expected)) {
      state.settings[field] = d[field];
    }
    ctx.commit([{ k: 'poker.settings', d: { ...state.settings } }]);
  });
  handle('poker.observer.set', (ctx, d) => {
    const ids = new Set(state.observers);
    if (d.observer) {
      ids.add(d.principalId);
    } else {
      ids.delete(d.principalId);
    }
    state.observers = [...ids].sort();
    ctx.commit([{ k: 'poker.observers', d: { ids: state.observers } }]);
  });
  handle('poker.spotlight.take', (ctx, d) => {
    const spot = state.spotlight;
    const elsewhere = spot && (spot.holder !== viewer.pid || (spot.client !== null && spot.client !== ctx.conn.cid));
    if (elsewhere && !d.force) {
      ctx.nack('spotlight_held', { d: { holder: spot.holder } });
      return;
    }
    state.spotlight = { holder: viewer.pid, roundId: d.roundId, client: ctx.conn.cid };
    ctx.commit([spotlightEvent()]);
  });
  handle('poker.spotlight.move', (ctx, d) => {
    const spot = state.spotlight;
    if (!spot || spot.holder !== viewer.pid || (spot.client !== null && spot.client !== ctx.conn.cid)) {
      ctx.nack('not_holder');
      return;
    }
    state.spotlight = { ...spot, roundId: d.roundId, client: ctx.conn.cid };
    ctx.commit([spotlightEvent()]);
  });
  handle('poker.spotlight.give', (ctx, d) => {
    state.spotlight = { holder: d.principalId, roundId: state.spotlight?.roundId ?? state.currentRoundId, client: null };
    ctx.commit([spotlightEvent()]);
  });
  handle('poker.spotlight.release', (ctx) => {
    state.spotlight = null;
    ctx.commit([spotlightEvent()]);
  });
  return state;
}

/** Someone else's spotlight moves (or is taken) by a tx the page did not cause. */
export function otherSpotlight(fake, state, holder, id) {
  state.spotlight = { holder: holder.pid, roundId: id, client: 'other' };
  fake.tx(ROOM, [{ k: 'poker.spotlight', d: { holder: holder.pid, roundId: id } }]);
}

export async function openStrip(page, path = `/poker/${CODE}`) {
  await page.goto(path);
  await page.locator('.nt-strip .nt-chip').first().waitFor();
}

export const chip = (page, n) => page.locator(`.nt-chip[data-id="${roundId(n)}"]`);
export const chipKeys = (page) => page.locator('.nt-chip .nt-chip-key').allTextContents();
export const roundParam = (page) => new URL(page.url()).searchParams.get('round');

/** The round the page shows, from the toolbar ("Round 3 · ABC-3"). */
export const shownRound = (page) => page.locator('.nt-round-name');

/** Dispatches a pointer event with `pointerType` on `locator` (synthetic touch works in every engine). */
export function pointer(locator, type, { x, y, pointerType = 'touch', id = 7 }) {
  return locator.evaluate((el, init) => {
    el.dispatchEvent(new PointerEvent(init.type, {
      bubbles: true,
      cancelable: true,
      composed: true,
      pointerId: init.id,
      pointerType: init.pointerType,
      isPrimary: true,
      button: init.type === 'pointermove' ? -1 : 0,
      buttons: init.type === 'pointerup' ? 0 : 1,
      clientX: init.x,
      clientY: init.y,
    }));
  }, { type, x, y, pointerType, id });
}

export async function center(locator) {
  const box = await locator.boundingBox();
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}
