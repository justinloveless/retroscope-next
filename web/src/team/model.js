// Team room reducers (U28 model contract). They mirror TeamProjection's wire events. Notices that ask
// the page to refetch over HTTP (`team.boards.changed`, `team.poker.changed`, `team.action.comments`)
// bump revision counters, so a screen can select a counter and refetch when it moves.

function withItems(state, change) {
  const items = new Map(state.items);
  change(items);
  return { ...state, items };
}

// Deleted ids are remembered so an HTTP page fetched before the delete cannot bring the item back.
function without(state, id) {
  const next = withItems(state, (items) => items.delete(id));
  return { ...next, removed: new Set(state.removed).add(id) };
}

function patchItem(state, id, patch) {
  const item = state.items.get(id);
  return item ? withItems(state, (items) => items.set(id, { ...item, ...patch(item) })) : state;
}

export function snapshot(d) {
  return {
    teamId: d.teamId,
    name: d.name,
    members: d.members,
    items: new Map(d.actionItems.map((item) => [item.id, item])),
    endorsementTypes: d.endorsementTypes,
    pointDescriptions: d.pointDescriptions ?? {},
    maxEndorsements: d.maxEndorsements,
    boardsRev: 0,
    pokerRev: 0,
    poker: {},
    commentsRev: {},
    removed: new Set(),
  };
}

export function apply(state, { k, d }) {
  switch (k) {
    case 'team.action.added':
    case 'team.action.updated':
      return withItems(state, (items) => items.set(d.item.id, d.item));
    case 'team.action.text':
      return patchItem(state, d.itemId, (item) => (d.sourceVersion > item.sourceVersion
        ? { text: d.text, sourceVersion: d.sourceVersion }
        : {}));
    case 'team.action.deleted':
      return without(state, d.itemId);
    case 'team.action.comments': {
      const next = patchItem(state, d.itemId, () => ({ commentCount: d.count }));
      return { ...next, commentsRev: { ...state.commentsRev, [d.itemId]: (state.commentsRev[d.itemId] ?? 0) + 1 } };
    }
    case 'team.members':
      return { ...state, members: d.members };
    case 'team.boards.changed':
      return { ...state, boardsRev: state.boardsRev + 1 };
    case 'team.poker.changed':
      return {
        ...state,
        pokerRev: state.pokerRev + 1,
        poker: { ...state.poker, [d.sessionId]: { pointed: d.pointed, total: d.total } },
      };
    case 'team.settings':
      return {
        ...state,
        pointDescriptions: d.pointDescriptions ?? state.pointDescriptions,
        maxEndorsements: d.maxEndorsements ?? state.maxEndorsements,
      };
    default:
      return state;
  }
}

/** The intent of a restored outbox entry (its apply closure did not survive the reload). */
export function optimistic(state, k, d, now = Date.now(), by = null) {
  switch (k) {
    case 'team.action.assign':
      return patchItem(state, d.itemId, () => ({ assignedTo: d.userId ?? undefined }));
    case 'team.action.done':
      return patchItem(state, d.itemId, (item) => (d.done
        ? { doneAt: item.doneAt ?? now, doneBy: item.doneBy ?? by ?? undefined }
        : { doneAt: undefined, doneBy: undefined }));
    case 'team.action.delete':
      return without(state, d.itemId);
    default:
      return state;
  }
}

export const model = { snapshot, apply, optimistic };
